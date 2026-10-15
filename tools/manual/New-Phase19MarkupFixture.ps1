<#
.SYNOPSIS
  Creates the manual-test project for the Phase 19 hold markup (docs/PHASE19_MANUAL_TEST_PLAN.md).

.DESCRIPTION
  Copies one of the product owner's climbing clips (never part of the repository) into -Destination\media and writes a
  project.json (format v4; the Phase 19 build reads it and saves it as v5) in -Destination (default:
  %TEMP%\aive-phase19-markup), outside the repository. The media carry no saved metadata: the app analyses them when the
  project opens. Open it in a Debug build without the folder picker:

    dotnet run --project src/App/App.csproj -- --open-project "<Destination>"

  Timeline (1920 × 1080 at the clip's frame rate): V1 the clip from 0 s for -Seconds (default 5) — pause on it, select it,
  "Mark holds" in the Inspector.
.PARAMETER Clip
  The video to use, e.g. the E1 clip c6: %USERPROFILE%\Desktop\AiVideoEditor-E1-Reference\clips\12x10\1014(6).mp4.
.PARAMETER Destination
  The project folder to create; must not exist unless -Force.
.PARAMETER Force
  Replace an existing folder at -Destination (only one this script created: it must contain .aive-fixture).
#>
param(
    [Parameter(Mandatory)][string]$Clip,
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase19-markup'),
    [int]$Seconds = 5,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Destination = [System.IO.Path]::GetFullPath($Destination)
if (-not (Test-Path -LiteralPath $Clip)) { throw "$Clip not found." }

if (Test-Path $Destination) {
    if (-not $Force) { throw "$Destination exists. Use -Force to replace it (only a folder this script created)." }
    if (-not (Test-Path (Join-Path $Destination '.aive-fixture'))) { throw "$Destination was not created by this script; not replaced." }
    Remove-Item -Recurse -Force $Destination
}
$media = Join-Path $Destination 'media'
New-Item -ItemType Directory -Force $media | Out-Null
Set-Content (Join-Path $Destination '.aive-fixture') 'Created by tools/manual/New-Phase19MarkupFixture.ps1'
$video = Join-Path $media ('clip' + [System.IO.Path]::GetExtension($Clip))
Copy-Item -LiteralPath $Clip $video

$ffprobe = (Get-Command ffprobe -ErrorAction Stop).Source
# The nominal rate (r_frame_rate): a phone clip's average rate can be an odd fraction (variable frame rate).
$rate = (& $ffprobe -v error -select_streams v:0 -show_entries stream=r_frame_rate -of csv=p=0 $video).Trim()
$num, $den = $rate.Split('/') | ForEach-Object { [long]$_ }
$ticks = [long]$Seconds * 10000000
$now = [DateTimeOffset]::Now.ToString('o')

$asset = [ordered]@{
    id = [guid]::NewGuid(); filePath = $video; relativePath = "media\$(Split-Path $video -Leaf)"
    fileSizeBytes = (Get-Item $video).Length; kind = 'Video'; importedAt = $now; thumbnailPath = $null; metadata = $null
}
$clipJson = [ordered]@{
    type = 'video'; id = [guid]::NewGuid(); timelineStartTicks = 0; durationTicks = $ticks
    fadeInTicks = 0; fadeOutTicks = 0; effects = @()
    mediaAssetId = $asset.id; sourceInTicks = 0; sourceOutTicks = $ticks
    speedRatio = [ordered]@{ numerator = 1; denominator = 1 }
    positionX = 0; positionY = 0; rotationDegrees = 0; scale = 1; opacity = 1
    crop = [ordered]@{ left = 0; top = 0; right = 0; bottom = 0 }
    volume = 1; isMuted = $false
}
function Track([string]$name, $clips) {
    [ordered]@{ id = [guid]::NewGuid(); name = $name; order = 0; isMuted = $false; isHidden = $false; isLocked = $false
                clips = @($clips); transitions = @() }
}
$project = [ordered]@{
    format = 'AiVideoEditor.Project'; formatVersion = 4; id = [guid]::NewGuid(); name = 'Phase 19 markup'
    createdAt = $now; modifiedAt = $now
    settings = [ordered]@{ frameWidth = 1920; frameHeight = 1080; frameRate = [ordered]@{ numerator = $num; denominator = $den }
                           isFrameRateLocked = $true; audioSampleRate = 48000 }
    mediaAssets = @($asset)
    timeline = [ordered]@{
        id = [guid]::NewGuid(); name = 'Main Sequence'
        videoTracks = @((Track 'V1' @($clipJson)))
        audioTracks = @((Track 'A1' @()))
        markers = @(); playheadTicks = 10000000; zoomPixelsPerSecond = 40; snappingEnabled = $true
    }
}
$project | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8NoBOM (Join-Path $Destination 'project.json')

Write-Host "Fixture project: $Destination"
Write-Host "Open it (Debug build):  dotnet run --project src/App/App.csproj -- --open-project `"$Destination`""
