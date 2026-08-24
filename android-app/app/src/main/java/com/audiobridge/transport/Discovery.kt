package com.audiobridge.transport

import android.content.Context
import android.net.wifi.WifiManager
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

data class DiscoveredServer(val name: String, val host: String, val port: Int)

/**
 * Finds PC servers on the local network by UDP broadcast — the PC's IP differs per
 * hotspot/router setup, so the user shouldn't have to type it in. See PROTOCOL.md ("Discovery").
 * Only meaningful for Wi-Fi; USB and Bluetooth don't need an IP at all.
 */
object Discovery {
    private const val DISCOVERY_PORT = 57121
    private val REQUEST = "ABDQ".toByteArray(Charsets.US_ASCII)
    private val RESPONSE_MAGIC = "ABDR".toByteArray(Charsets.US_ASCII)

    /** Blocking — call from a background thread. */
    fun discover(context: Context, timeoutMs: Int = 2000): List<DiscoveredServer> {
        val results = LinkedHashMap<String, DiscoveredServer>() // keyed by host, dedupe repeat replies

        val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        val multicastLock = wifiManager?.createMulticastLock("audiobridge-discovery")?.apply {
            setReferenceCounted(true)
            acquire()
        }

        try {
            DatagramSocket(null).use { socket ->
                socket.reuseAddress = true
                socket.broadcast = true
                socket.soTimeout = 300
                socket.bind(java.net.InetSocketAddress(0))

                for (broadcastAddress in broadcastAddresses(wifiManager)) {
                    try {
                        val packet = DatagramPacket(REQUEST, REQUEST.size, broadcastAddress, DISCOVERY_PORT)
                        socket.send(packet)
                    } catch (_: Exception) { /* try the next address */ }
                }

                val deadline = System.currentTimeMillis() + timeoutMs
                val buf = ByteArray(512)
                while (System.currentTimeMillis() < deadline) {
                    try {
                        val packet = DatagramPacket(buf, buf.size)
                        socket.receive(packet)
                        parseResponse(packet)?.let { results[it.host] = it }
                    } catch (_: SocketTimeoutException) {
                        // expected — just means no reply within this slice, keep polling until deadline
                    }
                }
            }
        } finally {
            try { multicastLock?.release() } catch (_: Exception) {}
        }

        return results.values.toList()
    }

    private fun parseResponse(packet: DatagramPacket): DiscoveredServer? {
        val data = packet.data
        val len = packet.length
        if (len < RESPONSE_MAGIC.size + 1) return null
        for (i in RESPONSE_MAGIC.indices) {
            if (data[i] != RESPONSE_MAGIC[i]) return null
        }
        var offset = RESPONSE_MAGIC.size
        val nameLen = data[offset].toInt() and 0xFF
        offset += 1
        if (len < offset + nameLen + 4) return null
        val name = String(data, offset, nameLen, Charsets.UTF_8)
        offset += nameLen
        val port = java.nio.ByteBuffer.wrap(data, offset, 4)
            .order(java.nio.ByteOrder.LITTLE_ENDIAN).int
        return DiscoveredServer(name, packet.address.hostAddress ?: return null, port)
    }

    /** Subnet broadcast address computed from the phone's own Wi-Fi IP/mask (more reliable across
     *  devices than the blanket 255.255.255.255, which some OEM Wi-Fi stacks drop). Falls back to
     *  the global broadcast address if DHCP info isn't available. */
    private fun broadcastAddresses(wifiManager: WifiManager?): List<InetAddress> {
        val addresses = mutableListOf<InetAddress>()
        val dhcp = wifiManager?.dhcpInfo
        if (dhcp != null && dhcp.ipAddress != 0 && dhcp.netmask != 0) {
            val broadcastInt = (dhcp.ipAddress and dhcp.netmask) or dhcp.netmask.inv()
            val bytes = byteArrayOf(
                (broadcastInt and 0xFF).toByte(),
                (broadcastInt shr 8 and 0xFF).toByte(),
                (broadcastInt shr 16 and 0xFF).toByte(),
                (broadcastInt shr 24 and 0xFF).toByte(),
            )
            try {
                addresses.add(InetAddress.getByAddress(bytes))
            } catch (_: Exception) { /* fall through to global broadcast below */ }
        }
        try {
            addresses.add(InetAddress.getByName("255.255.255.255"))
        } catch (_: Exception) { /* ignore */ }
        return addresses
    }
}
