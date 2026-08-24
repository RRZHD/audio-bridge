package com.audiobridge

import android.os.Handler
import android.os.Looper

/** Minimal status/log event bus from AudioService's worker threads to MainActivity's UI thread —
 *  avoids pulling in a LiveData dependency for something this small. */
object StatusBus {
    private val mainHandler = Handler(Looper.getMainLooper())

    @Volatile var statusListener: ((String) -> Unit)? = null
    @Volatile var logListener: ((String) -> Unit)? = null

    fun postStatus(text: String) {
        mainHandler.post { statusListener?.invoke(text) }
    }

    fun postLog(text: String) {
        mainHandler.post { logListener?.invoke(text) }
    }
}
