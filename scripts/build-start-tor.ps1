<#
# TorJet Core License v1.0 (see LICENSE). Using this Core in another program
# requires the mandatory attribution of https://github.com/Delta-Kronecker/TorJet.
.SYNOPSIS
  Compile scripts\start-tor.cs and scripts\tun-helper.cs using the .NET
  Framework csc. start-tor.cs -> TorJet.exe (console exe), tun-helper.cs -> gui/winexe.

.PARAMETER OutFile
  Output path for TorJet.exe (default: scripts\TorJet.exe).
  tun-helper.exe is written to the data\ folder next to it.

.PARAMETER Version
  Version embedded into the launcher and shown in the menu
  (e.g. "1.1.16" from the release tag). Falls back to the TORJET_VERSION
  environment variable, then "dev".
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot "TorJet.exe"),
    [string]$Version = ""
)

if (-not $Version) { $Version = $env:TORJET_VERSION }
if (-not $Version) { $Version = "dev" }
$Version = $Version.TrimStart('v', 'V')
$Version = ($Version -replace '[^0-9A-Za-z._-]', '_')

$candidates = @(
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"),
    (Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe")
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { Write-Host "[x] csc.exe ('.NET Framework 4.x') not found."; exit 1 }

$src = Join-Path $PSScriptRoot "start-tor.cs"
$srcUi = Join-Path $PSScriptRoot "TorJetUi.cs"
$versionSrc = Join-Path $env:TEMP "torjet-version.g.cs"
$versionCode = @"
namespace StartTor
{
    internal static class TorJetVersion
    {
        public const string App = "$Version";
    }
}
"@
Set-Content -Path $versionSrc -Value $versionCode -Encoding UTF8
try {
    & $csc -nologo -optimize+ -target:exe `
        -r:System.Windows.Forms.dll -r:System.Drawing.dll `
        -out:$OutFile $src $srcUi $versionSrc
    if ($LASTEXITCODE -ne 0) { Write-Host "[x] compile failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
    Write-Host "[ok] built $OutFile (version $Version)"
} finally {
    Remove-Item $versionSrc -ErrorAction SilentlyContinue
}

$helperDir = Join-Path (Split-Path -Parent $OutFile) "data"
New-Item -ItemType Directory -Path $helperDir -Force | Out-Null
$helperOut = Join-Path $helperDir "tun-helper.exe"
$helperSrc = Join-Path $PSScriptRoot "tun-helper.cs"
& $csc -nologo -optimize+ -target:winexe -out:$helperOut $helperSrc
if ($LASTEXITCODE -ne 0) { Write-Host "[x] tun-helper compile failed ($LASTEXITCODE)"; exit $LASTEXITCODE }
Write-Host "[ok] built $helperOut"
