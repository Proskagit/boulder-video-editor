<#
.SYNOPSIS
    Checks the unpacked minimal FFmpeg zip (DECISIONS.md D046 §13) for its notices and licence texts.

.DESCRIPTION
    -Package: the folder the zip was unpacked into. Checks that every required licence file is present and not empty,
    that THIRD-PARTY-NOTICES.txt names the SHA-256 of ffmpeg.exe and ffprobe.exe, and that every licence file under
    licenses/ (except COPYING3, which the notices only name) is reproduced in the notices (whitespace-normalised).
    Exit codes, whatever the calling shell or its $ErrorActionPreference: 0 — all checks pass; 1 — at least one check
    fails (each failure is listed); 2 — the check itself could not run (an unexpected error).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Package
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    $required = @(
        'THIRD-PARTY-NOTICES.txt',
        'licenses/ffmpeg/COPYING.GPLv2', 'licenses/ffmpeg/COPYING.LGPLv2.1', 'licenses/ffmpeg/LICENSE.md',
        'licenses/x264/COPYING', 'licenses/zimg/COPYING', 'licenses/graphengine/COPYING', 'licenses/zlib/LICENSE',
        'licenses/dav1d/COPYING', 'licenses/mingw-w64-runtime/COPYING.MinGW-w64-runtime.txt',
        'licenses/gcc-runtime/COPYING.RUNTIME', 'licenses/gcc-runtime/COPYING3',
        'bin/ffmpeg.exe', 'bin/ffprobe.exe')
    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($relative in $required) {
        $path = Join-Path $Package $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $failures.Add("Missing in the package: $relative") }
        elseif ((Get-Item -LiteralPath $path).Length -eq 0) { $failures.Add("Empty in the package: $relative") }
    }

    $noticesPath = Join-Path $Package 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath $noticesPath -PathType Leaf) {
        $notices = [System.IO.File]::ReadAllText($noticesPath)
        foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
            $exePath = Join-Path $Package "bin/$exe"
            if (Test-Path -LiteralPath $exePath -PathType Leaf) {
                $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash.ToLowerInvariant()
                if (-not $notices.Contains($hash)) { $failures.Add("THIRD-PARTY-NOTICES.txt does not name the SHA-256 of $exe ($hash)") }
            }
        }

        $normalised = ($notices -replace '\s+', ' ').Trim()
        $licences = Join-Path $Package 'licenses'
        if (Test-Path -LiteralPath $licences -PathType Container) {
            foreach ($file in Get-ChildItem -LiteralPath $licences -Recurse -File) {
                if ($file.Name -eq 'COPYING3') { continue }
                $text = ([System.IO.File]::ReadAllText($file.FullName) -replace '\s+', ' ').Trim()
                if (-not $normalised.Contains($text)) {
                    $failures.Add("Not reproduced in THIRD-PARTY-NOTICES.txt: licenses/$($file.FullName.Substring($licences.Length + 1).Replace('\', '/'))")
                }
            }
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $Package -Recurse -File | Sort-Object FullName) {
        Write-Host ('{0,10}  {1}' -f $file.Length, $file.FullName.Substring($Package.TrimEnd('\', '/').Length + 1))
    }

    if ($failures.Count -gt 0) {
        foreach ($f in $failures) { Write-Host "FAIL: $f" }
        Write-Host "Package notices check: $($failures.Count) failure(s)."
        exit 1
    }
    Write-Host 'Package notices check: PASS'
    exit 0
}
catch {
    Write-Host "Package notices check could not run: $($_.Exception.Message)"
    exit 2
}
