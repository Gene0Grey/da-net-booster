package com.danet.booster

import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.Uri
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.widget.LinearLayout
import android.widget.LinearLayout.LayoutParams
import android.widget.ScrollView
import android.widget.TextView
import java.util.Calendar

/** "Desk Signal": the phone sits next to the PC; it answers "is my signal OK" and "is it sharing" from arm's length. */
class MainActivity : Activity() {
    private val loop = Handler(Looper.getMainLooper())
    private val ring by lazy { PowerRing(this) }
    private val verb by lazy { label(16f, medium = true).apply { gravity = Gravity.CENTER } }

    private val pillDot by lazy { View(this) }
    private val pillText by lazy { label(12f, medium = true) }
    private val pill by lazy { LinearLayout(this) }

    private val meter by lazy { SignalMeter(this) }
    private val netV by lazy { label(40f, medium = true) }
    private val opV by lazy { label(14f, Palette.TEXT2) }
    private val qualityV by lazy { label(14f) }
    private val tipV by lazy { label(13f, Palette.TEXT2) }

    private val checklist by lazy { card() }
    private val tickTether by lazy { Tick(this) }
    private val fixTether by lazy { button("Turn on") { openTetherSettings() } }
    private val tickPlug by lazy { Tick(this) }
    private val tickBattery by lazy { Tick(this) }
    private val fixBattery by lazy { button("Fix") { askBatteryExemption() } }

    private val live by lazy { card() }
    private val rateV by lazy { label(14f) }
    private val graph by lazy { Graph(this) }
    private val metaV by lazy { label(12f, Palette.TEXT2) }

    private var lastUp = -1L // -1 = no baseline yet (first tick after the screen opens)
    private var lastDown = 0L

    private val tick = object : Runnable {
        override fun run() { refresh(); loop.postDelayed(this, 1000) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        ring.setOnClickListener { if (ProxyService.running) stop() else start() }

        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(px(20), px(12), px(20), px(24))
            addView(header(), LayoutParams(-1, px(56)))
            addView(updateBanner, LayoutParams(-1, -2).apply { bottomMargin = px(8) })
            addView(signalCard(), LayoutParams(-1, -2).apply { topMargin = px(8) })
            addView(ring, LayoutParams(px(168), px(168)).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = px(24) })
            addView(verb, LayoutParams(-1, -2).apply { topMargin = px(4); bottomMargin = px(24) })
            addView(checklistCard(), LayoutParams(-1, -2))
            addView(liveCard(), LayoutParams(-1, -2))
        }
        val scroll = ScrollView(this).apply {
            setBackgroundColor(Palette.BG)
            addView(col)
        }
        // targetSdk 35 is edge-to-edge: keep content out from under the status and gesture bars.
        if (Build.VERSION.SDK_INT >= 30) scroll.setOnApplyWindowInsetsListener { v, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars())
            v.setPadding(0, bars.top, 0, bars.bottom)
            insets
        } else scroll.fitsSystemWindows = true
        setContentView(scroll)

        if (intent?.action == ACTION_START) start()
    }

    override fun onResume() {
        super.onResume()
        loop.post(tick)
        showUpdate(Updates.cached(this))
        Updates.check(this) { showUpdate(it) }
    }

    private val updateText by lazy { label(14f, medium = true) }
    private val updateBanner by lazy {
        LinearLayout(this).apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(px(16), px(10), px(12), px(10))
            background = rounded(Palette.alpha(Palette.ACCENT, 0x24), 16)
            addView(updateText, LayoutParams(0, -2, 1f))
            addView(button("Update") { Updates.cached(this@MainActivity)?.let { Updates.install(this@MainActivity, it) } }, LayoutParams(-2, px(40)))
            visibility = View.GONE
        }
    }

    private fun showUpdate(r: Updates.Release?) {
        updateBanner.visibility = if (r == null) View.GONE else View.VISIBLE
        if (r != null) updateText.text = "Version ${r.version} is available"
    }
    override fun onPause() { loop.removeCallbacks(tick); super.onPause() }

    private fun header() = LinearLayout(this).apply {
        gravity = Gravity.CENTER_VERTICAL
        addView(label(20f, medium = true, text = "Da Net Booster"), LayoutParams(0, -2, 1f))
        pill.apply {
            gravity = Gravity.CENTER_VERTICAL
            setPadding(px(10), 0, px(12), 0)
            addView(pillDot, LayoutParams(px(6), px(6)).apply { marginEnd = px(8) })
            addView(pillText)
        }
        addView(pill, LayoutParams(-2, px(28)))
    }

    private fun signalCard() = card().apply {
        addView(LinearLayout(context).apply {
            gravity = Gravity.CENTER_VERTICAL
            addView(meter, LayoutParams(-2, -2).apply { marginEnd = px(20) })
            addView(LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                addView(LinearLayout(context).apply {
                    gravity = Gravity.BOTTOM
                    addView(netV)
                    addView(opV, LayoutParams(-2, -2).apply { marginStart = px(10); bottomMargin = px(9) })
                })
                addView(qualityV)
            })
        })
        addView(tipV, LayoutParams(-1, -2).apply { topMargin = px(14) })
    }

    private fun checklistCard() = checklist.apply {
        addView(label(14f, medium = true, text = "Before you connect"), LayoutParams(-1, -2).apply { bottomMargin = px(4) })
        addView(checkRow(tickPlug, "Plugged into the PC", null))
        addView(checkRow(tickTether, "USB tethering on", fixTether))
        addView(checkRow(tickBattery, "Keep running in background", fixBattery))
        addView(label(13f, Palette.TEXT2, text = "Then tap Start sharing here and Connect on the PC."), LayoutParams(-1, -2).apply { topMargin = px(8) })
    }

    private fun checkRow(t: Tick, text: String, action: View?) = LinearLayout(this).apply {
        gravity = Gravity.CENTER_VERTICAL
        minimumHeight = px(48)
        addView(t, LayoutParams(-2, -2).apply { marginEnd = px(14) })
        addView(label(15f, text = text), LayoutParams(0, -2, 1f))
        if (action != null) addView(action, LayoutParams(-2, px(40)))
    }

    private fun liveCard() = live.apply {
        addView(rateV)
        addView(graph, LayoutParams(-1, px(56)).apply { topMargin = px(12); bottomMargin = px(12) })
        addView(metaV)
    }

    private fun card() = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(px(20), px(18), px(20), px(18))
        background = rounded(Palette.SURFACE, 20, Palette.STROKE)
    }

    private fun button(text: String, onClick: () -> Unit) = TextView(this).apply {
        this.text = text
        textSize = 14f
        typeface = android.graphics.Typeface.create("sans-serif-medium", android.graphics.Typeface.NORMAL)
        setTextColor(Palette.ACCENT)
        gravity = Gravity.CENTER
        minWidth = px(64)
        setPadding(px(16), 0, px(16), 0)
        background = rounded(Palette.alpha(Palette.ACCENT, 0x24), 20)
        ripple()
        setOnClickListener { onClick() }
    }

    private fun start() {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission("android.permission.POST_NOTIFICATIONS") != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf("android.permission.POST_NOTIFICATIONS"), 0)
        startForegroundService(Intent(this, ProxyService::class.java))
        ring.set(true, animate = true)
        verb.text = "Stop sharing"
    }

    private fun stop() {
        stopService(Intent(this, ProxyService::class.java))
        ring.set(false, animate = false)
        verb.text = "Start sharing"
        graph.clear()
    }

    private fun refresh() {
        val on = ProxyService.running
        ring.set(on, animate = false)
        verb.text = if (on) "Stop sharing" else "Start sharing"

        // Honest status: running is not the same as a PC actually using it.
        val pcAttached = on && System.currentTimeMillis() - Stats.lastPoll < 3000
        val (text, color) = when {
            !on -> "Not sharing" to Palette.TEXT3
            pcAttached -> "PC connected" to Palette.GOOD
            else -> "Waiting for PC" to Palette.WARN
        }
        pillText.text = text
        pillText.setTextColor(if (on) color else Palette.TEXT2)
        pillDot.background = rounded(color, 3)
        pill.background = rounded(if (on) Palette.alpha(color, 0x24) else Palette.SURFACE, 14)

        val r = Radio.read(this)
        meter.level = if (r.onWifi) 0 else r.level
        meter.contentDescription = "Signal ${r.level} of 4"
        netV.text = r.net
        netV.setTextColor(if (r.onWifi) Palette.WARN else Palette.TEXT)
        opV.text = if (r.onWifi) "" else r.operator
        qualityV.text = when {
            r.onWifi -> "Phone is on Wi-Fi, not mobile data"
            r.dbm != null -> "${Fmt.dbm(r.dbm)} · ${r.quality}"
            else -> r.quality
        }
        qualityV.setTextColor(if (r.onWifi) Palette.WARN else Palette.level(r.level))
        val hour = Calendar.getInstance().get(Calendar.HOUR_OF_DAY)
        tipV.text = when {
            r.onWifi -> "Turn off the phone's Wi-Fi so the PC uses your mobile data."
            !r.onWifi && r.level <= 2 -> "Weak signal. Move the phone nearer a window or higher up."
            hour >= 19 -> "Peak hours until midnight: the tower is busier and ping may rise."
            else -> ""
        }
        tipV.visibility = if (tipV.text.isEmpty()) View.GONE else View.VISIBLE

        // The checklist stays up while sharing if USB tethering is off: the PC can't reach us without it.
        val tether = Tether.active
        checklist.visibility = if (on && tether) View.GONE else View.VISIBLE
        live.visibility = if (on) View.VISIBLE else View.GONE
        if (!on || !tether) {
            tickTether.done = tether
            fixTether.visibility = if (tether) View.GONE else View.VISIBLE
            val plug = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0)
            tickPlug.done = plug == BatteryManager.BATTERY_PLUGGED_USB
            val exempt = getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)
            tickBattery.done = exempt
            fixBattery.visibility = if (exempt) View.GONE else View.VISIBLE
            if (!on) return
        }

        val u = Stats.up.get()
        val d = Stats.down.get()
        if (lastUp < 0) { lastUp = u; lastDown = d; return } // no giant spike from bytes counted before the screen opened
        val du = (u - lastUp).coerceAtLeast(0)
        val dd = (d - lastDown).coerceAtLeast(0)
        lastUp = u; lastDown = d
        rateV.text = "Down ${Fmt.rate(dd.toDouble())}  ·  Up ${Fmt.rate(du.toDouble())}"
        graph.push(dd.toFloat(), du.toFloat())
        val n = Stats.tcp.get() + Stats.udp.get()
        metaV.text = "${if (n == 1) "1 connection" else "$n connections"} · ${Fmt.bytes(u + d)} · ${Fmt.clock(System.currentTimeMillis() - Stats.startedAt)}"
    }

    @SuppressLint("BatteryLife") // sideloaded tether app; being killed mid-game is the bigger problem
    /** Android has no public action for the tethering screen; try the usual component, then fall back. */
    private fun openTetherSettings() {
        val tries = listOf(
            Intent().setClassName("com.android.settings", "com.android.settings.TetherSettings"),
            Intent("android.settings.TETHER_SETTINGS"),
            Intent(Settings.ACTION_WIRELESS_SETTINGS),
        )
        for (i in tries) if (runCatching { startActivity(i) }.isSuccess) return
    }

    private fun askBatteryExemption() {
        startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
    }

    companion object {
        const val ACTION_START = "com.danet.booster.START"
    }
}
