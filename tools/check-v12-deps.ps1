# Audits a renderer or exported game folder against the V12 assemblies a build
# actually depends on. This exists because hardcoded checks (Gravity + Physics)
# missed V12.Voxel, which made `vcsdk build -r novav2` produce a host that could
# not resolve the planet component.
#
# Examples:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-v12-deps.ps1 -Renderer novav2
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-v12-deps.ps1 -Renderer novav2 -Source V12-Samples/VoxelPlanet/bin/Debug/net10.0
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/check-v12-deps.ps1 -Target C:/path/to/renderer -Packages -Strict
[CmdletBinding()]
param(
    # Renderer slot under ~/.purr/renderers to audit.
    [string]$Renderer = "novav2",

    # Explicit folder to audit (overrides -Renderer).
    [string]$Target = "",

    # Folder whose V12*.dll set is the requirement. Defaults to the current
    # NovaV2 host build output; pass a game output folder to audit a gamepak.
    [string]$Source = "",

    # Also list the newest Finite.V12.* packages in the Purr feed.
    [switch]$Packages,

    # Treat every runtime Finite.V12.* package in the feed as required too, so a
    # package the host does not reference (V12.Voxel before it was added) cannot
    # be missing from the renderer unnoticed.
    [switch]$IncludePackages,

    # Treat stale (older than source) assemblies as a failure too.
    [switch]$Strict
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path "$PSScriptRoot\..").Path
$purrHome = if ($env:PURR_HOME) { $env:PURR_HOME } else { Join-Path $env:USERPROFILE ".purr" }
$rid = if ($env:OS -eq "Windows_NT") { "win-x64" } else { "linux-x64" }

# Metapackages such as Finite.V12.All carry package dependencies but no lib/
# assembly, so they must not contribute a required V12.*.dll name.
function Test-PackageShipsAssemblies([string]$path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -match '^lib/.+\.dll$') { return $true }
        }
        return $false
    }
    finally {
        $zip.Dispose()
    }
}

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

function Find-HostBuildOutput {
    $temp = Join-Path $root "NovaV2\NovaV2.Host\.godot\mono\temp\bin"
    $candidates = @(
        (Join-Path $temp "Debug"),
        (Join-Path $temp "ExportDebug"),
        (Join-Path $root "NovaV2\NovaV2.Host\bin\Debug\net10.0")
    )

    # Pick the freshest build output, not the first path that exists: an export
    # writes ExportDebug while a dev build writes Debug, and auditing against
    # the wrong one produces false staleness.
    $best = $null
    $bestTime = [datetime]::MinValue
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }

        $engine = Get-ChildItem -LiteralPath $candidate -Recurse -File -Filter "V12.dll" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $engine) { continue }

        if ($engine.LastWriteTime -gt $bestTime) {
            $best = $candidate
            $bestTime = $engine.LastWriteTime
        }
    }

    return $best
}

function Get-V12Assemblies([string]$dir) {
    $map = @{}
    Get-ChildItem -LiteralPath $dir -Recurse -File -Filter "V12*.dll" -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -notmatch "\.resources\.dll$" -and
            $_.Name -notlike "V12.MCP*" -and
            $_.Name -notlike "V12.Sdk*"
        } |
        ForEach-Object {
            $existing = $map[$_.Name]
            if (-not $existing -or $_.LastWriteTime -gt $existing.LastWriteTime) {
                $map[$_.Name] = $_
            }
        }
    return $map
}

function Get-AssemblyVersion([string]$path) {
    try { return [System.Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString() }
    catch { return "?" }
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

if (-not $Source) {
    $Source = Find-HostBuildOutput
    if (-not $Source) {
        Write-Host "No host build output found. Build NovaV2.Host first, or pass -Source <dir>." -ForegroundColor Red
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
    Write-Host "Source not found: $Source" -ForegroundColor Red
    exit 1
}

$expected = Get-V12Assemblies $Source
$present = Get-V12Assemblies $Target

if ($expected.Count -eq 0) {
    Write-Host "Source has no V12*.dll assemblies: $Source" -ForegroundColor Red
    exit 1
}

# Optional: every runtime Finite.V12.* package in the feed is also required.
# This catches a package the host does not reference yet (the original V12.Voxel
# failure) without editing a list here.
$packageVersion = $null
$packageNames = @()
if ($IncludePackages) {
    $feed = Join-Path $purrHome "packages"
    $props = Join-Path $purrHome "sdk\V12.props"
    if (Test-Path -LiteralPath $props -PathType Leaf) {
        $match = [regex]::Match((Get-Content -LiteralPath $props -Raw), "<V12Version>([^<]+)</V12Version>")
        if ($match.Success) { $packageVersion = $match.Groups[1].Value.Trim() }
    }

    if ($packageVersion -and (Test-Path -LiteralPath $feed -PathType Container)) {
        $suffix = ".$packageVersion.nupkg"
        foreach ($nupkg in Get-ChildItem -LiteralPath $feed -File -Filter "Finite.V12*.nupkg") {
            if (-not $nupkg.Name.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ($nupkg.Name -match "^Finite\.V12\.Sdk\.") { continue }
            if ($nupkg.Name -match "^Finite\.V12\.MCP") { continue }
            if ($nupkg.Name -match "^Finite\.V12\.Editor\.") { continue }
            if (-not (Test-PackageShipsAssemblies $nupkg.FullName)) { continue }

            $id = $nupkg.Name.Substring(0, $nupkg.Name.Length - $suffix.Length)   # Finite.V12.Voxel
            if (-not $id.StartsWith("Finite.", [StringComparison]::OrdinalIgnoreCase)) { continue }
            $packageNames += ($id.Substring("Finite.".Length) + ".dll")           # V12.Voxel.dll
        }
    }
    elseif ($IncludePackages) {
        Write-Host "  note: no package version found in $props; skipping package requirements." -ForegroundColor DarkGray
    }

    $packageNames = @($packageNames | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object -Unique)
}

$missingHost = @()
$missingPackages = @()
$stale = @()
$ok = @()

foreach ($name in ($expected.Keys | Sort-Object)) {
    $sourceFile = $expected[$name]
    if (-not $present.ContainsKey($name)) {
        $missingHost += $name
        continue
    }

    $targetFile = $present[$name]
    $targetVersion = Get-AssemblyVersion $targetFile.FullName
    $sourceVersion = Get-AssemblyVersion $sourceFile.FullName

    if ($targetFile.LastWriteTime -lt $sourceFile.LastWriteTime -or
        ($targetVersion -ne "?" -and $sourceVersion -ne "?" -and $targetVersion -ne $sourceVersion)) {
        $stale += [pscustomobject]@{
            Name = $name
            TargetTime = $targetFile.LastWriteTime
            SourceTime = $sourceFile.LastWriteTime
            TargetVersion = $targetVersion
            SourceVersion = $sourceVersion
        }
    }
    else {
        $ok += $name
    }
}

$extra = @($present.Keys | Where-Object { -not $expected.ContainsKey($_) } | Sort-Object)

foreach ($name in $packageNames) {
    if (-not $present.ContainsKey($name)) {
        $missingPackages += $name
    }
}
$missing = @($missingHost + $missingPackages)

Write-Host ""
Write-Host "V12 dependency audit" -ForegroundColor Cyan
Write-Host "  source: $Source"
Write-Host "  target: $Target"
Write-Host ("  expected {0} host + {1} package, present {2}, missing {3}, stale {4}" -f `
    $expected.Count, $packageNames.Count, $present.Count, $missing.Count, $stale.Count)
Write-Host ""

foreach ($name in $missingHost) {
    Write-Host ("  MISSING  {0}  (in source {1})" -f $name, $expected[$name].LastWriteTime) -ForegroundColor Red
}

foreach ($name in $missingPackages) {
    Write-Host ("  MISSING  {0}  (package {1})" -f $name, $packageVersion) -ForegroundColor Red
}

foreach ($entry in $stale) {
    Write-Host ("  STALE    {0}  target {1:HH:mm:ss} v{2}  source {3:HH:mm:ss} v{4}" -f `
        $entry.Name, $entry.TargetTime, $entry.TargetVersion, $entry.SourceTime, $entry.SourceVersion) -ForegroundColor Yellow
}

foreach ($name in $ok) {
    Write-Host ("  ok       {0}  v{1}" -f $name, (Get-AssemblyVersion $present[$name].FullName))
}

foreach ($name in $packageNames) {
    if ($present.ContainsKey($name)) {
        Write-Host ("  ok       {0}  (package)" -f $name)
    }
}

foreach ($name in $extra) {
    Write-Host ("  extra    {0}  (not in source)" -f $name) -ForegroundColor DarkGray
}

if ($Packages) {
    $feed = Join-Path $purrHome "packages"
    Write-Host ""
    Write-Host "Finite.V12 packages in $feed" -ForegroundColor Cyan
    if (Test-Path -LiteralPath $feed -PathType Container) {
        Get-ChildItem -LiteralPath $feed -File -Filter "Finite.V12*.nupkg" |
            ForEach-Object {
                $name = $_.Name
                $dash = $name.IndexOf(".nupkg", [StringComparison]::OrdinalIgnoreCase)
                if ($dash -lt 0) { return }
                $idVersion = $name.Substring(0, $dash)
                [pscustomobject]@{ Name = $idVersion; Time = $_.LastWriteTime }
            } |
            Sort-Object Time -Descending |
            Select-Object -First 25 |
            ForEach-Object { Write-Host ("  {0}  {1:g}" -f $_.Name, $_.Time) }
    }
    else {
        Write-Host "  (feed missing)" -ForegroundColor DarkGray
    }
}

$failed = $missing.Count -gt 0 -or ($Strict -and $stale.Count -gt 0)
if ($failed) {
    Write-Host ""
    Write-Host "Fix: rebuild the renderer output so it carries every V12 assembly above." -ForegroundColor Yellow
    Write-Host "     tools/install-novav2-renderer.ps1" -ForegroundColor Yellow
    throw "V12 dependency audit failed: $($missing.Count) missing, $($stale.Count) stale."
}

Write-Host ""
Write-Host "V12 dependencies look consistent." -ForegroundColor Green
