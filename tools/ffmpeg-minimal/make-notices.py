"""Writes OUT/THIRD-PARTY-NOTICES.txt for the minimal FFmpeg (DECISIONS.md D046 §13) from what the build actually
packs: the binaries in OUT/bin, the components of sources.lock and the licence texts in OUT/licenses (copied verbatim).
Run by build.sh; the same file goes into the zip and to the release. Deterministic: no dates, only the inputs."""
import argparse
import hashlib
import pathlib

# Until the legal review is done the notices say so; changing this is a change of the recipe (a new build).
STATUS = 'DRAFT — prepared for review; NOT YET LEGALLY REVIEWED. Not a statement that every obligation is met.'
RULE = '=' * 100


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def text(path):
    return path.read_text(encoding='utf-8').replace('\r\n', '\n').rstrip()


def section(title, body):
    return f'\n{RULE}\n{title}\n{RULE}\n\n{body}\n'


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--recipe', required=True)
    p.add_argument('--out', required=True)
    a = p.parse_args()
    recipe, out = pathlib.Path(a.recipe), pathlib.Path(a.out)
    lic = out / 'licenses'

    lock = {}
    for line in (recipe / 'sources.lock').read_text().splitlines():
        if line.strip() and not line.startswith('#'):
            name, kind, version, location, pin, licence, files = line.split('\t')
            lock[name] = (version, pin, licence)

    binaries = '\n'.join(f'  {exe.name:<12} {exe.stat().st_size:>10} bytes  SHA-256 {sha256(exe)}'
                         for exe in sorted((out / 'bin').glob('*.exe')))
    rows = '\n'.join(f'  {name:<12} {version:<16} {pin:<64}  {licence}' for name, (version, pin, licence) in lock.items())
    files = '\n'.join(f'  licenses/{f.relative_to(lic).as_posix()}' for f in sorted(lic.rglob('*')) if f.is_file())

    head = f"""\
THIRD-PARTY NOTICES — AiVideoEditor minimal FFmpeg 9.0.2 (aive-minimal), win64
===============================================================================

*** {STATUS} ***

This file accompanies ffmpeg.exe and ffprobe.exe of the minimal FFmpeg build used by AiVideoEditor
(DECISIONS.md D046 §13), built for Windows x64 by the recipe tools/ffmpeg-minimal of
https://github.com/Proskagit/boulder-video-editor from the sources listed below.

{binaries}

LICENCE OF THE BINARIES
-----------------------
FFmpeg was configured with --enable-gpl (without --enable-version3 and without --enable-nonfree) and is statically
linked with libx264, licensed under the GNU General Public License version 2 or later. ffmpeg.exe and ffprobe.exe are
therefore distributed under the GNU General Public License, version 2 or (at your option) any later version; its text is
in licenses/ffmpeg/COPYING.GPLv2 and reproduced below. The complete corresponding source code is published as
aive-ffmpeg-minimal-9.0.2-sources.tar next to these binaries in their release.

AiVideoEditor runs these programs as separate processes; this file concerns the FFmpeg binaries only and does not state
the licence of AiVideoEditor.

COMPONENTS INCLUDED IN THE BINARIES (name, version, pinned SHA-256 or commit, licence)
--------------------------------------------------------------------------------------
{rows}

TOOLCHAIN RUNTIME LINKED STATICALLY INTO THE BINARIES
-----------------------------------------------------
  mingw-w64 runtime 12.0.0 (Debian mingw-w64 12.0.0-5): its licence asks that its notices accompany binaries;
      they are in licenses/mingw-w64-runtime/COPYING.MinGW-w64-runtime.txt and reproduced below.
  GCC 14.2.0 runtime libraries libgcc and libstdc++ (Debian gcc-mingw-w64 27, built from gcc-14 14.2.0-19): GPL version
      3 or later with the GCC Runtime Library Exception 3.1; texts in licenses/gcc-runtime/.
  Windows (win32) threads; winpthreads is not linked. The binaries import only Windows system libraries.

NOT INCLUDED
------------
  No libx265, libdvdcss, libdvdread, libdvdnav, OpenH264, LCEVCdec, rubberband or other external library; no
  non-free component; no network support.

PATENTS
-------
The codecs these programs implement (among them H.264, HEVC, MPEG-4 Part 2, AAC, AV1, VP8, VP9) may be covered by
patents in some countries. This file grants no patent licence and makes no statement about patents.

LICENCE FILES IN THIS PACKAGE
-----------------------------
{files}
"""
    gpl2 = text(lic / 'ffmpeg' / 'COPYING.GPLv2')
    x264 = text(lic / 'x264' / 'COPYING')
    parts = [
        head,
        section('FFmpeg 9.0.2 — licence summary (licenses/ffmpeg/LICENSE.md)', text(lic / 'ffmpeg' / 'LICENSE.md')),
        section('GNU GENERAL PUBLIC LICENSE, Version 2 — the licence of the binaries (licenses/ffmpeg/COPYING.GPLv2)', gpl2),
        section('GNU LESSER GENERAL PUBLIC LICENSE, Version 2.1 — most FFmpeg source files (licenses/ffmpeg/COPYING.LGPLv2.1)',
                text(lic / 'ffmpeg' / 'COPYING.LGPLv2.1')),
        section('x264 — Copyright (C) 2003-2025 x264 project; GPL version 2 or later (licenses/x264/COPYING)',
                'Identical to the GPL version 2 text above.' if x264.split() == gpl2.split() else x264),
        section('zimg and graphengine — WTFPL version 2; no copyright notice in their sources (licenses/zimg/COPYING, '
                'licenses/graphengine/COPYING)',
                text(lic / 'zimg' / 'COPYING') if text(lic / 'zimg' / 'COPYING') == text(lic / 'graphengine' / 'COPYING')
                else text(lic / 'zimg' / 'COPYING') + '\n\n--- graphengine ---\n\n' + text(lic / 'graphengine' / 'COPYING')),
        section('zlib 1.3.2 — Zlib licence (licenses/zlib/LICENSE)', text(lic / 'zlib' / 'LICENSE')),
        section('dav1d 1.5.4 — BSD-2-Clause (licenses/dav1d/COPYING)', text(lic / 'dav1d' / 'COPYING')),
        section('mingw-w64 runtime 12.0.0 — notices (licenses/mingw-w64-runtime/COPYING.MinGW-w64-runtime.txt)',
                text(lic / 'mingw-w64-runtime' / 'COPYING.MinGW-w64-runtime.txt')),
        section('GCC Runtime Library Exception 3.1 — libgcc, libstdc++ (licenses/gcc-runtime/COPYING.RUNTIME; '
                'the GPL version 3 it refers to: licenses/gcc-runtime/COPYING3)',
                text(lic / 'gcc-runtime' / 'COPYING.RUNTIME')),
    ]
    (out / 'THIRD-PARTY-NOTICES.txt').write_bytes(''.join(parts).replace('\n', '\r\n').encode('utf-8'))


if __name__ == '__main__':
    main()
