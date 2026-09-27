package com.danet.booster

import java.io.DataInputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.UnknownHostException
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong

/**
 * Minimal SOCKS5 server: no-auth, CONNECT (0x01) and hev "UDP-in-TCP" (0x05).
 * Every outbound socket is opened by this process, so traffic leaves as the phone's own traffic.
 *
 * UDP-in-TCP frame (both directions):
 *   DATLEN(2, BE, payload only) | HDRLEN(1) = 3 + len(ATYP+ADDR+PORT) | ATYP | ADDR | PORT(2) | DATA
 */
// ponytail: thread per connection, ceiling ~few hundred concurrent flows; move to NIO selector if browsing feels slow.
class Socks5(
    private val port: Int,
    private val bind: InetAddress = InetAddress.getLoopbackAddress(),
    private val allow: (InetAddress) -> Boolean = { true },
) {
    private val pool = Executors.newCachedThreadPool()
    private var server: ServerSocket? = null
    val localPort get() = server?.localPort ?: -1

    fun start() {
        val s = ServerSocket(port, 128, bind)
        server = s
        pool.execute {
            while (!s.isClosed) {
                val c = try { s.accept() } catch (e: IOException) { break }
                if (!allow(c.inetAddress)) { runCatching { c.close() }; continue }
                pool.execute { runCatching { handle(c) } }
            }
        }
    }

    fun stop() {
        runCatching { server?.close() }
        pool.shutdownNow()
    }

    private fun handle(c: Socket): Unit = c.use {
        c.tcpNoDelay = true
        val inp = DataInputStream(c.getInputStream().buffered())
        val out = c.getOutputStream()

        if (inp.readUnsignedByte() != 5) return
        val methods = ByteArray(inp.readUnsignedByte()).also(inp::readFully)
        if (0.toByte() !in methods) { out.write(byteArrayOf(5, 0xFF.toByte())); return }
        out.write(byteArrayOf(5, 0))

        if (inp.readUnsignedByte() != 5) return
        val cmd = inp.readUnsignedByte()
        inp.readUnsignedByte() // RSV
        val dst = readAddr(inp)
        when (cmd) {
            1 -> connect(c, inp, out, dst)
            5 -> udpInTcp(c, inp, out)
            else -> reply(out, 7) // command not supported
        }
    }

    private fun connect(c: Socket, inp: InputStream, out: OutputStream, dst: InetSocketAddress) {
        val remote = Socket()
        try {
            remote.connect(resolve(dst), 10_000)
        } catch (e: UnknownHostException) {
            remote.close(); reply(out, 4); return // host unreachable
        } catch (e: IOException) {
            remote.close(); reply(out, 5); return // connection refused
        }
        remote.use {
            remote.tcpNoDelay = true
            reply(out, 0)
            Stats.tcp.incrementAndGet()
            try {
                val down = pool.submit { pipe(remote.getInputStream(), out, Stats.down); runCatching { c.shutdownOutput() } }
                pipe(inp, remote.getOutputStream(), Stats.up)
                runCatching { remote.shutdownOutput() }
                runCatching { down.get() }
            } finally {
                Stats.tcp.decrementAndGet()
            }
        }
    }

    private fun udpInTcp(c: Socket, inp: DataInputStream, out: OutputStream) = DatagramSocket().use { udp ->
        reply(out, 0)
        c.soTimeout = 120_000 // drop idle UDP sessions
        Stats.udp.incrementAndGet()
        pool.execute {
            val buf = ByteArray(65535)
            val p = DatagramPacket(buf, buf.size)
            runCatching {
                while (true) {
                    p.setData(buf)
                    udp.receive(p)
                    Stats.down.addAndGet(p.length.toLong())
                    val f = frame(p.socketAddress as InetSocketAddress, buf, p.length)
                    synchronized(out) { out.write(f) }
                }
            }
            runCatching { c.close() }
        }
        try {
            while (true) {
                val datLen = inp.readUnsignedShort()
                val hdrLen = inp.readUnsignedByte()
                val dst = readAddr(inp)
                if (hdrLen != 3 + addrLen(dst)) throw IOException("bad hdrlen $hdrLen")
                val data = ByteArray(datLen).also(inp::readFully)
                val target = try { resolve(dst) } catch (e: UnknownHostException) { continue }
                udp.send(DatagramPacket(data, datLen, target))
                Stats.up.addAndGet(datLen.toLong())
            }
        } catch (_: IOException) {
        } finally {
            Stats.udp.decrementAndGet()
        }
    }

    companion object {
        private fun resolve(a: InetSocketAddress) =
            if (a.isUnresolved) InetSocketAddress(InetAddress.getByName(a.hostString), a.port) else a

        private fun readAddr(inp: DataInputStream): InetSocketAddress = when (val atyp = inp.readUnsignedByte()) {
            1 -> ByteArray(4).also(inp::readFully).let { InetSocketAddress(InetAddress.getByAddress(it), inp.readUnsignedShort()) }
            4 -> ByteArray(16).also(inp::readFully).let { InetSocketAddress(InetAddress.getByAddress(it), inp.readUnsignedShort()) }
            3 -> ByteArray(inp.readUnsignedByte()).also(inp::readFully)
                .let { InetSocketAddress.createUnresolved(String(it, Charsets.US_ASCII), inp.readUnsignedShort()) }
            else -> throw IOException("bad atyp $atyp")
        }

        /** Bytes of ATYP+ADDR+PORT as they were on the wire. */
        private fun addrLen(a: InetSocketAddress) = when {
            a.isUnresolved -> 1 + 1 + a.hostString.length + 2
            a.address.address.size == 4 -> 7
            else -> 19
        }

        private fun reply(out: OutputStream, code: Int) =
            out.write(byteArrayOf(5, code.toByte(), 0, 1, 0, 0, 0, 0, 0, 0))

        private fun pipe(i: InputStream, o: OutputStream, count: AtomicLong) = runCatching {
            val buf = ByteArray(65536)
            while (true) {
                val n = i.read(buf)
                if (n < 0) break
                o.write(buf, 0, n)
                count.addAndGet(n.toLong())
            }
        }

        internal fun frame(src: InetSocketAddress, data: ByteArray, len: Int): ByteArray {
            val a = src.address.address
            val addr = byteArrayOf(if (a.size == 4) 1 else 4) + a + byteArrayOf((src.port shr 8).toByte(), src.port.toByte())
            return byteArrayOf((len shr 8).toByte(), len.toByte(), (3 + addr.size).toByte()) + addr + data.copyOf(len)
        }
    }
}
