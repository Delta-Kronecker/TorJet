package com.torjet.app

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View
import kotlin.math.min

/**
 * The large clickable power ring from the Windows UI. Colors follow the exact
 * Windows theme: green when connected, amber while connecting/stopping, red
 * while restarting, muted grey idle.
 */
class PowerRingView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
    defStyleAttr: Int = 0
) : View(context, attrs, defStyleAttr) {

    enum class RingState { IDLE, CONNECTING, CONNECTED, RESTARTING, STOPPING }

    var ringState: RingState = RingState.IDLE
        set(value) {
            field = value
            invalidate()
        }

    var progress: Int = 0
        set(value) {
            field = value.coerceIn(0, 100)
            invalidate()
        }

    private val ringPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
    }
    private val arcPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 5f
        strokeCap = Paint.Cap.ROUND
    }
    private val powerPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = 5f
        strokeCap = Paint.Cap.ROUND
    }

    private fun colorOf(id: Int) = context.getColor(id)

    private val stateColor: Int
        get() = when (ringState) {
            RingState.CONNECTED -> colorOf(R.color.green)
            RingState.CONNECTING, RingState.STOPPING -> colorOf(R.color.amber)
            RingState.RESTARTING -> colorOf(R.color.red)
            RingState.IDLE -> colorOf(R.color.border_light)
        }

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        val cx = width / 2f
        val cy = height / 2f
        val r = min(width, height) / 2f - 4f
        val ring = RectF(cx - r, cy - r, cx + r, cy + r)

        // Windows paints a ring outline + a filled disc behind the power glyph.
        if (ringState == RingState.CONNECTING && progress > 0) {
            // background ring
            arcPaint.color = colorOf(R.color.border)
            canvas.drawOval(ring, arcPaint)
            // progress arc sweep
            val sweep = 360f * min(100, progress) / 100f
            if (sweep >= 1f) {
                arcPaint.color = colorOf(R.color.accent)
                canvas.drawArc(ring, -90f, sweep, false, arcPaint)
            }
        } else {
            ringPaint.style = Paint.Style.STROKE
            ringPaint.strokeWidth = 5f
            ringPaint.color = stateColor
            canvas.drawOval(ring, ringPaint)
            ringPaint.style = Paint.Style.FILL
        }

        // power glyph (same geometry as Windows: ring-diameter - 44 arc rect + top line)
        val glyph = if (ringState == RingState.IDLE) colorOf(R.color.text) else stateColor
        val gd = 2f * r - 44f
        if (gd > 0) {
            val arc = RectF(cx - gd / 2f, cy - gd / 2f, cx + gd / 2f, cy + gd / 2f)
            powerPaint.color = glyph
            canvas.drawArc(arc, -60f, 300f, false, powerPaint)
            canvas.drawLine(cx, cy - r + 16f, cx, cy - r + 46f, powerPaint)
        }
    }
}
