package com.audiobridge

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.media.AudioManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import java.util.concurrent.Executors
import kotlin.math.roundToInt

/**
 * Mirrors the phone's media volume with the PC's volume slider (see PROTOCOL.md, VOLUME frame).
 *
 * A change made with the phone's volume keys is sent to the PC; a level received from the PC is
 * applied to the media stream. The phone's own level goes to the PC when the session starts, so
 * connecting never makes either side jump. Changes the sync applied itself are not echoed back.
 */
class VolumeSync(
    private val context: Context,
    private val send: (level: Int, muted: Boolean) -> Unit,
) {
    private val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private val sender = Executors.newSingleThreadExecutor { r -> Thread(r, "AudioBridge-volume").apply { isDaemon = true } }
    private val main = Handler(Looper.getMainLooper())
    private val maxIndex = audio.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)

    @Volatile private var suppressUntil = 0L
    @Volatile private var lastSent = -1 to false
    private var registered = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(c: Context, intent: Intent) {
            val stream = intent.getIntExtra(EXTRA_VOLUME_STREAM_TYPE, -1)
            if (stream != AudioManager.STREAM_MUSIC) return
            if (System.currentTimeMillis() < suppressUntil) return // the echo of our own change
            sendCurrent()
        }
    }

    fun start() {
        val filter = IntentFilter(VOLUME_CHANGED_ACTION)
        if (Build.VERSION.SDK_INT >= 33) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            @Suppress("UnspecifiedRegisterReceiverFlag")
            context.registerReceiver(receiver, filter)
        }
        registered = true
        sendCurrent(force = true)
    }

    fun stop() {
        if (registered) try { context.unregisterReceiver(receiver) } catch (_: Exception) {}
        registered = false
        sender.shutdownNow()
    }

    /** Phone state -> PC (off the main thread: this does network I/O). */
    private fun sendCurrent(force: Boolean = false) {
        val level = (audio.getStreamVolume(AudioManager.STREAM_MUSIC) * 100f / maxIndex).roundToInt()
        val muted = audio.isStreamMute(AudioManager.STREAM_MUSIC)
        if (!force && lastSent == level to muted) return
        lastSent = level to muted
        try {
            sender.execute {
                try { send(level, muted) } catch (_: Exception) { /* session is going away */ }
            }
        } catch (_: Exception) { /* executor shut down */ }
    }

    /** PC -> phone. */
    fun applyFromPc(level: Int, muted: Boolean) {
        val index = (level.coerceIn(0, 100) * maxIndex / 100f).roundToInt()
        suppressUntil = System.currentTimeMillis() + 500
        lastSent = (index * 100f / maxIndex).roundToInt() to muted
        main.post {
            try {
                if (audio.getStreamVolume(AudioManager.STREAM_MUSIC) != index) {
                    audio.setStreamVolume(AudioManager.STREAM_MUSIC, index, 0)
                }
                if (audio.isStreamMute(AudioManager.STREAM_MUSIC) != muted) {
                    audio.adjustStreamVolume(
                        AudioManager.STREAM_MUSIC,
                        if (muted) AudioManager.ADJUST_MUTE else AudioManager.ADJUST_UNMUTE,
                        0,
                    )
                }
            } catch (_: SecurityException) {
                // e.g. Do Not Disturb policy forbids the change — the PC slider just won't move the phone.
            }
        }
    }

    private companion object {
        // Not in the public SDK, but stable since API 1 and what every volume-aware app listens to.
        const val VOLUME_CHANGED_ACTION = "android.media.VOLUME_CHANGED_ACTION"
        const val EXTRA_VOLUME_STREAM_TYPE = "android.media.EXTRA_VOLUME_STREAM_TYPE"
    }
}
