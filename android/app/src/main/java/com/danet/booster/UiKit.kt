package com.danet.booster

import android.animation.ValueAnimator
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.graphics.drawable.ShapeDrawable
import android.graphics.drawable.shapes.OvalShape
import android.view.View
import android.view.animation.DecelerateInterpolator
import android.widget.TextView
import kotlin.math.max
import kotlin.math.min

/** Colour roles shared with the desktop app. Good/Warn/Bad are for status only, never data series. */
object Palette {
    const val BG = 0xFF0B0E14.toInt()
    const val SURFACE = 0xFF131821.toInt()
    const val SURFACE2 = 0xFF1A202B.toInt()
    const val STROKE = 0xFF232A38.toInt()
    const val RING_IDLE = 0xFF2A3242.toInt()
    const val TEXT = 0xFFE6EAF2.toInt()
    const val TEXT2 = 0xFF8A93A6.toInt()
    const val TEXT3 = 0xFF5B6477.toInt()
    const val ACCENT = 0xFF4DA3FF.toInt()
    const val GOOD = 0xFF3DDC97.toInt()
    const val WARN = 0xFFFFB547.toInt()
    const val BAD = 0xFFFF5C6C.toInt()

    fun level(l: Int) = when { l >= 3 -> GOOD; l == 2 -> WARN; else -> BAD }
    fun alpha(c: Int, a: Int) = (c and 0x00FFFFFF) or (a shl 24)
}

fun Context.dp(v: Number) = v.toFloat() * resources.displayMetrics.density
fun Context.px(v: Number) = dp(v).toInt()

private val MEDIUM: Typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)

fun Context.label(size: Float, color: Int = Palette.TEXT, medium: Boolean = false, text: String = "") = TextView(this).apply {
    textSize = size
    setTextColor(color)
    this.text = text
    fontFeatureSettings = "tnum" // steady digits while values tick
    if (medium) typeface = MEDIUM
}

fun Context.rounded(color: Int, radiusDp: Int, stroke: Int? = null) = GradientDrawable().apply {
    setColor(color)
    cornerRadius = dp(radiusDp)
    if (stroke != null) setStroke(px(1), stroke)
}

/** Ripple on any tappable view. */
fun View.ripple(oval: Boolean = false) {
    val mask = if (oval) ShapeDrawable(OvalShape()) else context.rounded(Palette.TEXT, 12)
    foreground = RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), null, mask)
    isClickable = true
    isFocusable = true
}

/** Big start/stop control. Idle: quiet outline. On: solid ring, one expanding pulse when it starts, then still. */
class PowerRing(ctx: Context) : View(ctx) {
    var on = false
        private set
    private var pulse = 1f
    private val stroke = ctx.dp(8)
    private val ring = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = stroke; strokeCap = Paint.Cap.ROUND }
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Palette.SURFACE }
    private val oval = RectF()

    init {
        ripple(oval = true)
        contentDescription = "Start sharing"
    }

    fun set(value: Boolean, animate: Boolean) {
        if (value == on) return
        on = value
        contentDescription = if (on) "Stop sharing" else "Start sharing"
        if (on && animate) ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 600
            interpolator = DecelerateInterpolator(2f)
            addUpdateListener { pulse = it.animatedValue as Float; invalidate() }
        }.start() else invalidate()
    }

    override fun onDraw(c: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val r = min(width, height) / 2f - stroke * 2.5f
        val color = if (on) Palette.GOOD else Palette.TEXT2

        if (on && pulse < 1f) {
            ring.color = Palette.alpha(Palette.GOOD, ((1 - pulse) * 140).toInt())
            c.drawCircle(cx, cy, r + stroke * 2f * pulse, ring)
        }
        ring.color = if (on) Palette.GOOD else Palette.RING_IDLE
        c.drawCircle(cx, cy, r, ring)
        c.drawCircle(cx, cy, r - stroke / 2, fill)

        ring.color = color
        val g = r * 0.32f
        oval.set(cx - g, cy - g, cx + g, cy + g)
        c.drawArc(oval, -60f, 300f, false, ring)
        c.drawLine(cx, cy - g * 1.3f, cx, cy - g * 0.2f, ring)
    }
}

/** Four drawn signal bars (no glyphs), lit in the status colour for the level. */
class SignalMeter(ctx: Context) : View(ctx) {
    var level = 0
        set(v) { field = v.coerceIn(0, 4); invalidate() }
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val bar = RectF()
    private val w = ctx.dp(10)
    private val gap = ctx.dp(4)
    private val radius = ctx.dp(2)

    override fun onMeasure(ws: Int, hs: Int) = setMeasuredDimension((w * 4 + gap * 3).toInt(), context.px(36))

    override fun onDraw(c: Canvas) {
        for (i in 0 until 4) {
            val h = height * (i + 1) / 4f
            bar.set(i * (w + gap), height - h, i * (w + gap) + w, height.toFloat())
            paint.color = if (i < level) Palette.level(level) else Palette.STROKE
            c.drawRoundRect(bar, radius, radius, paint)
        }
    }
}

/** Rolling throughput: download as an accent area, upload as a muted line. */
class Graph(ctx: Context) : View(ctx) {
    private val cap = 60
    private val down = ArrayDeque<Float>()
    private val up = ArrayDeque<Float>()
    private val line = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = ctx.dp(1.5); strokeJoin = Paint.Join.ROUND }
    private val area = Paint(Paint.ANTI_ALIAS_FLAG)

    fun push(d: Float, u: Float) {
        down.addLast(d); up.addLast(u)
        while (down.size > cap) { down.removeFirst(); up.removeFirst() }
        invalidate()
    }

    fun clear() { down.clear(); up.clear(); invalidate() }

    override fun onDraw(c: Canvas) {
        if (down.size < 2) return
        val peak = max(1f, max(down.max(), up.max()))
        val offset = cap - down.size // newest at the right edge
        fun x(i: Int) = width * (i + offset) / (cap - 1f)
        fun path(s: ArrayDeque<Float>) = Path().apply {
            s.forEachIndexed { i, v ->
                val y = height - (v / peak) * height * 0.9f
                if (i == 0) moveTo(x(i), y) else lineTo(x(i), y)
            }
        }
        val d = path(down)
        val filled = Path(d).apply { lineTo(x(down.size - 1), height.toFloat()); lineTo(x(0), height.toFloat()); close() }
        area.shader = LinearGradient(0f, 0f, 0f, height.toFloat(), Palette.alpha(Palette.ACCENT, 0x55), Palette.alpha(Palette.ACCENT, 0), Shader.TileMode.CLAMP)
        c.drawPath(filled, area)
        line.color = Palette.ACCENT; c.drawPath(d, line)
        line.color = Palette.TEXT2; c.drawPath(path(up), line)
    }
}

/** Setup checklist row: drawn tick (filled when done, outlined when not). */
class Tick(ctx: Context) : View(ctx) {
    var done = false
        set(v) { field = v; invalidate() }
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { strokeWidth = ctx.dp(2); strokeCap = Paint.Cap.ROUND; strokeJoin = Paint.Join.ROUND }
    private val check = Path()

    override fun onMeasure(ws: Int, hs: Int) = context.px(20).let { setMeasuredDimension(it, it) }

    override fun onDraw(c: Canvas) {
        val r = width / 2f
        if (done) {
            paint.style = Paint.Style.FILL; paint.color = Palette.GOOD
            c.drawCircle(r, r, r, paint)
            paint.style = Paint.Style.STROKE; paint.color = Palette.BG
            check.reset(); check.moveTo(r * 0.55f, r); check.lineTo(r * 0.88f, r * 1.32f); check.lineTo(r * 1.45f, r * 0.7f)
            c.drawPath(check, paint)
        } else {
            paint.style = Paint.Style.STROKE; paint.color = Palette.TEXT3
            c.drawCircle(r, r, r - paint.strokeWidth / 2, paint)
        }
    }
}

object Fmt {
    fun rate(bps: Double) = when {
        bps >= 1e6 -> "%.1f MB/s".format(bps / 1e6)
        bps >= 1e3 -> "%.0f KB/s".format(bps / 1e3)
        else -> "%.0f B/s".format(bps)
    }
    fun bytes(b: Long) = when {
        b >= 1e9 -> "%.2f GB".format(b / 1e9)
        b >= 1e6 -> "%.1f MB".format(b / 1e6)
        else -> "%.0f KB".format(b / 1e3)
    }
    fun clock(ms: Long) = (ms / 1000).let { "%02d:%02d:%02d".format(it / 3600, it / 60 % 60, it % 60) }
    fun dbm(d: Int) = "−${-d} dBm" // true minus sign
}
