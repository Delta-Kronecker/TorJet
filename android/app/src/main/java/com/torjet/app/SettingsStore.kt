package com.torjet.app

import android.content.Context

/**
 * Persists the same settings the Windows core keeps in data\*.txt, stored
 * in Android SharedPreferences. Values and defaults match the C# core exactly.
 */
class SettingsStore(context: Context) {

    private val prefs = context.getSharedPreferences("torjet", Context.MODE_PRIVATE)

    var mode: Int
        get() = prefs.getInt("mode", TorrcBuilder.MODE_AUTO)
        set(v) = prefs.edit().putInt("mode", v).apply()

    var strategy: Int
        get() = prefs.getInt("strategy", TorrcBuilder.DEFAULT_STRATEGY)
        set(v) = prefs.edit().putInt("strategy", v).apply()

    var confluxSets: Int
        get() = prefs.getInt("conflux_sets", 32)
        set(v) = prefs.edit().putInt("conflux_sets", v).apply()

    var confluxLegs: Int
        get() = prefs.getInt("conflux_legs", 1)
        set(v) = prefs.edit().putInt("conflux_legs", v).apply()

    var confluxLinkedSets: Int
        get() = prefs.getInt("conflux_linked_sets", 32)
        set(v) = prefs.edit().putInt("conflux_linked_sets", v).apply()

    var confluxSelection: Int
        get() = prefs.getInt("conflux_selection", 1)
        set(v) = prefs.edit().putInt("conflux_selection", v).apply()

    var confluxRttMax: Int
        get() = prefs.getInt("conflux_rtt_max", 400)
        set(v) = prefs.edit().putInt("conflux_rtt_max", v).apply()

    var confluxRttPct: Int
        get() = prefs.getInt("conflux_rtt_pct", 20)
        set(v) = prefs.edit().putInt("conflux_rtt_pct", v).apply()

    var watchRttPct: Int
        get() = prefs.getInt("watch_rtt_pct", 0)
        set(v) = prefs.edit().putInt("watch_rtt_pct", v).apply()

    var keepAlive: Boolean
        get() = prefs.getBoolean("keep_alive", true)
        set(v) = prefs.edit().putBoolean("keep_alive", v).apply()

    var autoProxy: Boolean
        get() = prefs.getBoolean("auto_proxy", false)
        set(v) = prefs.edit().putBoolean("auto_proxy", v).apply()

    /** Remembered last-successful mode/strategy for the "memory" mode. */
    var lastSuccessMode: Int
        get() = prefs.getInt("last_success_mode", -1)
        set(v) = prefs.edit().putInt("last_success_mode", v).apply()

    var lastSuccessStrategy: Int
        get() = prefs.getInt("last_success_strategy", -1)
        set(v) = prefs.edit().putInt("last_success_strategy", v).apply()

    fun readConflux(name: String, def: Int): Int {
        return prefs.getInt(name, def)
    }

    fun writeConflux(name: String, v: Int) {
        prefs.edit().putInt(name, v).apply()
    }
}
