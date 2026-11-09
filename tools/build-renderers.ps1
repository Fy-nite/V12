<#
.SYNOPSIS
    Publish V12 renderers as self-contained single-file executables into Purr.

.DESCRIPTION
    Builds every renderer in the renderer table below with `dotnet publish`
    (PublishSingleFile=true), copies the result to
    $PURR_HOME/renderers/<id>/, and writes a renderer.json config alongside it.

    Add a renderer by appending an entry to $renderers. Entries may override
    any generated config field with their Config hashtable.

.PARAMETER Renderer
    Only build these renderer ids (default: all).

.PARAMETER RuntimeIdentifier
    .NET RID to publish for (default: the current machine's RID).

.PARAMETER FrameworkDependent
    Publish framework-dependent instead of self-contained.

.PARAMETER Output
    Renderers root (default: $PURR_HOME/renderers or ~/.purr/renderers).

.EXAMPLE
    pwsh tools/build-renderers.ps1

.EXAMPLE
    pwsh tools/build-renderers.ps1 -Renderer monogame -RuntimeIdentifier win-x64
#>
[CmdletBinding()]
param(
    [string[]]$Renderer = @(),
    [string]$Configuration = "Release",
    [Alias("rid")]
    [string]$RuntimeIdentifier = "",
    [string]$Version = "",
    [Alias("o")]
    [string]$Output = "",
    [switch]$FrameworkDependent,
    [switch]$KeepSymbols
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

if (-not $RuntimeIdentifier) {
    $ridLine = & dotnet --info | Select-String -Pattern '^\s*RID:\s*(\S+)' | Select-Object -First 1
    if ($ridLine -and $ridLine.Matches.Count -gt 0) {
        $RuntimeIdentifier = $ridLine.Matches[0].Groups[1].Value
    } else {
        $RuntimeIdentifier = "win-x64"
    }
}

if (-not $Version) {
    $propsPath = Join-Path $root "Directory.Build.props"
    if (Test-Path $propsPath) {
        $props = Get-Content $propsPath -Raw
        if ($props -match '<V12Version>([^<]+)</V12Version>') { $Version = $Matches[1].Trim() }
    }
    if (-not $Version) { $Version = "1.0.0" }
}

$purrHome = $env:PURR_HOME
if (-not $purrHome) {
    if ($env:HOME) { $purrHome = Join-Path $env:HOME ".purr" }
    else { $purrHome = Join-Path $env:USERPROFILE ".purr" }
}
$renderersDir = if ($Output) { $Output } else { Join-Path $purrHome "renderers" }
New-Item -ItemType Directory -Path $renderersDir -Force | Out-Null

# Renderer table. Add new renderers here as sibling projects come online.
$renderers = @(
    @{
        Id      = "monogame"
        Name    = "V12 MonoGame"
        Kind    = "monogame"
        Project = "V12.Monogame/Pulsar.GL/Pulsar.GL.csproj"
        Config  = @{
            gamePakMode = "sibling"
        }
    }
    @{
        Id      = "rockstar"
        Name    = "V12 Rockstar (R3D)"
        Kind    = "rockstar"
        Project = "Rockstar/Rockstar.csproj"
        Config  = @{
            gamePakMode = "sibling"
        }
    }
)

$selected = $renderers
if ($Renderer.Count -gt 0) {
    $known = $renderers | ForEach-Object { $_.Id }
    $missing = $Renderer | Where-Object { $_ -notin $known }
    if ($missing) {
        throw "Unknown renderer(s): $($missing -join ', '). Known: $($known -join ', ')"
    }
    $selected = $renderers | Where-Object { $_.Id -in $Renderer }
}

$selfContained = -not $FrameworkDependent
$verbosity = if ($VerbosePreference -eq "SilentlyContinue") { "quiet" } else { "normal" }
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "v12-renderers"

Write-Host "V12 renderers -> v$Version ($RuntimeIdentifier)" -ForegroundColor Cyan
Write-Host "  purr: $renderersDir" -ForegroundColor DarkGray

$published = @()
foreach ($r in $selected) {
    $proj = Join-Path $root $r.Project
    if (-not (Test-Path $proj)) {
        Write-Host "skip missing $($r.Project)" -ForegroundColor Yellow
        continue
    }

    if ($r.Executable) {
        $exeName = $r.Executable
    } else {
        $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($proj)
        $exeName = if ($RuntimeIdentifier -like "win-*") { "$assemblyName.exe" } else { $assemblyName }
    }

    $stage = Join-Path $stagingRoot $r.Id
    $dest = Join-Path $renderersDir $r.Id
    if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    Write-Host "`npublishing $($r.Name) ($($r.Project))..." -ForegroundColor Green

    $publishArgs = @(
        "publish", $proj,
        "-c", $Configuration,
        "-r", $RuntimeIdentifier,
        "-o", $stage,
        "--self-contained", $selfContained.ToString().ToLowerInvariant(),
        "-p:Version=$Version",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:PublishTrimmed=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-p:AllowedReferenceRelatedFileExtensions=none",
        "-v", $verbosity,
        "--nologo"
    )

    try {
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($r.Project) (exit $LASTEXITCODE)" }

        $builtExe = Join-Path $stage $exeName
        if (-not (Test-Path $builtExe)) {
            throw "expected executable not found: $builtExe"
        }

        if (-not $KeepSymbols) {
            Get-ChildItem $stage -Filter "*.pdb" -File | Remove-Item -Force
        }

        if (Test-Path $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        Copy-Item -Path (Join-Path $stage "*") -Destination $dest -Recurse -Force

        $config = [ordered]@{
            schemaVersion     = 1
            id                = $r.Id
            name              = $r.Name
            version           = $Version
            kind              = $r.Kind
            distribution      = "dotnet-singlefile"
            singleFile        = $true
            runtimeIdentifier = $RuntimeIdentifier
            selfContained     = $selfContained
            executable        = $exeName
            arguments         = @()
            sourceProject     = $r.Project
            publishedAtUtc    = (Get-Date).ToUniversalTime().ToString("o")
        }
        foreach ($key in $r.Config.Keys) { $config[$key] = $r.Config[$key] }

        $configPath = Join-Path $dest "renderer.json"
        $json = $config | ConvertTo-Json -Depth 10
        [System.IO.File]::WriteAllText($configPath, $json, (New-Object System.Text.UTF8Encoding($false)))

        Write-Host "  -> $dest" -ForegroundColor DarkGray
        Write-Host "  -> $configPath" -ForegroundColor DarkGray
        $published += $r.Id
    } finally {
        if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($published.Count -eq 0) {
    throw "No renderers were published."
}

Write-Host "`nDone. Published: $($published -join ', ')" -ForegroundColor Cyan
Write-Host "Renderer root: $renderersDir" -ForegroundColor White
Get-ChildItem $renderersDir -Directory | ForEach-Object {
    $configPath = Join-Path $_.FullName "renderer.json"
    if (Test-Path $configPath) { Write-Host "  $($_.Name): $configPath" -ForegroundColor DarkGray }
}
