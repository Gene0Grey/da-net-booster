package com.danet.booster

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import android.telephony.CellSignalStrengthLte
import android.telephony.CellSignalStrengthNr
import android.telephony.TelephonyManager

/** Which network the phone's own sockets use, and cellular signal. No runtime permission needed. */
object Radio {
    data class Info(val net: String, val operator: String, val dbm: Int?, val level: Int, val onWifi: Boolean) {
        val quality get() = when (level) { 4 -> "Excellent"; 3 -> "Good"; 2 -> "Fair"; 1 -> "Weak"; else -> "No signal" }
    }

    fun read(ctx: Context): Info {
        val tm = ctx.getSystemService(TelephonyManager::class.java)
        val op = tm.networkOperatorName.orEmpty().ifEmpty { tm.simOperatorName.orEmpty() }
        val cm = ctx.getSystemService(ConnectivityManager::class.java)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return Info("Offline", op, null, 0, false)
        if (caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) return Info("Wi-Fi", op, null, 4, true)

        if (Build.VERSION.SDK_INT < 29) return Info("Mobile", op, null, 0, false)
        val ss = tm.signalStrength ?: return Info("Mobile", op, null, 0, false)
        val cells = ss.cellSignalStrengths
        // NSA 5G reports both LTE and NR; NR is the one carrying data.
        val cell = cells.firstOrNull { it is CellSignalStrengthNr } ?: cells.firstOrNull()
        val net = when (cell) {
            is CellSignalStrengthNr -> "5G"
            is CellSignalStrengthLte -> "4G"
            null -> "Mobile"
            else -> "3G"
        }
        return Info(net, op, cell?.dbm?.takeIf { it in -150..-20 }, cell?.level ?: ss.level, false)
    }

    /** One-line JSON the desktop app polls through `adb forward tcp:1081 tcp:8001`. */
    fun json(ctx: Context): String {
        val r = read(ctx)
        // Without this exemption vivo (and other OEMs) pause the app ~15 s after the screen turns off, sharing or not.
        val exempt = ctx.getSystemService(android.os.PowerManager::class.java).isIgnoringBatteryOptimizations(ctx.packageName)
        return """{"up":${Stats.up.get()},"down":${Stats.down.get()},"tcp":${Stats.tcp.get()},"udp":${Stats.udp.get()},""" +
            """"net":"${r.net}","dbm":${r.dbm ?: "null"},"level":${r.level},"wifi":${r.onWifi},"exempt":$exempt}"""
    }
}
