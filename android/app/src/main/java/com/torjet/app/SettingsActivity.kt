package com.torjet.app

import android.graphics.Color
import android.os.Bundle
import android.view.Gravity
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity

class SettingsActivity : AppCompatActivity() {

    private lateinit var settings: SettingsStore
    private lateinit var list: LinearLayout

    companion object {
        private val LABELS = arrayOf(
            "Mode", "Auto proxy",
            "Strategy level", "Conflux sets", "Conflux legs", "Linked-set cap",
            "Keep-alive", "Set select", "Skip slow sets (RTT)", "Best % of sets",
            "Weak legs (top %)"
        )
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        settings = SettingsStore(this)
        list = findViewById(R.id.settingsList)
        findViewById<TextView>(R.id.backBtn).setOnClickListener { finish() }
        rebuild()
    }

    private fun rebuild() {
        list.removeAllViews()
        for (i in LABELS.indices) {
            list.addView(buildRow(i))
        }
    }

    private fun buildRow(index: Int): LinearLayout {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundColor(getColor(R.color.surface_alt))
            layoutParams = LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT,
                dp(46)
            ).apply { bottomMargin = dp(8) }
            setPadding(dp(12), 0, dp(8), 0)
        }

        val label = TextView(this).apply {
            text = LABELS[index]
            textSize = 13f
            setTextColor(getColor(R.color.text))
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.MATCH_PARENT, 1f)
            gravity = Gravity.CENTER_VERTICAL
        }

        val prev = TextView(this).apply {
            setBackgroundColor(Color.TRANSPARENT)
            text = "‹"
            textSize = 22f
            gravity = Gravity.CENTER
            setTextColor(getColor(R.color.muted))
            contentDescription = "decrease"
            layoutParams = LinearLayout.LayoutParams(dp(34), dp(38))
        }
        val value = TextView(this).apply {
            textSize = 13f
            setTextColor(getColor(R.color.text))
            gravity = Gravity.CENTER
            setPadding(dp(6), 0, dp(6), 0)
            minWidth = dp(64)
        }
        val next = TextView(this).apply {
            setBackgroundColor(Color.TRANSPARENT)
            text = "›"
            textSize = 22f
            gravity = Gravity.CENTER
            setTextColor(getColor(R.color.muted))
            contentDescription = "increase"
            layoutParams = LinearLayout.LayoutParams(dp(34), dp(38))
        }

        value.text = valueText(index)
        prev.setOnClickListener { cycle(index, -1); value.text = valueText(index) }
        next.setOnClickListener { cycle(index, +1); value.text = valueText(index) }

        row.addView(label)
        row.addView(prev)
        row.addView(value)
        row.addView(next)
        return row
    }

    private fun valueText(index: Int): String = when (index) {
        0 -> if (settings.mode < 6) prettyMode(settings.mode) else "Auto race"
        1 -> if (settings.autoProxy) "on" else "off"
        2 -> TorrcBuilder.STRATEGY_NAMES[settings.strategy]
        3 -> if (settings.confluxSets == 0) "consensus" else settings.confluxSets.toString()
        4 -> if (settings.confluxLegs == 0) "consensus" else settings.confluxLegs.toString()
        5 -> if (settings.confluxLinkedSets == 0) "consensus" else settings.confluxLinkedSets.toString()
        6 -> if (settings.keepAlive) "on" else "off"
        7 -> TorrcBuilder.SET_SELECTION_NAMES[settings.confluxSelection]
        8 -> if (settings.confluxRttMax == 0) "off" else "${settings.confluxRttMax} ms"
        9 -> if (settings.confluxRttPct == 0) "off" else "${settings.confluxRttPct}%"
        10 -> if (settings.watchRttPct == 0) "off" else "${settings.watchRttPct}%"
        else -> ""
    }

    private fun prettyMode(m: Int): String = when (m) {
        0 -> "Vanilla"
        1 -> "Obfs4"
        2 -> "WebTunnel"
        3 -> "Snowflake"
        4 -> "Direct"
        5 -> "Memory"
        else -> ""
    }

    /** Mirrors the Windows steppers: ranges + wrap/cycling. */
    private fun cycle(index: Int, dir: Int) {
        when (index) {
            0 -> settings.mode = wrap(settings.mode + dir, 7) // 0..6 (6 = auto)
            1 -> settings.autoProxy = !settings.autoProxy
            2 -> settings.strategy = wrap(settings.strategy + dir, TorrcBuilder.STRATEGY_NAMES.size)
            3 -> settings.confluxSets = clampCycle(settings.confluxSets + dir, 0, 32)
            4 -> settings.confluxLegs = clampCycle(settings.confluxLegs + dir, 0, 16)
            5 -> settings.confluxLinkedSets = clampCycle(settings.confluxLinkedSets + dir, 0, 32)
            6 -> settings.keepAlive = !settings.keepAlive
            7 -> settings.confluxSelection = wrap(settings.confluxSelection + dir, 4)
            8 -> settings.confluxRttMax = stepRtt(settings.confluxRttMax, dir)
            9 -> settings.confluxRttPct = stepClamp(settings.confluxRttPct, dir * 5, 0, 100)
            10 -> settings.watchRttPct = stepClamp(settings.watchRttPct, dir * 5, 0, 100)
        }
    }

    private fun wrap(v: Int, mod: Int): Int = ((v % mod) + mod) % mod

    private fun clampCycle(v: Int, min: Int, max: Int): Int = v.coerceIn(min, max)

    private fun stepClamp(v: Int, delta: Int, min: Int, max: Int): Int = (v + delta).coerceIn(min, max)

    /** RTT steps: 0 -> 400 -> 800 -> 1200 ... -> 10000 -> 0. */
    private fun stepRtt(v: Int, dir: Int): Int {
        if (v == 0 && dir > 0) return 400
        if (v <= 0) return 0
        var nv = v + dir * 400
        if (nv <= 0) return 0
        if (nv > 10000) return 0
        return nv
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()
}
