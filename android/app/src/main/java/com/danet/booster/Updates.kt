package com.danet.booster

import android.app.DownloadManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import kotlin.concurrent.thread

/**
 * Sideloaded updates from GitHub Releases (repo baked in at build time by CI).
 * Android always asks the user to confirm installing a sideloaded APK. That prompt is the OS's, not ours.
 */
object Updates {
    data class Release(val version: String, val apkUrl: String)

    private const val CHANNEL = "updates"
    private const val EVERY_MS = 6 * 3600_000L

    /** Latest newer release already known (from the last check), or null. */
    fun cached(ctx: Context): Release? {
        val p = ctx.getSharedPreferences("updates", 0)
        val v = p.getString("version", null) ?: return null
        val url = p.getString("url", null) ?: return null
        return if (newer(v, BuildConfig.VERSION_NAME)) Release(v, url) else null
    }

    /** Throttled background check; notifies once per new version and calls [found] on the main thread. */
    fun check(ctx: Context, found: (Release) -> Unit) {
        val repo = BuildConfig.UPDATE_REPO
        val p = ctx.getSharedPreferences("updates", 0)
        if (repo.isEmpty() || System.currentTimeMillis() - p.getLong("checked", 0) < EVERY_MS) return
        val app = ctx.applicationContext
        thread(isDaemon = true, name = "update-check") {
            runCatching {
                val c = URL("https://api.github.com/repos/$repo/releases/latest").openConnection() as HttpURLConnection
                c.setRequestProperty("Accept", "application/vnd.github+json")
                c.connectTimeout = 8000; c.readTimeout = 8000
                val j = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
                val version = j.getString("tag_name").removePrefix("v")
                val assets = j.getJSONArray("assets")
                val apk = (0 until assets.length()).map { assets.getJSONObject(it) }
                    .firstOrNull { it.getString("name").endsWith(".apk") }?.getString("browser_download_url") ?: return@runCatching
                val seen = p.getString("version", null)
                p.edit().putLong("checked", System.currentTimeMillis()).putString("version", version).putString("url", apk).apply()
                if (!newer(version, BuildConfig.VERSION_NAME)) return@runCatching
                if (seen != version) notify(app, version)
                Handler(Looper.getMainLooper()).post { found(Release(version, apk)) }
            }
        }
    }

    /** Download via the system DownloadManager, then hand the APK to the system installer. */
    fun install(ctx: Context, r: Release) {
        if (!ctx.packageManager.canRequestPackageInstalls()) {
            // One-time: allow this app to install updates ("Install unknown apps").
            ctx.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${ctx.packageName}")))
            return
        }
        val dm = ctx.getSystemService(DownloadManager::class.java)
        val id = dm.enqueue(
            DownloadManager.Request(Uri.parse(r.apkUrl))
                .setTitle("Da Net Booster ${r.version}")
                .setMimeType("application/vnd.android.package-archive")
                .setDestinationInExternalFilesDir(ctx, Environment.DIRECTORY_DOWNLOADS, "DaNetBooster-${r.version}.apk")
                .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
        )
        val app = ctx.applicationContext
        thread(isDaemon = true, name = "update-download") {
            // Poll instead of a receiver: survives the activity going away and needs no exported receiver.
            repeat(600) {
                Thread.sleep(1000)
                dm.query(DownloadManager.Query().setFilterById(id)).use { cur ->
                    if (!cur.moveToFirst()) return@thread
                    when (cur.getInt(cur.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS))) {
                        DownloadManager.STATUS_SUCCESSFUL -> {
                            val uri = dm.getUriForDownloadedFile(id) ?: return@thread
                            app.startActivity(
                                Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
                                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
                            )
                            return@thread
                        }
                        DownloadManager.STATUS_FAILED -> return@thread
                    }
                }
            }
        }
    }

    private fun notify(ctx: Context, version: String) {
        val nm = ctx.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Updates", NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(ctx, 1, Intent(ctx, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        nm.notify(2, Notification.Builder(ctx, CHANNEL)
            .setSmallIcon(R.drawable.ic_launcher_fg)
            .setContentTitle("Update available: $version")
            .setContentText("Tap to update Da Net Booster")
            .setContentIntent(open)
            .setAutoCancel(true)
            .build())
    }

    internal fun newer(a: String, b: String): Boolean {
        fun parts(v: String) = v.split('.', '-').map { it.takeWhile(Char::isDigit).toIntOrNull() ?: 0 }
        val x = parts(a); val y = parts(b)
        for (i in 0 until maxOf(x.size, y.size)) {
            val d = x.getOrElse(i) { 0 } - y.getOrElse(i) { 0 }
            if (d != 0) return d > 0
        }
        return false
    }
}
