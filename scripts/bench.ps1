<#
.SYNOPSIS
  TorBoost benchmark orchestrator (step 0 harness).

  Works against a scratch copy of the release folder so your Downloads copy is
  never modified. Each run boots tor, waits for 100% bootstrap, runs a battery
  of downloads (1 / 4 / 8 parallel streams) through the HTTP proxy and appends
  one CSV row per measurement.

.PARAMETER Release
  Pristine release folder (default: Downloads\torboost-win64-v1.1.7).

.PARAMETER WorkDir
  Scratch folder created from Release. Reused across runs (keeps cached
  consensus state so bootstrap is fast); delete it to force a clean state.

.PARAMETER Mode
  Connection mode: direct | webtunnel | obfs4 | vanilla.

.PARAMETER Iters
  Iterations per stream count (default 3).

.PARAMETER Streams
  Comma-separated stream counts (default "1,4,8").

.PARAMETER OutCSV
  CSV output path, absolute or relative to this script (default results\bench.csv).

.PARAMETER VariantName
  Label stored in the CSV "variant" column (default "baseline").

.PARAMETER ExtraTorrc
  Extra torrc lines (newline-separated) appended to torrc.template for this run.
  Leave empty for a pure baseline.

.PARAMETER ConfluxModes
  Comma-separated modes to run the quick conflux-engagement check for, then exit.
  (e.g. "direct,webtunnel,obfs4,vanilla"). Each boot waits 100% + 30s and prints
  conflux set/leg counts.
#>
param(
    [string]$Release = "C:\Users\agolb\Downloads\torboost-win64-v1.1.7",
    [string]$WorkDir = "$env:TEMP\torboost-bench",
    [string]$Mode = "direct",
    [int]$Iters = 3,
    [string]$Streams = "1,4,8",
    [string]$OutCSV = "results\bench.csv",
    [string]$VariantName = "baseline",
    [string]$ExtraTorrc = "",
    [string]$ConfluxModes = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$newExe = Join-Path $scriptDir "start-tor.exe"

if (-not (Test-Path $Release)) { Write-Host "[x] release folder not found: $Release" -ForegroundColor Red; exit 1 }
if (-not (Test-Path $newExe))  { Write-Host "[x] new start-tor.exe not built: $newExe" -ForegroundColor Red; exit 1 }
if (-not (Test-Path (Join-Path $Release "data\tor.exe"))) { Write-Host "[x] invalid release folder (no data\tor.exe)" -ForegroundColor Red; exit 1 }

# --- (re)build scratch work dir from the release, keeping cached consensus -----
if (-not (Test-Path (Join-Path $WorkDir "data\tor.exe"))) {
    Write-Host "[i] creating scratch bench folder: $WorkDir"
    New-Item -ItemType Directory -Path $WorkDir -Force | Out-Null
    Copy-Item (Join-Path $Release "*") -Destination $WorkDir -Recurse -Force
    Copy-Item (Join-Path $Release "data\*") -Destination (Join-Path $WorkDir "data") -Recurse -Force
}
# always refresh the freshly compiled launcher into the scratch folder
Copy-Item $newExe -Destination $WorkDir -Force

# pre-flight: kill any tor leaked from this workdir and drop a stale lock file
Get-Process tor -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -like "$WorkDir*" } catch { $true }
} | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Remove-Item (Join-Path $WorkDir "data\data\lock") -Force -ErrorAction SilentlyContinue

if ($OutCSV -notmatch '^\w:\\') { $OutCSV = Join-Path $repoRoot $OutCSV }
New-Item -ItemType Directory -Path (Split-Path $OutCSV) -Force | Out-Null

$torrcTemplate = Join-Path $WorkDir "data\torrc.template"
$torrcBackup  = "$torrcTemplate.bench-bak"

function Invoke-Bench {
    param([string]$Name, [string]$Extra)
    $env:BENCH_VARIANT = $Name
    if (-not (Test-Path $torrcBackup)) { Copy-Item $torrcTemplate $torrcBackup -Force }
    if ($Extra.Trim().Length -gt 0) {
        $base = Get-Content $torrcBackup -Raw
        $augmented = $base.TrimEnd() + "`r`n`r`n# --- bench variant: $Name ---`r`n" + ($Extra -replace "`n", "`r`n") + "`r`n"
        Set-Content -Path $torrcTemplate -Value $augmented -Encoding UTF8
    } else {
        Copy-Item $torrcBackup $torrcTemplate -Force
    }

    Write-Host "`n=== variant: $Name (mode=$Mode iters=$Iters streams=$Streams) ===" -ForegroundColor Cyan
    & (Join-Path $WorkDir "start-tor.exe") --bench $Mode --iters $Iters --streams $Streams --csv $OutCSV
    if ($LASTEXITCODE -ne 0) { Write-Host "[!] bench exited with code $LASTEXITCODE" -ForegroundColor Yellow }
}

if ($ConfluxModes.Trim().Length -gt 0) {
    $modes = $ConfluxModes -split "," | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
    foreach ($m in $modes) {
        Write-Host "`n=== conflux check: $m ===" -ForegroundColor Cyan
        & (Join-Path $WorkDir "start-tor.exe") --conflux-check $m
    }
    Remove-Item Env:BENCH_VARIANT -ErrorAction SilentlyContinue
    exit 0
}

Invoke-Bench -Name $VariantName -Extra $ExtraTorrc
Remove-Item Env:BENCH_VARIANT -ErrorAction SilentlyContinue

Write-Host "`n=== last rows of $OutCSV ===" -ForegroundColor Green
if (Test-Path $OutCSV) { Get-Content $OutCSV | Select-Object -Last ($Iters * (@($Streams -split ",").Count) + 1) }
