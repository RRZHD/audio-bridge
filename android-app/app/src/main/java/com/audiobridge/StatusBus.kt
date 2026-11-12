package com.audiobridge

import android.os.Handler
import android.os.Looper

/** Minimal status/log event bus from AudioService's worker threads to MainActivity's UI thread —
 *  avoids pulling in a LiveData dependency for something this small. */
object StatusBus {
    private val mainHandler = Handler(Looper.getMainLooper())

    @Volatile var statusListener: ((String) -> Unit)? = null
    @Volatile var logListener: ((String) -> Unit)? = null

    /** Last status posted, so a screen (or the Quick Settings tile) opened later can show it. */
    @Volatile var lastStatus: String = "Отключено"
        private set

    /** Notified on every status change (the tile listens here to redraw). */
    @Volatile var changed: (() -> Unit)? = null

    fun postStatus(text: String) {
        lastStatus = text
        mainHandler.post {
            statusListener?.invoke(text)
            changed?.invoke()
        }
    }

    fun postLog(text: String) {
        mainHandler.post { logListener?.invoke(text) }
    }
}
