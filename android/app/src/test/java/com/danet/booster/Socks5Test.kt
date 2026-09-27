package com.danet.booster

import java.io.DataInputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import kotlin.concurrent.thread
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test

class Socks5Test {
    private val lo = InetAddress.getLoopbackAddress()
    private lateinit var proxy: Socks5

    @Before fun up() { proxy = Socks5(0).also { it.start() } }
    @After fun down() = proxy.stop()

    private fun greet(): Pair<DataInputStream, Socket> {
        val s = Socket(lo, proxy.localPort).apply { soTimeout = 5000 }
        val inp = DataInputStream(s.getInputStream())
        s.getOutputStream().write(byteArrayOf(5, 1, 0))
        assertEquals(5, inp.read()); assertEquals(0, inp.read())
        return inp to s
    }

    private fun port(p: Int) = byteArrayOf((p shr 8).toByte(), p.toByte())

    @Test fun connectRoundTrip() {
        val echo = ServerSocket(0, 1, lo)
        thread { echo.accept().use { c -> c.getOutputStream().write(c.getInputStream().readNBytes(5)) } }

        val (inp, s) = greet()
        s.getOutputStream().write(byteArrayOf(5, 1, 0, 1) + lo.address + port(echo.localPort))
        val rep = ByteArray(10).also(inp::readFully)
        assertEquals(0, rep[1].toInt())
        s.getOutputStream().write("hello".toByteArray())
        assertEquals("hello", String(ByteArray(5).also(inp::readFully)))
        s.close(); echo.close()
    }

    @Test fun udpInTcpRoundTrip() {
        val echo = DatagramSocket(0, lo)
        thread {
            val p = DatagramPacket(ByteArray(1500), 1500)
            echo.receive(p)
            echo.send(DatagramPacket(p.data, p.length, p.socketAddress))
        }

        val (inp, s) = greet()
        s.getOutputStream().write(byteArrayOf(5, 5, 0, 1, 0, 0, 0, 0, 0, 0))
        assertEquals(0, ByteArray(10).also(inp::readFully)[1].toInt())

        val payload = "dns?".toByteArray()
        val addr = byteArrayOf(1) + lo.address + port(echo.localPort)
        s.getOutputStream().write(byteArrayOf(0, payload.size.toByte(), (3 + addr.size).toByte()) + addr + payload)

        assertEquals(payload.size, inp.readUnsignedShort())
        assertEquals(10, inp.readUnsignedByte())
        assertArrayEquals(addr, ByteArray(7).also(inp::readFully)) // source = echo server
        assertArrayEquals(payload, ByteArray(payload.size).also(inp::readFully))
        s.close(); echo.close()
    }
}

class TetherTest {
    private fun ip(s: String) = java.net.InetAddress.getByName(s).address

    @Test fun subnetFilter() {
        val net = ip("192.168.42.129")
        assertEquals(true, Tether.inSubnet(ip("192.168.42.7"), net, 24))
        assertEquals(false, Tether.inSubnet(ip("192.168.43.7"), net, 24))
        assertEquals(false, Tether.inSubnet(ip("10.0.0.1"), net, 24))
        assertEquals(false, Tether.inSubnet(ip("192.168.42.7"), net, 0)) // prefix 0 would allow everyone
        assertEquals(true, Tether.allowed(java.net.InetAddress.getLoopbackAddress()))
    }
}

class UpdatesTest {
    @Test fun versionOrder() {
        assertEquals(true, Updates.newer("1.4.0", "1.3.0"))
        assertEquals(true, Updates.newer("1.10.0", "1.9.9")) // numeric, not string, compare
        assertEquals(false, Updates.newer("1.3.0", "1.3.0"))
        assertEquals(false, Updates.newer("1.2.9", "1.3"))
    }
}

class Socks5ResilienceTest {
    private val lo = InetAddress.getLoopbackAddress()

    private fun greetAndConnect(port: Int, target: Int): Pair<DataInputStream, Socket> {
        val s = Socket(lo, port).apply { soTimeout = 5000 }
        val inp = DataInputStream(s.getInputStream())
        s.getOutputStream().write(byteArrayOf(5, 1, 0))
        inp.readFully(ByteArray(2))
        s.getOutputStream().write(byteArrayOf(5, 1, 0, 1) + lo.address + byteArrayOf((target shr 8).toByte(), target.toByte()))
        assertEquals(0, ByteArray(10).also(inp::readFully)[1].toInt())
        return inp to s
    }

    /** Regression: one bad connection used to be able to end the accept loop, leaving the proxy deaf until restarted. */
    @Test fun listenerSurvivesAFailingConnection() {
        var calls = 0
        val proxy = Socks5(0, allow = { if (calls++ == 0) throw IllegalStateException("boom") else true }).also { it.start() }
        val echo = ServerSocket(0, 5, lo)
        thread { echo.accept().use { c -> c.getOutputStream().write(c.getInputStream().readNBytes(2)) } }
        runCatching { Socket(lo, proxy.localPort).use { it.soTimeout = 2000; it.getInputStream().read() } } // rejected
        val (inp, s) = greetAndConnect(proxy.localPort, echo.localPort)
        s.getOutputStream().write("ok".toByteArray())
        assertEquals("ok", String(ByteArray(2).also(inp::readFully)))
        s.close(); echo.close(); proxy.stop()
    }

    /** Regression: a flow whose server never closes after the client half-closes used to leak two sockets and a thread forever. */
    @Test fun halfOpenFlowIsReaped() {
        val proxy = Socks5(0, halfCloseIdleMs = 300).also { it.start() }
        val silent = ServerSocket(0, 5, lo)
        val held = java.util.concurrent.LinkedBlockingQueue<Socket>()
        thread { held.put(silent.accept()) } // accepts and then never sends or closes
        val (inp, s) = greetAndConnect(proxy.localPort, silent.localPort)
        s.shutdownOutput() // client is done sending
        val t0 = System.currentTimeMillis()
        assertEquals(-1, inp.read()) // proxy must close its side instead of waiting forever
        assert(System.currentTimeMillis() - t0 < 4000)
        s.close(); held.poll()?.close(); silent.close(); proxy.stop()
    }
}
