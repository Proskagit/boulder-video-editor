<#
.SYNOPSIS
  Creates the manual-test project for the Phase 10 fade scenarios (docs/PHASE10_MANUAL_TEST_PLAN.md, 4–12b).

.DESCRIPTION
  Generates small media with ffmpeg and a project.json (format v3, D025) in -Destination (default:
  %TEMP%\aive-phase10-fades), outside the repository. The media carry no saved metadata: the app analyses them when the
  project opens. Open it in a Debug build without the folder picker:

    dotnet run --project src/App/App.csproj -- --open-project "<Destination>"

  Timeline (25 fps, 640 × 360; seconds = frame / 25):
    0–4 s     V1 "pattern" video with sound, no fades                    → scenario 4 (set the fades yourself)
    5–9 s     V1 red video; V2 pattern video, fades 25 / 25;
              A1 tone, fades 25 / 25                                     → scenario 5 (and the sound of 4)
    10–14 s   V1 image, fades 15 / 15; V2 text "FADE", fades 10 / 20      → scenario 6
    15–17 s   V1 pattern at 2× (50 frames), no fades                      → scenario 7
    18–22 s   V1 pattern at 0.5× (100 frames), no fades                   → scenario 7
    23–23.8 s V1 pattern, 20 frames, no fades                            → scenario 8
    25–29 s   V1 pattern, fades 40 / 40                                   → scenario 9
    30–34 s   V1 P | Q touching, a 10-frame dissolve on the cut (not rendered before Step 10.7),
              P fades 10 / 20, Q fades 15 / 10                            → the PO-8 note; don't edit P or Q
.PARAMETER Destination
  The project folder to create; must not exist unless -Force.
.PARAMETER Force
  Replace an existing folder at -Destination (only one this script created: it must contain .aive-fixture).
#>
param(
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase10-fades'),
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
Set-Content (Join-Path $Destination '.aive-fixture') 'Created by tools/manual/New-Phase10FadeFixture.ps1'

$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
function Invoke-Ffmpeg([string[]]$arguments) {
    & $ffmpeg -v error -y @arguments
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $($arguments -join ' ')" }
}

# --- media ----------------------------------------------------------------------------------------------------------
$pattern = Join-Path $media 'pattern.mp4'
$red = Join-Path $media 'red.mp4'
$still = Join-Path $media 'still.png'
$tone = Join-Path $media 'tone.wav'
Invoke-Ffmpeg @('-f', 'lavfi', '-i', 'testsrc2=s=640x360:r=25:d=8', '-f', 'lavfi', '-i', 'sine=f=440:r=48000:d=8',
    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '128k', '-shortest', $pattern)
Invoke-Ffmpeg @('-f', 'lavfi', '-i', 'color=c=0xC83232:s=640x360:r=25:d=8',
    '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18', '-pix_fmt', 'yuv420p', $red)
Invoke-Ffmpeg @('-f', 'lavfi', '-i', 'testsrc=s=480x270', '-frames:v', '1', $still)
Invoke-Ffmpeg @('-f', 'lavfi', '-i', 'sine=f=660:r=48000:d=8', '-af', 'volume=0.5', '-ac', '2', '-c:a', 'pcm_s16le', $tone)

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
$assets = [ordered]@{ pattern = Asset $pattern 'Video'; red = Asset $red 'Video'; still = Asset $still 'Image'; tone = Asset $tone 'Audio' }

$crop = [ordered]@{ left = 0; top = 0; right = 0; bottom = 0 }
function Clip([string]$type, [string]$asset, [long]$start, [long]$end, [long]$sourceIn = 0, [long]$num = 1, [long]$den = 1,
              [long]$fadeIn = 0, [long]$fadeOut = 0) {
    $frames = $end - $start
    $c = [ordered]@{
        type = $type; id = [guid]::NewGuid(); timelineStartTicks = T $start; durationTicks = T $frames
        fadeInTicks = T $fadeIn; fadeOutTicks = T $fadeOut; effects = @()
        mediaAssetId = $assets[$asset].id; sourceInTicks = T $sourceIn
        sourceOutTicks = (T $sourceIn) + [long]($frames * $frame * $num / $den)
        speedRatio = [ordered]@{ numerator = $num; denominator = $den }
    }
    if ($type -ne 'audio') { foreach ($k in 'positionX', 'positionY', 'rotationDegrees') { $c[$k] = 0 }; $c.scale = 1; $c.opacity = 1; $c.crop = $crop }
    if ($type -ne 'image') { $c.volume = 1; $c.isMuted = $false }
    $c
}
function Text([string]$text, [long]$start, [long]$end, [long]$fadeIn, [long]$fadeOut) {
    [ordered]@{
        type = 'text'; id = [guid]::NewGuid(); timelineStartTicks = T $start; durationTicks = T ($end - $start)
        fadeInTicks = T $fadeIn; fadeOutTicks = T $fadeOut; effects = @()
        text = $text; fontFamily = 'Segoe UI'; fontSize = 96; colorHex = '#FFFFFF'; alignment = 'Center'
        positionX = 0; positionY = 0; scale = 1; rotationDegrees = 0; opacity = 1
    }
}

$p = Clip 'video' 'pattern' 750 800 -fadeIn 10 -fadeOut 20
$q = Clip 'video' 'pattern' 800 850 -sourceIn 60 -fadeIn 15 -fadeOut 10
$v1 = @(
    (Clip 'video' 'pattern' 0 100),
    (Clip 'video' 'red' 125 225),
    (Clip 'image' 'still' 250 350 -fadeIn 15 -fadeOut 15),
    (Clip 'video' 'pattern' 375 425 -num 2 -den 1),
    (Clip 'video' 'pattern' 450 550 -num 1 -den 2),
    (Clip 'video' 'pattern' 575 595),
    (Clip 'video' 'pattern' 625 725 -fadeIn 40 -fadeOut 40),
    $p, $q
)
$v2 = @((Clip 'video' 'pattern' 125 225 -fadeIn 25 -fadeOut 25), (Text 'FADE' 250 350 10 20))
$a1 = @((Clip 'audio' 'tone' 125 225 -fadeIn 25 -fadeOut 25))
$dissolve = [ordered]@{ id = [guid]::NewGuid(); transitionTypeId = 'crossDissolve'; durationTicks = T 10; leftClipId = $p.id; rightClipId = $q.id }

function Track([string]$name, [int]$order, $clips, $transitions) {
    [ordered]@{ id = [guid]::NewGuid(); name = $name; order = $order; isMuted = $false; isHidden = $false; isLocked = $false
                clips = @($clips); transitions = @($transitions) }
}
$project = [ordered]@{
    format = 'AiVideoEditor.Project'; formatVersion = 3; id = [guid]::NewGuid(); name = 'Phase 10 fades'
    createdAt = $now; modifiedAt = $now
    settings = [ordered]@{ frameWidth = 640; frameHeight = 360; frameRate = [ordered]@{ numerator = 25; denominator = 1 }
                           isFrameRateLocked = $true; audioSampleRate = 48000 }
    mediaAssets = @($assets.Values)
    timeline = [ordered]@{
        id = [guid]::NewGuid(); name = 'Main Sequence'
        videoTracks = @((Track 'V1' 0 $v1 @($dissolve)), (Track 'V2' 1 $v2 @()))
        audioTracks = @((Track 'A1' 0 $a1 @()))
        markers = @(); playheadTicks = 0; zoomPixelsPerSecond = 40; snappingEnabled = $true
    }
}
$project | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8NoBOM (Join-Path $Destination 'project.json')

Write-Host "Fixture project: $Destination"
Write-Host "Open it (Debug build):  dotnet run --project src/App/App.csproj -- --open-project `"$Destination`""
