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
 * via the control port and log tail. Mirrors StartTorAndWait in the C# core.
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
    }

    private val appContext = context.applicationContext
    private val dataDir = File(appContext.filesDir, "tor")
    private val torExe = File(dataDir, "tor")
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

        // The buffered bootstrap waiter drives a bounded wait via the control
        // port instead of blocking on stdout, so sequential auto trials work.
        val ready = if (mode == TorrcBuilder.MODE_AUTO) {
            autoRace(strategy)
        } else {
            val resolved = resolveMode(mode, strategy)
            bootTor(resolved.first, resolved.second, maxWaitMs = 120_000L)
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

    /** Sequential auto: try vanilla, obfs4, webtunnel; keep the first to reach 100%. */
    private fun bootTor(mode: Int, strategy: Int, maxWaitMs: Long): Boolean {
        val torrc = buildTorrc(mode, strategy)
        if (torrc == null) {
            _ui.value = _ui.value.copy(
                state = State.ERROR,
                error = "mode ${TorrcBuilder.MODE_NAMES.getOrElse(mode) { "?" }} skipped: no bridges available"
            )
            return false
        }
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

        // pump stdout on a background thread (keeps stderr/stdout drained and
        // parses bootstrap lines into the UI; keeps a recent tail for errors)
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

        // bounded wait, polling the control port
        val deadline = System.currentTimeMillis() + maxWaitMs
        while (System.currentTimeMillis() < deadline && !stopRequested.get()) {
            if (!proc.isAlive) {
                // tor died on its own before bootstrap -> surface the reason
                val msg = processDiedReason(proc)
                _ui.value = _ui.value.copy(state = State.ERROR, error = msg)
                killProcess()
                return false
            }
            val pct = controlBootstrapPercent()
            if (pct >= 100) {
                _ui.value = _ui.value.copy(state = State.CONNECTED, bootPct = 100)
                return true
            }
            if (pct > _ui.value.bootPct) {
                _ui.value = _ui.value.copy(bootPct = pct)
            }
            Thread.sleep(1000)
        }
        // tor did not reach 100% in time; tear it down for the caller to fallback
        killProcess()
        return false
    }

    private fun processDiedReason(proc: Process): String {
        val code = try {
            proc.exitValue().toString()
        } catch (e: Exception) {
            "?"
        }
        val out = synchronized(pumpBuffer) {
            if (pumpBuffer.isEmpty()) "(no output)" else pumpBuffer.joinToString(" | ")
        }
        val log = torLogTail()
        val msg = if (log == "(no log)") out else "$out | $log"
        return "tor exited ($code): $msg"
    }

    private fun torLogTail(maxLines: Int = 12): String {
        return try {
            val lines = torLog.readLines()
            val tail = if (lines.size > maxLines) lines.takeLast(maxLines) else lines
            tail.joinToString(" | ")
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

    private fun autoRace(strategy: Int): Boolean {
        // try modes in order; memory/healthy-bridge prioritization omitted for brevity
        for (m in intArrayOf(0, 1, 2)) {
            if (stopRequested.get()) return false
            _ui.value = _ui.value.copy(state = State.CONNECTING, bootPct = 0, bootTag = TorrcBuilder.MODE_NAMES[m])
            if (bootTor(m, strategy, maxWaitMs = 60_000L)) return true
        }
        return false
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

    private fun buildTorrc(mode: Int, strategy: Int): String? {
        var sb = TorrcBuilder.TEMPLATE
            .replace("{socksport}", socksPort.toString())
            .replace("{keepport}", keepPort.toString())
            .replace("{httpport}", httpPort.toString())
            .replace("{dnsport}", dnsPort.toString())
            .replace("{ctrlport}", ctrlPort.toString())
            .replace("{datadir}", "data")

        val bridgeFile = TorrcBuilder.BRIDGE_FILES.getOrNull(mode)
        if (!bridgeFile.isNullOrEmpty()) {
            val bf = File(dataDir, "bridges/$bridgeFile")
            if (!bf.exists()) return null
            val lines = bf.readLines().map { it.trim() }.filter { it.isNotEmpty() }
            if (lines.isEmpty()) return null
            sb += "\n\n# --- bridges: ${TorrcBuilder.MODE_NAMES[mode]} ---\nUseBridges 1\n"
            sb += lines.filter { !it.startsWith("#") }.joinToString("\n") { "Bridge $it" }
            // transport plugin line only if the binary exists (optional binaries
            // like snowflake-client may be absent, tor must still start)
            val plugin = TorrcBuilder.PLUGIN_LINES.getOrNull(mode)
            if (!plugin.isNullOrEmpty() && File(dataDir, plugin.substringAfter("exec ").trim()).exists()) {
                sb += "\n$plugin"
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

    private fun controlBootstrapPercent(): Int {
        val lines = controlCommand("GETINFO status/bootstrap-phase") ?: return -1
        for (l in lines) {
            val m = Regex("PROGRESS=(\\d+)").find(l) ?: continue
            return m.groupValues[1].toIntOrNull() ?: -1
        }
        return -1
    }

    @Synchronized
    private fun controlCommand(cmd: String): List<String>? {
        val cookieHex = cookieHex()
        if (cookieHex == null) return null
        return try {
            Socket().use { s ->
                s.connect(InetSocketAddress("127.0.0.1", ctrlPort), 5000)
                s.soTimeout = 5000
                val rw = SocketIO(s)
                rw.writeLine("AUTHENTICATE $cookieHex")
                if (!rw.readLine().startsWith("250")) return null
                rw.writeLine(cmd)
                val out = mutableListOf<String>()
                // Multi-line replies (e.g. GETINFO) come as "250-status/..." lines and
                // terminate with "250 OK". Match on the terminator, not on any "250".
                var ln = rw.readLine()
                while (ln != null && !ln.startsWith("250 OK")) {
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
            val cookieHex = cookieHex() ?: return null
            Socket().use { s ->
                s.connect(InetSocketAddress("127.0.0.1", ctrlPort), 5000)
                s.soTimeout = 5000
                val rw = SocketIO(s)
                rw.writeLine("AUTHENTICATE $cookieHex")
                if (!rw.readLine().startsWith("250")) return null
                rw.writeLine(cmd)
                val out = mutableListOf<String>()
                var ln = rw.readLine()
                while (ln != null && !ln.startsWith("250 OK")) {
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

    private fun cookieHex(): String? {
        // ControlAuthenticationCookie 1 writes the cookie to DataDirectory
        // ("data" relative to the process cwd"), i.e. filesDir/tor/data/.
        val candidates = listOf(
            File(dataDir, "data/control_auth_cookie"),
            dataDir.run { File(this, "control_auth_cookie") }
        )
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

    /** Copies the bundled native tor + runtime bits from assets/jniLibs into filesDir and chmod +x. */
    private fun extractRuntimeFiles(): Boolean {
        if (torExe.exists() && torExe.length() > 100000) return true
        return try {
            // tor binary is bundled as a raw asset "native/tor" (see workflow)
            val assetName = "native/tor"
            appContext.assets.open(assetName).use { ins ->
                FileOutputStream(torExe).use { ins.copyTo(it) }
            }
            torExe.setExecutable(true, true)
            (geoip.exists() || copyAsset("native/geoip", geoip))
            (geoip6.exists() || copyAsset("native/geoip6", geoip6))
            copyAssetIfMissing("native/vanilla_tested.txt", File(dataDir, "bridges/vanilla_tested.txt"))
            copyAssetIfMissing("native/obfs4_tested.txt", File(dataDir, "bridges/obfs4_tested.txt"))
            copyAssetIfMissing("native/webtunnel_tested.txt", File(dataDir, "bridges/webtunnel_tested.txt"))
            copyAssetIfMissing("native/snowflake_tested.txt", File(dataDir, "bridges/snowflake_tested.txt"))
            // pluggable transports (chmod +x so tor can exec them)
            extractExec("native/obfs4proxy", File(dataDir, "obfs4proxy"))
            extractExec("native/webtunnel", File(dataDir, "webtunnel"))
            extractExec("native/snowflake-client", File(dataDir, "snowflake-client"))
            true
        } catch (e: Exception) {
            false
        }
    }

    private fun extractExec(name: String, dest: File) {
        try {
            if (dest.exists()) return
            appContext.assets.open(name).use { ins ->
                FileOutputStream(dest).use { ins.copyTo(it) }
            }
            dest.setExecutable(true, true)
        } catch (e: Exception) {
            // transport optional; a missing one only disables that bridge mode
        }
    }

    private fun copyAssetIfMissing(name: String, dest: File) {
        if (!dest.exists()) copyAssetSafely(name, dest)
    }

    private fun copyAsset(name: String, dest: File): Boolean = copyAssetSafely(name, dest)

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
