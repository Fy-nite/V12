# Installs every Finite.V12.* package from the Purr feed into an installed
# renderer slot. This is the step that was missing: a renderer only ships the
# assemblies its host happened to reference, so a new engine package (first
# case: V12.Voxel) was invisible to gamepaks until someone remembered to add it.
#
# By default it only fills in assemblies the renderer does not already have, so
# a fresh export is never downgraded. Use -Force to overwrite existing files.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/install-v12-packages.ps1 -Renderer novav2
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/install-v12-packages.ps1 -Renderer novav2 -DryRun
[CmdletBinding()]
param(
    [string]$Renderer = "novav2",
    [string]$Target = "",
    [string]$Feed = "",
    [string]$Version = "",
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$purrHome = if ($env:PURR_HOME) { $env:PURR_HOME } else { Join-Path $env:USERPROFILE ".purr" }
$rid = if ($env:OS -eq "Windows_NT") { "win-x64" } else { "linux-x64" }

function Resolve-RendererTarget([string]$id) {
    $base = Join-Path (Join-Path $purrHome "renderers") $id
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { return $null }

    $perRid = Join-Path $base $rid
    if (Test-Path -LiteralPath (Join-Path $perRid "renderer.json") -PathType Leaf) { return $perRid }

    $flat = Join-Path $base "renderer.json"
    if (Test-Path -LiteralPath $flat -PathType Leaf) { return $base }

    $child = Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "renderer.json") } |
        Select-Object -First 1
    if ($child) { return $child.FullName }

    return $base
}

if (-not $Target) {
    $Target = Resolve-RendererTarget $Renderer
    if (-not $Target) {
        Write-Host "Renderer '$Renderer' is not installed under $purrHome\renderers." -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $Target -PathType Container)) {
    Write-Host "Target not found: $Target" -ForegroundColor Red
    exit 1
}

if (-not $Feed) { $Feed = Join-Path $purrHome "packages" }
if (-not (Test-Path -LiteralPath $Feed -PathType Container)) {
    Write-Host "Package feed not found: $Feed. Run 'vcsdk sync --dev' first." -ForegroundColor Red
    exit 1
}

# The feed version is the single version all Finite.V12.* packages were packed
# at. Prefer the SDK props written by `vcsdk sync`; fall back to the newest
# engine package in the feed.
if (-not $Version) {
    $props = Join-Path $purrHome "sdk\V12.props"
    if (Test-Path -LiteralPath $props -PathType Leaf) {
        $match = [regex]::Match((Get-Content -LiteralPath $props -Raw), "<V12Version>([^<]+)</V12Version>")
        if ($match.Success) { $Version = $match.Groups[1].Value.Trim() }
    }
}

if (-not $Version) {
    $engine = Get-ChildItem -LiteralPath $Feed -File -Filter "Finite.V12.*.nupkg" |
        Where-Object { $_.Name -match "^Finite\.V12\.(\d[^\\/]*)\.nupkg$" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($engine) {
        $match = [regex]::Match($engine.Name, "^Finite\.V12\.(\d[^\\/]*)\.nupkg$")
        if ($match.Success) { $Version = $match.Groups[1].Value }
    }
}

if (-not $Version) {
    Write-Host "Could not determine a V12 package version in $Feed." -ForegroundColor Red
    exit 1
}

# Find every managed folder the host can load from. A Godot export keeps the
# assemblies in data_* and also mirrors some at the root.
$targetDirs = @()
$targetDirs += Get-ChildItem -LiteralPath $Target -Recurse -File -Filter "NovaV2.Host.dll" -ErrorAction SilentlyContinue |
    ForEach-Object { $_.DirectoryName }
$targetDirs += Get-ChildItem -LiteralPath $Target -Recurse -File -Filter "V12.dll" -ErrorAction SilentlyContinue |
    ForEach-Object { $_.DirectoryName }
$targetDirs = @($targetDirs | Sort-Object -Unique)

if ($targetDirs.Count -eq 0) {
    Write-Host "No managed host folder found under $Target (looked for NovaV2.Host.dll / V12.dll)." -ForegroundColor Red
    exit 1
}

$packages = @(Get-ChildItem -LiteralPath $Feed -File -Filter "Finite.V12*.nupkg" |
    Where-Object {
        $_.Name.EndsWith(".$Version.nupkg", [StringComparison]::OrdinalIgnoreCase) -and
        $_.Name -notmatch "^Finite\.V12\.Sdk\." -and
        $_.Name -notmatch "^Finite\.V12\.MCP" -and
        $_.Name -notmatch "^Finite\.V12\.Editor\."
    } |
    Sort-Object Name)

if ($packages.Count -eq 0) {
    Write-Host "No Finite.V12.* packages for version $Version in $Feed." -ForegroundColor Red
    exit 1
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host "V12 package install" -ForegroundColor Cyan
Write-Host "  feed:     $Feed"
Write-Host "  version:  $Version"
Write-Host "  renderer: $Target"
Write-Host "  targets:  $($targetDirs.Count) managed folder(s)"

$installed = 0
$skipped = 0
$missingLib = 0
$tempRoot = Join-Path $env:TEMP ("v12pkg_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    foreach ($package in $packages) {
        $packageName = [System.IO.Path]::GetFileNameWithoutExtension($package.Name)
        $extract = Join-Path $tempRoot $packageName
        New-Item -ItemType Directory -Path $extract -Force | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($package.FullName, $extract)

        $libRoot = Join-Path $extract "lib"
        if (-not (Test-Path -LiteralPath $libRoot -PathType Container)) {
            $missingLib++
            Write-Host ("  skip  {0}  (no lib/ folder)" -f $package.Name) -ForegroundColor DarkGray
            continue
        }

        $lib = $null
        foreach ($tfm in @("net10.0", "net9.0", "net8.0", "netstandard2.1", "netstandard2.0")) {
            $candidate = Join-Path $libRoot $tfm
            if (Test-Path -LiteralPath $candidate -PathType Container) { $lib = $candidate; break }
        }
        if (-not $lib) {
            $lib = (Get-ChildItem -LiteralPath $libRoot -Directory | Select-Object -First 1).FullName
        }

        $dlls = @(Get-ChildItem -LiteralPath $lib -File -Filter "*.dll" -ErrorAction SilentlyContinue)
        foreach ($dll in $dlls) {
            foreach ($dir in $targetDirs) {
                $destination = Join-Path $dir $dll.Name
                if ((Test-Path -LiteralPath $destination -PathType Leaf) -and -not $Force) {
                    $skipped++
                    continue
                }

                if (-not $DryRun) {
                    Copy-Item -LiteralPath $dll.FullName -Destination $destination -Force
                    $pdb = [System.IO.Path]::ChangeExtension($dll.FullName, ".pdb")
                    if (Test-Path -LiteralPath $pdb -PathType Leaf) {
                        Copy-Item -LiteralPath $pdb -Destination (Join-Path $dir ([System.IO.Path]::GetFileName($pdb))) -Force -ErrorAction SilentlyContinue
                    }
                }

                $installed++
                Write-Host ("  {0}  {1}" -f $(if ($DryRun) { "would" } else { "install" }), $dll.Name)
            }
        }
    }
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
$verb = if ($DryRun) { "Would install" } else { "Installed" }
Write-Host ("{0} {1}, skipped {2} existing, packages without lib {3}." -f $verb, $installed, $skipped, $missingLib) -ForegroundColor Green
if ($DryRun) { Write-Host "Dry run: no files were changed." -ForegroundColor Yellow }
