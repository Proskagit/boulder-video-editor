<#
.SYNOPSIS
    Builds the two distributions of Boulder Video Editor for Windows x64 (DECISIONS.md D070): the Setup installer and the
    single Portable archive — from one commit, with one command.

.DESCRIPTION
    pwsh -File tools/distribution/New-Release.ps1 -Version 1.3.0 -FfmpegPackage <unpacked aive-ffmpeg-minimal-*-win64>
        [-ModelsSource <models folder>] [-FfmpegSourcesTar <aive-ffmpeg-minimal-*-sources.tar>] [-OutputRoot <folder>]

    1. tools/portable/Publish-Portable.ps1: the application, self-contained win-x64 (.NET included), with the release's minimal
       FFmpeg in ffmpeg\ (appsettings.json points at it), the notices and licences.
    2. tools/models/Pack-Models.ps1 -NoZip: the models package (every file checked by its size and SHA-256 against models.json).
    3. Portable: the application + the models package as ai-models\ in one folder, packed into
       BoulderVideoEditor-<version>-win-x64-portable.zip (the models stored, the rest compressed). At its first start the
       application offers to install the models from ai-models\ (D070 §3).
    4. Setup: tools/installer/BoulderVideoEditor.iss compiled by Inno Setup 6 (ISCC.exe) into
       BoulderVideoEditor-<version>-setup.exe: the application and the models.
    5. The FFmpeg sources tar (GPL: the corresponding source next to the binaries) copied when given; SHA256SUMS.

    The result: artifacts/release/<version>/ (the work folders under artifacts/release/<version>/stage).
    Inno Setup is found by -Iscc, %ISCC%, PATH, %LOCALAPPDATA%\Programs\Inno Setup 6 or %ProgramFiles(x86)%\Inno Setup 6
    (winget install JRSoftware.InnoSetup).

.PARAMETER SkipSetup
    Builds the Portable archive only (no Inno Setup needed).
.PARAMETER SkipPortableZip
    Leaves the Portable folder unpacked (a quicker local check).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [string] $FfmpegPackage,
    [string] $ModelsSource = (Join-Path $env:LOCALAPPDATA 'BoulderVideoEditor\models'),
    [string] $FfmpegSourcesTar,
    [string] $OutputRoot,
    [string] $Iscc,
    [switch] $SkipSetup,
    [switch] $SkipPortableZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo "artifacts\release\$Version" }
$stage = Join-Path $OutputRoot 'stage'
$rid = 'win-x64'

function Find-Iscc {
    if ($Iscc) { if (Test-Path $Iscc) { return (Resolve-Path $Iscc).Path }; throw "-Iscc: $Iscc not found" }
    if ($env:ISCC -and (Test-Path $env:ISCC)) { return $env:ISCC }
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    throw 'Inno Setup 6 (ISCC.exe) not found: winget install JRSoftware.InnoSetup, or give -Iscc / %ISCC%, or -SkipSetup.'
}

function Invoke-Script([string] $script, [hashtable] $arguments) {
    & $script @arguments
    if (-not $?) { throw "$script failed" }
}

$isccPath = if ($SkipSetup) { $null } else { Find-Iscc }
if ($FfmpegSourcesTar -and -not (Test-Path $FfmpegSourcesTar)) { throw "-FfmpegSourcesTar: $FfmpegSourcesTar not found" }

if (Test-Path $OutputRoot) {
    # Only an output folder this script wrote is replaced (its stage\ marks it); any other non-empty folder is refused.
    $isOurs = Test-Path (Join-Path $OutputRoot 'stage')
    $isEmpty = -not (Get-ChildItem -Force $OutputRoot | Select-Object -First 1)
    if (-not $isOurs -and -not $isEmpty) { throw "-OutputRoot $OutputRoot is not empty and was not written by New-Release.ps1 - choose another folder." }
    Remove-Item -Recurse -Force $OutputRoot
}
New-Item -ItemType Directory -Force $stage | Out-Null

# --- 1. The application -------------------------------------------------------------------------------------------
Invoke-Script (Join-Path $repo 'tools\portable\Publish-Portable.ps1') @{ Version = $Version; OutputRoot = $stage; FfmpegPackage = $FfmpegPackage }
$app = Join-Path $stage "BoulderVideoEditor-$Version-$rid"
if (-not (Test-Path (Join-Path $app 'BoulderVideoEditor.exe'))) { throw "The published application is missing: $app" }

# --- 2. The models package ----------------------------------------------------------------------------------------
Invoke-Script (Join-Path $repo 'tools\models\Pack-Models.ps1') @{ Version = $Version; Source = $ModelsSource; OutputRoot = $stage; NoZip = $true }
$models = Join-Path $stage "BoulderVideoEditor-$Version-models"
$manifest = Get-Content -Raw -Encoding utf8 (Join-Path $models 'models.json') | ConvertFrom-Json
$modelsBytes = 0
foreach ($folder in $manifest.folders) { foreach ($file in $folder.files) { $modelsBytes += $file.length } }

$files = Join-Path $PSScriptRoot 'files'
function Write-Template([string] $template, [string] $destination) {
    $text = (Get-Content -Raw -Encoding UTF8 (Join-Path $files $template)).Replace('{{VERSION}}', $Version)
    Set-Content -Path $destination -Value $text -Encoding UTF8 -NoNewline
}
function Set-BuildInfoModels([string] $folder, [string] $line) {
    $path = Join-Path $folder 'BUILD-INFO.txt'
    $info = @(Get-Content -Encoding UTF8 $path | Where-Object { $_ -notlike 'AI models:*' }) + $line
    Set-Content -Path $path -Value $info -Encoding UTF8
}

# --- 3. Portable --------------------------------------------------------------------------------------------------
$portableName = "BoulderVideoEditor-$Version-$rid-portable"
$portable = Join-Path $OutputRoot $portableName
Copy-Item -Recurse $app $portable
Copy-Item -Recurse $models (Join-Path $portable 'ai-models')
Write-Template 'README.portable.txt' (Join-Path $portable 'README.txt')
Set-BuildInfoModels $portable "AI models: in ai-models\ ($([math]::Round($modelsBytes / 1MB)) MiB, models.json $($manifest.version)); installed into %LOCALAPPDATA%\BoulderVideoEditor\models at the first start on the user's consent."
# The application finds the package by ai-models\models.json next to BoulderVideoEditor.exe (ModelPackageInstaller.FindPackage).
if (-not (Test-Path (Join-Path $portable 'ai-models\models.json'))) { throw 'ai-models\models.json missing in the Portable folder' }

$portableZip = "$portable.zip"
if (-not $SkipPortableZip) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [System.IO.File]::Open($portableZip, [System.IO.FileMode]::CreateNew)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            $root = (Resolve-Path $portable).Path.TrimEnd('\')
            foreach ($file in Get-ChildItem -Recurse -File $root) {
                $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
                $level = if ($relative -like 'ai-models/models/*') { [System.IO.Compression.CompressionLevel]::NoCompression }
                         else { [System.IO.Compression.CompressionLevel]::Optimal }
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "$portableName/$relative", $level) | Out-Null
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    Write-Host ("Portable: {0} ({1:N0} MiB)" -f $portableZip, ((Get-Item $portableZip).Length / 1MB))
}

# --- 4. Setup -----------------------------------------------------------------------------------------------------
$setup = Join-Path $OutputRoot "BoulderVideoEditor-$Version-setup.exe"
if (-not $SkipSetup) {
    # The installer's program folder: the application with the Setup README; no ai-models\ (the installer places the models).
    $setupApp = Join-Path $stage 'setup-app'
    Copy-Item -Recurse $app $setupApp
    Write-Template 'README.setup.txt' (Join-Path $setupApp 'README.txt')
    Set-BuildInfoModels $setupApp "AI models: installed by the installer into %LOCALAPPDATA%\BoulderVideoEditor\models ($([math]::Round($modelsBytes / 1MB)) MiB, models.json $($manifest.version)); their notices in models-licenses\."

    # The [Files] lines of the models (each checked by its SHA-256 before it is copied) and the folders the uninstaller may remove.
    $includePath = Join-Path $stage 'models.generated.iss'
    & (Join-Path $repo 'tools\installer\New-ModelsInclude.ps1') -ModelsDir $models -OutFile $includePath

    & $isccPath '/Qp' "/DAppVersion=$Version" "/DAppDir=$setupApp" "/DModelsDir=$models" "/DModelsInclude=$includePath" `
        "/DOutputDir=$OutputRoot" "/DRepoRoot=$repo" (Join-Path $repo 'tools\installer\BoulderVideoEditor.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
    Write-Host ("Setup: {0} ({1:N0} MiB)" -f $setup, ((Get-Item $setup).Length / 1MB))
}

# --- 5. The FFmpeg sources and the checksums ----------------------------------------------------------------------
if ($FfmpegSourcesTar) { Copy-Item $FfmpegSourcesTar $OutputRoot }
$assets = @(Get-ChildItem -File $OutputRoot | Where-Object { $_.Name -ne 'SHA256SUMS' } | Sort-Object Name)
$sums = foreach ($asset in $assets) { "$((Get-FileHash -Algorithm SHA256 $asset.FullName).Hash.ToLowerInvariant()) *$($asset.Name)" }
Set-Content -Path (Join-Path $OutputRoot 'SHA256SUMS') -Value $sums -Encoding ascii
Write-Host "`nRelease $Version in $OutputRoot"
$sums | ForEach-Object { Write-Host "  $_" }
