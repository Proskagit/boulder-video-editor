<#
.SYNOPSIS
  Creates the manual-test projects for the Phase 28 transition scenarios (docs/PHASE28_MANUAL_TEST_PLAN.md).

.DESCRIPTION
  Cuts short 1920 × 1080 / 1080 × 1920 H.264 excerpts (60 fps, with the sound) out of the product owner's gym clips in
  -Source and writes three projects (format v3, read and saved as v8) under -Destination (default:
  %TEMP%\aive-phase28-transitions), outside the repository. No transition is prepared: they are added in the app. Each
  project holds exactly two clips on V1, so Ctrl+A selects the cut. The media carry no saved metadata: the app analyses
  them when the project opens:

    dotnet run --project src/App/App.csproj -- --open-project "<Destination>\handles"

  handles   (1920 × 1080): attempt A (video_20260927_101034, the climber dropping) | attempt B (video_20260927_101229), each
            12 s of a 16 s excerpt — 2 s of handle on both sides of the cut: every type fits.
  nohandles (1920 × 1080): the same excerpts from their first frame (15 s each) — no handle before B: Dissolve and Push
            unavailable, the others fit.
  vertical  (1080 × 1920): two attempts of one route (video_20260927_104651, rotation −90°), handles as above.
.PARAMETER Source
  The folder with the product owner's clips (video_20260927_*.mp4).
.PARAMETER Destination
  The folder to create; must not exist unless -Force.
.PARAMETER Force
  Replace an existing folder at -Destination (only one this script created: it must contain .aive-fixture).
#>
param(
    [Parameter(Mandatory)] [string]$Source,
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase28-transitions'),
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
Set-Content (Join-Path $Destination '.aive-fixture') 'Created by tools/manual/New-Phase28TransitionFixture.ps1'

$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
function Excerpt([string]$name, [string]$file, [double]$start, [string]$scale) {
    $out = Join-Path $media "$name.mp4"
    & $ffmpeg -v error -y -ss $start -t 16 -i (Join-Path $Source $file) -vf "fps=60,scale=$scale" -c:v libx264 -preset veryfast -crf 20 `
        -pix_fmt yuv420p -c:a aac -b:a 160k $out
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on $file" }
    $out
}
$a = Excerpt 'attemptA' 'video_20260927_101034.mp4' 70 '1920:1080'
$b = Excerpt 'attemptB' 'video_20260927_101229.mp4' 24 '1920:1080'
$va = Excerpt 'verticalA' 'video_20260927_104651.mp4' 72 '1080:1920'
$vb = Excerpt 'verticalB' 'video_20260927_104651.mp4' 148 '1080:1920'

$now = [DateTimeOffset]::Now.ToString('o')
$frame = 10000000 / 60            # ticks per frame at 60 fps (whole frames below are multiples of 3 → exact ticks)
function T([long]$frames) { [long][math]::Round($frames * $frame) }
$crop = [ordered]@{ left = 0; top = 0; right = 0; bottom = 0 }
function New-Project([string]$folder, [string]$name, [int]$w, [int]$h, [string]$first, [string]$second, [long]$sourceIn, [long]$frames) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    $assets = @(
        [ordered]@{ id = [guid]::NewGuid(); filePath = $first; relativePath = "..\media\$(Split-Path $first -Leaf)"; fileSizeBytes = (Get-Item $first).Length
                    kind = 'Video'; importedAt = $now; thumbnailPath = $null; metadata = $null },
        [ordered]@{ id = [guid]::NewGuid(); filePath = $second; relativePath = "..\media\$(Split-Path $second -Leaf)"; fileSizeBytes = (Get-Item $second).Length
                    kind = 'Video'; importedAt = $now; thumbnailPath = $null; metadata = $null })
    function Clip($asset, [long]$start) {
        [ordered]@{
            type = 'video'; id = [guid]::NewGuid(); timelineStartTicks = T $start; durationTicks = (T ($start + $frames)) - (T $start)
            fadeInTicks = 0; fadeOutTicks = 0; effects = @()
            mediaAssetId = $asset.id; sourceInTicks = T $sourceIn; sourceOutTicks = T ($sourceIn + $frames)
            speedRatio = [ordered]@{ numerator = 1; denominator = 1 }
            positionX = 0; positionY = 0; scale = 1; rotationDegrees = 0; opacity = 1; crop = $crop; volume = 1; isMuted = $false
        }
    }
    function Track([string]$tname, [int]$order, $clips) {
        [ordered]@{ id = [guid]::NewGuid(); name = $tname; order = $order; isMuted = $false; isHidden = $false; isLocked = $false
                    clips = @($clips); transitions = @() }
    }
    $project = [ordered]@{
        format = 'AiVideoEditor.Project'; formatVersion = 3; id = [guid]::NewGuid(); name = $name; createdAt = $now; modifiedAt = $now
        settings = [ordered]@{ frameWidth = $w; frameHeight = $h; frameRate = [ordered]@{ numerator = 60; denominator = 1 }
                               isFrameRateLocked = $true; audioSampleRate = 48000 }
        mediaAssets = $assets
        timeline = [ordered]@{
            id = [guid]::NewGuid(); name = 'Main Sequence'
            videoTracks = @((Track 'V1' 0 @((Clip $assets[0] 0), (Clip $assets[1] $frames))), (Track 'V2' 1 @()))
            audioTracks = @((Track 'A1' 0 @()))
            markers = @(); playheadTicks = 0; zoomPixelsPerSecond = 40; snappingEnabled = $true
        }
    }
    $project | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8NoBOM (Join-Path $folder 'project.json')
}
New-Project (Join-Path $Destination 'handles') 'Phase 28 — handles' 1920 1080 $a $b 120 720
New-Project (Join-Path $Destination 'nohandles') 'Phase 28 — no handles' 1920 1080 $a $b 0 900
New-Project (Join-Path $Destination 'vertical') 'Phase 28 — vertical' 1080 1920 $va $vb 120 720

Write-Host "Fixture projects: $Destination\handles, \nohandles, \vertical"
Write-Host "Open one (Debug build):  dotnet run --project src/App/App.csproj -- --open-project `"$Destination\handles`""
