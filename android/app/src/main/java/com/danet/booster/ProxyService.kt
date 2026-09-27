package com.danet.booster

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.PowerManager
import java.net.InetAddress
import java.net.ServerSocket
import kotlin.concurrent.thread

/**
 * Foreground service owning the SOCKS5 proxy the PC reaches over USB tethering (phone's tether IP :8000),
 * or over `adb forward tcp:1080 tcp:8000` in developer mode. Tether.allowed keeps Wi-Fi and mobile peers out.
 */
class ProxyService : Service() {
    private var proxy: Socks5? = null
    private var wake: PowerManager.WakeLock? = null
    private var statsServer: ServerSocket? = null

    override fun onBind(intent: Intent?) = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        val n = notification()
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(1, n)

        if (proxy == null) {
            Stats.reset()
            // Right after a stop the old listener can still hold the port for a moment; a crash here killed the app.
            proxy = retry { Socks5(PORT, ANY_V4, Tether::allowed).also { it.start() } } ?: run { stopSelf(); return START_NOT_STICKY }
            retry { startStatsServer() }
            wake = (getSystemService(POWER_SERVICE) as PowerManager)
                .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "danet:proxy").apply { acquire() }
            running = true
        }
        return START_STICKY
    }

    override fun onDestroy() {
        proxy?.stop()
        proxy = null
        runCatching { statsServer?.close() }
        wake?.takeIf { it.isHeld }?.release()
        running = false
        super.onDestroy()
    }

    /** Answers every connection with one JSON line of Radio.json(), then closes. */
    private fun startStatsServer() {
        val s = ServerSocket().apply { reuseAddress = true; bind(java.net.InetSocketAddress(ANY_V4, STATS_PORT), 8) }
        statsServer = s
        thread(isDaemon = true, name = "stats") {
            while (!s.isClosed) {
                val c = try { s.accept() } catch (e: Exception) {
                    if (s.isClosed) break
                    Thread.sleep(50)
                    continue // one failed accept must not stop the stats feed for the rest of the session
                }
                if (!runCatching { Tether.allowed(c.inetAddress) }.getOrDefault(false)) { runCatching { c.close() }; continue }
                Stats.lastPoll = System.currentTimeMillis()
                runCatching { c.use { it.getOutputStream().write((Radio.json(this@ProxyService) + "\n").toByteArray()) } }
            }
        }
    }

    private fun <T> retry(block: () -> T): T? {
        repeat(5) { attempt ->
            try { return block() } catch (e: java.io.IOException) { if (attempt < 4) Thread.sleep(300) }
        }
        return null
    }

    private fun notification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Tether", NotificationManager.IMPORTANCE_LOW))
        val stop = PendingIntent.getService(
            this, 0, Intent(this, ProxyService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher_fg)
            .setContentTitle("Da Net Booster")
            .setContentText("Sharing mobile data with your PC over USB")
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
    }

    companion object {
        const val PORT = 8000
        const val STATS_PORT = 8001
        private val ANY_V4: InetAddress = InetAddress.getByName("0.0.0.0")
        const val ACTION_STOP = "com.danet.booster.STOP"
        private const val CHANNEL = "tether"
        @Volatile var running = false
    }
}
