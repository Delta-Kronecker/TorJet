<#
.SYNOPSIS
  Tor Iran launcher: starts tor, waits for bootstrap, and sets the Windows
  system proxy (HTTP 127.0.0.1:8118) on success.

.DESCRIPTION
  Layout (fully portable, all files together):
    folder\
      tor.exe + DLLs + webtunnel.exe + obfs4proxy.exe + snowflake-client.exe
      torrc                 <- complete config (direct by default, or with
                                bridges after running fetch-bridges.ps1)
      data\                 <- created next to tor.exe: tor.log, bridges.txt

  There is NO runtime config generation: torrc already sits next to tor.exe.
  This launcher just starts tor, waits for 100% bootstrap and sets the system
  proxy.

.PARAMETER BinDir      Folder containing tor.exe (default: the launcher's own folder)
.PARAMETER BootstrapOnly  Exit after reaching 100% bootstrap (no proxy change, no wait)
.PARAMETER NewCircuit   Send SIGNAL NEWNYM to a running tor and exit
.PARAMETER Stop         Stop the running tor managed by this launcher and reset proxy

.EXAMPLE
  .\launcher.ps1
  .\launcher.ps1 -NewCircuit
#>
param(
    [string]$BinDir,
    [switch]$BootstrapOnly,
    [switch]$NewCircuit,
    [switch]$Stop
)

$ErrorActionPreference = "Stop"

if (-not $BinDir) { $BinDir = $PSScriptRoot }
$DataDir   = Join-Path $BinDir "data"
$RunTorrc  = Join-Path $BinDir "torrc"
$TorLog    = Join-Path $DataDir "tor.log"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

$TorExe = Join-Path $BinDir "tor.exe"

# ---------- control port helper ----------
function Send-Control([string]$cmd) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient("127.0.0.1", 9051)
        $s = $c.GetStream()
        $w = New-Object System.IO.StreamWriter($s)
        $r = New-Object System.IO.StreamReader($s)
        $w.NewLine = "`r`n"; $w.AutoFlush = $true
        $w.WriteLine("AUTHENTICATE `"newway-j7DJPvxLaS1H`""); $null = $r.ReadLine()
        $w.WriteLine($cmd);            $null = $r.ReadLine()
        $w.WriteLine("QUIT")
        $c.Close()
        return $true
    } catch { return $false }
}

# ---------- windows system proxy ----------
function Set-SystemProxy([bool]$on) {
    $k = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"
    if ($on) {
        Set-ItemProperty -Path $k -Name ProxyEnable -Value 1
        Set-ItemProperty -Path $k -Name ProxyServer -Value "127.0.0.1:8118"
        Set-ItemProperty -Path $k -Name ProxyOverride -Value "<local>"
    } else {
        Set-ItemProperty -Path $k -Name ProxyEnable -Value 0
    }
    Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WinINet {
  [DllImport("wininet.dll", SetLastError=true)]
  public static extern bool InternetSetOption(IntPtr h, int o, IntPtr b, int l);
  public static void Refresh(){
    InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
    InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
  }
}
"@
    [WinINet]::Refresh()
}

# ---------- new circuit / stop ----------
if ($NewCircuit) {
    if (Send-Control "SIGNAL NEWNYM") { Write-Host "New identity requested (NEWNYM)."; exit 0 }
    Write-Host "No tor control port found on 127.0.0.1:9051. Is tor running?"; exit 1
}

$torProc = Get-Process -Name "tor" -ErrorAction SilentlyContinue |
           Where-Object { $_.Path -and $_.Path -eq $TorExe }
if ($Stop) {
    if ($torProc) {
        $torProc | Stop-Process -Force
        Start-Sleep -Seconds 1
        Set-SystemProxy $false
        Write-Host "Tor stopped; system proxy reset."
    } else { Write-Host "No managed tor running." }
    exit 0
}
if ($torProc) {
    Write-Host "A tor instance from this folder is already running (PID $($torProc.Id))."
    Write-Host "Use -Stop to stop it, or -NewCircuit to rotate circuits."
    exit 0
}

# ---------- verify binaries ----------
if (-not (Test-Path $TorExe)) {
    Write-Host "[x] tor.exe not found in $BinDir"
    Write-Host "    Download the tor-win64-portable artifact from GitHub Actions."
    exit 1
}

# ---------- prepared torrc ----------
if (-not (Test-Path $RunTorrc)) {
    Write-Host "[x] torrc not found next to tor.exe: $RunTorrc"
    exit 1
}

# ---------- start tor ----------
if (Test-Path $TorLog)  { Remove-Item $TorLog -Force }

Write-Host "[i] starting tor.exe ..."
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $TorExe
$psi.Arguments = "-f `"$RunTorrc`""
$psi.WorkingDirectory = $BinDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$null = $proc.Start()

# ---------- wait for bootstrap ----------
$deadline = (Get-Date).AddMinutes(10)
$lastPct  = -1
$bootstrapped = $false
try {
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            Write-Host ""
            $proc.WaitForExit()
            Write-Host "[x] tor exited with code $($proc.ExitCode)"
            if (Test-Path $TorLog) {
                $logLines = @(Get-Content $TorLog -ErrorAction SilentlyContinue)
                if ($logLines.Count -gt 0) {
                    $logLines | Select-Object -Last 20 | ForEach-Object { Write-Host "    $_" }
                } else {
                    Write-Host "    (tor.log is empty - startup failed before logging started)"
                    Write-Host "    Likely a port conflict (e.g. 5353 mDNS, 9050 other Tor) or a config error."
                    Write-Host "    Run directly to see the error:  $TorExe -f $RunTorrc"
                }
            }
            exit 1
        }
        if (Test-Path $TorLog) {
            $log = Get-Content $TorLog -ErrorAction SilentlyContinue
            $last = $log | Select-Object -Last 1
            if ($last -match "Bootstrapped 100%") { $bootstrapped = $true; break }
            if ($last -match "Bootstrapped (\d+)%") {
                $pct = [int]$matches[1]
                if ($pct -ne $lastPct) { Write-Host "    Bootstrapped $pct% ..."; $lastPct = $pct }
            }
        }
        Start-Sleep -Seconds 3
    }
} finally {
    if (-not $bootstrapped) {
        Write-Host ""
        Write-Host "[x] bootstrap did not reach 100% in time."
        Write-Host "    Common fixes:"
        Write-Host "      - your network may now block direct Tor: run scripts\fetch-bridges.ps1 then retry"
        Write-Host "      - check the tail of: $TorLog"
        if (Test-Path $TorLog) { Get-Content $TorLog -Tail 8 | ForEach-Object { Write-Host "    $_" } }
        $proc | Stop-Process -Force
        Set-SystemProxy $false
        exit 1
    }
}

# ---------- success ----------
if ($BootstrapOnly) {
    Write-Host ""
    Write-Host "Bootstrapped 100%. Test mode: stopping tor and resetting proxy."
    $proc | Stop-Process -Force
    Set-SystemProxy $false
    exit 0
}

Set-SystemProxy $true
Write-Host ""
Write-Host "============================================================"
Write-Host "  Tor is UP (100% bootstrap). System proxy set to 127.0.0.1:8118"
Write-Host "  SOCKS5: 127.0.0.1:9050 | DNS: 127.0.0.1:53530"
Write-Host "  Press Enter to stop Tor and restore the system proxy."
Write-Host "============================================================"
$Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") | Out-Null

$proc | Stop-Process -Force
Set-SystemProxy $false
Write-Host "Tor stopped; system proxy restored."
