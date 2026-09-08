package com.torjet.app

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View
import kotlin.math.max

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
        strokeWidth = 6f
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
        val r = max(width, height) / 2f - 4f

        // background disc
        ringPaint.color = colorOf(R.color.surface)
        canvas.drawCircle(cx, cy, r, ringPaint)

        // progress arc drawn as a ring when connecting
        val arc = RectF(cx - r, cy - r, cx + r, cy + r)
        if (ringState == RingState.CONNECTING) {
            arcPaint.color = stateColor
            canvas.drawArc(arc, -90f, progress / 100f * 360f, false, arcPaint)
        } else {
            ringPaint.color = stateColor
            ringPaint.strokeWidth = 3f
            ringPaint.style = Paint.Style.STROKE
        }
        // outer ring
        ringPaint.style = Paint.Style.STROKE
        ringPaint.color = stateColor
        ringPaint.strokeWidth = 3f
        canvas.drawCircle(cx, cy, r, ringPaint)

        // power symbol
        powerPaint.color = if (ringState == RingState.IDLE) colorOf(R.color.muted) else stateColor
        val pr = r * 0.5f
        canvas.drawArc(RectF(cx - pr, cy - pr * 0.6f, cx + pr, cy + pr * 1.0f), 250f, 220f, false, powerPaint)
        canvas.drawLine(cx, cy - pr, cx, cy - pr * 0.2f, powerPaint)
    }
}
