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
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
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
    /** A half-closed TCP flow whose other direction moves no bytes for this long is closed (else it leaks forever). */
    private val halfCloseIdleMs: Long = 60_000,
) {
    private val pool = Executors.newCachedThreadPool()
    private val open: MutableSet<Socket> = ConcurrentHashMap.newKeySet()
    private var server: ServerSocket? = null
    val localPort get() = server?.localPort ?: -1

    fun start() {
        val s = ServerSocket().apply { reuseAddress = true; bind(InetSocketAddress(bind, port), 128) }
        server = s
        pool.execute {
            while (!s.isClosed) {
                // A failed accept (client reset mid-handshake, fd pressure...) must never end the loop: that left the
                // service "sharing" while refusing every new connection until the user restarted it on the phone.
                val c = try { s.accept() } catch (e: IOException) {
                    if (s.isClosed) break
                    Thread.sleep(50)
                    continue
                }
                if (!runCatching { allow(c.inetAddress) }.getOrDefault(false)) { runCatching { c.close() }; continue }
                pool.execute { runCatching { handle(c) } }
            }
        }
    }

    /** Stops listening and closes every live flow, so "Stop sharing" really stops and nothing lingers into a restart. */
    fun stop() {
        runCatching { server?.close() }
        open.forEach { runCatching { it.close() } }
        pool.shutdownNow()
    }

    private fun handle(c: Socket): Unit = c.use {
        open += c
        try { serve(c) } finally { open -= c }
    }

    private fun serve(c: Socket) {
        c.tcpNoDelay = true
        c.soTimeout = 15_000 // a client that connects and never finishes the handshake must not hold a thread forever
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
        c.soTimeout = 0
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
        open += remote
        remote.use {
            remote.tcpNoDelay = true
            reply(out, 0)
            Stats.tcp.incrementAndGet()
            try {
                val upBytes = AtomicLong()
                val downBytes = AtomicLong()
                val upDone = CountDownLatch(1)
                val down = pool.submit {
                    pipe(remote.getInputStream(), out, Stats.down, downBytes)
                    runCatching { c.shutdownOutput() }
                    // Server finished first: if the client then goes silent, close it to unblock the reader below.
                    if (!whileBusy(upBytes) { upDone.await(halfCloseIdleMs, TimeUnit.MILLISECONDS) }) runCatching { c.close() }
                }
                pipe(inp, remote.getOutputStream(), Stats.up, upBytes)
                upDone.countDown()
                runCatching { remote.shutdownOutput() }
                // Client finished first: keep receiving while the server is still sending; give up once it goes silent.
                whileBusy(downBytes) { runCatching { down.get(halfCloseIdleMs, TimeUnit.MILLISECONDS) }.isSuccess }
            } finally {
                Stats.tcp.decrementAndGet()
                open -= remote
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

        private fun pipe(i: InputStream, o: OutputStream, total: AtomicLong, flow: AtomicLong) = runCatching {
            val buf = ByteArray(65536)
            while (true) {
                val n = i.read(buf)
                if (n < 0) break
                o.write(buf, 0, n)
                total.addAndGet(n.toLong())
                flow.addAndGet(n.toLong())
            }
        }

        /** Repeats [waitSlice] while [bytes] keeps moving. True once it succeeds; false after a slice with no progress. */
        private inline fun whileBusy(bytes: AtomicLong, waitSlice: () -> Boolean): Boolean {
            var seen = bytes.get()
            while (true) {
                if (waitSlice()) return true
                val now = bytes.get()
                if (now == seen) return false
                seen = now
            }
        }

        internal fun frame(src: InetSocketAddress, data: ByteArray, len: Int): ByteArray {
            val a = src.address.address
            val addr = byteArrayOf(if (a.size == 4) 1 else 4) + a + byteArrayOf((src.port shr 8).toByte(), src.port.toByte())
            return byteArrayOf((len shr 8).toByte(), len.toByte(), (3 + addr.size).toByte()) + addr + data.copyOf(len)
        }
    }
}
