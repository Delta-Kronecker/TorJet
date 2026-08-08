# Tor Portable for Iran

Single-folder, portable Tor client for Iranian networks. Built in CI from the
official tor 0.4.9.11 sources plus pluggable transports (obfs4, snowflake,
webtunnel). No installation and no `%APPDATA%` — every file and all runtime
state stays inside one folder, so you can copy or move it anywhere.

## Layout

```
start-tor.exe          double-click to start Tor and set the system proxy
data\                  everything else lives here
  tor.exe              + DLLs, geoip, geoip6, transports (obfs4/snowflake/webtunnel)
  torrc                portable config (relative paths; sits next to tor.exe)
  scripts\             launcher.ps1 (new identity / stop) + fetch-bridges.ps1
  data\                runtime state (cached consensus, keys, tor.log)
```

## Usage

1. Download the `tor-win64-portable` artifact from GitHub Actions and unzip.
2. Double-click `start-tor.exe`.
3. Tor bootstraps (direct by default). On 100% the system proxy is enabled
   (HTTP 127.0.0.1:8118, SOCKS5 127.0.0.1:9050, DNS 127.0.0.1:53530).
   Press Enter to stop Tor and restore the proxy.

## If Tor is blocked

Run `data\scripts\fetch-bridges.ps1` to fetch fresh bridges (it rewrites
`data\torrc`), then start again.

## Advanced

```
data\scripts\launcher.ps1 -NewCircuit   request a new identity
data\scripts\launcher.ps1 -Stop         stop Tor and restore the proxy
```

## Build

`.github/workflows/build.yml` builds tor + transports on every push and
publishes the `tor-win64-portable` artifact.

- `tor-src\` — unmodified official tor 0.4.9.11 source
- `configs\torrc.iran` — the portable config template (becomes `data\torrc`)
- `scripts\start-tor.cs` — source of `start-tor.exe` (compiled with `build-start-tor.ps1`)
