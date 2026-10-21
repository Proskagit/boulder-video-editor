<#
.SYNOPSIS
  Creates the manual-test projects for the Phase 29 templates (docs/PHASE29_MANUAL_TEST_PLAN.md).

.DESCRIPTION
  Cuts 16 s excerpts (1920 × 1080 and 1080 × 1920 H.264, 60 fps, with the sound) out of the product owner's gym clips in
  -Source and writes two projects (format v9) under -Destination (default: %TEMP%\aive-phase29-templates), outside the
  repository: the climb on V1 and, on V2, the four templates one after another — REC 0–4 s (date 27.09.2026), VHS 4–8 s,
  «Боулдер» 8–12 s (route 3, grade 6B+, TOP), «Хайлайт» 12–15 s (the default title, a subtitle). The playhead starts at
  5 s (inside VHS). The media carry no saved metadata: the app analyses them when the project opens:

    dotnet run --project src/App/App.csproj -- --open-project "<Destination>\horizontal"

  horizontal (1920 × 1080): video_20260927_101229 from 30 s (a climber on the black route).
  vertical   (1080 × 1920): video_20260927_104651 from 70 s (the overhang; rotation −90°).
.PARAMETER Source
  The folder with the product owner's clips (video_20260927_*.mp4).
.PARAMETER Destination
  The folder to create; must not exist unless -Force.
.PARAMETER Force
  Replace an existing folder at -Destination (only one this script created: it must contain .aive-fixture).
#>
param(
    [Parameter(Mandatory)] [string]$Source,
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase29-templates'),
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
Set-Content (Join-Path $Destination '.aive-fixture') 'Created by tools/manual/New-Phase29TemplateFixture.ps1'

$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
function Excerpt([string]$name, [string]$file, [double]$start, [string]$scale) {
    $out = Join-Path $media "$name.mp4"
    & $ffmpeg -v error -y -ss $start -t 16 -i (Join-Path $Source $file) -vf "fps=60,scale=$scale" -c:v libx264 -preset veryfast -crf 20 `
        -pix_fmt yuv420p -c:a aac -b:a 160k $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $file" }
    $out
}
$h = Excerpt 'climbHorizontal' 'video_20260927_101229.mp4' 30 '1920:1080'
$v = Excerpt 'climbVertical' 'video_20260927_104651.mp4' 70 '1080:1920'

$now = [DateTimeOffset]::Now.ToString('o')
$frame = 10000000 / 60            # ticks per frame at 60 fps (frames below are multiples of 3 → exact ticks)
function T([long]$frames) { [long][math]::Round($frames * $frame) }
$crop = [ordered]@{ left = 0; top = 0; right = 0; bottom = 0 }
function Template([string]$kind, [long]$start, [long]$end, [long]$fadeIn, [long]$fadeOut, $fields) {
    $clip = [ordered]@{
        type = 'template'; id = [guid]::NewGuid(); timelineStartTicks = T $start; durationTicks = (T $end) - (T $start)
        fadeInTicks = T $fadeIn; fadeOutTicks = T $fadeOut; effects = @()
        template = $kind; counterStartTicks = 0; date = ''; route = ''; grade = ''; status = 'None'; accentHex = '#FFEA00'
        title = ''; subtitle = ''
    }
    foreach ($key in $fields.Keys) { $clip[$key] = $fields[$key] }
    $clip
}
function New-Project([string]$folder, [string]$name, [int]$w, [int]$hgt, [string]$file) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    $asset = [ordered]@{ id = [guid]::NewGuid(); filePath = $file; relativePath = "..\media\$(Split-Path $file -Leaf)"
                         fileSizeBytes = (Get-Item $file).Length; kind = 'Video'; importedAt = $now; thumbnailPath = $null; metadata = $null }
    $video = [ordered]@{
        type = 'video'; id = [guid]::NewGuid(); timelineStartTicks = 0; durationTicks = T 900; fadeInTicks = 0; fadeOutTicks = 0; effects = @()
        mediaAssetId = $asset.id; sourceInTicks = 0; sourceOutTicks = T 900; speedRatio = [ordered]@{ numerator = 1; denominator = 1 }
        positionX = 0; positionY = 0; scale = 1; rotationDegrees = 0; opacity = 1; crop = $crop; volume = 1; isMuted = $false
    }
    $templates = @(
        (Template 'Rec' 0 240 12 12 @{ date = '27.09.2026' }),
        (Template 'Vhs' 240 480 0 0 @{}),
        (Template 'Boulder' 480 720 21 18 @{ route = '3'; grade = '6B+'; status = 'Top' }),
        (Template 'Highlight' 720 900 36 18 @{ title = 'ЛУЧШАЯ ПОПЫТКА'; subtitle = 'Трасса 3 · 6B+' }))
    function Track([string]$tname, [int]$order, $clips) {
        [ordered]@{ id = [guid]::NewGuid(); name = $tname; order = $order; isMuted = $false; isHidden = $false; isLocked = $false
                    clips = @($clips); transitions = @() }
    }
    $project = [ordered]@{
        format = 'AiVideoEditor.Project'; formatVersion = 9; id = [guid]::NewGuid(); name = $name; createdAt = $now; modifiedAt = $now
        settings = [ordered]@{ frameWidth = $w; frameHeight = $hgt; frameRate = [ordered]@{ numerator = 60; denominator = 1 }
                               isFrameRateLocked = $true; audioSampleRate = 48000 }
        mediaAssets = @($asset)
        timeline = [ordered]@{
            id = [guid]::NewGuid(); name = 'Main Sequence'
            videoTracks = @((Track 'V1' 0 @($video)), (Track 'V2' 1 $templates))
            audioTracks = @((Track 'A1' 0 @()))
            markers = @(); playheadTicks = T 300; zoomPixelsPerSecond = 40; snappingEnabled = $true
        }
    }
    $project | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8NoBOM (Join-Path $folder 'project.json')
}
New-Project (Join-Path $Destination 'horizontal') 'Phase 29 — templates' 1920 1080 $h
New-Project (Join-Path $Destination 'vertical') 'Phase 29 — templates, vertical' 1080 1920 $v

Write-Host "Fixture projects: $Destination\horizontal, \vertical"
Write-Host "Open one (Debug build):  dotnet run --project src/App/App.csproj -- --open-project `"$Destination\horizontal`""
