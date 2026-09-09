package com.torjet.app

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Manages the embedded tor process on Android: copies the native binary + data
 * into the app's private dir, generates torrc, spawns tor, tracks bootstrap
 * via the control port and log tail.
 *
 * Mirrors the Windows core (scripts/start-tor.cs: StartTorAndWait + AutoRace):
 *  - There is NO absolute timeout. On the healthy-only set tor may be restarted
 *    once with EVERY bridge (fallback) if the reported bootstrap percentage
 *    stays frozen for StuckFallbackMinutes; once on the fallback set (or on a
 *    first run, which has no healthy/fallback split yet) we keep waiting
 *    indefinitely until 100%, tor exits, or the user stops.
 *  - auto mode races vanilla / obfs4 / webtunnel side by side; the first to
 *    reach 100% wins and its data directory is adopted as the primary one.
 */
class TorController(context: Context) {

    enum class State { IDLE, CONNECTING, CONNECTED, RESTARTING, STOPPING, ERROR }

    data class UiState(
        val state: State = State.IDLE,
        val bootPct: Int = 0,
        val bootTag: String = "",
        val socksPort: Int = DEFAULT_SOCKS,
        val httpPort: Int = DEFAULT_HTTP,
        val dnsPort: Int = DEFAULT_DNS,
        val error: String? = null
    )

    companion object {
        const val DEFAULT_SOCKS = 9050
        const val DEFAULT_KEEP = 9052
        const val DEFAULT_HTTP = 8118
        const val DEFAULT_DNS = 53530
        const val DEFAULT_CTRL = 9051
        private const val STUCK_FALLBACK_MINUTES = 2.0
        private const val STUCK_FALLBACK_MS = (STUCK_FALLBACK_MINUTES * 60_000).toLong()
        private const val POLL_MS = 1500L
        // Auto-race racer ports mirror start-tor.cs RacerSocksPorts/RacerHttpPorts/RacerDnsPorts.
        private val RACER_SOCKS = intArrayOf(9150, 9250, 9350)
        private val RACER_HTTP = intArrayOf(8150, 8250, 8350)
        private val RACER_DNS = intArrayOf(61530, 62530, 63530)
        private val RACER_DIRS = arrayOf("data-v", "data-o", "data-w")
        private val RACER_NAMES = arrayOf("vanilla", "obfs4", "webtunnel")
        private const val RACER_STALL_SECS = 120L
    }

    private val appContext = context.applicationContext
    // Writable app home (data files only - Android 10+ forbids execve() here).
    private val dataDir = File(appContext.filesDir, "tor")
    // Executables MUST live in nativeLibraryDir (installed from jniLibs/lib*.so):
    // app-private storage is W^X and execve() on it -> EACCES (error=13).
    private val binDir = appContext.applicationInfo.nativeLibraryDir
    private val torExe = File(binDir, "libtor.so")
    private val torrcFile = File(dataDir, "torrc")
    private val torLog = File(dataDir, "tor.log")
    private val geoip = File(dataDir, "geoip")
    private val geoip6 = File(dataDir, "geoip6")

    private var process: Process? = null
    private val running = AtomicBoolean(false)
    private val stopRequested = AtomicBoolean(false)
    private val pumpBuffer = java.util.ArrayDeque<String>()

    private val _ui = MutableStateFlow(UiState())
    val ui: StateFlow<UiState> = _ui.asStateFlow()

    val settings: SettingsStore = SettingsStore(appContext)

    private var socksPort = DEFAULT_SOCKS
    private var keepPort = DEFAULT_KEEP
    private var httpPort = DEFAULT_HTTP
    private var dnsPort = DEFAULT_DNS
    private var ctrlPort = DEFAULT_CTRL

    val isRunning: Boolean get() = running.get()

    fun prepareForBuild() {
        // called on first foreground run to extract runtime bits
    }

    suspend fun start(mode: Int, strategy: Int): Boolean = withContext(Dispatchers.IO) {
        stopRequested.set(false)
        cleanupInternal()
        ensureRuntimeFiles()
        if (!extractRuntimeFiles()) {
            _ui.value = _ui.value.copy(state = State.ERROR, error = "tor binary missing")
            return@withContext false
        }

        val ready = if (mode == TorrcBuilder.MODE_AUTO) {
            val winnerMode = autoRace(strategy)
            if (winnerMode >= 0) {
                // Winner's warm data dir is now the primary one. Restart on the
                // standard primary ports reusing the cached directory, so the
                // final bootstrap takes seconds (mirrors start-tor.cs).
                bootTor(winnerMode, strategy)
            } else {
                false
            }
        } else {
            val resolved = resolveMode(mode, strategy)
            bootTor(resolved.first, resolved.second)
        }

        if (!ready) {
            running.set(false)
            if (!stopRequested.get()) {
                _ui.value = _ui.value.copy(
                    state = State.ERROR,
                    error = _ui.value.error ?: "tor failed to connect"
                )
            }
            return@withContext false
        }
        running.set(true)
        true
    }

    /** Healthy/fallback two-phase single-mode boot. NO absolute timeout. */
    private fun bootTor(mode: Int, strategy: Int): Boolean {
        var fallbackUsed = !hasFallbackSection(mode)
        val firstTorrc = buildTorrc(mode, strategy, healthyOnly = !fallbackUsed)
        if (firstTorrc == null) {
            _ui.value = _ui.value.copy(
                state = State.ERROR,
                error = "mode ${TorrcBuilder.MODE_NAMES.getOrElse(mode) { "?" }} skipped: no bridges available"
            )
            return false
        }
        var torrc = firstTorrc

        while (!stopRequested.get()) {
            // Two-phase: first healthy-only; if it stalls, retry with EVERY bridge.
            buildTorrc(mode, strategy, healthyOnly = !fallbackUsed)?.let { torrc = it }
            torrcFile.writeText(torrc)
            if (torLog.exists()) torLog.delete()
            _ui.value = _ui.value.copy(state = State.CONNECTING, bootPct = 0, error = null)

            val pb = ProcessBuilder(torExe.absolutePath, "-f", "torrc")
            pb.directory(dataDir)
            pb.redirectErrorStream(true)
            val proc = try {
                pb.start()
            } catch (e: Exception) {
                val exists = torExe.exists()
                val exec = torExe.canExecute()
                _ui.value = _ui.value.copy(
                    state = State.ERROR,
                    error = "failed to start tor (exists=$exists exec=$exec): ${e.message}"
                )
                return false
            }
            process = proc

            // pump stdout on a background thread (drains output, feeds progress)
            synchronized(pumpBuffer) { pumpBuffer.clear() }
            val pump = Thread {
                try {
                    proc.inputStream.bufferedReader().forEachLine { line ->
                        synchronized(pumpBuffer) {
                            pumpBuffer.addLast(line)
                            while (pumpBuffer.size > 30) pumpBuffer.removeFirst()
                        }
                        parseProgressLine(line)
                    }
                } catch (_: Exception) {
                }
            }
            pump.isDaemon = true
            pump.start()

            var lastPct = -1
            var lastTag = ""
            var stuckSince = System.currentTimeMillis()
            var restartForFallback = false

            // Wait until 100%, tor exits, or the user stops. Only the frozen
            // percentage triggers the healthy->fallback restart (once).
            while (!stopRequested.get()) {
                if (!proc.isAlive) {
                    val msg = processDiedReason(proc)
                    _ui.value = _ui.value.copy(state = State.ERROR, error = msg)
                    killProcess()
                    return false
                }
                val pct = controlBootstrapPercent()
                if (pct >= 100) {
                    _ui.value = _ui.value.copy(state = State.CONNECTED, bootPct = 100, bootTag = lastTag)
                    return true
                }
                if (pct != lastPct) {
                    lastPct = pct
                    lastTag = lastTagFromPump() ?: lastTag
                    if (pct > 0) stuckSince = System.currentTimeMillis()
                }
                if (!fallbackUsed &&
                    System.currentTimeMillis() - stuckSince >= STUCK_FALLBACK_MS
                ) {
                    // frozen long enough on the healthy-only set -> widen to all bridges
                    restartForFallback = true
                    break
                }
                Thread.sleep(POLL_MS)
            }

            if (restartForFallback) {
                fallbackUsed = true
                killProcess()
                _ui.value = _ui.value.copy(
                    state = State.CONNECTING, bootPct = 0,
                    bootTag = "healthy bridges stalled - retrying with all bridges"
                )
                continue // outer loop rebuilds torrc with all bridges
            }
            if (stopRequested.get()) {
                killProcess()
                return false
            }
        }
        return false
    }

    private fun lastTagFromPump(): String? {
        synchronized(pumpBuffer) {
            for (line in pumpBuffer) {
                val m = Regex("Bootstrapped\\s+(\\d+)%\\s*\\(([^)]+)\\)").find(line) ?: continue
                return m.groupValues[2]
            }
            return null
        }
    }

    /** One racer leg of the auto race. */
    private data class Racer(
        val name: String,
        val mode: Int,
        val dir: String,
        val socks: Int,
        val keep: Int,
        val http: Int,
        val dns: Int,
        val ctrl: Int,
        var proc: Process? = null,
        var alive: Boolean = true,
        var lastPct: Int = -1,
        var fallbackUsed: Boolean = true,
        var startedAt: Long = 0,
        var lastProgressAt: Long = 0
    )

    /**
     * Auto mode: race vanilla / obfs4 / webtunnel side by side, each with its
     * own ports + data dir + control cookie (mirrors start-tor.cs AutoRace).
     * First to reach 100% wins; losers are killed; the winner's data directory
     * becomes the primary one and the caller reboots on standard ports with the
     * warm cache. There is NO global timeout.
     * @return the winning mode, or -1 on failure/abort.
     */
    private fun autoRace(strategy: Int): Int {
        val racers = ArrayList<Racer>()
        for (i in RACER_SOCKS.indices) {
            val r = Racer(
                name = RACER_NAMES[i],
                mode = i,
                dir = RACER_DIRS[i],
                socks = RACER_SOCKS[i],
                keep = RACER_SOCKS[i] + 2,
                http = RACER_HTTP[i],
                dns = RACER_DNS[i],
                ctrl = RACER_SOCKS[i] + 1,
                fallbackUsed = !hasFallbackSection(i)
            )
            r.startedAt = System.currentTimeMillis()
            racers.add(r)
        }

        _ui.value = _ui.value.copy(state = State.CONNECTING, bootPct = 0, error = null)

        // spawn every racer
        for (r in racers) {
            val torrc = buildTorrc(r.mode, strategy, healthyOnly = !r.fallbackUsed,
                socks = r.socks, keep = r.keep, http = r.http, dns = r.dns, ctrl = r.ctrl,
                datadir = r.dir, torlog = "${r.dir}/tor.log")
            if (torrc == null) {
                r.alive = false
                r.proc = null
                continue
            }
            val rcFile = File(dataDir, "torrc-${r.name}")
            rcFile.writeText(torrc)
            File(dataDir, r.dir).mkdirs()
            r.startedAt = System.currentTimeMillis()
            r.lastProgressAt = r.startedAt
            val pb = ProcessBuilder(torExe.absolutePath, "-f", "torrc-${r.name}")
            pb.directory(dataDir)
            pb.redirectErrorStream(true)
            val proc = try { pb.start() } catch (e: Exception) { null }
            r.proc = proc
            r.alive = proc != null
            if (proc == null) {
                _ui.value = _ui.value.copy(state = State.CONNECTING, bootPct = 0,
                    bootTag = "${r.name} failed to start")
            } else {
                // drain so a dead/lost racer's output never fills the pipe
                drainRacer(proc)
            }
        }

        val allDead = racers.all { !it.alive }
        if (allDead) {
            _ui.value = _ui.value.copy(state = State.ERROR, error = "all racers failed to start")
            return -1
        }

        // Single monitoring loop; first racer to 100% wins. No deadlines.
        while (!stopRequested.get()) {
            // Per-racer healthy-only stall: if the race began on the healthy-only set
            // and bootstrap % stays frozen, widen THIS racer alone to every bridge
            // (mirror AutoRestartRacer's 120 s stall). Non-fallback (first-run)
            // racers wait indefinitely like the C# core.
            for (r in racers) {
                if (!r.alive || r.fallbackUsed) continue
                if (System.currentTimeMillis() - r.lastProgressAt >= RACER_STALL_SECS * 1000) {
                    restartRacerWithAllBridges(r, strategy)
                    r.startedAt = System.currentTimeMillis()
                    r.lastProgressAt = r.startedAt
                }
            }

            var aliveCount = 0
            var bestPct = -1
            var bestDesc = ""
            for (r in racers) {
                if (!r.alive) continue
                if (r.proc == null || !r.proc!!.isAlive) {
                    r.alive = false
                    val tail = racerLogTail(r)
                    if (tail != "(no log)") {
                        _ui.value = _ui.value.copy(state = State.CONNECTING, bootPct = 0,
                            bootTag = "${r.name} died: ${tail.lines().firstOrNull()?.trim().orEmpty()}")
                    }
                    continue
                }
                aliveCount++
                val pct = controlBootstrapPercent(r.ctrl, cookieFor(r.dir))
                if (pct >= 100) {
                    return finalizeRace(racers, r, strategy)
                }
                if (pct > r.lastPct) {
                    r.lastPct = pct
                    r.lastProgressAt = System.currentTimeMillis()
                    if (pct > bestPct) {
                        bestPct = pct
                        bestDesc = "${r.name} ${pct}%"
                    }
                }
            }

            if (bestDesc.isNotEmpty()) {
                _ui.value = _ui.value.copy(state = State.CONNECTING, bootPct = bestPct, bootTag = bestDesc)
            }

            if (aliveCount == 0) {
                _ui.value = _ui.value.copy(state = State.ERROR, error = "all transports died")
                killRacers(racers)
                return -1
            }
            Thread.sleep(POLL_MS)
        }

        killRacers(racers)
        return -1
    }

    /**
     * Stops every racer (the winner too), adopts the winner's data directory
     * as the warm primary cache, records the mode, and returns it. The caller
     * then reboots on the standard primary ports so consumers (VPN, keep-alive)
     * keep their fixed ports. Mirrors start-tor.cs: the winner "restarts on the
     * primary ports reusing its cached directory, so the final bootstrap takes
     * seconds".
     */
    private fun finalizeRace(racers: List<Racer>, winner: Racer, strategy: Int): Int {
        killRacers(racers)
        // adopt the winner's data directory as the primary one (warm cache)
        try {
            val src = File(dataDir, winner.dir)
            val dst = File(dataDir, "data")
            if (dst.exists()) dst.deleteRecursively()
            src.copyRecursively(dst)
            File(dst, "lock").delete()
        } catch (_: Exception) {}
        settings.lastSuccessMode = winner.mode
        settings.lastSuccessStrategy = strategy
        return winner.mode
    }

    private fun restartRacerWithAllBridges(r: Racer, strategy: Int) {
        try { r.proc?.destroy(); r.proc?.waitFor(3, java.util.concurrent.TimeUnit.SECONDS) } catch (_: Exception) {}
        val torrc = buildTorrc(r.mode, strategy, healthyOnly = false,
            socks = r.socks, keep = r.keep, http = r.http, dns = r.dns, ctrl = r.ctrl,
            datadir = r.dir, torlog = "${r.dir}/tor.log")
        if (torrc == null) {
            r.alive = false
            return
        }
        File(dataDir, "torrc-${r.name}").writeText(torrc)
        r.fallbackUsed = true
        r.lastPct = -1
        r.lastProgressAt = System.currentTimeMillis()
        val pb = ProcessBuilder(torExe.absolutePath, "-f", "torrc-${r.name}")
        pb.directory(dataDir)
        pb.redirectErrorStream(true)
        val proc = try { pb.start() } catch (e: Exception) { null }
        r.proc = proc
        r.alive = proc != null
        if (proc != null) drainRacer(proc)
        _ui.value = _ui.value.copy(bootPct = 0, bootTag = "${r.name} fallback: all bridges")
    }

    private fun drainRacer(proc: Process) {
        Thread {
            try {
                proc.inputStream.bufferedReader().forEachLine { }
            } catch (_: Exception) {
            }
        }.apply { isDaemon = true }.start()
    }

    private fun racerLogTail(r: Racer, maxLines: Int = 15): String {
        return try {
            val f = File(dataDir, "${r.dir}/tor.log")
            val lines = f.readLines()
            val tail = if (lines.size > maxLines) lines.takeLast(maxLines) else lines
            tail.dropLastWhile { it.isBlank() }.joinToString("\n")
        } catch (e: Exception) {
            "(no log)"
        }
    }

    private fun killRacers(racers: List<Racer>) {
        for (r in racers) {
            try { r.proc?.destroy() } catch (_: Exception) {}
        }
        process = null
    }

    private fun processDiedReason(proc: Process): String {
        val code = try {
            proc.exitValue().toString()
        } catch (e: Exception) {
            "?"
        }
        val out = synchronized(pumpBuffer) {
            if (pumpBuffer.isEmpty()) "(no output)" else pumpBuffer.joinToString("\n")
        }
        val log = torLogTail()
        val msg = buildString {
            append("tor exited ($code)")
            if (log != "(no log)") {
                append("\n--- tor.log ---\n")
                append(log)
            }
            append("\n--- stdout/stderr ---\n")
            append(out)
        }
        return msg
    }

    private fun torLogTail(maxLines: Int = 12): String {
        return try {
            val lines = torLog.readLines()
            val tail = if (lines.size > maxLines) lines.takeLast(maxLines) else lines
            tail.dropLastWhile { it.isBlank() }.joinToString("\n")
        } catch (e: Exception) {
            "(no log)"
        }
    }

    private fun killProcess() {
        try {
            process?.destroy()
            process?.waitFor(3, java.util.concurrent.TimeUnit.SECONDS)
        } catch (_: Exception) {
        }
        process = null
    }

    private fun resolveMode(mode: Int, strategy: Int): Pair<Int, Int> {
        var m = mode
        var s = strategy
        if (m == 5) { // memory
            if (settings.lastSuccessMode >= 0) m = settings.lastSuccessMode
            if (settings.lastSuccessStrategy >= 0 && s < 0) s = settings.lastSuccessStrategy
        }
        settings.lastSuccessMode = m
        settings.lastSuccessStrategy = s
        return m to s
    }

    /**
     * True when the bridge file for a mode has already been prioritized into
     * "# === healthy === / # === remaining (fallback) ===" sections. On a
     * FIRST run there are none: every bridge is the whole list and the
     * two-phase stall timeout must not apply (mirrors HasFallbackSection).
     */
    private fun hasFallbackSection(mode: Int): Boolean {
        val bridgeFile = TorrcBuilder.BRIDGE_FILES.getOrNull(mode) ?: return false
        if (bridgeFile.isEmpty()) return false
        val bf = File(dataDir, "bridges/$bridgeFile")
        if (!bf.exists()) return false
        return try {
            bf.readLines().any { it.contains("remaining", ignoreCase = true) }
        } catch (e: Exception) {
            false
        }
    }

    private fun buildTorrc(
        mode: Int,
        strategy: Int,
        healthyOnly: Boolean = false,
        socks: Int = socksPort,
        keep: Int = keepPort,
        http: Int = httpPort,
        dns: Int = dnsPort,
        ctrl: Int = ctrlPort,
        datadir: String = "data",
        torlog: String = "tor.log"
    ): String? {
        var sb = TorrcBuilder.TEMPLATE
            .replace("{socksport}", socks.toString())
            .replace("{keepport}", keep.toString())
            .replace("{httpport}", http.toString())
            .replace("{dnsport}", dns.toString())
            .replace("{ctrlport}", ctrl.toString())
            .replace("{datadir}", datadir)
            .replace("{torlog}", torlog)

        val bridgeFile = TorrcBuilder.BRIDGE_FILES.getOrNull(mode)
        if (!bridgeFile.isNullOrEmpty()) {
            val bf = File(dataDir, "bridges/$bridgeFile")
            if (!bf.exists()) return null
            // healthyOnly: stop at the "# ... remaining (fallback) ..." header
            var lines = bf.readLines().map { it.trim() }.filter { it.isNotEmpty() }
            if (healthyOnly) {
                lines = lines.takeWhile { !it.contains("remaining", ignoreCase = true) }
            }
            lines = lines.filter { !it.startsWith("#") }
            if (lines.isEmpty()) return null
            sb += "\n\n# --- bridges: ${TorrcBuilder.MODE_NAMES[mode]} ---\nUseBridges 1\n"
            sb += lines.joinToString("\n") { "Bridge $it" }
            // transport plugin line only if the binary exists (optional binaries
            // like snowflake-client may be absent, tor must still start)
            val plugin = TorrcBuilder.PLUGIN_LINES.getOrNull(mode)
            if (!plugin.isNullOrEmpty()) {
                val resolved = plugin.replace("{bindir}", binDir)
                if (resolved.substringAfter("exec ").trim().let { File(it).exists() }) {
                    sb += "\n$resolved"
                }
            }
        }
        if (strategy >= 0 && strategy < TorrcBuilder.STRATEGY_TORRC.size &&
            TorrcBuilder.STRATEGY_TORRC[strategy].isNotEmpty()
        ) {
            sb += "\n\n# --- strategy: ${TorrcBuilder.STRATEGY_NAMES[strategy]} ---\n"
            sb += TorrcBuilder.STRATEGY_TORRC[strategy].joinToString("\n")
        }
        if (settings.confluxSets > 0) sb += "\n\n# --- conflux: ${settings.confluxSets} set(s) ---\nConfluxNumSets ${settings.confluxSets}\n"
        if (settings.confluxLinkedSets > 0) sb += "\n# --- conflux: linked-set cap ${settings.confluxLinkedSets} ---\nConfluxNumLinkedSets ${settings.confluxLinkedSets}\n"
        if (settings.confluxLegs > 0) sb += "\n# --- conflux: ${settings.confluxLegs} leg(s) per set ---\nConfluxNumLegs ${settings.confluxLegs}\n"
        if (settings.confluxSelection >= 0 && settings.confluxSelection < 4)
            sb += "\n# --- conflux: set selection ${TorrcBuilder.SET_SELECTION_NAMES[settings.confluxSelection]} ---\nConfluxSetSelection ${settings.confluxSelection}\n"
        if (settings.confluxRttMax > 0) sb += "\n# --- conflux: skip sets slower than ${settings.confluxRttMax} ms ---\nConfluxSetRttMax ${settings.confluxRttMax}\n"
        if (settings.confluxRttPct > 0) sb += "\n# --- conflux: keep best ${settings.confluxRttPct}% of sets by RTT ---\nConfluxSetRttPct ${settings.confluxRttPct}\n"
        return sb
    }

    private fun parseProgressLine(line: String) {
        val m = Regex("Bootstrapped\\s+(\\d+)%\\s*(?:\\(([^)]+)\\))?").find(line) ?: return
        val pct = m.groupValues[1].toIntOrNull() ?: 0
        val tag = m.groupValues[2]
        val cur = _ui.value
        if (pct > cur.bootPct) {
            _ui.value = cur.copy(bootPct = pct, bootTag = tag, state = if (pct >= 100) State.CONNECTED else State.CONNECTING)
        }
    }

    /** Poor-man's per-second bootstrap polling via the control port. */
    suspend fun pollBootstrapLoop() = withContext(Dispatchers.IO) {
        // Wait for the process to actually come up before polling (start() is
        // launched as a sibling, so it may not have spawned tor yet).
        while (!running.get() && !stopRequested.get()) {
            Thread.sleep(200)
        }
        while (running.get() && !stopRequested.get()) {
            val pct = controlBootstrapPercent()
            if (pct >= 0) {
                val cur = _ui.value
                if (pct > cur.bootPct) {
                    _ui.value = cur.copy(
                        bootPct = pct,
                        state = if (pct >= 100) State.CONNECTED else State.CONNECTING
                    )
                }
            }
            Thread.sleep(1000)
        }
    }

    private fun controlBootstrapPercent(port: Int = ctrlPort, cookie: File? = null): Int {
        val lines = controlCommand("GETINFO status/bootstrap-phase", port, cookie) ?: return -1
        for (l in lines) {
            val m = Regex("PROGRESS=(\\d+)").find(l) ?: continue
            return m.groupValues[1].toIntOrNull() ?: -1
        }
        return -1
    }

    @Synchronized
    private fun controlCommand(cmd: String, port: Int = ctrlPort, cookieFile: File? = null): List<String>? {
        val cookieHex = cookieHex(cookieFile)
        if (cookieHex == null) return null
        return try {
            Socket().use { s ->
                s.connect(InetSocketAddress("127.0.0.1", port), 5000)
                s.soTimeout = 5000
                val rw = SocketIO(s)
                rw.writeLine("AUTHENTICATE $cookieHex")
                if (!rw.readLine().startsWith("250")) return null
                rw.writeLine(cmd)
                val out = mutableListOf<String>()
                // Multi-line replies (e.g. GETINFO) come as "250-status/..." lines and
                // terminate with "250 OK". Bound the read so a peer that closes the
                // connection mid-reply can never hang the caller forever.
                var ln = rw.readLine()
                while (ln != null && !ln.startsWith("250 OK") && out.size < 200) {
                    out.add(ln)
                    ln = rw.readLine()
                }
                out
            }
        } catch (e: Exception) {
            null
        }
    }

    fun sendCommand(cmd: String): Boolean = controlCommand(cmd) != null

    /** Non-synchronized variant for background thread consumers. */
    fun sendCommandBlocking(cmd: String): List<String>? {
        return try {
            val cookieHex = cookieHex(null) ?: return null
            Socket().use { s ->
                s.connect(InetSocketAddress("127.0.0.1", ctrlPort), 5000)
                s.soTimeout = 5000
                val rw = SocketIO(s)
                rw.writeLine("AUTHENTICATE $cookieHex")
                if (!rw.readLine().startsWith("250")) return null
                rw.writeLine(cmd)
                val out = mutableListOf<String>()
                var ln = rw.readLine()
                while (ln != null && !ln.startsWith("250 OK") && out.size < 200) {
                    out.add(ln)
                    ln = rw.readLine()
                }
                out
            }
        } catch (e: Exception) {
            null
        }
    }

    fun newIdentity(): Boolean = sendCommand("SIGNAL NEWNYM")

    private fun cookieHex(cookieFile: File? = null): String? {
        val candidates = if (cookieFile != null) {
            listOf(cookieFile)
        } else {
            // ControlAuthenticationCookie 1 writes the cookie to DataDirectory
            // ("data" relative to the process cwd), i.e. filesDir/tor/data/.
            listOf(
                File(dataDir, "data/control_auth_cookie"),
                dataDir.run { File(this, "control_auth_cookie") }
            )
        }
        for (f in candidates) {
            if (!f.exists()) continue
            return try {
                f.readBytes().joinToString("") { "%02x".format(it) }
            } catch (e: Exception) {
                null
            }
        }
        return null
    }

    private fun cookieFor(racerDir: String): File = File(dataDir, "$racerDir/control_auth_cookie")

    private fun cleanupInternal() {
        try {
            process?.destroy()
            process = null
        } catch (_: Exception) {
        }
        if (File(dataDir, "data/lock").exists()) File(dataDir, "data/lock").delete()
    }

    suspend fun stop() = withContext(Dispatchers.IO) {
        stopRequested.set(true)
        running.set(false)
        try {
            process?.destroy()
        } catch (_: Exception) {
        }
        process = null
        _ui.value = _ui.value.copy(state = State.IDLE, bootPct = 0, error = null)
    }

    private fun ensureRuntimeFiles() {
        dataDir.mkdirs()
        File(dataDir, "data").mkdirs()
        File(dataDir, "bridges").mkdirs()
    }

    /**
     * Verifies the tor binary (pre-installed into nativeLibraryDir from
     * jniLibs/lib*.so) and copies data-only runtime files (geoip, bridges)
     * into the writable data dir. Executables are never written to filesDir
     * on Android 10+ (W^X forbids execve() there).
     */
    private fun extractRuntimeFiles(): Boolean {
        if (!torExe.exists()) return false
        return try {
            copyAssetIfMissing("native/geoip", geoip)
            copyAssetIfMissing("native/geoip6", geoip6)
            copyAssetIfMissing("native/vanilla_tested.txt", File(dataDir, "bridges/vanilla_tested.txt"))
            copyAssetIfMissing("native/obfs4_tested.txt", File(dataDir, "bridges/obfs4_tested.txt"))
            copyAssetIfMissing("native/webtunnel_tested.txt", File(dataDir, "bridges/webtunnel_tested.txt"))
            copyAssetIfMissing("native/snowflake_tested.txt", File(dataDir, "bridges/snowflake_tested.txt"))
            true
        } catch (e: Exception) {
            false
        }
    }

    private fun copyAssetIfMissing(name: String, dest: File) {
        if (!dest.exists()) copyAssetSafely(name, dest)
    }

    private fun copyAssetSafely(name: String, dest: File): Boolean {
        return try {
            appContext.assets.open(name).use { ins ->
                FileOutputStream(dest).use { ins.copyTo(it) }
            }
            true
        } catch (e: Exception) {
            false
        }
    }

    class SocketIO(private val socket: Socket) {
        private val r = socket.getInputStream().bufferedReader()
        private val w = socket.getOutputStream().bufferedWriter()
        fun writeLine(s: String) {
            w.write(s + "\r\n")
            w.flush()
        }
        fun readLine(): String = r.readLine() ?: ""
    }
}