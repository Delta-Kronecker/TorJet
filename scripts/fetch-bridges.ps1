<#
.SYNOPSIS
  Fetch fresh tested Tor bridges and write them into the portable torrc NEXT TO
  tor.exe (adding UseBridges 1 + Bridge lines), plus data\bridges.txt.

.DESCRIPTION
  Run this only when direct Tor connections are blocked. It downloads tested
  bridges from a GitHub bridge-list repo, filters out non-routable /
  documentation addresses (2001:db8, RFC1918, TEST-NET, ...), and updates the
  torrc next to tor.exe. Re-running restores a clean bridge set.

  Bridge sources (default repo Delta-Kronecker/Tor-Bridges-Collector):
    bridge\obfs4_tested.txt      -> obfs4  bridges
    bridge\webtunnel_tested.txt  -> webtunnel bridges
    bridge\vanilla_tested.txt    -> plain (vanilla) bridges

.EXAMPLE
  .\fetch-bridges.ps1 -Transports obfs4 -PerTransport 20
  .\fetch-bridges.ps1
#>
param(
    [string]$Transports = "obfs4,webtunnel,vanilla",
    [int]$PerTransport  = 8,
    [string]$BaseUrl    = "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge",
    [string]$BinDir     = ""
)

$ErrorActionPreference = "Stop"

if (-not $BinDir) { $BinDir = Split-Path $PSScriptRoot -Parent }
if (-not (Test-Path (Join-Path $BinDir "tor.exe"))) {
    Write-Host "[x] tor.exe not found in $BinDir"
    Write-Host "    Point -BinDir at the folder that contains tor.exe."
    exit 1
}
$DataDir   = Join-Path $BinDir "data"
$TorrcPath = Join-Path $BinDir "torrc"
$Template  = Join-Path $BinDir "torrc"

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

function Test-RoutableIpv4([string]$ip) {
    $o = $ip -split "\."
    if ($o.Count -ne 4) { return $false }
    foreach ($x in $o) { if ($x -notmatch "^\d{1,3}$") { return $false } }
    $a = [int]$o[0]; $b = [int]$o[1]
    if ($a -le 0)        { return $false }            # 0.x
    if ($a -eq 10)       { return $false }            # RFC1918
    if ($a -eq 127)      { return $false }            # loopback
    if ($a -eq 169 -and $b -eq 254) { return $false } # link-local
    if ($a -eq 172 -and $b -ge 16 -and $b -le 31) { return $false }  # RFC1918
    if ($a -eq 192 -and $b -eq 168)  { return $false } # RFC1918
    if ($a -eq 192 -and $b -eq 0)    { return $false } # 192.0.2 TEST-NET-1
    if ($a -eq 198)                  { return $false } # 198.18/15, 198.51.100
    if ($a -eq 203 -and $b -eq 0)    { return $false } # 203.0.113 TEST-NET-3
    if ($a -ge 224)      { return $false }            # multicast/reserved
    return $true
}

function Test-RoutableIpv6([string]$ip) {
    $low = $ip.ToLower().TrimStart("[").TrimEnd("]")
    if ($low -eq "::1")                { return $false }
    if ($low.StartsWith("2001:db8"))   { return $false }  # documentation (RFC 3849)
    if ($low.StartsWith("fe80"))       { return $false }  # link-local
    if ($low -match "^fc[0-9a-f]|^fd[0-9a-f]") { return $false }  # ULA
    if ($low.StartsWith("ff"))         { return $false }  # multicast
    if ($low -eq "::" -or $low -match "^::ffff:") { return $false }
    return $true
}

function Test-BridgeLine([string]$line) {
    if ($line -notmatch "\s[0-9A-Fa-f]{40}(\s|$)") { return $false }
    if ($line -match "obfs4\s+([0-9.]+):\d+")  { return (Test-RoutableIpv4 $matches[1]) }
    if ($line -match "obfs4\s+\[([0-9a-fA-F:]+)\]:\d+") { return (Test-RoutableIpv6 $matches[1]) }
    if ($line -match "webtunnel\s+(\d+\.\d+\.\d+\.\d+):\d+") { return (Test-RoutableIpv4 $matches[1]) }
    if ($line -match "webtunnel\s+\[([0-9a-fA-F:]+)\]:\d+") { return (Test-RoutableIpv6 $matches[1]) }
    if ($line -match "^(\d+\.\d+\.\d+\.\d+):\d+\s+[0-9A-Fa-f]{40}") { return (Test-RoutableIpv4 $matches[1]) }
    return $false
}

$all = @()
$failed = @()
foreach ($t in ($Transports -split ",")) {
    $t = $t.Trim()
    $file = switch ($t) {
        "webtunnel" { "webtunnel_tested.txt" }
        "vanilla"   { "vanilla_tested.txt" }
        default     { "$($t)_tested.txt" }
    }
    $url = "$BaseUrl/$file"
    try {
        $content = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30).Content
        $raw = $content -split "`n"
        if ($t -eq "vanilla") {
            $pattern = "^(\d+\.\d+\.\d+\.\d+:\d+)\s+[0-9A-Fa-f]{40}(\s|$)"
        } else {
            $pattern = "^$t\s"
        }
        $valid = @($raw | ForEach-Object { $_.Trim() } |
                   Where-Object { $_ -match $pattern } |
                   Where-Object { Test-BridgeLine $_ })
        if ($valid.Count -gt 0) {
            $picked = @($valid | Sort-Object { Get-Random } | Select-Object -First $PerTransport)
            Write-Host "[ok] $t : $($valid.Count) valid, picked $($picked.Count)"
            $all += $picked | ForEach-Object { "Bridge $_" }
        } else {
            Write-Host "[!!] $t : 0 usable bridges (all filtered out or unparsable)"
            $failed += $t
        }
    } catch {
        Write-Host "[!!] $t : $($_.Exception.Message)"
        $failed += $t
    }
}

$all = @($all | Select-Object -Unique)

# --- update the portable torrc next to tor.exe ---
$base = Get-Content $Template -Raw
$base = @($base -split "`n" | Where-Object { $_ -notmatch "^\s*(UseBridges\b|Bridge\b|#.*[Bb]ridge)" }) -join "`n"
if ($all.Count -gt 0) {
    $section = "`n# --- bridges fetched on $(Get-Date -Format 'yyyy-MM-dd HH:mm') ---`n" +
               "UseBridges 1`n" + ($all -join "`n") + "`n"
} else {
    $section = ""
}
[System.IO.File]::WriteAllText($TorrcPath, $base.TrimEnd() + $section,
                               (New-Object System.Text.UTF8Encoding($false)))

$bridgeDoc = @(
    "# Bridge lines used in $TorrcPath (fetched $(Get-Date -Format 'yyyy-MM-dd HH:mm'))"
    "# To refresh: re-run fetch-bridges.ps1"
) + $all
[System.IO.File]::WriteAllText((Join-Path $DataDir "bridges.txt"), ($bridgeDoc -join "`r`n") + "`r`n",
                               (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
if ($all.Count -gt 0) {
    Write-Host "Updated $TorrcPath with $($all.Count) bridges."
} else {
    Write-Host "No bridges fetched - torrc left WITHOUT bridges (direct connection)."
    Write-Host "Manual fallback:"
    Write-Host "  1. Open  https://bridges.torproject.org/options  in Tor Browser"
    Write-Host "  2. Choose 'obfs4' or 'WebTunnel' and copy the bridge lines"
    Write-Host "  3. Paste them into:  $TorrcPath  (add 'Bridge ' before each)"
}
if ($failed.Count) { Write-Host "No bridges for: $($failed -join ', ')" }
Write-Host "Run tor.exe (or start.bat / launcher.ps1) to connect."
