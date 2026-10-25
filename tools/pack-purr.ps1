param(
    [string]$Version = "",
    [string]$Output = "",
    [switch]$NoBuild,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

# Resolve version — prefer -Version, else Directory.Build.props V12Version, else 1.0.0
if (-not $Version) {
    $props = Get-Content "$root/Directory.Build.props" -Raw
    if ($props -match '<V12Version>([^<]+)</V12Version>') { $Version = $Matches[1].Trim() }
    else { $Version = "1.0.0" }
}

# Resolve Purr output — $PURR_HOME/packages wins, else ~/.purr/packages, else D:\V12Packages for compat
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
    @{ Path = "V12.Pak/V12.Pak.csproj"; PackageId = "Finite.V12.Pak" }
    # V12.Basic has no PackageId today — pack as Finite.V12.Basic if you add one, or keep as ProjectReference:
    # @{ Path = "V12.Basic/V12.Basic.csproj"; PackageId = "Finite.V12.Basic" },
    # V12.Monogame variants are not packed as NuGet by default (libraries, not engine core)
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
    # dotnet pack with explicit Version so root props V12Version is respected even where csproj hardcodes 1.0.0
    $cmd = "dotnet pack `"$proj`" -c Release -o `"$purrPackages`" -p:Version=$Version --nologo -v $verbosity $buildArgs"
    Write-Host "  $cmd" -ForegroundColor DarkGray
    Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0) { throw "pack failed for $($p.Path)" }

    # mirror to legacy D:\V12Packages so existing Scramble-Studio global source still works during transition
    if ($purrPackages -ne $legacyPackages -and (Test-Path $legacyPackages)) {
        $nupkg = Get-ChildItem $purrPackages -Filter "$($p.PackageId).$Version.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($nupkg) { Copy-Item $nupkg.FullName $legacyPackages -Force; Write-Host "  mirrored to $legacyPackages\$($nupkg.Name)" -ForegroundColor DarkGray }
    }
    $packed += $p.PackageId
}

# Optionally pack V12.Sdk tool (version 0.1.0 from its csproj, not V12Version)
$sdkProj = Join-Path $root "V12-Sdk/V12.Sdk.csproj"
if (Test-Path $sdkProj) {
    Write-Host "`npacking V12.Sdk (tool)..." -ForegroundColor Green
    $sdkVer = (Select-String -Path $sdkProj -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value.Trim()
    if (-not $sdkVer) { $sdkVer = "0.1.0" }
    dotnet pack "$sdkProj" -c Release -o "$root/V12-Sdk/nupkg" --nologo -v $verbosity
    if ($LASTEXITCODE -ne 0) { Write-Host "  V12.Sdk pack failed (non-fatal)" -ForegroundColor Yellow } else { Write-Host "  V12.Sdk $sdkVer -> V12-Sdk/nupkg" -ForegroundColor DarkGray }
}

Write-Host "`nDone. Packed: $($packed -join ', ') v$Version" -ForegroundColor Cyan
Write-Host "Feed: $purrPackages" -ForegroundColor White
Get-ChildItem $purrPackages -Filter "*.nupkg" | Sort-Object LastWriteTime -Descending | Select-Object -First 10 | Format-Table Name, Length, LastWriteTime -AutoSize

Write-Host "`nNext:" -ForegroundColor Cyan
Write-Host "  vcsdk build D:\git\Scramble-Studio    # will pick -p:RestoreAdditionalProjectSources=`"$purrPackages`"" 
Write-Host "  dotnet build D:\git\Scramble-Studio   # after 'vcsdk build' creates nuget.config with purr feed, or add it manually"
