[CmdletBinding()]
param(
    [string]$Demo = 'MultiplayerDemo',
    [int]$Port = 7777,
    [Alias('Server')][string]$HostAddress = '127.0.0.1',
    [ValidateSet('auto', 'on', 'off')][string]$HostXr = 'off',
    [ValidateSet('auto', 'on', 'off')][string]$ClientXr = 'off',
    [switch]$UseGame,
    [string]$HostExe,
    [string]$ClientExe,
    [string]$Pak,
    [string]$LogDir,
    [switch]$DebugInput,
    [switch]$Headless,
    [switch]$HotReload,
    [switch]$NoLog,
    [int]$StartupDelaySeconds = 2,
    [switch]$KeepRunning,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'v12-common.ps1')

$hostLaunch = Resolve-V12DemoLaunch -Demo $Demo -UseGame:$UseGame -Exe $(if ($HostExe) { $HostExe } else { $null }) -Pak $Pak -Xr $HostXr -Headless:$Headless -HotReload:$HotReload
$clientLaunch = Resolve-V12DemoLaunch -Demo $Demo -UseGame:$UseGame -Exe $(if ($ClientExe) { $ClientExe } else { $null }) -Pak $Pak -Xr $ClientXr -Headless:$Headless -HotReload:$HotReload

if ($HotReload) {
    Write-Warning 'Hot reload replaces the world on save; an active host/client session will drop and reconnect.'
}

$hostLog = if ($NoLog) { $null } else { New-V12LogPrefix -LogDir $LogDir -Name "pair-host-$Demo" }
$clientLog = if ($NoLog) { $null } else { New-V12LogPrefix -LogDir $LogDir -Name "pair-client-$Demo" }

Show-V12Launch -Launch $hostLaunch -Mode 'host' -Port $Port -LogPrefix $hostLog
Show-V12FreshnessWarning -Launch $hostLaunch
Show-V12Launch -Launch $clientLaunch -Mode 'client' -Port $Port -LogPrefix $clientLog
if ($clientLaunch.Exe -ne $hostLaunch.Exe) {
    Show-V12FreshnessWarning -Launch $clientLaunch
}
Write-Host ("  server:  {0}:{1}" -f $HostAddress, $Port)
Write-Host ''

if ($DryRun) { return }

$hostProcess = $null
$clientProcess = $null
$stoppedInFinally = $false
$keepProcesses = [bool]$KeepRunning

try {
    $hostProcess = Start-V12Process `
        -Exe $hostLaunch.Exe `
        -Arguments $hostLaunch.Arguments `
        -WorkingDirectory $hostLaunch.WorkingDirectory `
        -Mode 'host' `
        -HostAddress '127.0.0.1' `
        -Port $Port `
        -DebugInput:$DebugInput `
        -LogPrefix $hostLog

    Write-Host ("  host pid:   {0}" -f $hostProcess.Id)
    Start-Sleep -Seconds $StartupDelaySeconds

    $clientProcess = Start-V12Process `
        -Exe $clientLaunch.Exe `
        -Arguments $clientLaunch.Arguments `
        -WorkingDirectory $clientLaunch.WorkingDirectory `
        -Mode 'client' `
        -HostAddress $HostAddress `
        -Port $Port `
        -DebugInput:$DebugInput `
        -LogPrefix $clientLog

    Write-Host ("  client pid: {0}" -f $clientProcess.Id)
    Write-Host ''

    if ($KeepRunning) {
        Write-Host 'Both processes are running in the background.'
        return
    }

    Write-Host 'Host and client are running. Press Ctrl+C to stop both.'
    while ($true) {
        $hostProcess.Refresh()
        $clientProcess.Refresh()
        if ($hostProcess.HasExited -or $clientProcess.HasExited) { break }
        Start-Sleep -Milliseconds 250
    }
}
catch {
    $keepProcesses = $false
    throw
}
finally {
    if (-not $keepProcesses) {
        Stop-V12Process $clientProcess
        Stop-V12Process $hostProcess
        $stoppedInFinally = $true
    }
}

if ($stoppedInFinally) {
    Start-Sleep -Milliseconds 300
    if ($hostLog) { Show-V12LogTail "$hostLog.out" }
    if ($clientLog) { Show-V12LogTail "$clientLog.out" }
}
