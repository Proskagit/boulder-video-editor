<#
.SYNOPSIS
    Checks the unpacked minimal FFmpeg zip (DECISIONS.md D046 §13) for its notices and licence texts.

.DESCRIPTION
    -Package: the folder the zip was unpacked into. Checks that every required licence file is present and not empty,
    that THIRD-PARTY-NOTICES.txt names the SHA-256 of ffmpeg.exe and ffprobe.exe, and that every licence file under
    licenses/ (except COPYING3, which the notices only name) is reproduced in the notices (whitespace-normalised). Also
    that the AOMedia Patent License file is the official text (its SHA-256), that the notices carry the Independent JPEG
    Group statement, and that they carry the copyright line each program prints with -version.
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
        'licenses/dav1d/AOM-PATENT-LICENSE-1.0.txt',
        'licenses/ffmpeg/ijg/jrevdct.c.header.txt', 'licenses/ffmpeg/ijg/jfdctfst.c.header.txt',
        'licenses/ffmpeg/ijg/jfdctint_template.c.header.txt',
        'bin/ffmpeg.exe', 'bin/ffprobe.exe')
    # The official AOMedia Patent License 1.0: libaom's PATENTS at v3.15.2 (af3dc9aadc793c1b00edc003e3fc19a44fdeb574).
    $aomSha256 = '661fb8e504744e95587b556b94a58343448300606a41bea8c7a9b97125696e61'
    $ijgStatement = 'this software is based in part on the work of the Independent JPEG Group'
    $failures = [System.Collections.Generic.List[string]]::new()

    foreach ($relative in $required) {
        $path = Join-Path $Package $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $failures.Add("Missing in the package: $relative") }
        elseif ((Get-Item -LiteralPath $path).Length -eq 0) { $failures.Add("Empty in the package: $relative") }
    }

    $aomPath = Join-Path $Package 'licenses/dav1d/AOM-PATENT-LICENSE-1.0.txt'
    if (Test-Path -LiteralPath $aomPath -PathType Leaf) {
        $aomHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $aomPath).Hash.ToLowerInvariant()
        if ($aomHash -ne $aomSha256) { $failures.Add("licenses/dav1d/AOM-PATENT-LICENSE-1.0.txt is not the official text (SHA-256 $aomHash)") }
    }

    $noticesPath = Join-Path $Package 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath $noticesPath -PathType Leaf) {
        $notices = [System.IO.File]::ReadAllText($noticesPath)
        foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
            $exePath = Join-Path $Package "bin/$exe"
            if (Test-Path -LiteralPath $exePath -PathType Leaf) {
                $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash.ToLowerInvariant()
                if (-not $notices.Contains($hash)) { $failures.Add("THIRD-PARTY-NOTICES.txt does not name the SHA-256 of $exe ($hash)") }

                # The copyright line the program itself prints, e.g. "Copyright (c) 2000-2026 the FFmpeg developers".
                $versionLine = (& $exePath -hide_banner -version 2>&1 | Select-Object -First 1) -as [string]
                $copyright = [regex]::Match("$versionLine", 'Copyright \(c\) \d{4}-\d{4} the FFmpeg developers').Value
                if (-not $copyright) { $failures.Add("$exe -version prints no copyright line: $versionLine") }
                elseif (-not $notices.Contains("${exe}: $copyright")) { $failures.Add("THIRD-PARTY-NOTICES.txt lacks the copyright line of ${exe}: $copyright") }
            }
        }

        if (-not ($notices -replace '\s+', ' ').Contains($ijgStatement)) {
            $failures.Add("THIRD-PARTY-NOTICES.txt lacks the Independent JPEG Group statement: '$ijgStatement'")
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

            # The notices list the package's licence files in make-notices.py's order: the relative path with '/'
            # separators, ordinal (case-sensitive) — the same on every OS. The list must be exactly the files, in it.
            $actual = [string[]]@(Get-ChildItem -LiteralPath $licences -Recurse -File |
                ForEach-Object { 'licenses/' + $_.FullName.Substring($licences.Length + 1).Replace('\', '/') })
            [System.Array]::Sort($actual, [System.StringComparer]::Ordinal)
            $listed = [regex]::Match($notices, '(?ms)^LICENCE FILES IN THIS PACKAGE\r?\n-+\r?\n(.*?)(?:\r?\n\r?\n|\z)')
            $listedFiles = if ($listed.Success) { @($listed.Groups[1].Value -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @() }
            if (-not $listed.Success) { $failures.Add('THIRD-PARTY-NOTICES.txt has no LICENCE FILES IN THIS PACKAGE list') }
            elseif (($listedFiles -join "`n") -cne ($actual -join "`n")) {
                $failures.Add("The LICENCE FILES list differs from the package's licence files in ordinal order (listed: $($listedFiles -join ', '); expected: $($actual -join ', '))")
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
