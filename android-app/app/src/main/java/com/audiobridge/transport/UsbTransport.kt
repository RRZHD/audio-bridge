package com.audiobridge.transport

import java.net.InetSocketAddress
import java.net.Socket

/**
 * Requires `adb reverse tcp:PORT tcp:PORT` to already be running on the PC (over the existing USB
 * debugging connection) — that maps our 127.0.0.1:port here to the PC's listener. See PROTOCOL.md.
 */
class UsbTransport(private val port: Int) : Transport {
    override val kind = TransportKind.USB

    override fun connect(): OpenConnection {
        val socket = Socket()
        socket.tcpNoDelay = true
        socket.connect(InetSocketAddress("127.0.0.1", port), 5000)
        return OpenConnection(socket.getInputStream(), socket.getOutputStream(), socket)
    }
}
