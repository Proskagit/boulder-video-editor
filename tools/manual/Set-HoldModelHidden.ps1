#Requires -Version 7
<#
.SYNOPSIS
  Hides the installed SAM 2.1 model from the app for check D4 ("model missing"), and puts it back.

.DESCRIPTION
  -Hide renames %LOCALAPPDATA%\AiVideoEditor\models\sam2.1-large to sam2.1-large.hidden-for-D4 (nothing is deleted or
  copied). -Restore renames it back. -Status prints where the model is. Close the app before -Hide / -Restore.

.EXAMPLE
  pwsh -File tools\manual\Set-HoldModelHidden.ps1 -Hide
  pwsh -File tools\manual\Set-HoldModelHidden.ps1 -Restore
#>
param([switch]$Hide, [switch]$Restore, [switch]$Status)

$ErrorActionPreference = 'Stop'
$models = Join-Path $env:LOCALAPPDATA 'AiVideoEditor\models'
$model = Join-Path $models 'sam2.1-large'
$hidden = Join-Path $models 'sam2.1-large.hidden-for-D4'

if (@($Hide, $Restore, $Status | Where-Object { $_ }).Count -ne 1) { throw 'Pass exactly one of -Hide, -Restore, -Status.' }
if (Get-Process -Name 'AiVideoEditor' -ErrorAction SilentlyContinue) {
    if (-not $Status) { throw 'Close the application first.' }
}

if ($Status) {
    if (Test-Path $model) { 'Model in place (normal).' }
    elseif (Test-Path $hidden) { 'Model HIDDEN — run -Restore after the check.' }
    else { "No model found in $models." }
    return
}
if ($Hide) {
    if (-not (Test-Path $model)) { throw "No model at $model." }
    if (Test-Path $hidden) { throw "$hidden already exists." }
    Rename-Item $model (Split-Path $hidden -Leaf)
    'Model hidden. Run with -Restore when D4 is done.'
}
if ($Restore) {
    if (-not (Test-Path $hidden)) { throw "Nothing to restore: $hidden not found." }
    if (Test-Path $model) { throw "$model already exists; nothing renamed." }
    Rename-Item $hidden (Split-Path $model -Leaf)
    'Model restored.'
}
