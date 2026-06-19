#!/bin/bash
# sync-wiki-sources.sh — 从引擎仓库同步文档/配置到 LLM Wiki 项目
#
# 用途:
#   1. 同步 wiki-purpose.md / wiki-schema.md → wiki 项目的 purpose.md / schema.md
#   2. 同步知识性文档到 raw/sources/<分类>/
#
# 注意:
#   - 约束层文件 (AGENTS.md/CLAUDE.md/BLUEPRINT.md) 不同步, 属约束层, 强制读取, 不进 Wiki
#   - 代码文件不同步, 只记录到 feature 页面的 code_path 字段
#   - Wiki 项目位于仓库外, 运行时派生物不进引擎 git 历史
#
# 用法: ./build/sync-wiki-sources.sh

set -e

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WIKI="/Users/liliang/MyGameRender-wiki/MetalRenderingEngine"
S="$WIKI/raw/sources"

# 前置检查
if [ ! -d "$WIKI" ]; then
  echo "❌ Wiki 项目目录不存在: $WIKI"
  echo "   请先在 LLM Wiki 中创建项目 MetalRenderingEngine"
  exit 1
fi

mkdir -p "$S"/{blueprints,assessments,roadmaps,research,decisions,dxmt-docs}

echo "== 1. 同步 Wiki 配置源文件 (仓库受 git 管理 → wiki 项目生效) =="
cp "$REPO/wiki-purpose.md" "$WIKI/purpose.md"
cp "$REPO/wiki-schema.md"  "$WIKI/schema.md"
echo "✅ wiki-purpose.md → purpose.md"
echo "✅ wiki-schema.md  → schema.md"
echo "   (若改了 purpose/schema, 需在 LLM Wiki 中手动触发重新摄入)"

echo ""
echo "== 2. 同步知识性文档 =="
echo "   (AGENTS.md/CLAUDE.md/BLUEPRINT.md 属约束层, 不同步)"

# 第一批: 蓝图与路线图
cp "$REPO/csharp-metal-rendering-blueprint.md"      "$S/blueprints/"
cp "$REPO/metal-engine-blueprint-phase7-12.md"      "$S/blueprints/"
cp "$REPO/csharp-metal-engine-gap-analysis.md"      "$S/assessments/"
cp "$REPO/docs/phase-1-6-roadmap.md"                "$S/roadmaps/"
cp "$REPO/docs/phase-7-11-roadmap.md"               "$S/roadmaps/"

# 第二批: 专项设计与调研
cp "$REPO/docs/slang-reflection-binding-design.md"  "$S/research/"
cp "$REPO/docs/shadergen-support-matrix.md"         "$S/research/"
cp "$REPO/docs/knowledge.md"                        "$S/research/"
cp "$REPO/docs/perf.md"                             "$S/assessments/"
cp "$REPO/docs/command-recorder-boundaries.md"      "$S/decisions/"
cp "$REPO/docs/argument-buffer-layout.md"           "$S/decisions/"   # 踩坑, 摄入时生成 pitfall

# 第三批: DXMT 借鉴参考
cp "$REPO"/references/dxmt/docs/*.md                "$S/dxmt-docs/"

TOTAL=$(find "$S" -type f | wc -l | tr -d ' ')
echo "✅ 已同步 $TOTAL 个文档到 raw/sources/"
echo "   LLM Wiki 会通过 SHA256 增量缓存自动检测变更并重新摄入。"
echo "   也可在 LLM Wiki 中点击「重新扫描资料源」立即触发。"
