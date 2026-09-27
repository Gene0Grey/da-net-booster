package com.danet.booster

import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/**
 * Android "USB tethering" used purely as a cable to the PC. The proxy listens on all IPv4 addresses, and this filter
 * lets through only loopback (adb developer mode) and the PC on the USB-tethering subnet, never Wi-Fi or mobile peers.
 */
object Tether {
    private val names = listOf("rndis", "ncm", "usb")
    @Volatile private var cache: List<Pair<ByteArray, Int>> = emptyList()
    @Volatile private var cachedAt = 0L

    /** IPv4 address/prefix pairs of the USB-tethering interface(s); empty when tethering is off. */
    fun subnets(): List<Pair<ByteArray, Int>> {
        val now = System.currentTimeMillis()
        if (now - cachedAt > 2000) {
            cache = runCatching {
                NetworkInterface.getNetworkInterfaces().toList()
                    .filter { n -> n.isUp && names.any { n.name.startsWith(it) } }
                    .flatMap { it.interfaceAddresses }
                    .filter { it.address is Inet4Address }
                    .map { it.address.address to it.networkPrefixLength.toInt() }
            }.getOrDefault(emptyList())
            cachedAt = now
        }
        return cache
    }

    val active get() = subnets().isNotEmpty()

    fun allowed(peer: InetAddress): Boolean =
        peer.isLoopbackAddress || (peer is Inet4Address && subnets().any { (net, prefix) -> inSubnet(peer.address, net, prefix) })

    internal fun inSubnet(a: ByteArray, net: ByteArray, prefix: Int): Boolean {
        if (a.size != net.size || prefix !in 1..a.size * 8) return false
        for (bit in 0 until prefix) {
            val mask = 0x80 ushr (bit % 8)
            if ((a[bit / 8].toInt() and mask) != (net[bit / 8].toInt() and mask)) return false
        }
        return true
    }
}
