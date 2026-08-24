package com.audiobridge.transport

import java.io.Closeable
import java.io.InputStream
import java.io.OutputStream

enum class TransportKind { WIFI, USB, BLUETOOTH }

data class OpenConnection(val input: InputStream, val output: OutputStream, val closeable: Closeable)

/** A transport just needs to produce a duplex byte stream — the wire protocol above it (see
 *  Protocol.kt) is identical regardless of which one is used. */
interface Transport {
    val kind: TransportKind

    /** Blocking connect. Throws on failure/timeout. */
    fun connect(): OpenConnection
}
