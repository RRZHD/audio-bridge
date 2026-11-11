package com.audiobridge

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.os.Process
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import com.audiobridge.transport.BluetoothTransport
import com.audiobridge.transport.OpenConnection
import com.audiobridge.transport.Transport
import com.audiobridge.transport.TransportKind
import com.audiobridge.transport.UsbTransport
import com.audiobridge.transport.WifiTransport
import java.io.BufferedInputStream
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
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
        const val EXTRA_BUFFER_MS = "buffer_ms"

        const val DEFAULT_PC_BITRATE = 320_000
        const val DEFAULT_MIC_BITRATE = 32_000
        const val DEFAULT_BUFFER_MS = PlayoutBuffer.DEFAULT_TARGET_MS

        private const val TAG = "AudioBridge"
        private const val STATS_INTERVAL_MS = 10_000L
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
        val bufferMs = intent.getIntExtra(EXTRA_BUFFER_MS, DEFAULT_BUFFER_MS)

        ServiceCompat.startForeground(
            this, NOTIFICATION_ID, buildNotification("Подключение..."),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE or ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
        )

        if (running.get()) return START_STICKY

        val transport: Transport = when (mode) {
            "WIFI" -> WifiTransport(host, port)
            "USB" -> UsbTransport(applicationContext)
            "BLUETOOTH" -> BluetoothTransport(btAddress)
            else -> return START_NOT_STICKY
        }

        running.set(true)
        workerThread = thread(name = "AudioBridge-worker") { runLoop(transport, pcBitrate, micBitrate, bufferMs) }
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
        releaseLocks()
        super.onDestroy()
    }

    private fun runLoop(transport: Transport, pcBitrate: Int, micBitrate: Int, bufferMs: Int) {
        while (running.get()) {
            try {
                StatusBus.postStatus("Подключение (${transport.kind})...")
                val conn = transport.connect()
                StatusBus.postStatus("Подключено (${transport.kind})")
                updateNotification("Подключено (${transport.kind})")
                acquireLocks(transport.kind)
                try {
                    runSession(transport.kind, conn, pcBitrate, micBitrate, bufferMs, Settings(applicationContext).maxRawSampleRate)
                } finally {
                    releaseLocks()
                    // However the session ended (even by an exception) the link must be released, or an
                    // accessory stays open and every later open fails with "could not open /dev/usb_accessory".
                    try { conn.closeable.close() } catch (_: Exception) { }
                }
            } catch (e: Exception) {
                Log.e(TAG, "session error", e)
                StatusBus.postLog("Ошибка соединения: $e")
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

    /** Name shown on the PC next to this connection (the phone's user-visible device name). */
    private fun deviceName(): String =
        try {
            android.provider.Settings.Global.getString(contentResolver, android.provider.Settings.Global.DEVICE_NAME)
        } catch (_: Exception) {
            null
        }?.takeIf { it.isNotBlank() } ?: Build.MODEL

    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    // Without these Android is free to doze the CPU and put the Wi-Fi radio into power-save while
    // the screen is off — which shows up as bursts of latency and dropouts mid-stream.
    private fun acquireLocks(kind: TransportKind) {
        try {
            val pm = getSystemService(PowerManager::class.java)
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioBridge:session").apply {
                setReferenceCounted(false)
                acquire()
            }
            if (kind == TransportKind.WIFI) {
                val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                @Suppress("DEPRECATION")
                val mode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
                    WifiManager.WIFI_MODE_FULL_LOW_LATENCY else WifiManager.WIFI_MODE_FULL_HIGH_PERF
                wifiLock = wm.createWifiLock(mode, "AudioBridge:wifi").apply {
                    setReferenceCounted(false)
                    acquire()
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "Could not acquire wake/wifi lock: ${e.message}")
        }
    }

    private fun releaseLocks() {
        try { wifiLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        try { wakeLock?.takeIf { it.isHeld }?.release() } catch (_: Exception) {}
        wifiLock = null
        wakeLock = null
    }

    private fun runSession(kind: TransportKind, conn: OpenConnection, pcBitrate: Int, micBitrate: Int, bufferMs: Int, maxRawRate: Int) {
        val writeLock = Any()
        val sessionAlive = AtomicBoolean(true)
        val lastRecvAt = java.util.concurrent.atomic.AtomicLong(System.currentTimeMillis())

        val micCodec: MicAudioCodec = if (kind == TransportKind.BLUETOOTH)
            OpusMicCodec(MIC_SAMPLE_RATE, micBitrate) else RawPcm16MicCodec()

        // Tell the PC what bitrate to use for its Opus encoder (only takes effect if this session
        // ends up on the Opus/Bluetooth path — ignored by the raw Wi-Fi/USB path). See PROTOCOL.md.
        try {
            synchronized(writeLock) {
                FrameIO.writeFrame(conn.output, FrameType.CONFIG, FrameIO.buildConfigPayload(pcBitrate, maxRawRate, deviceName()))
            }
        } catch (e: Exception) {
            StatusBus.postLog("Не удалось отправить CONFIG: ${e.message}")
        }

        var pcCodec: PcAudioCodec? = null
        val playout = AtomicReference<PlayoutBuffer?>(null)
        var streamRate = 0
        // Sized generously per FORMAT frame below — raw Wi-Fi/USB chunks are at the PC's native
        // sample rate (uncapped, can exceed 48kHz for high-res audio setups), unlike the fixed
        // 48kHz/stereo Opus/Bluetooth path.
        var pcInterleavedScratch = FloatArray(4096)

        // Volume mirrors the PC slider both ways. Created before closeSession so it can be stopped there.
        val volumeSync = VolumeSync(applicationContext) { level, muted ->
            synchronized(writeLock) {
                FrameIO.writeFrame(conn.output, FrameType.VOLUME, byteArrayOf(level.toByte(), if (muted) 1 else 0))
            }
        }

        // Headset button -> PC media key (play/pause, next, previous).
        val headset = HeadsetControl(applicationContext) { command ->
            try {
                synchronized(writeLock) {
                    FrameIO.writeFrame(conn.output, FrameType.MEDIA, byteArrayOf(command.toByte()))
                }
            } catch (_: Exception) { /* session is going away */ }
        }

        fun closeSession() {
            if (!sessionAlive.compareAndSet(true, false)) return
            try { conn.closeable.close() } catch (_: IOException) { /* ignore */ }
            playout.getAndSet(null)?.release()
            volumeSync.stop()
            headset.stop()
        }

        // Reader thread: FORMAT + AUDIO_PC frames -> jitter buffer -> AudioTrack.
        val readerThread = thread(name = "AudioBridge-reader") {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
            // Frames are 5-byte header + payload: read through a buffer, not one tiny syscall each.
            val input = BufferedInputStream(conn.input, 64 * 1024)
            var lastStatsAt = System.currentTimeMillis()
            try {
                while (sessionAlive.get()) {
                    val frame = FrameIO.readFrame(input)
                    lastRecvAt.set(System.currentTimeMillis())
                    when (frame.type) {
                        FrameType.FORMAT -> {
                            val fmt = FrameIO.parseFormat(frame.payload)
                            pcCodec = if (fmt.codec == PcAudioCodecKind.OPUS)
                                OpusPcCodec(fmt.sampleRate, fmt.channels, 320_000)
                            else RawFloat32PcCodec()
                            // 2x the 20ms chunk size as headroom.
                            val neededSamples = (fmt.sampleRate * CHUNK_MILLIS / 1000) * fmt.channels * 2
                            if (pcInterleavedScratch.size < neededSamples) {
                                pcInterleavedScratch = FloatArray(neededSamples)
                            }
                            streamRate = fmt.sampleRate
                            playout.getAndSet(
                                PlayoutBuffer(
                                    AudioTrackSink(createPlaybackTrack(fmt.sampleRate, fmt.channels)),
                                    fmt.sampleRate, fmt.channels, bufferMs,
                                    onEvent = { StatusBus.postLog(it) },
                                )
                            )?.release()
                            StatusBus.postLog("Формат ПК: ${fmt.sampleRate}Hz, ${fmt.channels}ch, codec=${fmt.codec}, буфер $bufferMs мс")
                        }
                        FrameType.AUDIO_PC -> {
                            val codec = pcCodec ?: continue
                            val buffer = playout.get() ?: continue
                            // decode() returns INTERLEAVED samples — write exactly that many.
                            val samples = codec.decode(frame.payload, pcInterleavedScratch)
                            if (!buffer.write(pcInterleavedScratch, samples)) {
                                throw IOException("AudioTrack write failed")
                            }
                            if (buffer.isStalled()) {
                                // The output accepted the track but isn't consuming it. If we were
                                // asked for a high rate, drop to 48 kHz for next time and reconnect.
                                if (streamRate > DEFAULT_MAX_RAW_RATE) {
                                    Settings(applicationContext).maxRawSampleRate = DEFAULT_MAX_RAW_RATE
                                    throw IOException("Устройство не воспроизводит $streamRate Гц — снижаю до 48 кГц и переподключаюсь")
                                }
                                throw IOException("Устройство вывода не воспроизводит звук (позиция воспроизведения не меняется)")
                            }
                            val now = System.currentTimeMillis()
                            if (now - lastStatsAt >= STATS_INTERVAL_MS) {
                                lastStatsAt = now
                                val line = buffer.statsLine()
                                Log.i(TAG, line)
                                StatusBus.postLog(line)
                            }
                        }
                        FrameType.VOLUME -> if (frame.payload.size >= 2) {
                            volumeSync.applyFromPc(frame.payload[0].toInt() and 0xFF, (frame.payload[1].toInt() and 1) != 0)
                        }
                        else -> { /* PING / unknown: no-op, already refreshed lastRecvAt above */ }
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "reader stopped", e)
                StatusBus.postLog("Reader stopped: $e")
            }
            closeSession()
        }

        // Send the phone's volume to the PC now (so neither side jumps) and follow volume-key changes.
        try {
            volumeSync.start()
            headset.start()
        } catch (e: Exception) {
            // Volume sync / headset button are extras: never let them take the audio session down.
            Log.e(TAG, "extras failed", e)
            StatusBus.postLog("Громкость/кнопка наушников недоступны: $e")
        }

        // Mic thread: AudioRecord -> AUDIO_MIC frames. Off unless the user switched it on — a phone
        // microphone that is silently live (and, on the PC, played in the user's headphones) is a
        // nasty default.
        val micEnabled = Settings(applicationContext).micEnabled
        if (!micEnabled) StatusBus.postLog("Микрофон не передаётся (включается переключателем в приложении)")
        val micThread = if (!micEnabled) null else thread(name = "AudioBridge-mic") {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO)
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
                Log.e(TAG, "mic stopped", e)
                StatusBus.postLog("Mic capture stopped: $e")
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
        micThread?.join(2000)
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
