# Minimal FFmpeg for the portable build

The recipe of the FFmpeg the portable build is to ship (DECISIONS.md D046 §13). **Not integrated**: the application, its
tests and `ci.yml` keep using the pinned gyan.dev 9.0.1; nothing here is released or copied into the portable folder.

| File | Purpose |
|---|---|
| `sources.lock` | Every source: FFmpeg 9.0.2, x264 `0480cb05fa`, zimg `67e0603271` + graphengine `cb5b2ce133`, zlib 1.3.2, dav1d 1.5.4 — pinned by SHA-256 (tarballs) or full commit (git), with the licence |
| `components.txt` | The FFmpeg components enabled after `--disable-everything` |
| `Dockerfile` | The toolchain: `debian:trixie-20261020` by digest, packages from `snapshot.debian.org` at `20261020T000000Z`, every version pinned (mingw-w64 GCC 14.2.0 win32 threads, mingw-w64 12.0.0, binutils 2.44, nasm, meson, ninja, autotools) |
| `fetch-sources.sh` | The only step with network: fetches and verifies the sources (SHA-256, FFmpeg's GPG signature by `FCF986EA…58D8`, commits), keeps git components as bundles, fetches the Debian sources of the statically linked toolchain runtime (`mingw-w64`, `gcc-mingw-w64`, `gcc-14`), writes `SHA256SUMS` |
| `build.sh` | Builds without network (refuses to run with it): zlib, x264, zimg, dav1d, FFmpeg; fails if configure enables an external library beyond zlib / libx264 / libzimg / libdav1d (+ d3d11va / dxva2) or drops an agreed component; writes the binaries, the licence texts, the configuration and the manifest |
| `make-notices.py` | `THIRD-PARTY-NOTICES.txt` packed with the binaries: their SHA-256, the licence of the binaries (GPLv2 or later), the copyright notices (FFmpeg's lines as the programs print them, derived from `fftools/` and `config.h`), the Independent JPEG Group statement, the components, the statically linked toolchain runtime, and every licence text of `licenses/` reproduced — including the AOMedia Patent License, the IJG terms, the mingw-w64 runtime's notices and GCC's runtime library exception |
| `licenses/AOM-PATENT-LICENSE-1.0.txt` | The Alliance for Open Media Patent License 1.0, byte for byte the official text — libaom's `PATENTS` at tag `v3.15.2` (commit `af3dc9aadc793c1b00edc003e3fc19a44fdeb574`, https://aomedia.googlesource.com/aom), SHA-256 `661fb8e5…6e61`, checked by `build.sh`; its §1.2.1 asks to reproduce it with binary AV1 implementations (dav1d). dav1d's own `doc/PATENTS` is the same text with a typo |
| `make-manifest.py` | `manifest.json` / `MANIFEST.md`: components, revisions, licences, source checksums, toolchain, configuration, binaries' sizes and SHA-256 |
| `Test-PackageNotices.ps1` | Checks the unpacked zip: every licence file present and not empty (incl. the AOMedia licence and the three IJG headers), the AOMedia licence equal to the official text, both binaries' SHA-256 and the copyright line each prints with `-version` named in `THIRD-PARTY-NOTICES.txt`, the IJG statement in it, every licence text reproduced in it; exits 0 / 1 (failures listed) / 2 (could not run) whatever the calling shell |
| `Test-MinimalFfmpeg.ps1` | Windows check of the binaries against the application's real command lines (see below) |

## Building

The workflow `.github/workflows/ffmpeg-minimal.yml` (separate from `ci.yml`; on `workflow_dispatch` or a push touching
these files): builds the image, fetches the sources, builds **twice** in fresh containers with `--network=none` and
fails unless both binaries are identical, packs `aive-ffmpeg-minimal-9.0.2-win64.zip` (binaries, `THIRD-PARTY-NOTICES.txt`, licences, manifest)
and `aive-ffmpeg-minimal-9.0.2-sources.tar` (every source archive, the Debian source packages, this recipe, the image id)
with `SHA256SUMS` as 30-day artifacts, then runs the Windows check. By hand on Linux with Docker:

```bash
docker build -t aive-ffmpeg-toolchain tools/ffmpeg-minimal
docker run --rm -v "$PWD/tools/ffmpeg-minimal:/recipe:ro" -v "$PWD/sources:/sources" aive-ffmpeg-toolchain bash /recipe/fetch-sources.sh
docker run --rm --network=none -v "$PWD/tools/ffmpeg-minimal:/recipe:ro" -v "$PWD/sources:/sources:ro" -v "$PWD/out:/out" aive-ffmpeg-toolchain bash /recipe/build.sh
```

The corresponding-source check is the build itself: `build.sh` runs only from the fetched `sources` (verified against
`SHA256SUMS`) without network, and the second build must give the same bytes. A successful build does **not** by itself
meet the GPL's obligations (the source offer, its storage, the notices): see D046 §13.

## Checking the binaries

```powershell
pwsh -File tools/ffmpeg-minimal/Test-MinimalFfmpeg.ps1 -Candidate <folder with the minimal ffmpeg.exe / ffprobe.exe> `
    -Reference <gyan.dev 9.0.1 bin folder> -EnabledConfig <out/enabled-config.txt> -Out <report folder>
```

The reference makes the fixtures (the minimal build has no encoders for them) and decodes them for comparison. Checked:
version and `-buildconf`; the agreed components present, the excluded absent (lists and the binary's strings); every
target format decoded through the application's own `pipe:1` command lines; the export's two runs through `pipe:0`
(H.264 stream without the SEI, PSNR, AAC stream, x264's version string); the HDR graph (`zscale` + `lut3d`); AV1 in
software through `libdav1d` (also under `-hwaccel auto`) and the native decoder with `d3d11va` where the hardware
exists; sizes and SHA-256. `FAIL` fails the run; `DIFF` (works, output not identical to 9.0.1's) is for review.
