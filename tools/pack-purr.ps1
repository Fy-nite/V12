param(
    [string]$Version = "",
    [string]$Output = "",
    [switch]$NoBuild,
    [switch]$Verbose,
    [switch]$Dev
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

# Resolve the version: -Version wins, else the root V12Version property, else 1.0.0
if (-not $Version) {
    $props = Get-Content "$root/Directory.Build.props" -Raw
    if ($props -match '<V12Version>([^<]+)</V12Version>') { $Version = $Matches[1].Trim() }
    else { $Version = "1.0.0" }
}

# -Dev stamps a unique prerelease version so NuGet never reuses a cached
# extraction of an older build with the same version.
if ($Dev) {
    $base = $Version
    $dash = $base.IndexOf('-')
    if ($dash -ge 0) { $base = $base.Substring(0, $dash) }
    $stamp = [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $sha = ""
    try { $sha = (git -C $root rev-parse --short HEAD 2>$null).Trim() } catch { }
    if (-not $sha) { $sha = "local" }
    $Version = "$base-dev.$stamp.$sha"
    Write-Host "Dev version: $Version" -ForegroundColor Yellow
}

# Resolve the package output: -Output wins, else $PURR_HOME/packages, else ~/.purr/packages
$purrHome = $env:PURR_HOME
if (-not $purrHome) {
    if ($env:HOME) { $purrHome = Join-Path $env:HOME ".purr" }
    else { $purrHome = Join-Path $env:USERPROFILE ".purr" }
}
$purrPackages = if ($Output) { $Output } else { Join-Path $purrHome "packages" }
$legacyPackages = "D:\V12Packages"

foreach ($dir in @($purrPackages, $legacyPackages)) {
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
}

Write-Host "V12-Monorepo pack -> v$Version" -ForegroundColor Cyan
Write-Host "  purr: $purrPackages" -ForegroundColor DarkGray
if (Test-Path $legacyPackages) { Write-Host "  legacy: $legacyPackages" -ForegroundColor DarkGray }

$projects = @(
    @{ Path = "V12-Engine/V12.csproj"; PackageId = "Finite.V12" },
    @{ Path = "V12.Rendering/V12.Rendering.csproj"; PackageId = "Finite.V12.Rendering" }
    @{ Path = "V12.Audio/V12.Audio.csproj"; PackageId = "Finite.V12.Audio" }
    @{ Path = "V12.Audio.OwnAudio/V12.Audio.OwnAudio.csproj"; PackageId = "Finite.V12.Audio.OwnAudio" }
    @{ Path = "V12.Basic/V12.Basic.csproj"; PackageId = "Finite.V12.Basic" }
    @{ Path = "V12.Paper/V12.Paper.csproj"; PackageId = "Finite.V12.Paper" }
    @{ Path = "V12.Editor/V12.Editor.csproj"; PackageId = "Finite.V12.Editor" }
    @{ Path = "V12.Pak/V12.Pak.csproj"; PackageId = "Finite.V12.Pak" }
    @{ Path = "Nova/Nova.Shared/Nova.Shared.csproj"; PackageId = "Finite.V12.Renderers.Nova.Shared" }
    @{ Path = "V12-Sdk/V12.Sdk.csproj"; PackageId = "Finite.V12.Sdk" }
    # Library-only projects (for example renderer hosts) are not packed as NuGet.
)

# Also pack Locus if present (Finite.V12.Locus)
if (Test-Path "Locus/Locus.Renderer/Locus.Renderer.csproj") {
    $projects += @{ Path = "Locus/Locus.Renderer/Locus.Renderer.csproj"; PackageId = "Finite.V12.Locus" }
}

$verbosity = if ($Verbose) { "minimal" } else { "quiet" }
$buildArgs = if ($NoBuild) { "--no-build" } else { "" }

$packed = @()
foreach ($p in $projects) {
    $proj = Join-Path $root $p.Path
    if (-not (Test-Path $proj)) {
        Write-Host "skip missing $($p.Path)" -ForegroundColor Yellow
        continue
    }
    Write-Host "`npacking $($p.PackageId) ($($p.Path))..." -ForegroundColor Green
    # Pass an explicit version so the root version wins over a csproj default.
    $cmd = "dotnet pack `"$proj`" -c Release -o `"$purrPackages`" -p:Version=$Version -p:GeneratePackageOnBuild=false -p:DebugSymbols=true -p:DebugType=portable --nologo -v $verbosity $buildArgs"
    $cmdbuild = "dotnet build `"$proj`" -c Release -p:Version=$Version -p:DefineConstants=`"TRACY`" -p:GeneratePackageOnBuild=false --nologo -v $verbosity $buildArgs "
    Write-Host "  $cmdbuild" -ForegroundColor DarkGray
    Write-Host "  $cmd" -ForegroundColor DarkGray
    Invoke-Expression $cmdbuild
    Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0) { throw "pack failed for $($p.Path)" }

    # Mirror to the legacy feed, when present, for consumers still reading that location.
    if ($purrPackages -ne $legacyPackages -and (Test-Path $legacyPackages)) {
        $nupkg = Get-ChildItem $purrPackages -Filter "$($p.PackageId).$Version.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($nupkg) { Copy-Item $nupkg.FullName $legacyPackages -Force; Write-Host "  mirrored to $legacyPackages\$($nupkg.Name)" -ForegroundColor DarkGray }
    }
    $packed += $p.PackageId
}

# Optionally pack the SDK tool using its own version, not the engine version.
$sdkProj = Join-Path $root "V12-Sdk/V12.Sdk.csproj"
if (Test-Path $sdkProj) {
    Write-Host "`npacking V12.Sdk (tool)..." -ForegroundColor Green
    $sdkVer = (Select-String -Path $sdkProj -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value.Trim()
    if (-not $sdkVer) { $sdkVer = "0.1.0" }
    dotnet pack "$sdkProj" -c Release -o "$root/V12-Sdk/nupkg" --nologo -v $verbosity
    if ($LASTEXITCODE -ne 0) { Write-Host "  V12.Sdk pack failed (non-fatal)" -ForegroundColor Yellow } else { Write-Host "  V12.Sdk $sdkVer -> V12-Sdk/nupkg" -ForegroundColor DarkGray }
}

# Drop matching versions from the NuGet global cache so the next restore
# extracts the freshly built package even when the version is unchanged.
$nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"
$suffix = ".$Version.nupkg"
if (Test-Path $nugetCache) {
    foreach ($nupkg in Get-ChildItem $purrPackages -Filter "*.nupkg" -ErrorAction SilentlyContinue) {
        if (-not $nupkg.Name.EndsWith($suffix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $id = $nupkg.Name.Substring(0, $nupkg.Name.Length - $suffix.Length).ToLowerInvariant()
        $cacheDir = Join-Path $nugetCache "$id\$Version"
        if (Test-Path $cacheDir) {
            Remove-Item -Recurse -Force $cacheDir -ErrorAction SilentlyContinue
            Write-Host "  cleared nuget cache: $cacheDir" -ForegroundColor DarkGray
        }
    }
}

Write-Host "`nDone. Packed: $($packed -join ', ') v$Version" -ForegroundColor Cyan
Write-Host "Feed: $purrPackages" -ForegroundColor White
Get-ChildItem $purrPackages -Filter "*.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 10 | Format-Table Name, Length, LastWriteTime -AutoSize

Write-Host "`nNext:" -ForegroundColor Cyan
Write-Host "  vcsdk build <your project>  # will pick -p:RestoreAdditionalProjectSources=`"$purrPackages`"" 
Write-Host "  dotnet build <your project>   # after 'vcsdk build' creates nuget.config with purr feed, or add it manually"
