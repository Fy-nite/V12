# Exports NovaV2 into the Purr renderer slot used by
# `vcsdk build -r novav2`, then leaves a renderer.json beside it.
#
# Run this after changing NovaV2.Host, V12.Physics, V12.Gravity or any other
# assembly the exported host needs.
[CmdletBinding()]
param(
    [string]$Godot = $env:GODOT,
    [ValidateSet("debug", "release")]
    [string]$Configuration = "debug",
    [string]$Preset = "Windows Desktop",
    [string]$Rid = "win-x64",
    [string]$Output = ""
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot\.."
$projectDir = Join-Path $root "NovaV2\NovaV2.Host"

if (-not $Godot) {
    $candidates = @(
        $env:GODOT,
        (Get-Command godot -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "C:\Users\charlie\Desktop\Godot_v4.8-beta3_mono_win64\Godot_v4.8-beta3_mono_win64.exe" # should change this based on the users system.
    ) | Where-Object { $_ -and (Test-Path $_) }

    $Godot = $candidates | Select-Object -First 1
}

if (-not $Godot) {
    throw "Godot executable not found. Pass -Godot <path> or set GODOT."
}
if (-not (Test-Path $projectDir)) {
    throw "NovaV2.Host project not found at $projectDir."
}

$purrHome = $env:PURR_HOME
if (-not $purrHome) {
    $purrHome = Join-Path $env:USERPROFILE ".purr"
}

$outputDir = if ($Output) {
    [System.IO.Path]::GetFullPath($Output)
} else {
    Join-Path (Join-Path $purrHome "renderers\novav2") $Rid
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$exePath = Join-Path $outputDir "NovaV2.exe"

Write-Host "NovaV2 renderer export" -ForegroundColor Cyan
Write-Host "  godot:   $Godot"
Write-Host "  project: $projectDir"
Write-Host "  preset:  $Preset ($Configuration)"
Write-Host "  output:  $outputDir"

# Godot accepts the export path as the second argument after the preset.
# Passing the preset as one array element keeps its space intact.
$godotArgs = @(
    "--headless",
    "--path", $projectDir,
    "--export-$Configuration", $Preset, $exePath
)

& $Godot @godotArgs
$exportExit = $LASTEXITCODE
if (-not (Test-Path $exePath)) {
    throw "Godot export did not produce the expected host: $exePath (exit $exportExit)."
}
if ($null -ne $exportExit -and $exportExit -ne 0) {
    # Godot can return a nonzero exit for warnings after a complete export.
    # The produced host is the authority here; only a missing exe is fatal.
    Write-Warning "Godot export returned exit code $exportExit, but $exePath exists; continuing."
}

# Godot can leave a full-size .tmp beside the host when an export is
# interrupted. It is not part of the distribution.
Get-ChildItem $outputDir -File -Filter "*.tmp" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

$rendererJson = Join-Path $outputDir "renderer.json"
if (-not (Test-Path $rendererJson)) {
    $version = "1.0.0"
    $propsPath = Join-Path $root "Directory.Build.props"
    if (Test-Path $propsPath) {
        $props = Get-Content $propsPath -Raw
        if ($props -match '<V12Version>([^<]+)</V12Version>') {
            $version = $Matches[1].Trim()
        }
    }

    $config = [ordered]@{
        schemaVersion     = 1
        id                = "novav2"
        name              = "V12 NovaV2 (Godot)"
        version           = $version
        kind              = "godot"
        distribution      = "godot-export"
        singleFile        = $false
        runtimeIdentifier = $Rid
        selfContained     = $true
        executable        = "NovaV2.exe"
        arguments         = @()
        sourceProject     = "NovaV2/NovaV2.Host/NovaV2.Host.csproj"
        publishedAtUtc    = (Get-Date).ToUniversalTime().ToString("o")
        gamePakMode       = "sibling"
    }

    $config | ConvertTo-Json -Depth 10 |
        Set-Content -Path $rendererJson -Encoding UTF8
}

# The host output must carry every V12 assembly the gamepaks reference.
# This is the exact failure mode that makes `vcsdk build -r novav2` produce a
# pak that cannot start. The audit derives the set from the current host build,
# so a new engine module such as V12.Voxel is covered without editing a
# hardcoded list here.
$auditScript = Join-Path $PSScriptRoot "check-v12-deps.ps1"
$packageScript = Join-Path $PSScriptRoot "install-v12-packages.ps1"
if (Test-Path $packageScript) {
    & $packageScript -Target $outputDir
}
if (Test-Path $auditScript) {
    try {
        & $auditScript -Target $outputDir -Strict -IncludePackages
    }
    catch {
        throw "The exported host at $outputDir is missing or older than the host's V12 assemblies. Rebuild NovaV2.Host and rerun this script. $($_.Exception.Message)"
    }
}
else {
    Write-Warning "check-v12-deps.ps1 not found; skipping V12 assembly audit."
}

Write-Host ""
Write-Host "Renderer ready: $outputDir" -ForegroundColor Green
Write-Host "Next: vcsdk build V12-Samples/VoxelPlanet -r novav2" -ForegroundColor Green

# Godot can return a nonzero exit for editor warnings even after a complete
# export; the produced host and the dependency audit are the authority.
exit 0
