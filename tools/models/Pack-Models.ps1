<#
.SYNOPSIS
    Packs the AI models archive of Boulder Video Editor (S1, DECISIONS.md D066 §3).

.DESCRIPTION
    pwsh -File tools/models/Pack-Models.ps1 [-Source <models folder>] [-Version 1.0.0]
    Takes every file listed in models.json from -Source (default %LOCALAPPDATA%\BoulderVideoEditor\models), refuses any file whose
    size or SHA-256 differs, and writes artifacts/release/BoulderVideoEditor-<version>-models.zip:
      models\<folder>\<files>, models.json, Install-Models.cmd / .ps1, README.txt, THIRD-PARTY-NOTICES.txt, licenses\.
    The archive is stored without compression (ONNX weights do not compress) and its SHA-256 is printed.
    With -NoZip only the folder is written (D070: tools/distribution/New-Release.ps1 puts it into the Portable folder as
    ai-models and into the installer).
#>
[CmdletBinding()]
param(
    [string] $Source = (Join-Path $env:LOCALAPPDATA 'BoulderVideoEditor\models'),
    [string] $Version = '1.0.0',
    [string] $OutputRoot,
    [switch] $NoZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\release' }
$manifest = Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot 'models.json') | ConvertFrom-Json
$stage = Join-Path $OutputRoot "BoulderVideoEditor-$Version-models"
$zip = "$stage.zip"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
if (Test-Path $zip) { Remove-Item -Force $zip }
New-Item -ItemType Directory -Force (Join-Path $stage 'models') | Out-Null

foreach ($folder in $manifest.folders) {
    $dir = New-Item -ItemType Directory -Force (Join-Path $stage "models\$($folder.folder)")
    foreach ($file in $folder.files) {
        $path = Join-Path $Source (Join-Path $folder.folder $file.name)
        if (-not (Test-Path -LiteralPath $path)) { throw "Missing: $path" }
        $length = (Get-Item -LiteralPath $path).Length
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($length -ne $file.length -or $hash -ne $file.sha256) { throw "Not the pinned file: $path ($length bytes, $hash)" }
        Copy-Item -LiteralPath $path -Destination $dir.FullName
        Write-Host "  $($folder.folder)\$($file.name)"
    }
}
foreach ($f in 'models.json', 'Install-Models.ps1', 'Install-Models.cmd', 'THIRD-PARTY-NOTICES.txt', 'README.txt') {
    Copy-Item (Join-Path $PSScriptRoot $f) $stage
}
Copy-Item -Recurse (Join-Path $PSScriptRoot 'licenses') (Join-Path $stage 'licenses')

if ($NoZip) {
    Write-Host "Models package: $stage"
    return
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::NoCompression, $false)
$size = (Get-Item $zip).Length
Write-Host ("{0}: {1:N0} MB, SHA-256 {2}" -f $zip, ($size / 1MB), (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant())
