package com.audiobridge.transport

import android.content.Context
import android.net.wifi.WifiManager
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.SocketTimeoutException

data class DiscoveredServer(val name: String, val host: String, val port: Int)

/** An IPv4 network interface the phone could reach the PC through. */
data class NetLink(val name: String, val local: Inet4Address, val broadcast: InetAddress?)

/**
 * Finds PC servers by UDP broadcast — the PC's IP differs per network (hotspot, router, USB
 * modem), so the user shouldn't have to type it in. See PROTOCOL.md ("Discovery").
 *
 * The probe is sent per interface from a socket bound to that interface's own address, so it leaves
 * through the intended link (Wi-Fi *or* the USB-modem interface) and the PC's reply — whose source
 * address is the PC's IP on that link — is the address to connect to.
 */
object Discovery {
    // Must match DiscoveryResponder.DiscoveryPort on the PC side.
    private const val DISCOVERY_PORT = 45120
    private val REQUEST = "ABDQ".toByteArray(Charsets.US_ASCII)
    private val RESPONSE_MAGIC = "ABDR".toByteArray(Charsets.US_ASCII)

    // Mobile-data / virtual interfaces that can never lead to a PC.
    private val IGNORED_PREFIXES = listOf("lo", "rmnet", "ccmni", "dummy", "tun", "ppp", "p2p", "v4-", "clat")

    /** Every up, non-loopback IPv4 interface worth probing (Wi-Fi, USB modem, Ethernet...). */
    fun candidateLinks(): List<NetLink> {
        val links = mutableListOf<NetLink>()
        val interfaces = try { NetworkInterface.getNetworkInterfaces()?.toList() } catch (_: Exception) { null } ?: return links
        for (ni in interfaces) {
            val name = ni.name.lowercase()
            if (!ni.isUp || ni.isLoopback || IGNORED_PREFIXES.any { name.startsWith(it) }) continue
            for (ia in ni.interfaceAddresses) {
                val addr = ia.address as? Inet4Address ?: continue
                if (addr.isLinkLocalAddress) continue
                links.add(NetLink(ni.name, addr, ia.broadcast))
            }
        }
        return links
    }

    /** Wi-Fi discovery: probes all candidate interfaces at once. Blocking — call off the main thread. */
    fun discover(context: Context, timeoutMs: Int = 2000): List<DiscoveredServer> {
        val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as? WifiManager
        val multicastLock = wifiManager?.createMulticastLock("audiobridge-discovery")?.apply {
            setReferenceCounted(true)
            acquire()
        }
        try {
            val results = LinkedHashMap<String, DiscoveredServer>() // keyed by host: dedupe repeat replies
            val lock = Any()
            val threads = candidateLinks().map { link ->
                Thread {
                    val found = probe(link, timeoutMs)
                    synchronized(lock) { found.forEach { results[it.host] = it } }
                }.also { it.start() }
            }
            threads.forEach { it.join(timeoutMs + 1500L) }
            return synchronized(lock) { results.values.toList() }
        } finally {
            try { multicastLock?.release() } catch (_: Exception) {}
        }
    }

    /** Probes a single link (e.g. the USB-modem interface). Blocking. */
    fun probe(link: NetLink, timeoutMs: Int): List<DiscoveredServer> {
        val results = LinkedHashMap<String, DiscoveredServer>()
        try {
            DatagramSocket(null).use { socket ->
                socket.reuseAddress = true
                socket.broadcast = true
                socket.soTimeout = 250
                socket.bind(InetSocketAddress(link.local, 0)) // pin the probe to this interface

                val targets = listOfNotNull(link.broadcast, InetAddress.getByName("255.255.255.255"))
                val deadline = System.currentTimeMillis() + timeoutMs
                var nextSend = 0L
                val buf = ByteArray(512)
                while (System.currentTimeMillis() < deadline) {
                    val now = System.currentTimeMillis()
                    if (now >= nextSend) { // re-send a few times: the first packet can be lost while the link comes up
                        for (t in targets) {
                            try { socket.send(DatagramPacket(REQUEST, REQUEST.size, t, DISCOVERY_PORT)) } catch (_: Exception) {}
                        }
                        nextSend = now + 500
                    }
                    try {
                        val packet = DatagramPacket(buf, buf.size)
                        socket.receive(packet)
                        parseResponse(packet)?.let { results[it.host] = it }
                    } catch (_: SocketTimeoutException) {
                        // no reply in this slice — keep polling until the deadline
                    }
                }
            }
        } catch (_: Exception) {
            // an interface that can't be bound/used just yields nothing
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
}
