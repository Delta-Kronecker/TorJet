<#
.SYNOPSIS
  Compile scripts\start-tor.cs into start-tor.exe using the .NET Framework csc.

.PARAMETER OutFile
  Output path for start-tor.exe (default: scripts\start-tor.exe)
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot "start-tor.exe")
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
