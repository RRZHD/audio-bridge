package com.audiobridge

import android.content.Context
import android.media.session.MediaSession
import android.media.session.PlaybackState
import android.os.SystemClock

/**
 * Makes the headphones' button control the PC's playback.
 *
 * While streaming, the app owns an active MediaSession (it is playing audio, after all), so Android
 * routes the headset button to it: one click = play/pause, double = next, triple = previous. Each is
 * forwarded to the PC, which presses the matching Windows media key. The session always reports
 * "playing" so a single click is always delivered as a toggle.
 */
class HeadsetControl(
    context: Context,
    private val send: (command: Int) -> Unit,
) {
    private val session = MediaSession(context.applicationContext, "AudioBridge")
    // Callbacks arrive on the main thread; the write to the PC is network I/O and must not run there.
    private val sender = java.util.concurrent.Executors.newSingleThreadExecutor { r -> Thread(r, "AudioBridge-headset").apply { isDaemon = true } }
    private fun dispatch(command: Int) { try { sender.execute { send(command) } } catch (_: Exception) { } }

    fun start() {
        // The callback needs a Looper; this runs on a worker thread, so hand it the main one.
        session.setCallback(object : MediaSession.Callback() {
            override fun onPlay() = dispatch(PLAY_PAUSE)
            override fun onPause() = dispatch(PLAY_PAUSE)
            override fun onStop() = dispatch(PLAY_PAUSE)
            override fun onSkipToNext() = dispatch(NEXT)
            override fun onSkipToPrevious() = dispatch(PREVIOUS)
        }, android.os.Handler(android.os.Looper.getMainLooper()))
        val actions = PlaybackState.ACTION_PLAY or PlaybackState.ACTION_PAUSE or PlaybackState.ACTION_PLAY_PAUSE or
            PlaybackState.ACTION_SKIP_TO_NEXT or PlaybackState.ACTION_SKIP_TO_PREVIOUS or PlaybackState.ACTION_STOP
        session.setPlaybackState(
            PlaybackState.Builder()
                .setActions(actions)
                .setState(PlaybackState.STATE_PLAYING, 0L, 1f, SystemClock.elapsedRealtime())
                .build()
        )
        session.isActive = true
    }

    fun stop() {
        try {
            session.isActive = false
            session.release()
            sender.shutdownNow()
        } catch (_: Exception) {
        }
    }

    companion object {
        const val PLAY_PAUSE = 1
        const val NEXT = 2
        const val PREVIOUS = 3
    }
}
