# One-command V12 module sync:
#   1. vcsdk sync packs every root-level Finite.V12.* module (auto-discovered,
#      so a new package needs no SDK edit).
#   2. Every installed renderer slot gets any Finite.V12.* assembly it is
#      missing, so gamepaks can resolve new packages without a renderer rebuild.
#
# This does not rebuild the renderer host itself. If an engine change altered a
# host assembly, run tools/install-novav2-renderer.ps1 after this.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-v12.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-v12.ps1 -DryRun
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/sync-v12.ps1 -Renderer novav2 -Force
[CmdletBinding()]
param(
    # Pack the stable version instead of a unique dev stamp.
    [switch]$Stable,

    # Install into one renderer id instead of every installed slot.
    [string]$Renderer = "",

    # Overwrite existing assemblies (default: only install missing ones).
    [switch]$Force,

    # Show what would be copied, without changing files.
    [switch]$DryRun,

    # Skip packing and only run the renderer install step.
    [switch]$SkipSync
)

$ErrorActionPreference = "Stop"

$purrHome = if ($env:PURR_HOME) { $env:PURR_HOME } else { Join-Path $env:USERPROFILE ".purr" }
$installScript = Join-Path $PSScriptRoot "install-v12-packages.ps1"
if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
    throw "install-v12-packages.ps1 not found next to this script."
}

if (-not $SkipSync) {
    $vcsdk = Get-Command vcsdk -ErrorAction SilentlyContinue
    if (-not $vcsdk) {
        throw "vcsdk was not found on PATH. Install it or pass -SkipSync to only install packages."
    }

    $syncArgs = @("sync")
    if (-not $Stable) { $syncArgs += "--dev" }

    Write-Host ("[sync-v12] vcsdk {0}" -f ($syncArgs -join " ")) -ForegroundColor Cyan
    & vcsdk @syncArgs
    if ($LASTEXITCODE -ne 0) {
        throw "vcsdk sync failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Host "[sync-v12] Packing skipped; installing from the existing feed." -ForegroundColor Yellow
}

$rendererRoot = Join-Path $purrHome "renderers"
$targets = @()

function Add-RendererSlot([string]$directory) {
    if (Test-Path -LiteralPath (Join-Path $directory "renderer.json") -PathType Leaf) {
        $script:targets += (Resolve-Path -LiteralPath $directory).Path
    }
}

if ($Renderer) {
    $base = Join-Path $rendererRoot $Renderer
    if (-not (Test-Path -LiteralPath $base -PathType Container)) {
        throw "Renderer '$Renderer' is not installed under $rendererRoot."
    }

    Add-RendererSlot $base
    foreach ($ridDir in Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue) {
        Add-RendererSlot $ridDir.FullName
    }
}
elseif (Test-Path -LiteralPath $rendererRoot -PathType Container) {
    foreach ($dir in Get-ChildItem -LiteralPath $rendererRoot -Directory) {
        Add-RendererSlot $dir.FullName
        foreach ($ridDir in Get-ChildItem -LiteralPath $dir.FullName -Directory -ErrorAction SilentlyContinue) {
            Add-RendererSlot $ridDir.FullName
        }
    }
}

$targets = @($targets | Sort-Object -Unique)
if ($targets.Count -eq 0) {
    Write-Host "[sync-v12] No installed renderers found under $rendererRoot." -ForegroundColor Yellow
    exit 0
}

$failed = 0
foreach ($target in $targets) {
    Write-Host ""
    Write-Host "[sync-v12] $target" -ForegroundColor Cyan
    try {
        & $installScript -Target $target -Force:$Force -DryRun:$DryRun
    }
    catch {
        $failed++
        Write-Host ("[sync-v12] {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

Write-Host ""
if ($failed -gt 0) {
    throw "V12 package install failed for $failed renderer(s)."
}

Write-Host "[sync-v12] Done. New Finite.V12.* packages are available to all projects on this machine." -ForegroundColor Green
