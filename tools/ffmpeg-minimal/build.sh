#!/usr/bin/env bash
# Builds the minimal FFmpeg (DECISIONS.md D046 §13) for win64 from SOURCES (fetch-sources.sh) into OUT, without network:
# run it with `docker run --network=none`. zlib, x264, x265, SVT-AV1, zimg (with graphengine) and dav1d are built as static libraries,
# then ffmpeg.exe / ffprobe.exe with --disable-everything and only components.txt enabled. The build fails if configure
# enables any external library beyond zlib, libx264, libx265, libsvtav1 (Phase 57), libzimg, libdav1d and NVENC (ffnvcodec: nv-codec-headers, headers
# only — the NVIDIA driver is loaded at run time; S1, D066).
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

# --- CMake cross toolchain (x265, SVT-AV1) ---------------------------------------------------------------------------
cat > "$WORK/cross-mingw64.cmake" <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR AMD64)
set(CMAKE_C_COMPILER $HOST-gcc)
set(CMAKE_CXX_COMPILER $HOST-g++)
set(CMAKE_RC_COMPILER $HOST-windres)
set(CMAKE_ASM_NASM_COMPILER nasm)
set(CMAKE_FIND_ROOT_PATH /usr/$HOST $DEPS)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
EOF

# --- x265 (software HEVC, 8-bit only; Phase 57, D076) ---------------------------------------------------------------
# Its version comes from `git describe --tags`: the bundle has no tags, so the pinned commit is tagged with the version.
checkout x265 "$WORK/src/x265"
git -C "$WORK/src/x265" tag "$(lock x265 3)" HEAD
(
    cd "$WORK/src/x265"
    cmake -S source -B build-win64 -G Ninja -DCMAKE_TOOLCHAIN_FILE="$WORK/cross-mingw64.cmake" -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="$DEPS" -DENABLE_SHARED=OFF -DENABLE_CLI=OFF -DENABLE_ASSEMBLY=ON \
        -DCMAKE_ASM_NASM_FLAGS=-w-macro-params-legacy
    ninja -C build-win64 -j"$JOBS" && ninja -C build-win64 install
)
# CMake lists the toolchain's implicit libraries in x265.pc; gcc_s would import libgcc_s_seh-1.dll - the static libgcc is
# linked anyway.
sed -i 's/ -lgcc_s//g' "$DEPS/lib/pkgconfig/x265.pc"

# --- SVT-AV1 (software AV1 encoder; Phase 57, D076) ----------------------------------------------------------------
checkout svt-av1 "$WORK/src/svt-av1"
git -C "$WORK/src/svt-av1" tag "v$(lock svt-av1 3)" HEAD
(
    cd "$WORK/src/svt-av1"
    cmake -S . -B build-win64 -G Ninja -DCMAKE_TOOLCHAIN_FILE="$WORK/cross-mingw64.cmake" -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="$DEPS" -DCMAKE_INSTALL_LIBDIR=lib -DBUILD_SHARED_LIBS=OFF -DBUILD_APPS=OFF -DBUILD_DEC=OFF \
        -DBUILD_TESTING=OFF
    ninja -C build-win64 -j"$JOBS" && ninja -C build-win64 install
)
echo "x265.pc / SvtAv1Enc.pc private libraries:"
grep -h '^Libs' "$DEPS/lib/pkgconfig/x265.pc" "$DEPS/lib/pkgconfig/SvtAv1Enc.pc"

# --- nv-codec-headers (NVENC; headers and ffnvcodec.pc only, nothing compiled) -------------------------------------
checkout nv-codec-headers "$WORK/src/nv-codec-headers"
(
    cd "$WORK/src/nv-codec-headers"
    make install PREFIX="$DEPS"
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
    --enable-w32threads --enable-zlib --enable-libx264 --enable-libx265 --enable-libsvtav1 --enable-libzimg --enable-libdav1d --enable-d3d11va --enable-dxva2
    --enable-ffnvcodec --enable-nvenc
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
# cuda: the CUDA driver loader of nv-codec-headers that NVENC selects (dynamic loading of nvcuda.dll; no CUDA SDK, no
# code of NVIDIA linked in).
expected="cuda d3d11va dxva2 ffnvcodec libdav1d libsvtav1 libx264 libx265 libzimg nvenc zlib "
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
copy_licence x265     "$WORK/src/x265" COPYING
copy_licence svt-av1  "$WORK/src/svt-av1" LICENSE.md PATENTS.md
# nv-codec-headers has no licence file: each header carries its MIT terms and copyright in its first comment block, copied
# here verbatim (as the IJG headers below).
mkdir -p "$OUT/licenses/nv-codec-headers"
for f in dynlink_cuda.h dynlink_cuviddec.h dynlink_loader.h dynlink_nvcuvid.h nvEncodeAPI.h; do
    awk '{ print } /\*\// { exit }' "$WORK/src/nv-codec-headers/include/ffnvcodec/$f" > "$OUT/licenses/nv-codec-headers/$f.header.txt"
    grep -q "Permission is hereby granted" "$OUT/licenses/nv-codec-headers/$f.header.txt"         || { echo "No MIT licence header in nv-codec-headers/$f" >&2; exit 1; }
done

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

# AV1 (dav1d): the Alliance for Open Media Patent License 1.0 asks to be reproduced with binary implementations
# (its §1.2.1 b). The text is the official one — libaom's PATENTS at tag v3.15.2 (commit
# af3dc9aadc793c1b00edc003e3fc19a44fdeb574), kept in the recipe byte for byte; dav1d's doc/PATENTS has a typo.
AOM_LICENCE_SHA256=661fb8e504744e95587b556b94a58343448300606a41bea8c7a9b97125696e61
echo "$AOM_LICENCE_SHA256  $RECIPE/licenses/AOM-PATENT-LICENSE-1.0.txt" | sha256sum --quiet -c -
cp "$RECIPE/licenses/AOM-PATENT-LICENSE-1.0.txt" "$OUT/licenses/dav1d/AOM-PATENT-LICENSE-1.0.txt"

# The Independent JPEG Group's files compiled into libavcodec (idctdsp: jrevdct.c; fdctdsp: jfdctfst.c, jfdctint.c via
# jfdctint_template.c): their licence asks executables' documentation to credit the IJG (make-notices.py states it)
# and its terms are their header comments, copied here verbatim (the first comment block of each file).
mkdir -p "$OUT/licenses/ffmpeg/ijg"
for f in jrevdct.c jfdctfst.c jfdctint_template.c; do
    awk '{ print } /\*\// { exit }' "$WORK/src/ffmpeg-9.0.2/libavcodec/$f" > "$OUT/licenses/ffmpeg/ijg/$f.header.txt"
    grep -q "Independent JPEG Group" "$OUT/licenses/ffmpeg/ijg/$f.header.txt" \
        || { echo "No IJG licence header in libavcodec/$f" >&2; exit 1; }
done

python3 "$RECIPE/make-notices.py" --recipe "$RECIPE" --out "$OUT" --ffmpeg-src "$WORK/src/ffmpeg-9.0.2"
python3 "$RECIPE/make-manifest.py" --recipe "$RECIPE" --sources "$SOURCES" --out "$OUT"
echo "Built:"
sha256sum "$OUT/bin/"*.exe
