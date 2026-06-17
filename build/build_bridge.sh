#!/usr/bin/env bash
# 编译 native/bridge.m → out/libmetal_bridge.dylib
# 仅依赖 Apple Metal.framework + Foundation；启用 ARC 仅用于 __bridge 语法支持，
# 引用计数仍由 H2ID/ID2H 宏 + CFRetain/CFRelease 手动管理。
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
