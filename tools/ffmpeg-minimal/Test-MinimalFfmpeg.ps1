<#
.SYNOPSIS
    Checks a minimal FFmpeg build (DECISIONS.md D046 §13) against the application's real use, without the test suite.

.DESCRIPTION
    -Candidate: the folder with the minimal ffmpeg.exe / ffprobe.exe. -Reference: a full FFmpeg (the pinned gyan.dev
    9.0.1 essentials) that makes the fixtures — the minimal build has no encoders for them — and decodes them for
    comparison. Checks:
      1. version, -buildconf (only libx264, libzimg, libdav1d; GPL without version3; --disable-everything);
      2. the components of components.txt present (from enabled-config.txt of the build when given, and from the
         binaries' own lists), the excluded ones absent (lists and the binaries' strings);
      3. every target format decoded by the application's own command lines (pipe:1, BGRA / f32le), compared with the
         reference's decode of the same fixture;
      4. the export's two ffmpeg runs (f32le → AAC MP4, BGRA rawvideo + that audio → H.264 MP4) through pipe:0,
         compared with the reference's (H.264 stream without the SEI, PSNR of the decoded video, the AAC stream);
      5. the HDR graph (zscale + lut3d) on an HLG fixture;
      6. AV1 in software through libdav1d (also with -hwaccel auto); the native AV1 decoder with d3d11va when the machine
         has the hardware (otherwise SKIP);
      7. the binaries' sizes and SHA-256.
    Writes report.md and report.json to -Out; exits 1 when a check FAILs. DIFF marks an output that works but is not
    identical to the reference's (9.0.2 against 9.0.1) — reported for review, not a failure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Candidate,
    [Parameter(Mandatory)] [string] $Reference,
    [Parameter(Mandatory)] [string] $Out,
    [string] $EnabledConfig,
    [string] $Components = (Join-Path $PSScriptRoot 'components.txt')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$cand = @{ ffmpeg = (Join-Path $Candidate 'ffmpeg.exe'); ffprobe = (Join-Path $Candidate 'ffprobe.exe') }
$ref = @{ ffmpeg = (Join-Path $Reference 'ffmpeg.exe'); ffprobe = (Join-Path $Reference 'ffprobe.exe') }
foreach ($f in @($cand.ffmpeg, $cand.ffprobe, $ref.ffmpeg)) { if (-not (Test-Path $f)) { throw "Missing: $f" } }
New-Item -ItemType Directory -Force $Out | Out-Null
$fx = Join-Path $Out 'fixtures'; $wk = Join-Path $Out 'work'
New-Item -ItemType Directory -Force $fx, $wk | Out-Null

$results = [System.Collections.Generic.List[object]]::new()
function Add-Result([string] $check, [string] $status, [string] $detail) {
    $results.Add([pscustomobject]@{ Check = $check; Status = $status; Detail = $detail })
    $color = @{ PASS = 'Green'; FAIL = 'Red'; DIFF = 'Yellow'; SKIP = 'DarkGray'; INFO = 'Gray' }[$status]
    Write-Host ("[{0}] {1} — {2}" -f $status, $check, $detail) -ForegroundColor $color
}

# Runs a tool with ArgumentList (no shell quoting), optionally feeding stdin from a file and writing stdout to a file —
# the application's own pipe:0 / pipe:1 use. Returns the exit code and stderr.
function Invoke-Tool([string] $exe, [string[]] $arguments, [string] $stdin, [string] $stdout) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($a in $arguments) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = [bool]$stdout
    $psi.RedirectStandardInput = [bool]$stdin
    $p = [System.Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    $outTask = $null
    if ($stdout) {
        $fs = [System.IO.File]::Create($stdout)
        $outTask = $p.StandardOutput.BaseStream.CopyToAsync($fs)
    }
    if ($stdin) {
        $in = [System.IO.File]::OpenRead($stdin)
        $in.CopyTo($p.StandardInput.BaseStream)
        $in.Dispose()
        $p.StandardInput.Close()
    }
    if ($outTask) { $outTask.Wait(); $fs.Dispose() }
    $p.WaitForExit()
    [pscustomobject]@{ Code = $p.ExitCode; Err = $err.Result }
}

function Get-Sha256([string] $path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }
function Get-Lines([string] $exe, [string[]] $arguments) { (& $exe @arguments 2>&1) -split "`r?`n" }

# The entries of -decoders / -encoders / -filters: the lines after the legend's '------'.
function Get-Listed([string] $exe, [string] $list, [string] $pattern) {
    $body = $false
    foreach ($line in Get-Lines $exe @('-hide_banner', $list)) {
        if ($line -match '^\s*-{6}') { $body = $true; continue }
        if ($body -and $line -match $pattern) { $Matches[1] }
    }
}

# A file path as a filter option value, escaped like the application's FfmpegVideoDecoder.FilterValue: forward
# slashes, then the option value's level (\ ' :) and the graph's (\ ' [ ] , ;).
function ConvertTo-FilterValue([string] $path) {
    $escape = { param($text, $special) -join ($text.ToCharArray() | ForEach-Object { if ($special.Contains($_)) { "\$_" } else { "$_" } }) }
    $value = & $escape ($path.Replace('\', '/')) "\':"
    & $escape $value "\'[],;"
}

# --- 1. Version and configuration -------------------------------------------------------------------------------------
$version = (Get-Lines $cand.ffmpeg @('-hide_banner', '-version'))[0]
Add-Result 'version' $(if ($version -match '9\.0\.2') { 'PASS' } else { 'FAIL' }) $version
$probeVersion = (Get-Lines $cand.ffprobe @('-hide_banner', '-version'))[0]
Add-Result 'ffprobe version' $(if ($probeVersion -match '9\.0\.2') { 'PASS' } else { 'FAIL' }) $probeVersion
$buildconf = (Get-Lines $cand.ffmpeg @('-hide_banner', '-buildconf')) -join "`n"
Set-Content -Path (Join-Path $Out 'buildconf.txt') -Value $buildconf -Encoding utf8
$libs = @([regex]::Matches($buildconf, '--enable-(lib[a-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$expectedLibs = @('libdav1d', 'libx264', 'libzimg')
Add-Result 'external libraries' $(if (-not (Compare-Object $libs $expectedLibs)) { 'PASS' } else { 'FAIL' }) ($libs -join ', ')
$flagsOk = $buildconf -match '--enable-gpl' -and $buildconf -notmatch '--enable-version3' -and $buildconf -notmatch '--enable-nonfree' -and $buildconf -match '--disable-everything'
Add-Result 'licence flags' $(if ($flagsOk) { 'PASS' } else { 'FAIL' }) 'gpl, no version3, no nonfree, --disable-everything'

# --- 2. Components ---------------------------------------------------------------------------------------------------
$wanted = Get-Content $Components | Where-Object { $_ -match '^\s*[a-z]' } | ForEach-Object { $k, $n = -split $_; [pscustomobject]@{ Kind = $k; Name = $n } }
if ($EnabledConfig) {
    $enabled = Get-Content $EnabledConfig
    $missing = @($wanted | Where-Object { "CONFIG_$($_.Name.ToUpperInvariant())_$($_.Kind.ToUpperInvariant())" -notin $enabled })
    Add-Result 'components enabled (build config)' $(if (-not $missing) { 'PASS' } else { 'FAIL' }) $(if ($missing) { ($missing | ForEach-Object { "$($_.Kind):$($_.Name)" }) -join ' ' } else { "$($wanted.Count) components" })
} else {
    Add-Result 'components enabled (build config)' 'SKIP' 'no -EnabledConfig given'
}
$codecLine = '^\s*[VASDFTXBI\.]{6}\s+(\S+)'
$decoders = @(Get-Listed $cand.ffmpeg '-decoders' $codecLine)
$encoders = @(Get-Listed $cand.ffmpeg '-encoders' $codecLine)
$filters = @(Get-Listed $cand.ffmpeg '-filters' '^\s*[TSC\.]{2,3}\s+(\S+)\s')
$protocols = @(Get-Lines $cand.ffmpeg @('-hide_banner', '-protocols') | ForEach-Object { $_.Trim() })
$hwaccels = @(Get-Lines $cand.ffmpeg @('-hide_banner', '-hwaccels') | ForEach-Object { $_.Trim() })
$demuxers = (Get-Lines $cand.ffmpeg @('-hide_banner', '-demuxers')) -join "`n"
$muxers = (Get-Lines $cand.ffmpeg @('-hide_banner', '-muxers')) -join "`n"
$listName = @{ 'demuxer:mov' = 'mov,mp4,m4a'; 'demuxer:matroska' = 'matroska,webm'; 'demuxer:pcm_f32le' = 'f32le'
    'demuxer:image_png_pipe' = 'png_pipe'; 'demuxer:image_jpeg_pipe' = 'jpeg_pipe'; 'demuxer:image_webp_pipe' = 'webp_pipe'
    'demuxer:image_bmp_pipe' = 'bmp_pipe'; 'muxer:pcm_f32le' = 'f32le' }
$absent = foreach ($w in $wanted) {
    $key = "$($w.Kind):$($w.Name)"
    $name = if ($listName.ContainsKey($key)) { $listName[$key] } else { $w.Name }
    $found = switch ($w.Kind) {
        'decoder' { $name -in $decoders } 'encoder' { $name -in $encoders } 'filter' { $name -in $filters }
        'protocol' { $name -in $protocols } 'demuxer' { $demuxers -match "(?m)^\s*D\S*\s+$([regex]::Escape($name))[\s,]" }
        'muxer' { $muxers -match "(?m)^\s*\S*E\s+$([regex]::Escape($name))[\s,]" }
        'hwaccel' { (($w.Name -split '_')[1] -replace '^d3d11va2$', 'd3d11va') -in $hwaccels }
        'parser' { $true }   # not listed by the CLI; covered by the build config and the decode checks
        default { $false }
    }
    if (-not $found) { $key }
}
Add-Result 'components present (binaries)' $(if (-not $absent) { 'PASS' } else { 'FAIL' }) $(if ($absent) { $absent -join ' ' } else { 'decoders, encoders, filters, protocols, demuxers, muxers, hwaccels' })
$extraEncoders = @($encoders | Where-Object { $_ -notin @('libx264', 'aac', 'rawvideo', 'pcm_f32le') })
Add-Result 'no other encoders' $(if (-not $extraEncoders) { 'PASS' } else { 'FAIL' }) $(if ($extraEncoders) { $extraEncoders -join ' ' } else { 'libx264, aac, rawvideo, pcm_f32le only' })
$denied = @('libx265', 'libaom-av1', 'libopenh264', 'liblcevc_dec', 'librubberband', 'libdvdnav', 'libdvdread')
$deniedFound = @(($decoders + $encoders + $filters) | Where-Object { $_ -in $denied })
$bytes = [System.IO.File]::ReadAllBytes($cand.ffmpeg)
$text = [System.Text.Encoding]::ASCII.GetString($bytes)
$deniedStrings = @('dvdcss', 'OpenH264', 'LCEVC_', 'x265 (build', 'rubberband') | Where-Object { $text.Contains($_) }
Add-Result 'excluded components absent' $(if (-not $deniedFound -and -not $deniedStrings) { 'PASS' } else { 'FAIL' }) $(if ($deniedFound -or $deniedStrings) { "$deniedFound $deniedStrings" } else { 'libdvdcss, openh264, LCEVCdec, libx265, rubberband' })

# --- 3. Fixtures (made by the reference) and the application's decoding ------------------------------------------------
$src = @('-f', 'lavfi', '-i', 'testsrc2=s=640x360:r=30:d=1')
$snd = @('-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:d=1')
$fixtures = [ordered]@{
    'h264-aac.mp4'      = @($src + $snd + @('-c:v', 'libx264', '-c:a', 'aac', '-shortest'))
    'hevc10-aac.mp4'    = @($src + $snd + @('-vf', 'format=yuv420p10le', '-c:v', 'libx265', '-x265-params', 'log-level=error', '-c:a', 'aac', '-shortest'))
    'mpeg4-mp3.avi'     = @($src + $snd + @('-c:v', 'mpeg4', '-q:v', '3', '-c:a', 'libmp3lame', '-shortest'))
    'mjpeg-pcm.avi'     = @($src + $snd + @('-c:v', 'mjpeg', '-q:v', '3', '-pix_fmt', 'yuvj420p', '-c:a', 'pcm_s16le', '-shortest'))
    'vp8-vorbis.webm'   = @($src + $snd + @('-c:v', 'libvpx', '-b:v', '1M', '-c:a', 'libvorbis', '-shortest'))
    'vp9-opus.webm'     = @($src + $snd + @('-c:v', 'libvpx-vp9', '-b:v', '1M', '-c:a', 'libopus', '-shortest'))
    'av1-flac.mkv'      = @($src + $snd + @('-c:v', 'libaom-av1', '-cpu-used', '8', '-b:v', '500k', '-c:a', 'flac', '-shortest'))
    'h264-lpcm.mov'     = @($src + $snd + @('-c:v', 'libx264', '-c:a', 'pcm_s24be', '-shortest'))
    'aac.m4a'           = @($snd + @('-c:a', 'aac'))
    'alac.m4a'          = @($snd + @('-c:a', 'alac'))
    'mp3.mp3'           = @($snd + @('-c:a', 'libmp3lame'))
    'aac-adts.aac'      = @($snd + @('-c:a', 'aac', '-f', 'adts'))
    's16.wav'           = @($snd + @('-c:a', 'pcm_s16le'))
    's24.wav'           = @($snd + @('-c:a', 'pcm_s24le'))
    's32.wav'           = @($snd + @('-c:a', 'pcm_s32le'))
    'f32.wav'           = @($snd + @('-c:a', 'pcm_f32le'))
    'u8.wav'            = @($snd + @('-c:a', 'pcm_u8'))
    'flac.flac'         = @($snd + @('-c:a', 'flac'))
    'opus-audio.webm'   = @($snd + @('-c:a', 'libopus'))
    'vorbis-audio.mkv'  = @($snd + @('-c:a', 'libvorbis'))
    'image.png'         = @('-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=1', '-frames:v', '1')
    'image.jpg'         = @('-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=1', '-frames:v', '1', '-q:v', '3')
    'image.webp'        = @('-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=1', '-frames:v', '1', '-c:v', 'libwebp')
    'image.bmp'         = @('-f', 'lavfi', '-i', 'testsrc2=s=640x360:d=1', '-frames:v', '1')
}
foreach ($name in $fixtures.Keys) {
    $r = Invoke-Tool $ref.ffmpeg (@('-hide_banner', '-loglevel', 'error', '-y') + $fixtures[$name] + @((Join-Path $fx $name))) $null $null
    if ($r.Code -ne 0) { Add-Result "fixture $name" 'FAIL' "reference could not make it: $($r.Err.Trim())" }
}

function Get-VideoArgs([string] $file, [string] $hw) {
    $a = @('-hide_banner', '-nostdin', '-nostats', '-loglevel', 'info')
    if ($hw) { $a += @('-hwaccel', $hw) }
    $a + @('-copyts', '-autorotate', '-i', $file, '-map', '0:v:0', '-an', '-sn', '-dn',
        '-vf', "scale=w='min(iw,1920)':h='min(ih,1080)':force_original_aspect_ratio=decrease,format=bgra,showinfo=checksum=0",
        '-fps_mode', 'passthrough', '-pix_fmt', 'bgra', '-f', 'rawvideo', 'pipe:1')
}
function Get-AudioArgs([string] $file, [string] $tempo) {
    @('-hide_banner', '-nostdin', '-nostats', '-loglevel', 'info', '-copyts', '-i', $file, '-map', '0:a:0', '-vn', '-sn', '-dn',
        '-af', "aresample=48000:async=1,aformat=sample_fmts=flt:channel_layouts=stereo,ashowinfo$tempo", '-f', 'f32le', 'pipe:1')
}
function Compare-Decode([string] $label, [string[]] $candArgs, [string[]] $refArgs) {
    $co = Join-Path $wk "$label.cand"; $ro = Join-Path $wk "$label.ref"
    $c = Invoke-Tool $cand.ffmpeg $candArgs $null $co
    $r = Invoke-Tool $ref.ffmpeg $refArgs $null $ro
    if ($c.Code -ne 0 -or (Get-Item $co).Length -eq 0) {
        $lines = $c.Err -split "`r?`n" | Where-Object { $_ -match 'rror|not found|nvalid' } | Select-Object -Last 3
        Add-Result "decode $label" 'FAIL' "exit $($c.Code): $($lines -join ' | ')"; return $null
    }
    if ((Get-Sha256 $co) -eq (Get-Sha256 $ro)) { Add-Result "decode $label" 'PASS' "identical to the reference ($((Get-Item $co).Length) bytes)" }
    else { Add-Result "decode $label" 'DIFF' "decoded ($((Get-Item $co).Length) bytes, reference $((Get-Item $ro).Length)); output differs from the reference" }
    $c
}
foreach ($name in $fixtures.Keys) {
    $file = Join-Path $fx $name
    if (-not (Test-Path $file)) { continue }
    $hasVideo = $name -notmatch '\.(m4a|mp3|aac|wav|flac)$' -and $name -notmatch '-audio\.'
    $hasAudio = $name -notmatch '^image\.'
    if ($hasVideo) { $null = Compare-Decode "$name video" (Get-VideoArgs $file $null) (Get-VideoArgs $file $null) }
    if ($hasAudio) { $null = Compare-Decode "$name audio" (Get-AudioArgs $file '') (Get-AudioArgs $file '') }
}
$tempo = ',apad=pad_dur=0.25,atempo=0.5,atempo=0.8'
$null = Compare-Decode 'aac.m4a audio at 0.4x (apad, atempo)' (Get-AudioArgs (Join-Path $fx 'aac.m4a') $tempo) (Get-AudioArgs (Join-Path $fx 'aac.m4a') $tempo)

# --- 4. The export's two runs (application: FfmpegExportEncoder) --------------------------------------------------------
$frames = Join-Path $wk 'export-frames.bgra'; $audio = Join-Path $wk 'export-audio.f32'
$null = Invoke-Tool $ref.ffmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=s=1280x720:r=30:d=2', '-pix_fmt', 'bgra', '-f', 'rawvideo', $frames) $null $null
$null = Invoke-Tool $ref.ffmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=48000:d=2', '-ac', '2', '-f', 'f32le', $audio) $null $null
function Invoke-Export([string] $exe, [string] $tag) {
    $common = @('-hide_banner', '-nostats', '-loglevel', 'error', '-y')
    $aac = Join-Path $wk "export-audio-$tag.mp4"; $mp4 = Join-Path $wk "export-$tag.mp4"
    $a = Invoke-Tool $exe ($common + @('-f', 'f32le', '-ar', '48000', '-ac', '2', '-i', 'pipe:0', '-c:a', 'aac', '-profile:a', 'aac_low',
            '-b:a', '256000', '-ar', '48000', '-ac', '2', '-f', 'mp4', $aac)) $audio $null
    $v = Invoke-Tool $exe ($common + @('-f', 'rawvideo', '-pix_fmt', 'bgra', '-s', '1280x720', '-framerate', '30/1', '-i', 'pipe:0', '-i', $aac,
            '-map', '0:v:0', '-map', '1:a:0',
            '-vf', 'scale=out_color_matrix=bt709:out_range=tv,format=yuv420p,setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709',
            '-fps_mode', 'passthrough', '-c:v', 'libx264', '-preset', 'medium', '-crf', '18', '-pix_fmt', 'yuv420p',
            '-colorspace', 'bt709', '-color_primaries', 'bt709', '-color_trc', 'bt709', '-color_range', 'tv',
            '-c:a', 'copy', '-movflags', '+faststart', '-f', 'mp4', $mp4)) $frames $null
    [pscustomobject]@{ Audio = $a; Video = $v; Mp4 = $mp4 }
}
$ec = Invoke-Export $cand.ffmpeg 'cand'; $er = Invoke-Export $ref.ffmpeg 'ref'
if ($ec.Audio.Code -ne 0 -or $ec.Video.Code -ne 0) {
    Add-Result 'export H.264 / AAC' 'FAIL' "audio exit $($ec.Audio.Code), video exit $($ec.Video.Code): $($ec.Audio.Err.Trim()) $($ec.Video.Err.Trim())"
} else {
    $streams = foreach ($t in 'cand', 'ref') {
        $mp4 = Join-Path $wk "export-$t.mp4"; $h264 = Join-Path $wk "export-$t.h264"; $adts = Join-Path $wk "export-$t.aac"
        $null = Invoke-Tool $ref.ffmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-i', $mp4, '-map', '0:v', '-c', 'copy', '-bsf:v', 'filter_units=remove_types=6', '-f', 'h264', $h264) $null $null
        $null = Invoke-Tool $ref.ffmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-i', $mp4, '-map', '0:a', '-c', 'copy', '-f', 'adts', $adts) $null $null
        [pscustomobject]@{ H264 = (Get-Sha256 $h264); Aac = (Get-Sha256 $adts) }
    }
    $psnr = (Invoke-Tool $ref.ffmpeg @('-hide_banner', '-i', $ec.Mp4, '-i', $er.Mp4, '-lavfi', '[0:v][1:v]psnr', '-f', 'null', '-') $null $null).Err -split "`r?`n" | Where-Object { $_ -match 'PSNR' } | Select-Object -Last 1
    $sei = (Get-Content -Raw -Encoding Latin1 $ec.Mp4) -match 'x264 - core 165 r3223 0480cb0'
    Add-Result 'export H.264 stream (without SEI)' $(if ($streams[0].H264 -eq $streams[1].H264) { 'PASS' } else { 'DIFF' }) "candidate vs reference; $psnr"
    Add-Result 'export x264 version string' $(if ($sei) { 'PASS' } else { 'DIFF' }) 'core 165 r3223 0480cb0 expected in the SEI'
    Add-Result 'export AAC stream' $(if ($streams[0].Aac -eq $streams[1].Aac) { 'PASS' } else { 'DIFF' }) 'candidate vs reference (FFmpeg 9.0.2 against 9.0.1)'
}

# --- 5. HDR graph: zscale + lut3d (application: FfmpegVideoDecoder.HdrFilters) -----------------------------------------
$hlg = Join-Path $fx 'hlg10.mp4'
$null = Invoke-Tool $ref.ffmpeg @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=s=640x360:r=30:d=1',
    '-vf', 'format=yuv420p10le', '-c:v', 'libx265', '-x265-params', 'log-level=error:colorprim=bt2020:transfer=arib-std-b67:colormatrix=bt2020nc',
    '-color_primaries', 'bt2020', '-color_trc', 'arib-std-b67', '-colorspace', 'bt2020nc', $hlg) $null $null
$cube = Join-Path $wk 'identity.cube'
$n = 17; $sb = [System.Text.StringBuilder]::new("LUT_3D_SIZE $n`n")
for ($b = 0; $b -lt $n; $b++) { for ($g = 0; $g -lt $n; $g++) { for ($r = 0; $r -lt $n; $r++) {
    [void]$sb.AppendFormat([cultureinfo]::InvariantCulture, "{0:0.000000} {1:0.000000} {2:0.000000}`n", $r / ($n - 1), $g / ($n - 1), $b / ($n - 1)) } } }
[System.IO.File]::WriteAllText($cube, $sb.ToString())
$lut = ConvertTo-FilterValue $cube
$hdrGraph = "scale=w='min(iw,1920)':h='min(ih,1080)':force_original_aspect_ratio=decrease:force_divisible_by=2," +
    "zscale=min=bt2020nc:pin=bt2020:tin=arib-std-b67:rin=limited:p=bt2020:t=arib-std-b67:r=full,format=gbrpf32le," +
    "lut3d=file=$($lut):interp=tetrahedral,format=bgra,showinfo=checksum=0"
$hdrArgs = @('-hide_banner', '-nostdin', '-nostats', '-loglevel', 'info', '-copyts', '-autorotate', '-i', $hlg, '-map', '0:v:0', '-an', '-sn', '-dn',
    '-vf', $hdrGraph, '-fps_mode', 'passthrough', '-pix_fmt', 'bgra', '-f', 'rawvideo', 'pipe:1')
$null = Compare-Decode 'HDR HLG (zscale + lut3d)' $hdrArgs $hdrArgs

# --- 6. AV1 ------------------------------------------------------------------------------------------------------------
$av1 = Join-Path $fx 'av1-flac.mkv'
if (Test-Path $av1) {
    foreach ($hw in @($null, 'auto')) {
        $o = Join-Path $wk "av1-$($hw ?? 'sw').bgra"
        $r = Invoke-Tool $cand.ffmpeg (Get-VideoArgs $av1 $hw) $null $o
        $dav1d = $r.Err -match '\(av1 \(libdav1d\)'
        Add-Result "AV1 software decode via libdav1d$(if ($hw) { " (-hwaccel $hw)" })" $(if ($r.Code -eq 0 -and $dav1d -and (Get-Item $o).Length -gt 0) { 'PASS' } else { 'FAIL' }) "exit $($r.Code), libdav1d in the log: $dav1d"
    }
    $o = Join-Path $wk 'av1-native-d3d11va.yuv'
    $r = Invoke-Tool $cand.ffmpeg @('-hide_banner', '-nostdin', '-loglevel', 'info', '-hwaccel', 'd3d11va', '-c:v', 'av1', '-i', $av1, '-map', '0:v:0',
        '-f', 'rawvideo', '-pix_fmt', 'yuv420p', 'pipe:1') $null $o
    if ($r.Code -eq 0 -and (Get-Item $o).Length -gt 0) { Add-Result 'AV1 native decoder with d3d11va' 'PASS' 'hardware AV1 decoding available here' }
    elseif ($r.Err -match "doesn't support hardware accelerated AV1|Failed setup|No device|Device creation failed|ENOSYS|Function not implemented") {
        Add-Result 'AV1 native decoder with d3d11va' 'SKIP' 'no AV1 decoding hardware in this environment'
    } else { Add-Result 'AV1 native decoder with d3d11va' 'DIFF' "exit $($r.Code): $(($r.Err -split "`r?`n" | Select-Object -Last 2) -join ' | ')" }
}
$hwH264 = Invoke-Tool $cand.ffmpeg (Get-VideoArgs (Join-Path $fx 'h264-aac.mp4') 'auto') $null (Join-Path $wk 'h264-hwauto.bgra')
Add-Result 'H.264 with -hwaccel auto' $(if ($hwH264.Code -eq 0) { 'PASS' } else { 'INFO' }) "exit $($hwH264.Code) (the application falls back to software on failure)"

# --- 7. Sizes and checksums --------------------------------------------------------------------------------------------
foreach ($exe in $cand.ffmpeg, $cand.ffprobe) {
    $i = Get-Item $exe
    Add-Result "binary $($i.Name)" 'INFO' ("{0} bytes ({1:0.0} MiB), SHA-256 {2}" -f $i.Length, ($i.Length / 1MB), (Get-Sha256 $exe))
}

# --- Report ------------------------------------------------------------------------------------------------------------
$results | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $Out 'report.json') -Encoding utf8
$md = @('# Minimal FFmpeg — verification', '', "Candidate: ``$($cand.ffmpeg)`` — $version", "Reference: ``$($ref.ffmpeg)``", '',
    '| Check | Status | Detail |', '|---|---|---|') + ($results | ForEach-Object { "| $($_.Check) | $($_.Status) | $($_.Detail -replace '\|', '/') |" })
Set-Content -Path (Join-Path $Out 'report.md') -Value $md -Encoding utf8
$failed = @($results | Where-Object Status -eq 'FAIL')
Write-Host "FAIL: $($failed.Count), DIFF: $(@($results | Where-Object Status -eq 'DIFF').Count), PASS: $(@($results | Where-Object Status -eq 'PASS').Count)"
if ($failed) { exit 1 }
