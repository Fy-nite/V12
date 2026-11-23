[CmdletBinding()]
param(
    [string]$Demo = 'MultiplayerDemo',
    [int]$Port = 7777,
    [ValidateSet('auto', 'on', 'off')][string]$Xr = 'auto',
    [switch]$UseGame,
    [string]$Exe,
    [string]$Pak,
    [string]$LogDir,
    [switch]$DebugInput,
    [switch]$Headless,
    [switch]$HotReload,
    [switch]$NoWait,
    [switch]$NoLog,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'v12-common.ps1')

$launch = Resolve-V12DemoLaunch -Demo $Demo -UseGame:$UseGame -Exe $Exe -Pak $Pak -Xr $Xr -Headless:$Headless -HotReload:$HotReload
$logPrefix = if ($NoLog) { $null } else { New-V12LogPrefix -LogDir $LogDir -Name "host-$Demo" }

Show-V12Launch -Launch $launch -Mode 'host' -Port $Port -LogPrefix $logPrefix
Show-V12FreshnessWarning -Launch $launch
if ($DryRun) { return }

$process = Start-V12Process `
    -Exe $launch.Exe `
    -Arguments $launch.Arguments `
    -WorkingDirectory $launch.WorkingDirectory `
    -Mode 'host' `
    -HostAddress '127.0.0.1' `
    -Port $Port `
    -DebugInput:$DebugInput `
    -LogPrefix $logPrefix

Write-Host ("  pid:     {0}" -f $process.Id)
Write-Host ''

if ($NoWait) {
    Write-Host 'Started in the background. Close the game window or stop the PID when done.'
    return
}

try {
    Wait-V12Process $process
}
finally {
    Stop-V12Process $process
}
if ($logPrefix) { Show-V12LogTail "$logPrefix.out" }
