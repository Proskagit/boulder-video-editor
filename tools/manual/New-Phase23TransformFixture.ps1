<#
.SYNOPSIS
  Creates the manual-test project for the Phase 23 Preview tools (docs/PHASE23_MANUAL_TEST_PLAN.md, D038).

.DESCRIPTION
  Generates small media with ffmpeg and a project.json (format v3) in -Destination (default:
  %TEMP%\aive-phase23-transform), outside the repository. The media carry no saved metadata: the app analyses them when the
  project opens. Open it in a Debug build without the picker:

    dotnet run --project src/App/App.csproj -- --open-project "<Destination>"

  Canvas 1920 × 1080, 25 fps (seconds = frame / 25):
    0–8 s    a 2 × 2 composition: q0–q3 on V1–V4, each at 50 % in its quarter                         → rows 2–4, 21
    10–18 s  V1 "phone": 640 × 360 stored with a 90° display rotation (a portrait 360 × 640 picture)    → rows 9, 22
             V2 the text "TITLE" (Segoe UI 96) above the centre                                          → rows 5, 13
    20–28 s  V1 "bars" rotated by 30° at 60 %; V2 the image "still" at 40 % in the lower right          → rows 5, 7, 10
    30–38 s  V5 (locked) "pattern"                                                                      → row 18
.PARAMETER Destination
  The project folder to create; must not exist unless -Force.
.PARAMETER Force
  Replace an existing folder at -Destination (only one this script created: it must contain .aive-fixture).
#>
param(
    [string]$Destination = (Join-Path ([System.IO.Path]::GetTempPath()) 'aive-phase23-transform'),
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
Set-Content (Join-Path $Destination '.aive-fixture') 'Created by tools/manual/New-Phase23TransformFixture.ps1'

$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
function Invoke-Ffmpeg([string[]]$arguments) {
    & $ffmpeg -v error -y @arguments
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed: $($arguments -join ' ')" }
}

# --- media ----------------------------------------------------------------------------------------------------------
$encode = @('-c:v', 'libx264', '-preset', 'veryfast', '-crf', '18', '-pix_fmt', 'yuv420p', '-an')
$sources = [ordered]@{
    q0 = 'testsrc2=s=640x360:r=25:d=10'; q1 = 'smptebars=s=640x360:r=25:d=10'
    q2 = 'rgbtestsrc=s=640x360:r=25:d=10'; q3 = 'testsrc=s=640x360:r=25:d=10'
    bars = 'smptehdbars=s=640x360:r=25:d=10'; pattern = 'testsrc2=s=640x360:r=25:d=10'
}
$files = @{}
foreach ($name in $sources.Keys) {
    $files[$name] = Join-Path $media "$name.mp4"
    Invoke-Ffmpeg (@('-f', 'lavfi', '-i', $sources[$name]) + $encode + @($files[$name]))
}
# A phone-like clip: frames stored landscape with a 90° display rotation (the app shows it portrait, D019).
$files['phone'] = Join-Path $media 'phone.mp4'
$plain = Join-Path $media 'phone-plain.mp4'
Invoke-Ffmpeg (@('-f', 'lavfi', '-i', 'testsrc2=s=640x360:r=25:d=10') + $encode + @($plain))
Invoke-Ffmpeg @('-display_rotation', '90', '-i', $plain, '-c', 'copy', $files['phone'])
Remove-Item $plain
$files['still'] = Join-Path $media 'still.png'
Invoke-Ffmpeg @('-f', 'lavfi', '-i', 'mandelbrot=s=480x270', '-frames:v', '1', $files['still'])

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
$assets = [ordered]@{}
foreach ($name in $files.Keys) { $assets[$name] = Asset $files[$name] ($(if ($name -eq 'still') { 'Image' } else { 'Video' })) }

function Clip([string]$type, [string]$asset, [long]$start, [long]$end, [double]$x = 0, [double]$y = 0, [double]$scale = 1, [double]$rotation = 0) {
    $frames = $end - $start
    $c = [ordered]@{
        type = $type; id = [guid]::NewGuid(); timelineStartTicks = T $start; durationTicks = T $frames
        fadeInTicks = 0; fadeOutTicks = 0; effects = @()
        mediaAssetId = $assets[$asset].id; sourceInTicks = 0; sourceOutTicks = T $frames
        speedRatio = [ordered]@{ numerator = 1; denominator = 1 }
        positionX = $x; positionY = $y; scale = $scale; rotationDegrees = $rotation; opacity = 1
        crop = [ordered]@{ left = 0; top = 0; right = 0; bottom = 0 }
    }
    if ($type -eq 'video') { $c.volume = 1; $c.isMuted = $false }
    $c
}
$text = [ordered]@{
    type = 'text'; id = [guid]::NewGuid(); timelineStartTicks = T 250; durationTicks = T 200
    fadeInTicks = 0; fadeOutTicks = 0; effects = @()
    text = 'TITLE'; fontFamily = 'Segoe UI'; fontSize = 96; colorHex = '#FFFFFF'; alignment = 'Center'
    positionX = 0; positionY = -300; scale = 1; rotationDegrees = 0; opacity = 1
}

function Track([string]$name, [int]$order, $clips, [bool]$locked = $false) {
    [ordered]@{ id = [guid]::NewGuid(); name = $name; order = $order; isMuted = $false; isHidden = $false; isLocked = $locked
                clips = @($clips); transitions = @() }
}
$v1 = @((Clip 'video' 'q0' 0 200 -480 -270 0.5), (Clip 'video' 'phone' 250 450), (Clip 'video' 'bars' 500 700 0 0 0.6 30))
$v2 = @((Clip 'video' 'q1' 0 200 480 -270 0.5), $text, (Clip 'image' 'still' 500 700 600 300 0.4))
$v3 = @((Clip 'video' 'q2' 0 200 -480 270 0.5))
$v4 = @((Clip 'video' 'q3' 0 200 480 270 0.5))
$v5 = @((Clip 'video' 'pattern' 750 950))
$project = [ordered]@{
    format = 'AiVideoEditor.Project'; formatVersion = 3; id = [guid]::NewGuid(); name = 'Phase 23 transform'
    createdAt = $now; modifiedAt = $now
    settings = [ordered]@{ frameWidth = 1920; frameHeight = 1080; frameRate = [ordered]@{ numerator = 25; denominator = 1 }
                           isFrameRateLocked = $true; audioSampleRate = 48000 }
    mediaAssets = @($assets.Values)
    timeline = [ordered]@{
        id = [guid]::NewGuid(); name = 'Main Sequence'
        videoTracks = @((Track 'V1' 0 $v1), (Track 'V2' 1 $v2), (Track 'V3' 2 $v3), (Track 'V4' 3 $v4), (Track 'V5' 4 $v5 $true))
        audioTracks = @((Track 'A1' 0 @()))
        markers = @(); playheadTicks = (T 25); zoomPixelsPerSecond = 40; snappingEnabled = $true
    }
}
$project | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8NoBOM (Join-Path $Destination 'project.json')

Write-Host "Fixture project: $Destination"
Write-Host "Open it (Debug build):  dotnet run --project src/App/App.csproj -- --open-project `"$Destination`""
