package com.danet.booster

import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/** Live proxy counters (pure JVM so Socks5Test can run without Android). */
object Stats {
    val up = AtomicLong()
    val down = AtomicLong()
    val tcp = AtomicInteger()
    val udp = AtomicInteger()
    @Volatile var startedAt = 0L
    /** Last time the desktop app polled stats: proves a PC is actually attached. */
    @Volatile var lastPoll = 0L

    fun reset() {
        up.set(0); down.set(0)
        startedAt = System.currentTimeMillis()
    }
}
