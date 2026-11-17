<#
.SYNOPSIS
    Installs the AI models of AiVideoEditor into %LOCALAPPDATA%\AiVideoEditor\models (S1, DECISIONS.md D066 §3).

.DESCRIPTION
    Run from the unpacked models archive (Install-Models.cmd does it with a double click). Every file listed in models.json
    is checked by its size and SHA-256 before and after copying; a file already installed with the right content is kept.
    Nothing is downloaded; nothing outside %LOCALAPPDATA%\AiVideoEditor\models is written; other folders there are left
    as they are. Exit code 0 when every model is in place, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string] $Target = (Join-Path $env:LOCALAPPDATA 'AiVideoEditor\models')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$manifest = Get-Content -Raw -Encoding utf8 (Join-Path $here 'models.json') | ConvertFrom-Json

function Test-ModelFile([string] $path, $file) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $path).Length -ne $file.length) { return $false }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() -eq $file.sha256
}

$failed = 0
foreach ($folder in $manifest.folders) {
    $sourceDir = Join-Path $here (Join-Path 'models' $folder.folder)
    $targetDir = Join-Path $Target $folder.folder
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    foreach ($file in $folder.files) {
        $source = Join-Path $sourceDir $file.name
        $dest = Join-Path $targetDir $file.name
        if (Test-ModelFile $dest $file) {
            Write-Host "  ok        $($folder.folder)\$($file.name)"
            continue
        }
        if (-not (Test-ModelFile $source $file)) {
            Write-Host "  DAMAGED   $($folder.folder)\$($file.name) in the archive - unpack the archive again" -ForegroundColor Red
            $failed++
            continue
        }
        Copy-Item -LiteralPath $source -Destination $dest -Force
        if (Test-ModelFile $dest $file) {
            Write-Host "  installed $($folder.folder)\$($file.name)"
        } else {
            Write-Host "  FAILED    $($folder.folder)\$($file.name) - not enough disk space?" -ForegroundColor Red
            $failed++
        }
    }
}

if ($failed -eq 0) {
    Write-Host "`nThe models of AiVideoEditor $($manifest.version) are installed in $Target." -ForegroundColor Green
    exit 0
}
Write-Host "`n$failed file(s) not installed." -ForegroundColor Red
exit 1
