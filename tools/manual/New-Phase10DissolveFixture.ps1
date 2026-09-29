<#
.SYNOPSIS
  Creates the manual-test project for the Phase 10 dissolve scenarios (docs/PHASE10_MANUAL_TEST_PLAN.md, 13–20).

.DESCRIPTION
  Generates small media with ffmpeg and a project.json (format v3, D025) in -Destination (default:
  %TEMP%\aive-phase10-dissolves), outside the repository. No dissolve is prepared: they are added in the app. The media
  carry no saved metadata: the app analyses them when the project opens. Open it in a Debug build without the picker:

    dotnet run --project src/App/App.csproj -- --open-project "<Destination>"

  Timeline (25 fps, 640 × 360; seconds = frame / 25), all on V1:
    0–4 s | 4–8 s     "pattern" (source 0–4 s, 440 Hz) | "bars" (source 1–5 s, 880 Hz): both trimmed — handles on
                      both sides (1 s before "bars": enough at 1× and 2×, not at 4×)                                           → scenarios 13, 16, 17, 18
    9–11 s | 11–13 s  "short" twice, each the whole 2 s file: no handles    → scenario 14
    14–18 s | 18–22 s | 22–26 s  "pattern" (source 2–6 s) | image | text "TITLE"  → scenario 15
.PARAMETER Destination
  The project folder to create; must not exist unless -Force.
.PARAMETER Force
  Replace an existing folder at -Destination (only one this script created: it must contain .aive-fixture).
#>
param(
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase10-dissolves'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Destination = [System.IO.Path]::GetFullPath($Destination)

if (Test-Path $Destination) {
    if (-not $Force) { throw "$Destination exists. Use -Force to replace it (only a folder this script created)." }
    if (-not (Test-Path (Join-Path $Destination '.aive-fixture'))) { throw "$Destination was not created by this script; not replaced." }
    Remove-Item -Recurse -Force $Destination
}
$media = Join-Path $Destination 'media'
New-Item -ItemType Directory -Force $media | Out-Null
Set-Content (Join-Path $Destination '.aive-fixture') 'Created by tools/manual/New-Phase10DissolveFixture.ps1'

$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
function Invoke-Ffmpeg([string[]]$arguments) {
    & $ffmpeg -v error -y @arguments
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $($arguments -join ' ')" }
}

# --- media ----------------------------------------------------------------------------------------------------------
$pattern = Join-Path $media 'pattern.mp4'
$bars = Join-Path $media 'bars.mp4'
$short = Join-Path $media 'short.mp4'
$still = Join-Path $media 'still.png'
$encode = @('-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '128k', '-shortest')
Invoke-Ffmpeg (@('-f', 'lavfi', '-i', 'testsrc2=s=640x360:r=25:d=12', '-f', 'lavfi', '-i', 'sine=f=440:r=48000:d=12') + $encode + @($pattern))
Invoke-Ffmpeg (@('-f', 'lavfi', '-i', 'smptebars=s=640x360:r=25:d=12', '-f', 'lavfi', '-i', 'sine=f=880:r=48000:d=12') + $encode + @($bars))
Invoke-Ffmpeg (@('-f', 'lavfi', '-i', 'testsrc=s=640x360:r=25:d=2', '-f', 'lavfi', '-i', 'sine=f=660:r=48000:d=2') + $encode + @($short))
Invoke-Ffmpeg @('-f', 'lavfi', '-i', 'mandelbrot=s=480x270', '-frames:v', '1', $still)

# --- project.json (format v3) ------------------------------------------------------------------------------------------
$frame = 400000                    # ticks per frame at 25 fps
function T([long]$frames) { $frames * $frame }
$now = [DateTimeOffset]::Now.ToString('o')

function Asset([string]$path, [string]$kind) {
    [ordered]@{
        id = [guid]::NewGuid(); filePath = $path; relativePath = "media\$(Split-Path $path -Leaf)"
        fileSizeBytes = (Get-Item $path).Length; kind = $kind; importedAt = $now; thumbnailPath = $null; metadata = $null
    }
}
$assets = [ordered]@{ pattern = Asset $pattern 'Video'; bars = Asset $bars 'Video'; short = Asset $short 'Video'; still = Asset $still 'Image' }

$crop = [ordered]@{ left = 0; top = 0; right = 0; bottom = 0 }
function Clip([string]$type, [string]$asset, [long]$start, [long]$end, [long]$sourceIn = 0) {
    $frames = $end - $start
    $c = [ordered]@{
        type = $type; id = [guid]::NewGuid(); timelineStartTicks = T $start; durationTicks = T $frames
        fadeInTicks = 0; fadeOutTicks = 0; effects = @()
        mediaAssetId = $assets[$asset].id; sourceInTicks = T $sourceIn; sourceOutTicks = T ($sourceIn + $frames)
        speedRatio = [ordered]@{ numerator = 1; denominator = 1 }
        positionX = 0; positionY = 0; scale = 1; rotationDegrees = 0; opacity = 1; crop = $crop
    }
    if ($type -eq 'video') { $c.volume = 1; $c.isMuted = $false }
    $c
}
$text = [ordered]@{
    type = 'text'; id = [guid]::NewGuid(); timelineStartTicks = T 550; durationTicks = T 100
    fadeInTicks = 0; fadeOutTicks = 0; effects = @()
    text = 'TITLE'; fontFamily = 'Segoe UI'; fontSize = 96; colorHex = '#FFFFFF'; alignment = 'Center'
    positionX = 0; positionY = 0; scale = 1; rotationDegrees = 0; opacity = 1
}

$v1 = @(
    (Clip 'video' 'pattern' 0 100),
    (Clip 'video' 'bars' 100 200 -sourceIn 25),
    (Clip 'video' 'short' 225 275),
    (Clip 'video' 'short' 275 325),
    (Clip 'video' 'pattern' 350 450 -sourceIn 50),
    (Clip 'image' 'still' 450 550),
    $text
)

function Track([string]$name, [int]$order, $clips) {
    [ordered]@{ id = [guid]::NewGuid(); name = $name; order = $order; isMuted = $false; isHidden = $false; isLocked = $false
                clips = @($clips); transitions = @() }
}
$project = [ordered]@{
    format = 'AiVideoEditor.Project'; formatVersion = 3; id = [guid]::NewGuid(); name = 'Phase 10 dissolves'
    createdAt = $now; modifiedAt = $now
    settings = [ordered]@{ frameWidth = 640; frameHeight = 360; frameRate = [ordered]@{ numerator = 25; denominator = 1 }
                           isFrameRateLocked = $true; audioSampleRate = 48000 }
    mediaAssets = @($assets.Values)
    timeline = [ordered]@{
        id = [guid]::NewGuid(); name = 'Main Sequence'
        videoTracks = @((Track 'V1' 0 $v1), (Track 'V2' 1 @()))
        audioTracks = @((Track 'A1' 0 @()))
        markers = @(); playheadTicks = 0; zoomPixelsPerSecond = 40; snappingEnabled = $true
    }
}
$project | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8NoBOM (Join-Path $Destination 'project.json')

Write-Host "Fixture project: $Destination"
Write-Host "Open it (Debug build):  dotnet run --project src/App/App.csproj -- --open-project `"$Destination`""
