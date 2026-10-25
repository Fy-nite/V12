param(
    [string]$Ref = "main"
)
# Pull all submodules to latest remote main/master — the "auto-update" workflow for the umbrella.
# In the monorepo, libs are submodules pointing at main; this advances them.
# Run from V12-Monorepo root or via `pwsh tools/sync-purr.ps1`.

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root

Write-Host "Syncing submodules to origin/$Ref..." -ForegroundColor Cyan
git submodule sync | Out-Null
git submodule update --init --recursive
# advance each submodule to its remote tip
git submodule foreach --recursive "git fetch origin && git checkout $Ref 2>nul || git checkout master 2>nul || git checkout main 2>nul; git pull --ff-only; echo `$(pwd): `$(git rev-parse --short HEAD)"

Write-Host "`nStatus after sync:" -ForegroundColor Cyan
git submodule status
Write-Host "`nIf you want to commit the pointer bump:" -ForegroundColor Yellow
Write-Host "  git add .gitmodules V12-Engine V12.Basic V12.Pak V12.Monogame V12-Sdk Nova VNode"
Write-Host "  git commit -m 'chore: bump submodules to latest $Ref'"
