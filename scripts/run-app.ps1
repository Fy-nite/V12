[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$App,
    [string[]]$Arguments = @(),
    [string]$Game,
    [string]$Pak,
    [ValidateSet('auto', 'on', 'off')][string]$Xr = 'auto',
    [ValidateSet('auto', 'off', 'host', 'client')][string]$Mode = 'auto',
    [Alias('Server')][string]$HostAddress = '127.0.0.1',
    [int]$Port = 7777,
    [string]$LogDir,
    [switch]$DebugInput,
    [switch]$Headless,
    [switch]$UseGame,
    [switch]$HotReload,
    [switch]$NoWait,
    [switch]$NoLog,
    [switch]$DryRun,
    [switch]$List,
    [hashtable]$Environment
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'v12-common.ps1')

$repoRoot = Get-V12RepoRoot
$releasesRoot = Get-V12ReleasesRoot

$demoPresets = @(
    'MultiplayerDemo',
    'ControllerDemo',
    'HelloDotnet',
    'SkinnedCharacter'
)

$exePresets = @{
    NovaV2 = @{
        NovaV2 = $true
        Paths = @(
            (Join-Path $releasesRoot 'NovaV2\NovaV2.exe'),
            (Join-Path $repoRoot 'releases\NovaV2\NovaV2.exe')
        )
    }
    Nova = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $releasesRoot 'Nova\Nova.exe'),
            (Join-Path $repoRoot 'releases\Nova\Nova.exe')
        )
    }
    V12Studio = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $releasesRoot 'V12Studio\V12Studio.exe')
        )
    }
    V12StudioAlpha1 = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $releasesRoot 'V12StudioAlpha1\V12Studio.exe')
        )
    }
    V12StudioProwl = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $releasesRoot 'V12Studio_ProwlEdition\V12Studio.exe')
        )
    }
    V12StudioTracy = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $releasesRoot 'V12Studio_TracyProwl\V12Studio.exe')
        )
    }
    Rockstar = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $repoRoot 'Rockstar\bin\Release\net10.0\win-x64\publish\Rockstar.exe'),
            (Join-Path $repoRoot 'Rockstar\bin\Release\net10.0\win-x64\Rockstar.exe'),
            (Join-Path $repoRoot 'Rockstar\bin\Debug\net10.0\Rockstar.exe')
        )
    }
    VNode = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $repoRoot 'VNode\src\bin\Release\net10.0\win-x64\publish\VNode.exe'),
            (Join-Path $repoRoot 'VNode\src\bin\Release\net10.0\win-x64\VNode.exe'),
            (Join-Path $repoRoot 'VNode\src\bin\Debug\net10.0\VNode.exe')
        )
    }
    V12Consumer = @{
        NovaV2 = $false
        Paths = @(
            (Join-Path $repoRoot 'Nova\V12.ConsumerApp\bin\Debug\net10.0\V12.ConsumerApp.exe'),
            (Join-Path $repoRoot 'Nova\V12.ConsumerApp\bin\Release\net10.0\V12.ConsumerApp.exe')
        )
    }
}

if ($List) {
    Write-Host 'Demo apps (exported sample or V12-Samples project):'
    foreach ($name in ($demoPresets | Sort-Object)) {
        $exe = Find-V12SampleExe $name
        $pak = Find-V12SamplePak $name
        $detail = if ($exe) { $exe } elseif ($pak) { $pak } else { 'no build found' }
        Write-Host ("  {0,-18} {1}" -f $name, $detail)
    }
    Write-Host ''
    Write-Host 'Application presets:'
    foreach ($name in ($exePresets.Keys | Sort-Object)) {
        $preset = $exePresets[$name]
        $found = $preset.Paths | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
        $detail = if ($found) { $found } else { 'not built' }
        Write-Host ("  {0,-18} {1}" -f $name, $detail)
    }
    return
}

if (-not $App) {
    throw 'Pass -App <preset|exe|pak|project> or -List.'
}

function New-V12AppLaunch {
    param(
        [string]$Exe,
        [string[]]$AppArguments,
        [string]$WorkingDirectory,
        [bool]$NovaV2 = $true
    )
    return [pscustomobject]@{
        Exe              = $Exe
        Arguments        = $AppArguments
        WorkingDirectory = $WorkingDirectory
        Kind             = 'exe'
        Pak              = $null
        Demo             = $null
        NovaV2           = $NovaV2
    }
}

function Add-V12NovaV2Flags {
    param(
        [string[]]$BaseArguments,
        [string]$PakPath,
        [string]$GameDir,
        [string]$XrMode,
        [switch]$IsHeadless
    )

    $args = New-Object System.Collections.Generic.List[string]
    if ($XrMode -eq 'off') {
        $args.Add('--xr-mode'); $args.Add('off')
    }
    foreach ($argument in $BaseArguments) { $args.Add($argument) }

    if ($GameDir) {
        $args.Add('--game'); $args.Add($GameDir)
    }
    elseif ($PakPath) {
        $args.Add('--pak'); $args.Add($PakPath)
    }

    if ($IsHeadless) { $args.Add('--headless') }

    switch ($XrMode) {
        'off' { $args.Add('--xr'); $args.Add('0') }
        'on'  { $args.Add('--xr'); $args.Add('on') }
        'auto' { }
    }

    return $args.ToArray()
}

$launch = $null
$pakPath = if ($Pak) { Resolve-V12Path $Pak } else { $null }
$gameDir = if ($Game) { Resolve-V12Path $Game } else { $null }

if ($Pak -and -not (Test-Path -LiteralPath $pakPath -PathType Leaf)) {
    throw "Pak not found: $pakPath"
}
if ($Game -and -not (Test-Path -LiteralPath $gameDir -PathType Container)) {
    throw "Game folder not found: $gameDir"
}

if ($gameDir) {
    $launch = Resolve-V12GameLaunch -GameDir $gameDir -Xr $Xr -Headless:$Headless -UseGame:$UseGame -HotReload:$HotReload
}
elseif (-not ($demoPresets -contains $App) -and -not ($exePresets.ContainsKey($App)) -and (Test-Path -LiteralPath $App)) {
    $resolved = (Resolve-Path -LiteralPath $App).Path

    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        $extension = [System.IO.Path]::GetExtension($resolved).ToLowerInvariant()

        if ($extension -eq '.exe') {
            $isNovaV2 = Test-V12NovaV2Exe $resolved
            if ($isNovaV2) {
                $exeArgs = Add-V12NovaV2Flags `
                    -BaseArguments $Arguments `
                    -PakPath $pakPath `
                    -GameDir $null `
                    -XrMode $Xr `
                    -IsHeadless:$Headless
            }
            else {
                if ($Pak -or $Xr -ne 'auto') {
                    Write-Warning "The selected exe is not a NovaV2 app; -Pak/-Xr are ignored. Use -Arguments for app-specific flags."
                }
                $exeArgs = $Arguments
            }
            $launch = New-V12AppLaunch -Exe $resolved -AppArguments $exeArgs -WorkingDirectory (Split-Path -Parent $resolved) -NovaV2 $isNovaV2
        }
        elseif ($extension -eq '.v12pak') {
            $novaV2 = Find-V12NovaV2Exe
            if (-not $novaV2) {
                throw 'No NovaV2 exe found. Set V12_NOVAV2_EXE or build/export NovaV2.'
            }
            $exeArgs = Add-V12NovaV2Flags `
                -BaseArguments $Arguments `
                -PakPath $resolved `
                -GameDir $null `
                -XrMode $Xr `
                -IsHeadless:$Headless
            $launch = New-V12AppLaunch -Exe $novaV2 -AppArguments $exeArgs -WorkingDirectory (Split-Path -Parent $novaV2)
        }
        else {
            throw "Unsupported file type: $resolved"
        }
    }
    elseif (Test-Path -LiteralPath $resolved -PathType Container) {
        if (Test-Path -LiteralPath (Join-Path $resolved 'project.godot') -PathType Leaf) {
            $godot = Find-V12Godot
            $engineArgs = New-Object System.Collections.Generic.List[string]
            $engineArgs.Add('--path'); $engineArgs.Add($resolved)
            if ($Headless) { $engineArgs.Add('--headless') }
            if ($Xr -eq 'off') { $engineArgs.Add('--xr-mode'); $engineArgs.Add('off') }
            $userArgs = New-Object System.Collections.Generic.List[string]
            if ($pakPath) { $userArgs.Add('--pak'); $userArgs.Add($pakPath) }
            if ($HotReload) { $userArgs.Add('--hot-reload') }
            switch ($Xr) {
                'off' { $userArgs.Add('--xr'); $userArgs.Add('0') }
                'on'  { $userArgs.Add('--xr'); $userArgs.Add('on') }
            }

            $allArgs = New-Object System.Collections.Generic.List[string]
            foreach ($argument in $engineArgs) { $allArgs.Add($argument) }
            $allArgs.Add('--')
            foreach ($argument in $userArgs) { $allArgs.Add($argument) }
            $launch = [pscustomobject]@{
                Exe              = $godot
                Arguments        = $allArgs.ToArray()
                WorkingDirectory = Split-Path -Parent $godot
                Kind             = 'godot-project'
                Pak              = $pakPath
                Demo             = $null
                NovaV2           = $false
            }
        }
        else {
            $launch = Resolve-V12GameLaunch -GameDir $resolved -Xr $Xr -Headless:$Headless -UseGame:$UseGame -HotReload:$HotReload
        }
    }
    else {
        throw "Unsupported path: $resolved"
    }
}
elseif ($demoPresets -contains $App) {
    $launch = Resolve-V12DemoLaunch `
        -Demo $App `
        -UseGame:$UseGame `
        -Pak $(if ($Pak) { $Pak } else { $null }) `
        -Xr $Xr `
        -Headless:$Headless `
        -HotReload:$HotReload
}
elseif ($exePresets.ContainsKey($App)) {
    $preset = $exePresets[$App]
    $resolvedExe = $null
    foreach ($candidate in $preset.Paths) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $resolvedExe = (Resolve-Path -LiteralPath $candidate).Path
            break
        }
    }
    if (-not $resolvedExe) {
        throw ("No executable found for '{0}'. Expected one of:`n{1}" -f $App, ($preset.Paths -join "`n"))
    }

    if ($preset.NovaV2) {
        $exeArgs = Add-V12NovaV2Flags `
            -BaseArguments $Arguments `
            -PakPath $pakPath `
            -GameDir $null `
            -XrMode $Xr `
            -IsHeadless:$Headless
    }
    else {
        if ($Pak -or $Xr -ne 'auto') {
            Write-Warning "'$App' is not a NovaV2 app; -Pak/-Xr are ignored. Use -Arguments for app-specific flags."
        }
        $exeArgs = $Arguments
    }

    $launch = New-V12AppLaunch -Exe $resolvedExe -AppArguments $exeArgs -WorkingDirectory (Split-Path -Parent $resolvedExe) -NovaV2 $preset.NovaV2
}
else {
    $known = @($demoPresets) + @($exePresets.Keys) | Sort-Object -Unique
    throw ("Unknown app or path '{0}'. Known apps: {1}" -f $App, ($known -join ', '))
}

if ($Arguments.Count -gt 0 -and $launch.Kind -ne 'exe') {
    # Demo/game/project resolvers take their base args; append caller extras.
    $launch.Arguments = @($launch.Arguments) + $Arguments
}

$logPrefix = if ($NoLog) { $null } else { New-V12LogPrefix -LogDir $LogDir -Name "app-$App" }

if ($HotReload -and $launch.Kind -notin @('godot-game', 'novav2-game', 'godot-project')) {
    Write-Warning '-HotReload only applies to -Game or -UseGame project launches.'
}

Show-V12FreshnessWarning -Launch $launch
Show-V12Launch -Launch $launch -Mode $Mode -Port $Port -LogPrefix $logPrefix
if ($DryRun) { return }

$process = Start-V12Process `
    -Exe $launch.Exe `
    -Arguments $launch.Arguments `
    -WorkingDirectory $launch.WorkingDirectory `
    -Mode $Mode `
    -HostAddress $HostAddress `
    -Port $Port `
    -DebugInput:$DebugInput `
    -LogPrefix $logPrefix `
    -Environment $Environment

Write-Host ("  pid:     {0}" -f $process.Id)
Write-Host ''

if ($NoWait) {
    Write-Host 'Started in the background. Close the app window or stop the PID when done.'
    return
}

try {
    Wait-V12Process $process
}
finally {
    Stop-V12Process $process
}
if ($logPrefix) { Show-V12LogTail "$logPrefix.out" }
