# Shared helpers for the V12 launch scripts. Dot-source this file, do not run it.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:ReleasesRoot =
    if ($env:V12_RELEASES) { $env:V12_RELEASES }
    elseif (Test-Path -LiteralPath 'D:\releases' -PathType Container) { 'D:\releases' }
    else { Join-Path $script:RepoRoot 'releases' }

function Get-V12RepoRoot { return $script:RepoRoot }
function Get-V12ReleasesRoot { return $script:ReleasesRoot }

function Resolve-V12Path {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $script:RepoRoot $Path))
}

function Find-V12Godot {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:GODOT_EXE) { $candidates.Add($env:GODOT_EXE) }

    $candidates.Add('C:\Users\charlie\Desktop\Godot_v4.8-beta3_mono_win64\Godot_v4.8-beta3_mono_win64_console.exe')
    $candidates.Add('C:\Users\charlie\Desktop\Godot_v4.8-beta3_mono_win64\Godot_v4.8-beta3_mono_win64.exe')

    $command = Get-Command 'Godot_v*_console.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { $candidates.Add($command.Source) }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Godot was not found. Set GODOT_EXE to a Godot 4.8 console executable.'
}

function Find-V12NovaV2Exe {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($env:V12_NOVAV2_EXE) { $candidates.Add($env:V12_NOVAV2_EXE) }
    $candidates.Add((Join-Path $script:ReleasesRoot 'NovaV2\NovaV2.exe'))
    $candidates.Add((Join-Path $script:RepoRoot 'releases\NovaV2\NovaV2.exe'))

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Find-V12SampleExe {
    param([string]$Name)
    $candidates = @(
        (Join-Path $script:RepoRoot "V12-Samples\$Name\bin\$Name.exe"),
        (Join-Path $script:RepoRoot "V12-Samples\$Name\bin\Release\net10.0\$Name.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Find-V12SamplePak {
    param([string]$Name)
    $candidates = @(
        (Join-Path $script:RepoRoot "V12-Samples\$Name\bin\$Name.v12pak"),
        (Join-Path $script:RepoRoot "V12-Samples\$Name\bin\Release\net10.0\$Name.v12pak")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    return $null
}

function Test-V12DemoExists {
    param([string]$Demo)
    return (Test-Path -LiteralPath (Join-Path $script:RepoRoot "V12-Samples\$Demo") -PathType Container)
}

<#
.SYNOPSIS
Resolves a V12-Samples demo to an executable and argument list.

By default it prefers the exported game exe (V12-Samples\<Demo>\bin\<Demo>.exe),
which already auto-loads its sibling pak. -UseGame runs the current NovaV2.Host
project through Godot instead, which is the hot-iteration path.
#>
function Resolve-V12DemoLaunch {
    param(
        [Parameter(Mandatory = $true)][string]$Demo,
        [switch]$UseGame,
        [string]$Exe,
        [string]$Pak,
        [ValidateSet('auto', 'on', 'off')][string]$Xr = 'auto',
        [switch]$Headless,
        [switch]$HotReload
    )

    if ($HotReload -and -not $UseGame) {
        throw "-HotReload requires -UseGame: live C# compilation needs the project folder."
    }

    $demoRoot = Resolve-V12Path (Join-Path 'V12-Samples' $Demo)
    $demoDir = $demoRoot
    $exported = if ($Exe) { Resolve-V12Path $Exe } else { Find-V12SampleExe $Demo }
    $pakPath = if ($Pak) { Resolve-V12Path $Pak } else { Find-V12SamplePak $Demo }
    $generic = Find-V12NovaV2Exe
    $hostProject = Resolve-V12Path 'NovaV2\NovaV2.Host'

    if ($Exe -and -not (Test-Path -LiteralPath $exported -PathType Leaf)) {
        throw "Executable not found: $exported"
    }
    if ($Pak -and -not (Test-Path -LiteralPath $pakPath -PathType Leaf)) {
        throw "Pak not found: $pakPath"
    }

    $engineArgs = New-Object System.Collections.Generic.List[string]
    $userArgs = New-Object System.Collections.Generic.List[string]
    $resolvedExe = $null
    $workDir = $null
    $kind = ''

    if ($UseGame) {
        if (-not (Test-Path -LiteralPath $demoDir -PathType Container)) {
            throw "Demo folder not found: $demoDir"
        }
        $godot = Find-V12Godot
        $resolvedExe = $godot
        $workDir = Split-Path -Parent $godot
        $engineArgs.Add('--path'); $engineArgs.Add($hostProject)
        $userArgs.Add('--game'); $userArgs.Add($demoDir)
        if ($HotReload) { $userArgs.Add('--hot-reload') }
        if ($Pak) { $userArgs.Add('--pak'); $userArgs.Add($pakPath) }
        $kind = 'godot-game'
    }
    elseif ($exported) {
        $resolvedExe = $exported
        $workDir = Split-Path -Parent $exported
        if ($Pak) { $userArgs.Add('--pak'); $userArgs.Add($pakPath) }
        $kind = 'export'
    }
    elseif ($generic -and $pakPath) {
        $resolvedExe = $generic
        $workDir = Split-Path -Parent $generic
        $userArgs.Add('--pak'); $userArgs.Add($pakPath)
        $kind = 'novav2-pak'
    }
    elseif ($generic -and (Test-Path -LiteralPath $demoDir -PathType Container)) {
        $resolvedExe = $generic
        $workDir = Split-Path -Parent $generic
        $userArgs.Add('--game'); $userArgs.Add($demoDir)
        if ($HotReload) { $userArgs.Add('--hot-reload') }
        $kind = 'novav2-game'
    }
    elseif (Test-Path -LiteralPath $demoDir -PathType Container) {
        $godot = Find-V12Godot
        $resolvedExe = $godot
        $workDir = Split-Path -Parent $godot
        $engineArgs.Add('--path'); $engineArgs.Add($hostProject)
        $userArgs.Add('--game'); $userArgs.Add($demoDir)
        if ($HotReload) { $userArgs.Add('--hot-reload') }
        $kind = 'godot-game'
    }
    else {
        throw "Could not resolve a launch target for demo '$Demo'. Build the sample or pass -Pak/-Exe."
    }

    if ($Headless) {
        if ($kind -like 'godot*') { $engineArgs.Add('--headless') }
        else { $userArgs.Add('--headless') }
    }

    # AGENTS.md: desktop tests and samples pass `--xr-mode off` so Godot
    # never starts OpenXR. NovaV2's own `--xr 0` still disables its rig.
    if ($Xr -eq 'off') {
        $engineArgs.Add('--xr-mode'); $engineArgs.Add('off')
    }

    switch ($Xr) {
        'off' { $userArgs.Add('--xr'); $userArgs.Add('0') }
        'on'  { $userArgs.Add('--xr'); $userArgs.Add('on') }
        'auto' { }
    }

    $arguments = New-Object System.Collections.Generic.List[string]
    foreach ($argument in $engineArgs) { $arguments.Add($argument) }
    if ($kind -like 'godot*') { $arguments.Add('--') }
    foreach ($argument in $userArgs) { $arguments.Add($argument) }

    return [pscustomobject]@{
        Exe              = $resolvedExe
        Arguments        = $arguments.ToArray()
        WorkingDirectory = $workDir
        Kind             = $kind
        Pak              = $pakPath
        Demo             = $Demo
        NovaV2           = (Test-V12NovaV2Exe $resolvedExe)
    }
}

function Resolve-V12GameLaunch {
    param(
        [Parameter(Mandatory = $true)][string]$GameDir,
        [ValidateSet('auto', 'on', 'off')][string]$Xr = 'auto',
        [switch]$Headless,
        [switch]$UseGame,
        [switch]$HotReload
    )

    $resolvedGame = Resolve-V12Path $GameDir
    if (-not (Test-Path -LiteralPath $resolvedGame -PathType Container)) {
        throw "Game folder not found: $resolvedGame"
    }

    $engineArgs = New-Object System.Collections.Generic.List[string]
    $userArgs = New-Object System.Collections.Generic.List[string]
    $generic = Find-V12NovaV2Exe
    $resolvedExe = $null
    $workDir = $null
    $kind = ''

    if (-not $UseGame -and $generic) {
        $resolvedExe = $generic
        $workDir = Split-Path -Parent $generic
        $userArgs.Add('--game'); $userArgs.Add($resolvedGame)
        if ($HotReload) { $userArgs.Add('--hot-reload') }
        $kind = 'novav2-game'
    }
    else {
        $godot = Find-V12Godot
        $hostProject = Resolve-V12Path 'NovaV2\NovaV2.Host'
        $resolvedExe = $godot
        $workDir = Split-Path -Parent $godot
        $engineArgs.Add('--path'); $engineArgs.Add($hostProject)
        $userArgs.Add('--game'); $userArgs.Add($resolvedGame)
        if ($HotReload) { $userArgs.Add('--hot-reload') }
        $kind = 'godot-game'
    }

    if ($Headless) {
        if ($kind -like 'godot*') { $engineArgs.Add('--headless') }
        else { $userArgs.Add('--headless') }
    }
    if ($Xr -eq 'off') {
        $engineArgs.Add('--xr-mode'); $engineArgs.Add('off')
    }
    switch ($Xr) {
        'off' { $userArgs.Add('--xr'); $userArgs.Add('0') }
        'on'  { $userArgs.Add('--xr'); $userArgs.Add('on') }
        'auto' { }
    }

    $arguments = New-Object System.Collections.Generic.List[string]
    foreach ($argument in $engineArgs) { $arguments.Add($argument) }
    if ($kind -like 'godot*') { $arguments.Add('--') }
    foreach ($argument in $userArgs) { $arguments.Add($argument) }

    return [pscustomobject]@{
        Exe              = $resolvedExe
        Arguments        = $arguments.ToArray()
        WorkingDirectory = $workDir
        Kind             = $kind
        Pak              = $null
        Demo             = $null
        NovaV2           = $true
    }
}

function Test-V12NovaV2Exe {
    param([string]$Exe)
    if (-not $Exe) { return $false }

    $directory = Split-Path -Parent $Exe
    if ($directory) {
        if (Test-Path -LiteralPath (Join-Path $directory 'data_NovaV2.Host_windows_x86_64') -PathType Container) {
            return $true
        }
        if (Test-Path -LiteralPath (Join-Path $directory 'data_V12TwoDog.Godot_windows_x86_64') -PathType Container) {
            return $false
        }
    }

    $name = [System.IO.Path]::GetFileNameWithoutExtension($Exe)
    return $name -in @('NovaV2', 'MultiplayerDemo', 'ControllerDemo')
}

function Get-V12NewestWriteTime {
    param([string[]]$Paths)
    $newest = [DateTime]::MinValue
    foreach ($path in $Paths) {
        if (-not $path -or -not (Test-Path -LiteralPath $path)) { continue }
        $item = Get-Item -LiteralPath $path
        if ($item.PSIsContainer) {
            $files = Get-ChildItem -LiteralPath $path -Recurse -File -Include *.cs, *.worldml, *.csproj, manifest.json -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\(bin|obj|\.godot|logs)\\' }
            foreach ($file in $files) {
                if ($file.LastWriteTime -gt $newest) { $newest = $file.LastWriteTime }
            }
        }
        elseif ($item.LastWriteTime -gt $newest) {
            $newest = $item.LastWriteTime
        }
    }
    return $newest
}

function Show-V12FreshnessWarning {
    param($Launch)

    if (-not $Launch -or -not $Launch.Exe) { return }
    if (-not $Launch.NovaV2) { return }
    if ($Launch.Kind -like 'godot*') { return }
    if (-not (Test-Path -LiteralPath $Launch.Exe -PathType Leaf)) { return }

    $exeTime = (Get-Item -LiteralPath $Launch.Exe).LastWriteTime
    $sourcePaths = @(
        (Join-Path $script:RepoRoot 'NovaV2\NovaV2.Shared'),
        (Join-Path $script:RepoRoot 'NovaV2\NovaV2.Host'),
        (Join-Path $script:RepoRoot 'V12-Engine\Core'),
        (Join-Path $script:RepoRoot 'V12.Basic'),
        (Join-Path $script:RepoRoot 'V12.Rendering')
    )
    if ($Launch.Demo) {
        $sourcePaths += (Join-Path $script:RepoRoot "V12-Samples\$($Launch.Demo)")
    }

    $newest = Get-V12NewestWriteTime $sourcePaths
    if ($newest -gt $exeTime) {
        $hint = if ($Launch.Demo) {
            'Pass -UseGame for current code, or re-export the app.'
        }
        else {
            'Re-export the app for current code.'
        }
        Write-Warning ("{0} is older than the V12 sources ({1:g} vs {2:g}). {3}" -f $Launch.Exe, $exeTime, $newest, $hint)
    }

    if ($Launch.Pak -and (Test-Path -LiteralPath $Launch.Pak -PathType Leaf)) {
        $pakTime = (Get-Item -LiteralPath $Launch.Pak).LastWriteTime
        $pakNewest = if ($Launch.Demo) {
            Get-V12NewestWriteTime @((Join-Path $script:RepoRoot "V12-Samples\$($Launch.Demo)"))
        }
        else {
            $newest
        }
        if ($pakNewest -gt $pakTime) {
            Write-Warning ("{0} is older than the demo sources ({1:g} vs {2:g}). Run vcsdk pack before launching." -f $Launch.Pak, $pakTime, $pakNewest)
        }
    }
}

function Format-V12ArgumentLine {
    param([string[]]$Arguments)
    if (-not $Arguments -or $Arguments.Count -eq 0) { return '' }

    $parts = foreach ($argument in $Arguments) {
        $text = [string]$argument
        if ($text -eq '') { '""' }
        elseif ($text -match '[\s"]') { '"' + ($text -replace '"', '\"') + '"' }
        else { $text }
    }
    return ($parts -join ' ')
}

function Set-V12ProcessEnvironment {
    param([string]$Name, $Value)
    if ($null -eq $Value) {
        [Environment]::SetEnvironmentVariable($Name, $null, 'Process')
    }
    else {
        [Environment]::SetEnvironmentVariable($Name, [string]$Value, 'Process')
    }
}

<#
.SYNOPSIS
Starts a V12 process with multiplayer env, optional logs, and restores the
caller environment afterwards.
#>
function Start-V12Process {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory,
        [ValidateSet('auto', 'off', 'host', 'client')][string]$Mode = 'auto',
        [string]$HostAddress = '127.0.0.1',
        [int]$Port = 7777,
        [switch]$DebugInput,
        [string]$LogPrefix,
        [hashtable]$Environment
    )

    $names = New-Object System.Collections.Generic.List[string]
    $names.Add('V12_MP_MODE')
    $names.Add('V12_MP_HOST')
    $names.Add('V12_MP_PORT')
    $names.Add('V12_MP_DEBUG')
    if ($Environment) {
        foreach ($key in $Environment.Keys) { $names.Add($key) }
    }

    $saved = @{}
    foreach ($name in ($names | Select-Object -Unique)) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    try {
        if ($Mode -eq 'auto') {
            Set-V12ProcessEnvironment 'V12_MP_MODE' $null
        }
        else {
            Set-V12ProcessEnvironment 'V12_MP_MODE' $Mode
        }
        Set-V12ProcessEnvironment 'V12_MP_HOST' $HostAddress
        Set-V12ProcessEnvironment 'V12_MP_PORT' $Port
        Set-V12ProcessEnvironment 'V12_MP_DEBUG' ($(if ($DebugInput) { '1' } else { $null }))

        if ($Environment) {
            foreach ($key in $Environment.Keys) {
                Set-V12ProcessEnvironment $key $Environment[$key]
            }
        }

        $start = @{
            FilePath         = $Exe
            WorkingDirectory = $WorkingDirectory
            PassThru         = $true
        }

        $argumentLine = Format-V12ArgumentLine $Arguments
        if ($argumentLine) { $start.ArgumentList = $argumentLine }

        if ($LogPrefix) {
            $directory = Split-Path -Parent $LogPrefix
            if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
            $start.RedirectStandardOutput = "$LogPrefix.out"
            $start.RedirectStandardError = "$LogPrefix.err"
        }

        $process = Start-Process @start
        if ($LogPrefix) {
            $process | Add-Member -NotePropertyName V12LogPath -NotePropertyValue "$LogPrefix.out" -Force
        }
        return $process
    }
    finally {
        foreach ($name in $saved.Keys) {
            Set-V12ProcessEnvironment $name $saved[$name]
        }
    }
}

function New-V12LogPrefix {
    param([string]$LogDir, [string]$Name)
    if (-not $LogDir) { $LogDir = Join-Path $script:RepoRoot 'logs' }
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $safeName = $Name -replace '[^\w.-]', '_'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    return (Join-Path $LogDir "$safeName-$stamp")
}

function Show-V12Launch {
    param(
        $Launch,
        [string]$Mode,
        [int]$Port,
        [string]$LogPrefix
    )

    $modeLabel = if ($Mode -eq 'auto') { 'app default' } else { $Mode }
    $argumentLine = Format-V12ArgumentLine $Launch.Arguments
    $commandLine = if ($argumentLine) { '"{0}" {1}' -f $Launch.Exe, $argumentLine } else { '"{0}"' -f $Launch.Exe }

    Write-Host ("[{0}] {1}" -f $modeLabel, $Launch.Exe)
    Write-Host ("  workdir: {0}" -f $Launch.WorkingDirectory)
    Write-Host ("  command: {0}" -f $commandLine)
    Write-Host ("  port:    {0}" -f $Port)
    if ($LogPrefix) {
        Write-Host ("  log:     {0}.out" -f $LogPrefix)
        Write-Host ("  errors:  {0}.err" -f $LogPrefix)
    }
    Write-Host ''
}

function Show-V12LogTail {
    param([string]$Path, [int]$Lines = 20)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    Write-Host ("--- last {0} lines of {1} ---" -f $Lines, $Path)
    Get-Content -LiteralPath $Path -Tail $Lines | ForEach-Object { Write-Host $_ }
    Write-Host ''
}

function Wait-V12Process {
    param($Process)
    if ($null -eq $Process) { return }

    Write-Host ("PID {0} is running. Press Ctrl+C to stop waiting." -f $Process.Id)
    try {
        Wait-Process -Id $Process.Id -ErrorAction Stop
    }
    catch {
        # Already gone.
    }

    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            Write-Host ("PID {0} exited with code {1}." -f $Process.Id, $Process.ExitCode)
        }
    }
    catch { }
}

function Get-V12DescendantProcessIds {
    param([int]$RootId)
    $result = New-Object System.Collections.Generic.List[int]
    $queue = New-Object System.Collections.Generic.Queue[int]
    $queue.Enqueue($RootId)

    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        try {
            $children = Get-CimInstance Win32_Process -Filter "ParentProcessId=$current" -ErrorAction SilentlyContinue
            foreach ($child in $children) {
                $childId = [int]$child.ProcessId
                if (-not $result.Contains($childId)) {
                    $result.Add($childId)
                    $queue.Enqueue($childId)
                }
            }
        }
        catch { }
    }

    return $result
}

function Stop-V12Process {
    param($Process)
    if ($null -eq $Process) { return }
    try {
        $Process.Refresh()

        # A Godot editor launch can spawn a child process with the actual
        # window. Collect the tree first, then kill the tree. The root may
        # already have exited while its window child is still alive.
        $descendants = Get-V12DescendantProcessIds -RootId $Process.Id

        if (-not $Process.HasExited) {
            $taskkill = Get-Command 'taskkill.exe' -ErrorAction SilentlyContinue
            if ($taskkill) {
                & $taskkill.Source /PID $Process.Id /T /F 2>$null | Out-Null
            }
            else {
                Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            }
        }

        foreach ($childId in $descendants) {
            Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue
        }
    }
    catch { }
}
