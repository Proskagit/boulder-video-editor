#Requires -Version 7
<#
.SYNOPSIS
  Starts the Debug build of this checkout on a throw-away project for the remaining Phase 31 checks
  (docs/PHASE31_REMAINING_CHECKS.md): A2 look, C4, D1a, D3, D5.

.DESCRIPTION
  1. Creates a project with one clip in -Destination (default %TEMP%\aive-phase31-check) via
     New-Phase19MarkupFixture.ps1: the clip is COPIED there; the original is never touched.
  2. With -Offline, renames the copied clip so the project opens with offline media (D5).
  3. Builds src/App (Debug) and runs it with --open-project <Destination>.
  Run it again to start from a clean project (the folder is recreated each time).

.EXAMPLE
  pwsh -File tools\manual\Start-Phase31Check.ps1
.EXAMPLE
  pwsh -File tools\manual\Start-Phase31Check.ps1 -Offline
#>
param(
    [string]$Clip = (Join-Path $env:USERPROFILE 'Desktop\AiVideoEditor-E1-Reference\clips\12x10\1014(6).mp4'),
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase31-check'),
    [int]$Seconds = 10,
    [switch]$Offline,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$branch = (git -C $repo branch --show-current)
Write-Host "Checkout: $repo (branch $branch)"
git -C $repo merge-base --is-ancestor 077b4e3 HEAD 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "This checkout does not contain the B1-B3 fixes (077b4e3, merged with PR #38); update it from main."
}
if (-not (Test-Path -LiteralPath $Clip)) { throw "Clip not found: $Clip (pass -Clip <path to any climbing video>)." }

& (Join-Path $PSScriptRoot 'New-Phase19MarkupFixture.ps1') -Clip $Clip -Destination $Destination -Seconds $Seconds -Force
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'The fixture script failed.' }

if ($Offline) {
    $copy = Get-ChildItem (Join-Path $Destination 'media') -File | Where-Object Extension -ne '.offline' | Select-Object -First 1
    Rename-Item $copy.FullName ($copy.Name + '.offline')
    Write-Host "Offline: the project's COPY of the clip renamed to $($copy.Name).offline (the original untouched)."
}

$app = Join-Path $repo 'src\App\App.csproj'
if (-not $NoBuild) {
    dotnet build $app -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
Write-Host "Opening $Destination"
dotnet run --project $app -c Debug --no-build -- --open-project $Destination
