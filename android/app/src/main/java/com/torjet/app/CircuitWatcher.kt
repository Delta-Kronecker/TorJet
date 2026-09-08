package com.torjet.app

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Circuit health monitor. Mirrors the Windows core's CONFLUX-driven weak-leg
 * pruning: periodically asks tor for the conflux set summary and reports the
 * set/leg/linked counts. Full RTT-based pruning is intentionally simplified to
 * a status reporter on Android to keep the daemon layer lean.
 */
object CircuitWatcher {

    data class ConfluxStatus(
        val sets: Int = 0,
        val legs: Int = 0,
        val linked: Int = 0
    )

    suspend fun query(controller: TorController): ConfluxStatus = withContext(Dispatchers.IO) {
        val lines = controller.sendCommandBlocking("CONFLUX QUERY")
        var sets = 0
        var legs = 0
        var linked = 0
        lines?.forEach { l ->
            val sm = Regex("SET[=\\s]+(\\d+)", RegexOption.IGNORE_CASE).find(l)
            if (sm != null) sets = sm.groupValues[1].toIntOrNull() ?: 0
            val lm = Regex("LEGS[=\\s]+(\\d+)", RegexOption.IGNORE_CASE).find(l)
            if (lm != null) legs = lm.groupValues[1].toIntOrNull() ?: 0
            val km = Regex("LINKED[=\\s]+(\\d+)", RegexOption.IGNORE_CASE).find(l)
            if (km != null) linked = km.groupValues[1].toIntOrNull() ?: 0
        }
        ConfluxStatus(sets, legs, linked)
    }
}
