<#
# TorJet Core License v1.0 (see LICENSE). Using this Core in another program
# requires the mandatory attribution of https://github.com/Delta-Kronecker/TorJet.
.SYNOPSIS
  Compile scripts\start-tor.cs and scripts\tun-helper.cs using the .NET
  Framework csc. start-tor.cs -> TorJet.exe (console exe), tun-helper.cs -> gui/winexe.

.PARAMETER OutFile
  Output path for TorJet.exe (default: scripts\TorJet.exe).
  tun-helper.exe is written to the data\ folder next to it.
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot "TorJet.exe")
)

$candidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { Write-Host "[x] csc.exe ('.NET Framework 4.x') not found."; exit 1 }

$src = Join-Path $PSScriptRoot "start-tor.cs"
& $csc -nologo -optimize+ -target:exe -out:$OutFile $src
if ($LASTEXITCODE -ne 0) { Write-Host "[x] compile failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
Write-Host "[ok] built $OutFile"

$helperDir = Join-Path (Split-Path -Parent $OutFile) "data"
New-Item -ItemType Directory -Path $helperDir -Force | Out-Null
$helperOut = Join-Path $helperDir "tun-helper.exe"
$helperSrc = Join-Path $PSScriptRoot "tun-helper.cs"
& $csc -nologo -optimize+ -target:winexe -out:$helperOut $helperSrc
if ($LASTEXITCODE -ne 0) { Write-Host "[x] tun-helper compile failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
Write-Host "[ok] built $helperOut"
