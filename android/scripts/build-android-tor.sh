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
export OUT

# Emit a single-line annotation pointing at the first real error in a log.
emit_error() {
  local log="$1"
  local line b64
  line=$(grep -m1 -iE "error|fail|invalid|fatal|cannot|not found|no such|unable to|conflict|missing|unknown|died|Cannot|Error|undefined reference|ld: " "$log" 2>/dev/null || true)
  # keep under the ~64KB annotation limit: base64 of the last 60 lines only
  b64=$(tail -60 "$log" | base64 -w0 2>/dev/null || true)
  if [ -n "$line" ]; then
    echo "::error::$line"
    echo "::error::$log TAIL-BASE64:$b64"
  else
    echo "::error::step $log failed; TAIL-BASE64:$b64"
  fi
}

# Run a build step; capture output to a log; on failure, annotate + exit.
run_step() {
  local name="$1"; shift
  local log="$OUT/${name}.log"
  echo "::group::$name → $log"
  local rc=0
  "$@" >"$log" 2>&1 || rc=$?
  echo "::endgroup::"
  if [ $rc -ne 0 ]; then
    echo "::error::$name exit=$rc (137=SIGKILL/OOM)"
    emit_error "$log"
    exit 1
  fi
}

NDK="${ANDROID_NDK_HOME:-${NDK:-}}"
if [ -z "$NDK" ]; then
  echo "::error::NDK not set (set ANDROID_NDK_HOME)"; exit 1
fi
# Some deps (openssl Configure) look for ANDROID_NDK_ROOT; others prefer HOMEs.
export ANDROID_NDK_ROOT="${ANDROID_NDK_ROOT:-$NDK}"
export ANDROID_NDK_HOME="$NDK"

TRIPLE="aarch64-linux-android24"
TOOLCHAIN="$NDK/toolchains/llvm/prebuilt/linux-x86_64"
SYSROOT="$TOOLCHAIN/sysroot"
export SYSROOT
export CC="$TOOLCHAIN/bin/${TRIPLE}-clang"
export CXX="$TOOLCHAIN/bin/${TRIPLE}-clang++"
export AR="$TOOLCHAIN/bin/llvm-ar"
export RANLIB="$TOOLCHAIN/bin/llvm-ranlib"
export STRIP="$TOOLCHAIN/bin/llvm-strip"

API=24
export API
export CC CXX AR RANLIB STRIP
# -O1 keeps clang's per-compilation memory low enough for the 7GB GitHub runner
# (-O2 with parallel -j jobs OOMs and silently kills the compile).
export CFLAGS="--sysroot=$SYSROOT -O1 -pipe -fPIC -fno-stack-protector -fvisibility=hidden -DANDROID"
export CXXFLAGS="$CFLAGS"
export CPPFLAGS="--sysroot=$SYSROOT -I$OUT/include"
export LDFLAGS="--sysroot=$SYSROOT -L$OUT/lib -Wl,-rpath-link=$OUT/lib"
export PKG_CONFIG_PATH="$OUT/lib/pkgconfig"
export PKG_CONFIG_LIBDIR="$OUT/lib/pkgconfig"
export PATH="$TOOLCHAIN/bin:$PATH"

JOBS=2
export MAKEFLAGS="-j$JOBS"

DEPS="$OUT/deps-src"
export DEPS
mkdir -p "$DEPS" "$OUT/include" "$OUT/lib"

fetch_dep() {
  local url="$1" file="$2"
  # only redownload if the tarball is not already present
  [ -f "$file" ] || curl -fsSL --retry 3 "$url" -o "$file"
}

# Build zlib
if [ ! -f "$OUT/lib/libz.a" ]; then
  fetch_dep "https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz" zlib.tar.gz
  tar xzf zlib.tar.gz -C "$DEPS"
  run_step zlib \
    bash -c 'cd "$DEPS/zlib-1.3.1" && \
      CC="$CC" AR="$AR" RANLIB="$RANLIB" CFLAGS="$CFLAGS" \
      ./configure --static --prefix="$OUT" && \
      make -j"$JOBS" && make install'
fi

# Build libevent
if [ ! -f "$OUT/lib/libevent.a" ]; then
  fetch_dep "https://github.com/libevent/libevent/releases/download/release-2.1.12-stable/libevent-2.1.12-stable.tar.gz" libevent.tar.gz
  tar xzf libevent.tar.gz -C "$DEPS"
  run_step libevent \
    bash -c 'cd "$DEPS/libevent-2.1.12-stable" && \
      ./configure --host=aarch64-linux-android --prefix="$OUT" \
        --disable-shared --enable-static --disable-openssl --disable-samples \
        --disable-libevent-regress --disable-debug-mode && \
      make -j"$JOBS" && make install'
fi

# Build openssl
if [ ! -f "$OUT/lib/libssl.a" ]; then
  fetch_dep "https://github.com/openssl/openssl/releases/download/openssl-3.3.2/openssl-3.3.2.tar.gz" openssl.tar.gz
  tar xzf openssl.tar.gz -C "$DEPS"
  run_step openssl \
    bash -c 'cd "$DEPS/openssl-3.3.2" && \
      ./Configure android-arm64 -D__ANDROID_API__='"$API"' --prefix="$OUT" no-shared no-tests && \
      make -j"$JOBS" && make install_sw'
fi

# Build zstd
if [ ! -f "$OUT/lib/libzstd.a" ]; then
  fetch_dep "https://github.com/facebook/zstd/releases/download/v1.5.6/zstd-1.5.6.tar.gz" zstd.tar.gz
  tar xzf zstd.tar.gz -C "$DEPS"
  mkdir -p "$OUT/lib" "$OUT/include"
  run_step zstd \
    bash -c 'cd "$DEPS/zstd-1.5.6" && make -C lib -j"$JOBS" libzstd.a && \
      cp lib/libzstd.a "'"$OUT"'/lib/" && cp lib/zstd.h "'"$OUT"'/include/"'
fi

# Build tor with TorJet extensions
if [ ! -f "$OUT/bin/tor" ]; then
  rm -rf "$OUT/tor-build"
  cp -r "$TORS" "$OUT/tor-build"
  run_step tor \
    bash -c 'cd "$OUT/tor-build" && \
      chmod +x configure config.status scripts/build/combine_libs 2>/dev/null || true && \
      ./configure --host=aarch64-linux-android --prefix="$OUT" \
        --disable-asciidoc --disable-man --disable-html-docs \
        --disable-tool-name-check --disable-system-torrc --disable-nls \
        --enable-static-tor --with-zlib-dir="$OUT" --with-openssl-dir="$OUT" \
        --with-libevent-dir="$OUT" --with-zstd-dir="$OUT" && \
      make -j"$JOBS" src/app/tor'
  mkdir -p "$OUT/bin"
  cp "$OUT/tor-build/src/app/tor" "$OUT/bin/tor"
  cp "$OUT/tor-build/src/config/geoip" "$OUT/tor-build/src/config/geoip6" "$OUT/bin/"
  "$STRIP" "$OUT/bin/tor"
fi

echo "Built:"
ls -la "$OUT/bin/"