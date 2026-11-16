#Requires -Version 7
<#
.SYNOPSIS
  Builds the portable target build of Phase 41 (D060) from this checkout and starts it for the manual check
  (docs/PHASE41_MANUAL_TEST_PLAN.md).

.DESCRIPTION
  1. Checks the checkout: the branch, that it contains Phase 41 (D060), uncommitted changes (the build is then marked .dirty),
     ffmpeg / ffprobe on PATH (the portable folder has no FFmpeg, D046 par. 2).
  2. Optionally (-Tests) runs the automated suites in Release first (about 5 minutes).
  3. Publishes artifacts\portable\AiVideoEditor-<Version>-win-x64 with tools\portable\Publish-Portable.ps1.
  4. Optionally (-Project) copies a project folder (project.json and everything beside it except media — media stay where
     the project points) to -Destination, so the check never saves the original. Open that copy in the app with «Открыть».
  5. Opens the manual test plan and starts AiVideoEditor.exe.
  Run it again to rebuild and to start from a fresh copy of the project.

.EXAMPLE
  pwsh -File tools\manual\Start-Phase41Check.ps1
.EXAMPLE
  pwsh -File tools\manual\Start-Phase41Check.ps1 -Tests -Project 'D:\Climbing\Session 2026-11-12'
#>
param(
    [string]$Version = '0.41.0',
    [string]$Project,
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase41-check'),
    [switch]$Tests,
    [switch]$Zip,
    [switch]$NoBuild,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# --- 1. The checkout ------------------------------------------------------------------------------------------------
$branch = (git -C $repo branch --show-current)
$commit = (git -C $repo rev-parse --short HEAD)
Write-Host "Checkout: $repo (branch $branch, $commit)"
if (-not (Select-String -Path (Join-Path $repo 'DECISIONS.md') -Pattern '^## D060' -Quiet)) {
    throw 'This checkout does not contain Phase 41 (no D060 in DECISIONS.md). Use the feat/phase-41-export-ux branch or main after PR #67.'
}
if (git -C $repo status --porcelain --untracked-files=no) {
    Write-Warning 'Uncommitted changes: the build will be marked .dirty.'
}
foreach ($tool in 'ffmpeg', 'ffprobe') {
    $found = Get-Command $tool -ErrorAction SilentlyContinue
    if (-not $found) { throw "$tool is not on PATH: the portable build has no FFmpeg (D046 par. 2). Install it or add it to PATH." }
    Write-Host "$tool`: $($found.Source)"
}

# --- 2. The automated suites (optional) ---------------------------------------------------------------------------
if ($Tests) {
    Write-Host 'Running the test suites (Release)…'
    dotnet test (Join-Path $repo 'AiVideoEditor.sln') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed — see above. The build is not published.' }
}

# --- 3. The portable build ------------------------------------------------------------------------------------------
$folder = Join-Path $repo "artifacts\portable\AiVideoEditor-$Version-win-x64"
if (-not $NoBuild) {
    $publish = @{ Version = $Version }
    if ($Zip) { $publish.Zip = $true }
    & (Join-Path $repo 'tools\portable\Publish-Portable.ps1') @publish
}
$exe = Join-Path $folder 'AiVideoEditor.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Not built: $exe" }
Write-Host "Build: $folder"

# --- 4. A copy of the project (optional) --------------------------------------------------------------------------
if ($Project) {
    if (-not (Test-Path -LiteralPath (Join-Path $Project 'project.json'))) { throw "No project.json in $Project" }
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    # The project file and its small companions; large media are referenced by absolute paths and stay where they are.
    Get-ChildItem -LiteralPath $Project -Force | Where-Object { $_.PSIsContainer -or $_.Length -lt 50MB } |
        Copy-Item -Destination $Destination -Recurse -Force
    Write-Host "Project copy: $Destination  (open it in the app with «Открыть»; the original is not touched)"
}

# --- 5. Start -------------------------------------------------------------------------------------------------------
$plan = Join-Path $repo 'docs\PHASE41_MANUAL_TEST_PLAN.md'
Write-Host "Manual plan: $plan"
if (-not $NoStart) {
    Start-Process $plan
    Start-Process $exe -WorkingDirectory $folder
}
