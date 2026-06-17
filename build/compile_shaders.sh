#!/usr/bin/env bash
# 编译所有 .slang → .dxil → .metallib
# 路径：src/MetalRenderingEngine.Shaders/**/*.slang → out/shaders/*.metallib
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SHADER_ROOT="$REPO_ROOT/src/MetalRenderingEngine.Shaders"
OUT_DIR="$REPO_ROOT/out/shaders"

mkdir -p "$OUT_DIR"

# 根据所在子目录推断 stage
infer_stage() {
    local path="$1"
    case "$path" in
        */Compute/*) echo "compute" ;;
        *.vert.slang) echo "vertex" ;;
        *.frag.slang) echo "fragment" ;;
        */Render/*) echo "fragment" ;;   # 默认 render 目录的为 fragment（在引入更多类型前的占位）
        *) echo "compute" ;;
    esac
}

compile_one() {
    local slang_file="$1"
    local rel="${slang_file#$SHADER_ROOT/}"
    local name="$(basename "$slang_file" .slang)"
    local stage; stage="$(infer_stage "$slang_file")"
    local dxil="$OUT_DIR/$name.dxil"
    local metallib="$OUT_DIR/$name.metallib"

    echo "[shader] $rel (stage=$stage)"

    # 1) Slang → DXIL
    slangc "$slang_file" \
        -target dxil \
        -entry main \
        -stage "$stage" \
        -profile sm_6_0 \
        -o "$dxil"

    # 2) DXIL → metallib（metal-shaderconverter 是 metal-irconverter 的对外别名）
    metal-shaderconverter "$dxil" -o "$metallib"

    echo "[shader] ✅ $metallib"
}

found=0
while IFS= read -r -d '' f; do
    compile_one "$f"
    found=$((found + 1))
done < <(find "$SHADER_ROOT" -name "*.slang" -print0)

if [ "$found" -eq 0 ]; then
    echo "[shader] (no .slang files found under $SHADER_ROOT)"
    exit 1
fi

echo "[shader] Done. $found shader(s) compiled."
