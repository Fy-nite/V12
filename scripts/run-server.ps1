# Alias for run-host.ps1. The V12 multiplayer env name is V12_MP_MODE=host.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'run-host.ps1') @args
