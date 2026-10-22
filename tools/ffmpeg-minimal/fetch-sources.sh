#!/usr/bin/env bash
# Fetches every source of the minimal FFmpeg into SOURCES (default /sources) and verifies it against sources.lock:
# the release tarballs by SHA-256 (and FFmpeg's by its GPG signature), the git components by their full commit, kept as
# git bundles (complete history up to the pinned commit — x264's version string is computed from it). Also fetches the
# Debian source packages of the toolchain runtime linked statically into the binaries (the mingw-w64 CRT and libgcc /
# libstdc++). This is the only step with network access; build.sh then runs without it. Run inside the toolchain image.
set -euo pipefail

RECIPE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCES="${SOURCES:-/sources}"
FFMPEG_KEY_FINGERPRINT="FCF986EA15E6E293A5644F10B4322F04D67658D8"

mkdir -p "$SOURCES/tarballs" "$SOURCES/git" "$SOURCES/debian" "$SOURCES/keys"

while IFS=$'\t' read -r name kind version location pin licence files; do
    [[ -z "$name" || "$name" == \#* ]] && continue
    case "$kind" in
    tarball)
        file="$SOURCES/tarballs/$(basename "$location")"
        curl -fsSL --retry 5 -o "$file" "$location"
        echo "$pin  $file" | sha256sum -c -
        ;;
    git)
        repo="$(mktemp -d)"
        git clone --quiet --no-checkout "$location" "$repo"
        [[ "$(git -C "$repo" rev-parse --verify "$pin^{commit}")" == "$pin" ]] || { echo "$name: $pin not found" >&2; exit 1; }
        git -C "$repo" branch --force pinned "$pin"
        git -C "$repo" bundle create "$SOURCES/git/$name.bundle" pinned
        git bundle verify "$SOURCES/git/$name.bundle" >/dev/null
        rm -rf "$repo"
        ;;
    *) echo "unknown kind '$kind' for $name" >&2; exit 1 ;;
    esac
done < "$RECIPE/sources.lock"

# FFmpeg's release signature, checked against the release key's fingerprint published on ffmpeg.org.
curl -fsSL --retry 5 -o "$SOURCES/tarballs/ffmpeg-9.0.2.tar.xz.asc" https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz.asc
curl -fsSL --retry 5 -o "$SOURCES/keys/ffmpeg-devel.asc" https://ffmpeg.org/ffmpeg-devel.asc
export GNUPGHOME="$(mktemp -d)"
gpg --batch --quiet --import "$SOURCES/keys/ffmpeg-devel.asc"
gpg --batch --status-fd 1 --verify "$SOURCES/tarballs/ffmpeg-9.0.2.tar.xz.asc" "$SOURCES/tarballs/ffmpeg-9.0.2.tar.xz" \
    | grep -q "VALIDSIG $FFMPEG_KEY_FINGERPRINT" || { echo "FFmpeg tarball: no valid signature by $FFMPEG_KEY_FINGERPRINT" >&2; exit 1; }
rm -rf "$GNUPGHOME"

# The statically linked toolchain runtime: the mingw-w64 CRT and headers, GCC's libgcc / libstdc++ (gcc-mingw-w64 is
# built from gcc-14's sources). Versions as installed in the image (Dockerfile).
(
    cd "$SOURCES/debian"
    apt-get update -qq
    apt-get source --download-only mingw-w64=12.0.0-5 gcc-mingw-w64=27 gcc-14=14.2.0-19
)

(cd "$SOURCES" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)
echo "Sources in $SOURCES:"
du -sh "$SOURCES"/*
