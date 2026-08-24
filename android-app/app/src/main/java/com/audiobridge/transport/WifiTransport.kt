package com.audiobridge.transport

import java.net.InetSocketAddress
import java.net.Socket

/** ПК раздаёт Wi-Fi, телефон подключается напрямую к его IP:port. */
class WifiTransport(private val host: String, private val port: Int) : Transport {
    override val kind = TransportKind.WIFI

    override fun connect(): OpenConnection {
        val socket = Socket()
        socket.tcpNoDelay = true
        socket.connect(InetSocketAddress(host, port), 5000)
        return OpenConnection(socket.getInputStream(), socket.getOutputStream(), socket)
    }
}
