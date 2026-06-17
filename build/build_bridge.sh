#!/usr/bin/env bash
# 编译 native/bridge.m → out/libmetal_bridge.dylib
# 仅依赖 Apple Metal.framework + Foundation；纯 ObjC，无 ARC（手动 retain/release 通过 CFRetain/CFRelease）
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DIR="$REPO_ROOT/native"
OUT_DIR="$REPO_ROOT/out"
OUT="$OUT_DIR/libmetal_bridge.dylib"

mkdir -p "$OUT_DIR"

echo "[bridge] Compiling $SRC_DIR/bridge.m → $OUT"

clang -dynamiclib \
  -arch arm64 \
  -mmacosx-version-min=14.0 \
  -fobjc-arc \
  -O2 -Wall -Wextra -Wno-unused-parameter \
  -framework Metal -framework Foundation \
  -framework CoreFoundation -framework QuartzCore -framework AppKit \
  -install_name "@rpath/libmetal_bridge.dylib" \
  -o "$OUT" \
  "$SRC_DIR/bridge.m"

echo "[bridge] ✅ Built $OUT"
file "$OUT"
