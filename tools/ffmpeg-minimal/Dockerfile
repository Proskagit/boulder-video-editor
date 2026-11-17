# The toolchain image of the minimal FFmpeg (DECISIONS.md D046 §13).
# Base: Debian 13 "trixie", the dated tag trixie-20261020 pinned by its digest. Packages: the Debian archive as it was
# at SNAPSHOT (snapshot.debian.org never changes a published state) with every version pinned, so the same Dockerfile
# always gives the same toolchain. The archive is read over HTTP: the base image has no CA certificates, and apt
# checks every index against the Release file's signature by the Debian archive keys the image ships. The image only
# holds tools: the sources come from fetch-sources.sh and the build runs without network (build.sh).
FROM debian:trixie-20261020@sha256:4c25275446ca371766b8b093c696782eabce06263fccacb8d5d813b3dc44b308

ARG SNAPSHOT=20261020T000000Z
ENV DEBIAN_FRONTEND=noninteractive LANG=C.UTF-8 LC_ALL=C.UTF-8 TZ=UTC

RUN rm -f /etc/apt/sources.list.d/* \
 && printf '%s\n' \
      "deb [check-valid-until=no] http://snapshot.debian.org/archive/debian/${SNAPSHOT} trixie main" \
      "deb-src [check-valid-until=no] http://snapshot.debian.org/archive/debian/${SNAPSHOT} trixie main" \
      > /etc/apt/sources.list \
 && printf 'Acquire::Retries "5";\nAPT::Install-Recommends "false";\n' > /etc/apt/apt.conf.d/99minimal-ffmpeg \
 && apt-get update \
 && apt-get install -y \
      gcc-mingw-w64-x86-64-win32=14.2.0-19+27+b1 \
      g++-mingw-w64-x86-64-win32=14.2.0-19+27+b1 \
      gcc-mingw-w64-x86-64-win32-runtime=14.2.0-19+27+b1 \
      gcc-mingw-w64-base=14.2.0-19+27+b1 \
      binutils-mingw-w64-x86-64=2.44-3+12+b1 \
      mingw-w64-x86-64-dev=12.0.0-5 \
      mingw-w64-common=12.0.0-5 \
      nasm=2.16.03-1 \
      meson=1.7.0-1 \
      ninja-build=1.12.1-1 \
      make=4.4.1-2 \
      pkgconf=1.8.1-4 \
      autoconf=2.72-3.1 \
      automake=1:1.17-4 \
      libtool=2.5.4-4 \
      git=1:2.47.3-0+deb13u1 \
      xz-utils=5.8.1-1+deb13u1 \
      ca-certificates=20250419 \
      curl=8.14.1-2+deb13u5 \
      python3=3.13.5-1 \
      perl=5.40.1-6+deb13u1 \
      patch=2.8-2 \
      dpkg-dev=1.22.22 \
      gnupg=2.4.7-21+deb13u1 \
 && rm -rf /var/lib/apt/lists/*

# The toolchain's own record, read by make-manifest.py.
RUN dpkg-query -W -f='${Package}\t${Version}\t${source:Package}\t${source:Version}\n' > /toolchain-packages.tsv \
 && echo "${SNAPSHOT}" > /toolchain-snapshot.txt

WORKDIR /build
