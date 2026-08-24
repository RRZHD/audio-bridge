package com.audiobridge

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import com.audiobridge.transport.BluetoothTransport
import com.audiobridge.transport.OpenConnection
import com.audiobridge.transport.Transport
import com.audiobridge.transport.TransportKind
import com.audiobridge.transport.UsbTransport
import com.audiobridge.transport.WifiTransport
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

class AudioService : Service() {

    companion object {
        const val ACTION_START = "com.audiobridge.action.START"
        const val ACTION_STOP = "com.audiobridge.action.STOP"
        const val EXTRA_MODE = "mode"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_BT_ADDRESS = "bt_address"
        const val EXTRA_PC_BITRATE = "pc_bitrate"
        const val EXTRA_MIC_BITRATE = "mic_bitrate"

        const val DEFAULT_PC_BITRATE = 320_000
        const val DEFAULT_MIC_BITRATE = 32_000

        private const val CHANNEL_ID = "audio_bridge_channel"
        private const val NOTIFICATION_ID = 1

        const val MIC_SAMPLE_RATE = 16000
        const val CHUNK_MILLIS = 20
        const val MIC_SAMPLES_PER_CHUNK = MIC_SAMPLE_RATE * CHUNK_MILLIS / 1000 // 320
        const val RECONNECT_DELAY_MS = 2000L
        const val PING_INTERVAL_MS = 3000L
        const val DEAD_CONNECTION_MS = 9000L
    }

    private val running = AtomicBoolean(false)
    private var workerThread: Thread? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopBridge()
            return START_NOT_STICKY
        }

        val mode = intent?.getStringExtra(EXTRA_MODE) ?: return START_NOT_STICKY
        val host = intent.getStringExtra(EXTRA_HOST) ?: ""
        val port = intent.getIntExtra(EXTRA_PORT, 57120)
        val btAddress = intent.getStringExtra(EXTRA_BT_ADDRESS) ?: ""
        val pcBitrate = intent.getIntExtra(EXTRA_PC_BITRATE, DEFAULT_PC_BITRATE)
        val micBitrate = intent.getIntExtra(EXTRA_MIC_BITRATE, DEFAULT_MIC_BITRATE)

        ServiceCompat.startForeground(
            this, NOTIFICATION_ID, buildNotification("Подключение..."),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
        )

        if (running.get()) return START_STICKY

        val transport: Transport = when (mode) {
            "WIFI" -> WifiTransport(host, port)
            "USB" -> UsbTransport(port)
            "BLUETOOTH" -> BluetoothTransport(btAddress)
            else -> return START_NOT_STICKY
        }

        running.set(true)
        workerThread = thread(name = "AudioBridge-worker") { runLoop(transport, pcBitrate, micBitrate) }
        return START_STICKY
    }

    private fun stopBridge() {
        running.set(false)
        workerThread?.interrupt()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        StatusBus.postStatus("Отключено")
    }

    override fun onDestroy() {
        running.set(false)
        workerThread?.interrupt()
        super.onDestroy()
    }

    private fun runLoop(transport: Transport, pcBitrate: Int, micBitrate: Int) {
        while (running.get()) {
            try {
                StatusBus.postStatus("Подключение (${transport.kind})...")
                val conn = transport.connect()
                StatusBus.postStatus("Подключено (${transport.kind})")
                updateNotification("Подключено (${transport.kind})")
                runSession(transport.kind, conn, pcBitrate, micBitrate)
            } catch (e: Exception) {
                StatusBus.postLog("Ошибка соединения: ${e.message}")
            }
            if (!running.get()) break
            StatusBus.postStatus("Переподключение...")
            updateNotification("Переподключение...")
            try {
                Thread.sleep(RECONNECT_DELAY_MS)
            } catch (_: InterruptedException) {
                break
            }
        }
    }

    private fun runSession(kind: TransportKind, conn: OpenConnection, pcBitrate: Int, micBitrate: Int) {
        val writeLock = Any()
        val sessionAlive = AtomicBoolean(true)
        val lastRecvAt = java.util.concurrent.atomic.AtomicLong(System.currentTimeMillis())

        val micCodec: MicAudioCodec = if (kind == TransportKind.BLUETOOTH)
            OpusMicCodec(MIC_SAMPLE_RATE, micBitrate) else RawPcm16MicCodec()

        // Tell the PC what bitrate to use for its Opus encoder (only takes effect if this session
        // ends up on the Opus/Bluetooth path — ignored by the raw Wi-Fi/USB path). See PROTOCOL.md.
        try {
            synchronized(writeLock) {
                FrameIO.writeFrame(conn.output, FrameType.CONFIG, FrameIO.buildConfigPayload(pcBitrate))
            }
        } catch (e: Exception) {
            StatusBus.postLog("Не удалось отправить CONFIG: ${e.message}")
        }

        var pcCodec: PcAudioCodec? = null
        var audioTrack: AudioTrack? = null
        var pcChannels = 0
        // Sized generously per FORMAT frame below — raw Wi-Fi/USB chunks are at the PC's native
        // sample rate (uncapped, can exceed 48kHz for high-res audio setups), unlike the fixed
        // 48kHz/stereo Opus/Bluetooth path.
        var pcInterleavedScratch = FloatArray(4096)

        fun closeSession() {
            if (!sessionAlive.compareAndSet(true, false)) return
            try { conn.closeable.close() } catch (_: IOException) { /* ignore */ }
            audioTrack?.let { try { it.stop(); it.release() } catch (_: Exception) {} }
        }

        // Reader thread: FORMAT + AUDIO_PC frames -> AudioTrack playback.
        val readerThread = thread(name = "AudioBridge-reader") {
            try {
                while (sessionAlive.get()) {
                    val frame = FrameIO.readFrame(conn.input)
                    lastRecvAt.set(System.currentTimeMillis())
                    when (frame.type) {
                        FrameType.FORMAT -> {
                            val fmt = FrameIO.parseFormat(frame.payload)
                            pcChannels = fmt.channels
                            pcCodec = if (fmt.codec == PcAudioCodecKind.OPUS)
                                OpusPcCodec(fmt.sampleRate, fmt.channels, 320_000)
                            else RawFloat32PcCodec()
                            // 2x the 20ms chunk size as jitter headroom.
                            val neededSamples = (fmt.sampleRate * CHUNK_MILLIS / 1000) * fmt.channels * 2
                            if (pcInterleavedScratch.size < neededSamples) {
                                pcInterleavedScratch = FloatArray(neededSamples)
                            }
                            audioTrack?.let { try { it.stop(); it.release() } catch (_: Exception) {} }
                            audioTrack = createPlaybackTrack(fmt.sampleRate, fmt.channels).also { it.play() }
                            StatusBus.postLog("Формат ПК: ${fmt.sampleRate}Hz, ${fmt.channels}ch, codec=${fmt.codec}")
                        }
                        FrameType.AUDIO_PC -> {
                            val codec = pcCodec ?: continue
                            val track = audioTrack ?: continue
                            val decoded = codec.decode(frame.payload, pcInterleavedScratch)
                            track.write(pcInterleavedScratch, 0, decoded * pcChannels, AudioTrack.WRITE_BLOCKING)
                        }
                        else -> { /* PING / unknown: no-op, already refreshed lastRecvAt above */ }
                    }
                }
            } catch (e: Exception) {
                StatusBus.postLog("Reader stopped: ${e.message}")
            }
            closeSession()
        }

        // Mic thread: AudioRecord -> AUDIO_MIC frames.
        val micThread = thread(name = "AudioBridge-mic") {
            var record: AudioRecord? = null
            try {
                val minBuf = AudioRecord.getMinBufferSize(
                    MIC_SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT
                )
                record = AudioRecord(
                    MediaRecorder.AudioSource.VOICE_COMMUNICATION,
                    MIC_SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
                    maxOf(minBuf, MIC_SAMPLES_PER_CHUNK * 2 * 4)
                )
                record.startRecording()
                val buf = ShortArray(MIC_SAMPLES_PER_CHUNK)
                while (sessionAlive.get()) {
                    val read = record.read(buf, 0, buf.size, AudioRecord.READ_BLOCKING)
                    if (read <= 0) continue
                    val encoded = micCodec.encode(buf, read)
                    synchronized(writeLock) {
                        FrameIO.writeFrame(conn.output, FrameType.AUDIO_MIC, encoded)
                    }
                }
            } catch (e: Exception) {
                StatusBus.postLog("Mic capture stopped: ${e.message}")
            } finally {
                record?.let { try { it.stop(); it.release() } catch (_: Exception) {} }
            }
            closeSession()
        }

        // Watchdog: PING keepalive + dead-connection detection.
        try {
            while (sessionAlive.get()) {
                Thread.sleep(PING_INTERVAL_MS)
                if (!sessionAlive.get()) break
                if (System.currentTimeMillis() - lastRecvAt.get() > DEAD_CONNECTION_MS) {
                    StatusBus.postLog("Соединение мертво (нет данных от ПК) — переподключение")
                    break
                }
                try {
                    synchronized(writeLock) {
                        FrameIO.writeFrame(conn.output, FrameType.PING, ByteArray(0))
                    }
                } catch (e: Exception) {
                    break
                }
            }
        } catch (_: InterruptedException) {
            // service stopping
        }

        closeSession()
        readerThread.join(2000)
        micThread.join(2000)
    }

    private fun createPlaybackTrack(sampleRate: Int, channels: Int): AudioTrack {
        val channelMask = if (channels >= 2) AudioFormat.CHANNEL_OUT_STEREO else AudioFormat.CHANNEL_OUT_MONO
        val format = AudioFormat.Builder()
            .setEncoding(AudioFormat.ENCODING_PCM_FLOAT)
            .setSampleRate(sampleRate)
            .setChannelMask(channelMask)
            .build()
        val attrs = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_MEDIA)
            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
            .build()
        val minBuf = AudioTrack.getMinBufferSize(sampleRate, channelMask, AudioFormat.ENCODING_PCM_FLOAT)
        val bufSize = maxOf(minBuf, sampleRate * channels * 4 / 5) // ~200ms headroom against jitter
        return AudioTrack.Builder()
            .setAudioAttributes(attrs)
            .setAudioFormat(format)
            .setBufferSizeInBytes(bufSize)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build()
    }

    private fun buildNotification(text: String): android.app.Notification {
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(CHANNEL_ID, "Audio Bridge", NotificationManager.IMPORTANCE_LOW)
            manager.createNotificationChannel(channel)
        }
        val stopIntent = Intent(this, AudioService::class.java).apply { action = ACTION_STOP }
        val stopPending = PendingIntent.getService(
            this, 0, stopIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Audio Bridge")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setOngoing(true)
            .addAction(0, "Стоп", stopPending)
            .build()
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification(text))
    }
}
