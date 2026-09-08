#!/usr/bin/env bash
# Cross-compiles tor (0.4.9.11 + TorJet conflux extensions from ../tor-src)
# plus libevent/openssl/zlib/zstd for Android ARM64 (aarch64) using the NDK.
# Produces a standalone executable and geoip/geoip6 ready to embed in the app.
#
# Usage:
#   NDK=/path/to/ndk (or ANDROID_NDK_HOME) ./build-android-tor.sh <tor-src-dir> <out-dir>
set -euo pipefail

TORS="$1"
OUT="$2"

NDK="${ANDROID_NDK_HOME:-${NDK:-}}"
if [ -z "$NDK" ]; then
  echo "::error::NDK not set (set ANDROID_NDK_HOME)"; exit 1
fi
# Some deps (openssl Configure) look for ANDROID_NDK_ROOT; others prefer HOMEs.
export ANDROID_NDK_ROOT="${ANDROID_NDK_ROOT:-$NDK}"
export ANDROID_NDK_HOME="$NDK"

TRIPLE="aarch64-linux-android24"
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/linux-x86_64"
CC="$TOOLCHAIN/bin/${TRIPLE}-clang"
CXX="$TOOLCHAIN/bin/${TRIPLE}-clang++"
AR="$TOOLCHAIN/bin/llvm-ar"
RANLIB="$TOOLCHAIN/bin/llvm-ranlib"
STRIP="$TOOLCHAIN/bin/llvm-strip"
SYSROOT="$TOOLCHAIN/sysroot"

API=24
export CC CXX AR RANLIB STRIP SYSROOT
export CFLAGS="--sysroot=$SYSROOT -O2 -fPIC -fno-stack-protector -fvisibility=hidden -DANDROID"
export CXXFLAGS="$CFLAGS"
export CPPFLAGS="--sysroot=$SYSROOT -I$OUT/include"
export LDFLAGS="--sysroot=$SYSROOT -L$OUT/lib -Wl,-rpath-link=$OUT/lib"
export PKG_CONFIG_PATH="$OUT/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$OUT/lib/pkgconfig"
export PATH="$TOOLCHAIN/bin:$PATH"

DEPS="$OUT/deps-src"
mkdir -p "$DEPS" "$OUT/include" "$OUT/lib"

# Build zlib
if [ ! -f "$OUT/lib/libz.a" ]; then
  curl -fsSL https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz -o zlib.tar.gz
  tar xzf zlib.tar.gz -C "$DEPS"
  ( cd "$DEPS/zlib-1.3.1" && \
    CC="$CC" AR="$AR" RANLIB="$RANLIB" CFLAGS="$CFLAGS" \
    ./configure --static --prefix="$OUT" && make -j$(nproc) && make install )
fi

# Build libevent
if [ ! -f "$OUT/lib/libevent.a" ]; then
  curl -fsSL https://github.com/libevent/libevent/releases/download/release-2.1.12-stable/libevent-2.1.12-stable.tar.gz -o libevent.tar.gz
  tar xzf libevent.tar.gz -C "$DEPS"
  ( cd "$DEPS/libevent-2.1.12-stable" && \
    ./configure --host=aarch64-linux-android --prefix="$OUT" \
      --disable-shared --enable-static --disable-openssl --disable-samples \
      --disable-libevent-regress --disable-debug-mode && \
    make -j$(nproc) && make install )
fi

# Build openssl
if [ ! -f "$OUT/lib/libssl.a" ]; then
  curl -fsSL https://github.com/openssl/openssl/releases/download/openssl-3.3.2/openssl-3.3.2.tar.gz -o openssl.tar.gz
  tar xzf openssl.tar.gz -C "$DEPS"
  ( cd "$DEPS/openssl-3.3.2" && \
    ./Configure android-aarch64 -D__ANDROID_API__=$API --prefix="$OUT" no-shared no-tests && \
    make -j$(nproc) && make install_sw )
fi

# Build zstd
if [ ! -f "$OUT/lib/libzstd.a" ]; then
  curl -fsSL https://github.com/facebook/zstd/releases/download/v1.5.6/zstd-1.5.6.tar.gz -o zstd.tar.gz
  tar xzf zstd.tar.gz -C "$DEPS"
  ( cd "$DEPS/zstd-1.5.6" && \
    CC="$CC" CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" \
    make -C lib -j$(nproc) libzstd.a && \
    mkdir -p "$OUT/lib" "$OUT/include" && \
    cp lib/libzstd.a "$OUT/lib/" && cp lib/zstd.h "$OUT/include/" )
fi

# Build tor with TorJet extensions
cp -r "$TORS" "$OUT/tor-build"
cd "$OUT/tor-build"
./configure --host=aarch64-linux-android --prefix="$OUT" \
  --disable-asciidoc --disable-man --disable-html-docs \
  --disable-tool-name-check --disable-system-torrc --disable-nls \
  --enable-static-tor --with-zlib-dir="$OUT" --with-openssl-dir="$OUT" \
  --with-libevent-dir="$OUT" --with-zstd-dir="$OUT"
# Restamp to avoid maintainer regenerate
find . -name '*.m4' -o -name 'Makefile.am' | xargs touch || true
make -j$(nproc)
mkdir -p "$OUT/bin"
cp src/app/tor "$OUT/bin/tor"
cp src/config/geoip src/config/geoip6 "$OUT/bin/"
$STRIP "$OUT/bin/tor"
echo "Built:"
ls -la "$OUT/bin/"
