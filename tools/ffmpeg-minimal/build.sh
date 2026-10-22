#!/usr/bin/env bash
# Builds the minimal FFmpeg (DECISIONS.md D046 §13) for win64 from SOURCES (fetch-sources.sh) into OUT, without network:
# run it with `docker run --network=none`. zlib, x264, zimg (with graphengine) and dav1d are built as static libraries,
# then ffmpeg.exe / ffprobe.exe with --disable-everything and only components.txt enabled. The build fails if configure
# enables any external library beyond zlib, libx264, libzimg and libdav1d.
set -euo pipefail

RECIPE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOURCES="${SOURCES:-/sources}"
OUT="${OUT:-/out}"
WORK=/build            # fixed path: it appears in the binaries (assert messages), so both builds must use it
DEPS=$WORK/deps
HOST=x86_64-w64-mingw32
JOBS="$(nproc)"

# Reproducibility: the FFmpeg 9.0.2 tag's date; no PE timestamps; a fixed locale, time zone and umask.
export SOURCE_DATE_EPOCH=1789692057 TZ=UTC LC_ALL=C.UTF-8
umask 022

# Nothing may come from the network from here on.
if curl -fsS --max-time 5 -o /dev/null https://ffmpeg.org/ 2>/dev/null; then
    echo "build.sh must run without network (docker run --network=none)" >&2
    exit 1
fi

(cd "$SOURCES" && sha256sum --quiet -c SHA256SUMS)

rm -rf "$WORK/src" "$DEPS" && mkdir -p "$WORK/src" "$DEPS" "$OUT"
export PKG_CONFIG_LIBDIR="$DEPS/lib/pkgconfig" PKG_CONFIG_PATH=""

lock() { awk -F'\t' -v n="$1" -v c="$2" '$1 == n { print $c }' "$RECIPE/sources.lock"; }
checkout() { # name destination
    git clone --quiet "$SOURCES/git/$1.bundle" "$2"
    git -C "$2" checkout --quiet --detach "$(lock "$1" 5)"
    [[ "$(git -C "$2" rev-parse HEAD)" == "$(lock "$1" 5)" ]]
}

# --- zlib (PNG, compressed MP4 / Matroska headers) ---------------------------------------------------------------
tar -xJf "$SOURCES/tarballs/zlib-1.3.2.tar.xz" -C "$WORK/src"
(
    cd "$WORK/src/zlib-1.3.2"
    CHOST=$HOST CFLAGS="-O2" ./configure --prefix="$DEPS" --static
    make -j"$JOBS" && make install
)

# --- x264 (built from its git history, so its version string is "r3223 0480cb0" as in the calibrated build) -------
checkout x264 "$WORK/src/x264"
(
    cd "$WORK/src/x264"
    ./configure --host=$HOST --cross-prefix=$HOST- --prefix="$DEPS" --enable-static --disable-cli \
        --disable-opencl --disable-avs --disable-swscale --disable-lavf --disable-ffms --disable-gpac --disable-lsmash \
        --disable-bashcompletion
    make -j"$JOBS" && make install
)

# --- zimg with its graphengine submodule (zscale) ----------------------------------------------------------------
checkout zimg "$WORK/src/zimg"
rmdir "$WORK/src/zimg/graphengine" 2>/dev/null || true
checkout graphengine "$WORK/src/zimg/graphengine"
(
    cd "$WORK/src/zimg"
    ./autogen.sh
    ./configure --host=$HOST --prefix="$DEPS" --enable-static --disable-shared
    make -j"$JOBS" && make install
)

# --- dav1d (software AV1) ----------------------------------------------------------------------------------------
tar -xJf "$SOURCES/tarballs/dav1d-1.5.4.tar.xz" -C "$WORK/src"
cat > "$WORK/cross-mingw64.txt" <<EOF
[binaries]
c = '$HOST-gcc'
cpp = '$HOST-g++'
ar = '$HOST-ar'
strip = '$HOST-strip'
windres = '$HOST-windres'
nasm = 'nasm'
pkg-config = 'pkg-config'

[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
(
    cd "$WORK/src/dav1d-1.5.4"
    meson setup build --cross-file "$WORK/cross-mingw64.txt" --prefix="$DEPS" --libdir=lib --buildtype=release \
        --default-library=static -Denable_tools=false -Denable_tests=false -Denable_examples=false
    ninja -C build -j"$JOBS" && ninja -C build install
)

# --- FFmpeg --------------------------------------------------------------------------------------------------------
tar -xJf "$SOURCES/tarballs/ffmpeg-9.0.2.tar.xz" -C "$WORK/src"
mapfile -t components < <(awk '!/^#/ && NF == 2 { print "--enable-" $1 "=" $2 }' "$RECIPE/components.txt")
configure_args=(
    --prefix="$WORK/ffmpeg-install"
    --target-os=mingw32 --arch=x86_64 --cross-prefix=$HOST- --enable-cross-compile
    --pkg-config=pkg-config --pkg-config-flags=--static
    --enable-gpl
    --disable-autodetect --disable-everything
    --enable-static --disable-shared
    --disable-ffplay --disable-doc --disable-network --disable-debug
    --enable-w32threads --enable-zlib --enable-libx264 --enable-libzimg --enable-libdav1d --enable-d3d11va --enable-dxva2
    --extra-cflags="-I$DEPS/include"
    --extra-ldflags="-L$DEPS/lib -static -Wl,--no-insert-timestamp"
    --extra-libs="-lstdc++"
    --extra-version=aive-minimal
    "${components[@]}"
)
(
    cd "$WORK/src/ffmpeg-9.0.2"
    ./configure "${configure_args[@]}"
    make -j"$JOBS"
    make install
)

# --- Checks on the configuration: only the agreed external libraries ------------------------------------------------
config_h="$WORK/src/ffmpeg-9.0.2/config.h"
enabled_external="$(
    sed -n '/^EXTERNAL_AUTODETECT_LIBRARY_LIST="/,/^"/p; /^EXTERNAL_LIBRARY_LIST="/,/^"/p; /^EXTERNAL_LIBRARY_GPL_LIST="/,/^"/p;
            /^EXTERNAL_LIBRARY_NONFREE_LIST="/,/^"/p; /^EXTERNAL_LIBRARY_VERSION3_LIST="/,/^"/p; /^EXTERNAL_LIBRARY_GPLV3_LIST="/,/^"/p;
            /^HWACCEL_AUTODETECT_LIBRARY_LIST="/,/^"/p; /^HWACCEL_LIBRARY_LIST="/,/^"/p; /^HWACCEL_LIBRARY_NONFREE_LIST="/,/^"/p' \
        "$WORK/src/ffmpeg-9.0.2/configure" \
    | tr -s ' "\n' '\n' | grep -E '^[a-z0-9_]+$' | sort -u \
    | while read -r lib; do
          up="$(tr '[:lower:]' '[:upper:]' <<<"$lib")"
          grep -q "^#define CONFIG_${up} 1$" "$config_h" && echo "$lib"
      done | sort -u | tr '\n' ' '
)"
echo "Enabled external / hardware libraries: $enabled_external"
expected="d3d11va dxva2 libdav1d libx264 libzimg zlib "
[[ "$enabled_external" == "$expected" ]] || {
    echo "Unexpected external libraries: got '$enabled_external', expected '$expected'" >&2
    exit 1
}
# Every agreed component really enabled (configure silently drops one whose dependencies are missing, e.g. a hwaccel
# whose DXVA structures the mingw-w64 headers lack).
# The components' switches are in config_components.h (since FFmpeg 5.1), the libraries' in config.h.
components_h="$WORK/src/ffmpeg-9.0.2/config_components.h"
missing=""
while read -r kind name; do
    up="$(tr '[:lower:]' '[:upper:]' <<<"${name}_${kind}")"
    grep -q "^#define CONFIG_${up} 1$" "$components_h" || missing+=" $kind:$name"
done < <(awk '!/^#/ && NF == 2' "$RECIPE/components.txt")
[[ -z "$missing" ]] || { echo "Components not enabled:$missing" >&2; exit 1; }
grep -q '^#define CONFIG_GPL 1$' "$config_h"
grep -q '^#define CONFIG_VERSION3 0$' "$config_h"
grep -q '^#define CONFIG_NONFREE 0$' "$config_h"

# --- Output ----------------------------------------------------------------------------------------------------------
mkdir -p "$OUT/bin" "$OUT/licenses"
cp "$WORK/ffmpeg-install/bin/ffmpeg.exe" "$WORK/ffmpeg-install/bin/ffprobe.exe" "$OUT/bin/"
sed -n 's/^#define FFMPEG_CONFIGURATION "\(.*\)"$/\1/p' "$config_h" > "$OUT/ffmpeg-configuration.txt"
grep -E '^CONFIG_[A-Z0-9_]+=yes$' "$WORK/src/ffmpeg-9.0.2/ffbuild/config.mak" | sed 's/=yes$//' | sort > "$OUT/enabled-config.txt"

copy_licence() { # component source-dir file...
    local c="$1" dir="$2"; shift 2
    mkdir -p "$OUT/licenses/$c"
    for f in "$@"; do cp "$dir/$f" "$OUT/licenses/$c/"; done
}
copy_licence ffmpeg   "$WORK/src/ffmpeg-9.0.2" COPYING.GPLv2 COPYING.LGPLv2.1 LICENSE.md
copy_licence x264     "$WORK/src/x264" COPYING
copy_licence zimg     "$WORK/src/zimg" COPYING
copy_licence graphengine "$WORK/src/zimg/graphengine" COPYING
copy_licence zlib     "$WORK/src/zlib-1.3.2" LICENSE
copy_licence dav1d    "$WORK/src/dav1d-1.5.4" COPYING

# The toolchain runtime linked statically: mingw-w64's notices (its licence asks for them with binaries) and GCC's runtime
# library exception with the GPL version 3 it refers to — read from the Debian source packages in SOURCES, the same ones
# offered as source, so the texts match the pinned toolchain exactly.
mkdir -p "$OUT/licenses/mingw-w64-runtime" "$OUT/licenses/gcc-runtime" "$WORK/gcc-licence"
tar -xjOf "$SOURCES/debian/mingw-w64_12.0.0.orig.tar.bz2" \
    mingw-w64-v12.0.0/COPYING.MinGW-w64-runtime/COPYING.MinGW-w64-runtime.txt \
    > "$OUT/licenses/mingw-w64-runtime/COPYING.MinGW-w64-runtime.txt"
tar -xzOf "$SOURCES/debian/gcc-14_14.2.0.orig.tar.gz" gcc-14-14.2.0/gcc-14.2.0-dfsg.tar.xz \
    | tar -xJ -C "$WORK/gcc-licence" gcc-14.2.0/COPYING.RUNTIME gcc-14.2.0/COPYING3
cp "$WORK/gcc-licence/gcc-14.2.0/COPYING.RUNTIME" "$WORK/gcc-licence/gcc-14.2.0/COPYING3" "$OUT/licenses/gcc-runtime/"
for f in "$OUT/licenses/mingw-w64-runtime/COPYING.MinGW-w64-runtime.txt" "$OUT/licenses/gcc-runtime/COPYING.RUNTIME" \
         "$OUT/licenses/gcc-runtime/COPYING3"; do
    [[ -s "$f" ]] || { echo "Missing licence text: $f" >&2; exit 1; }
done

python3 "$RECIPE/make-notices.py" --recipe "$RECIPE" --out "$OUT"
python3 "$RECIPE/make-manifest.py" --recipe "$RECIPE" --sources "$SOURCES" --out "$OUT"
echo "Built:"
sha256sum "$OUT/bin/"*.exe
