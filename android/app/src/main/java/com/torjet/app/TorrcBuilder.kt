package com.torjet.app

/**
 * Mirrors the Windows core's torrc generation (scripts/start-tor.cs:
 * BuildTorrc + StrategyTorrc + configs/torrc.jet). All paths are relative so
 * the data folder stays fully portable.
 */
object TorrcBuilder {

    val MODE_NAMES = arrayOf("vanilla", "obfs4", "webtunnel", "snowflake", "direct", "memory")
    val STRATEGY_NAMES = arrayOf("standard", "balanced", "aggressive", "ultimate", "lowlatency")
    const val MODE_AUTO = MODE_NAMES.size // pseudo-mode for the auto race
    const val DEFAULT_STRATEGY = 3        // "ultimate"

    val STRATEGY_DESC = arrayOf(
        "no tuning - stock tor config, most compatible",
        "reuse circuits 24 h, 24-min prebuild window - fewer handshakes",
        "more guards + faster scheduler + deeper reuse",
        "max concurrency + greedy (Vanilla) scheduler",
        "lowest ping: KISTLite pacing and RTT set filters"
    )

    val BRIDGE_FILES = arrayOf(
        "vanilla_tested.txt",
        "obfs4_tested.txt",
        "webtunnel_tested.txt",
        "snowflake_tested.txt",
        "",
        ""
    )

    /** Mirrors StrategyTorrc[][] from the Windows core. */
    val STRATEGY_TORRC: Array<Array<String>> = arrayOf(
        arrayOf(),                                                                          // standard
        arrayOf(                                                                             // balanced
            "MaxCircuitDirtiness 86400",
            "CircuitsAvailableTimeout 1440",
            "CircuitStreamTimeout 30"
        ),
        arrayOf(                                                                             // aggressive
            "MaxCircuitDirtiness 86400",
            "CircuitsAvailableTimeout 2880",
            "CircuitStreamTimeout 20",
            "CircuitBuildTimeout 20",
            "NumPrimaryGuards 15",
            "Schedulers KISTLite,Vanilla",
            "KISTSchedRunInterval 5 msec",
            "MaxClientCircuitsPending 96"
        ),
        arrayOf(                                                                             // ultimate
            "MaxCircuitDirtiness 86400",
            "CircuitsAvailableTimeout 4320",
            "CircuitStreamTimeout 10",
            "CircuitBuildTimeout 20",
            "NumPrimaryGuards 20",
            "Schedulers Vanilla",
            "MaxClientCircuitsPending 128",
            "CircuitPriorityHalflife 5",
            "SocksTimeout 120"
        ),
        arrayOf(                                                                             // lowlatency
            "MaxCircuitDirtiness 86400",
            "CircuitsAvailableTimeout 1440",
            "CircuitStreamTimeout 15",
            "CircuitBuildTimeout 20",
            "NumEntryGuards 10",
            "NumPrimaryGuards 10",
            "Schedulers KISTLite",
            "KISTSchedRunInterval 5 msec",
            "CircuitPriorityHalflife 3",
            "SocksTimeout 60"
        )
    )

    val SET_SELECTION_NAMES = arrayOf("first", "round-robin", "least-streams", "fastest")

    /** Mirrors configs/torrc.jet. Placeholders: {socksport}, {keepport}, {httpport}, {dnsport}, {ctrlport}, {datadir}. */
    val TEMPLATE: String = """
# TorJet - Android portable Tor client config (tor 0.4.9.11)
SocksPort 127.0.0.1:{socksport} IsolateSOCKSAuth
SocksPort 127.0.0.1:{keepport} NoIsolateSOCKSAuth
HTTPTunnelPort 127.0.0.1:{httpport}
DNSPort 127.0.0.1:{dnsport}
ControlPort 127.0.0.1:{ctrlport}
CookieAuthentication 1
SocksPolicy accept 127.0.0.1
SocksPolicy reject *

GeoIPFile geoip
GeoIPv6File geoip6

CircuitPadding 0
ConnectionPadding 0
UseMicrodescriptors 1

DormantOnFirstStartup 0
DormantCanceledByStartup 1
LearnCircuitBuildTimeout 0
CircuitBuildTimeout 30
MaxCircuitDirtiness 3600
NumEntryGuards 15
NumDirectoryGuards 6
MaxClientCircuitsPending 64
SocksTimeout 60
KeepalivePeriod 3600

ClientBootstrapConsensusAuthorityDownloadInitialDelay 0
ClientBootstrapConsensusFallbackDownloadInitialDelay 0
ClientBootstrapConsensusAuthorityOnlyDownloadInitialDelay 0
ClientBootstrapConsensusMaxInProgressTries 6
FetchDirInfoEarly 1
FetchDirInfoExtraEarly 1
PathsNeededToBuildCircuits 0.25

ClientTransportPlugin webtunnel exec webtunnel
ClientTransportPlugin obfs4 exec obfs4proxy
ClientTransportPlugin snowflake exec snowflake-client

DataDirectory {datadir}
Log notice file tor.log
DisableDebuggerAttachment 1
AvoidDiskWrites 1
SafeLogging 1

ConfluxEnabled 1
ConfluxClientUX throughput
    """.trimIndent()
}
