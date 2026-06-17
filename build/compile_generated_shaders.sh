#!/usr/bin/env bash
# 编译源生成器输出的 Slang 着色器 → .dxil → .metallib
# 由 MetalShaders.targets 在 dotnet build 时自动调用
set -euo pipefail

GEN_DIR="$1"
OUT_DIR="$2"

if [ -z "$GEN_DIR" ] || [ -z "$OUT_DIR" ]; then
    echo "用法: $0 <生成文件目录> <输出目录>"
    exit 1
fi

mkdir -p "$OUT_DIR"

compiled=0
while IFS= read -r -d '' genfile; do
    basename="${genfile##*/}"
    struct_name="${basename%.Slang.g.cs}"

    if [ "$basename" = "$struct_name" ]; then
        continue  # 不是 .Slang.g.cs 文件
    fi

    slang_file="$OUT_DIR/$struct_name.generated.slang"

    # 从生成的 C# const 类中提取 Slang 源代码
    # 查找 Source = @"..." 模式
    awk '
    /Source = @"/ { in_source = 1; next }
    in_source {
        if (/^";$/) {
            in_source = 0
        } else {
            gsub(/^    /, "")
            sub(/"$/, "")
            print
        }
    }' "$genfile" > "$slang_file"

    if [ ! -s "$slang_file" ]; then
        echo "[shader:gen] ⚠️  $basename: 未能提取 Slang 源码，跳过"
        continue
    fi

    # 检查是否是计算着色器（含 numthreads）
    if grep -q "numthreads" "$slang_file"; then
        stage="compute"
    elif grep -q 'shader("vertex")' "$slang_file"; then
        stage="vertex"
    elif grep -q 'shader("fragment")' "$slang_file"; then
        stage="fragment"
    else
        echo "[shader:gen] ⚠️  $basename: 无法推断着色器阶段，跳过"
        continue
    fi

    dxil="$OUT_DIR/$struct_name.generated.dxil"
    metallib="$OUT_DIR/$struct_name.generated.metallib"

    echo "[shader:gen] $basename (stage=$stage)"

    # 1) Slang → DXIL
    slangc "$slang_file" \
        -target dxil \
        -entry main \
        -stage "$stage" \
        -profile sm_6_0 \
        -o "$dxil"

    # 2) DXIL → metallib
    metal-shaderconverter "$dxil" -o "$metallib"

    echo "[shader:gen] ✅ $basename → $metallib"
    compiled=$((compiled + 1))
done < <(find "$GEN_DIR" -name "*.Slang.g.cs" -print0)

if [ "$compiled" -eq 0 ]; then
    echo "[shader:gen] (没有需要编译的生成着色器)"
fi

echo "[shader:gen] Done. $compiled shader(s) compiled."
