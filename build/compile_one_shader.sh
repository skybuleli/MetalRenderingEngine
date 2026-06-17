#!/bin/bash
# compile_one_shader.sh — 单个着色器编译包装脚本
# 封装 slangc + metal-shaderconverter 两步，错误重映射为 MSBuild 标准格式。
# 用法：compile_one_shader.sh <slang_file> <stage> <dxil_output> <metallib_output>
set -o pipefail

slang_file="$1"
dxil_out="$2"
metallib_out="$3"

mkdir -p "$(dirname "$dxil_out")" "$(dirname "$metallib_out")"

# 从文件名推断着色器阶段
case "$(basename "$slang_file")" in
  *.vert.slang) stage="vertex" ;;
  *.frag.slang) stage="fragment" ;;
  *) stage="compute" ;;
esac

# ── Step 1: slangc → DXIL ─────────────────────────────────
# 管道 Perl 将 slangc 的多行错误重映射为 MSBuild 兼容格式：
#   error[E30012]: msg     →  file(line,col): error E30012: msg
#    --> file:line:col
slangc "$slang_file" \
    -target dxil \
    -entry main \
    -stage "$stage" \
    -profile sm_6_0 \
    -o "$dxil_out" 2>&1 | perl -e '
while (<>) {
    if (/^error\[([^\]]+)\]:\s*(.*)$/) {
        $code = $1; $msg = $2;
        $loc = <>;
        chomp $loc;
        if ($loc =~ /-->\s+([^:]+):(\d+):(\d+)/) {
            printf "%s(%s,%s): error %s: %s\n", $1, $2, $3, $code, $msg;
        } else {
            print STDERR "error $code: $msg\n";
        }
    } else {
        print STDERR $_;
    }
}
'
slangc_rc=$?
if [ $slangc_rc -ne 0 ]; then
    exit $slangc_rc
fi

# ── Step 2: DXIL → metallib ────────────────────────────────
# 同时输出 MSC reflection JSON，用于运行时 argument buffer 布局解析
metal-shaderconverter "$dxil_out" -o "$metallib_out" \
  --output-reflection-file "${metallib_out%.metallib}.reflect.json" 2>&1
