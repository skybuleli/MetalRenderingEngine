# LLM Wiki 集成指南 — MetalRenderingEngine

> 版本 2.0 · 2026-06-19
> 目标：将 LLM Wiki 作为 MetalRenderingEngine 开发过程的**知识检索层**，
> 让 Claude Code / Codex 等 Agent 按需秒级获取设计决策、API 状态、踩坑记录，
> 而非每次会话重新通读 13+ 份 markdown 再合成。
>
> 配套文件：`wiki-purpose.md`（知识库目的）、`wiki-schema.md`（结构规则）。
> 本指南是**操作手册**（给人看），那两份是**LLM 配置**（给 LLM Wiki 摄入时读）。

---

## 一、为什么值得做

### 当前痛点

每次新 Agent 会话开始时，Agent 需要：
1. 读 `AGENTS.md` 了解约束
2. 读 `BLUEPRINT.md` → 顺指针读两份 34KB 蓝图
3. 读 `docs/phase-7-11-roadmap.md` 了解当前任务
4. 读 `csharp-metal-engine-gap-analysis.md` 了解缺口
5. 读 `docs/argument-buffer-layout.md` 等踩坑笔记避免重踩
6. 手动判断哪些文档已过时、哪些是最新的
7. 合成分散在 13+ 个文件中的关联信息

**这消耗了 Agent 上下文窗口的相当一部分，且每次会话都重复。**

### LLM Wiki 的解决方案

```
之前: Agent 逐个读文件 → 手工合成 → 回答问题
之后: Agent 一次 Hybrid 检索 → 拿到结构化答案 + 来源链接
```

核心能力：
- **Hybrid 检索**（分词 + 向量 + 图谱 2 跳扩展）→ 查"Argument Buffer 状态"返回精确页面，而非分散的段落
- **知识图谱** → 自动发现 `MetalCommandList ↔ ICommandRecorder ↔ 批量回放` 之间的关联
- **自动保鲜** → 新文档丢进 sources/，自动摄入、更新受影响页面
- **MCP Server + Agent Skill** → 一条命令接入，Agent 直接调用 API
- **踩坑沉淀** → argument buffer、MSC 反射等踩过的坑落成 `pitfall` 页面，避免每个 Agent 重新踩

---

## 二、职责分离原则（核心，先读）

这一节是整个集成方案的**地基**。LLM Wiki 是 LLM 生成的派生知识，可能过时或失真；`AGENTS.md` 是人写的项目宪法，强制且权威。两者必须职责分离：

| 层 | 文件 | 何时读 | 权威性 |
|----|------|--------|--------|
| **约束层** | `AGENTS.md`（项目宪法） | 每次 Agent 会话**强制必读** | **最高，冲突时以此为准** |
| **知识层** | LLM Wiki | Agent 按需检索时读取 | 可引用、可回溯，但可能过时或失真 |

**冲突仲裁规则**：当 Wiki 内容与 `AGENTS.md` 的硬性约束（第 3 节架构约束、第 5 节禁止事项）矛盾时，一律以 `AGENTS.md` 为准，Wiki 内容视为待修正的过时信息。

典型例子：Wiki 某个 feature 页面说 `newLibraryWithSource` 可用，但 `AGENTS.md` §5.1 禁止运行时 MSL 编译（仅 SpirvCross 例外）——以 `AGENTS.md` 为准。

落地：本职责分离已写入 `AGENTS.md` 第 11 节"项目知识库（可选检索层）"，所有 Agent 启动时强制读到。

**推论：什么不进 Wiki。** 以下内容属于约束层，必须留在 `AGENTS.md`/`CLAUDE.md` 强制读取，**不放入 Wiki 的 sources**：
- `AGENTS.md`、`CLAUDE.md`、`BLUEPRINT.md`（入口指针）
- 架构禁令、技术约束锁定值、依赖白名单
- 代码文件本身（`.cs`/`.m`/`.slang`/`.h`）——只记录到 feature 页面的 `code_path` 字段

---

## 三、项目配置

### 3.1 路径规划

| 用途 | 路径 | 说明 |
|------|------|------|
| 引擎仓库 | `/Users/liliang/MyGameRender` | git 管理，受版本控制 |
| Wiki 项目 | `/Users/liliang/MyGameRender-wiki` | **仓库外**，与仓库平级。Wiki 是 LLM 生成的派生物，体积大、变动频繁，不进引擎 git 历史 |

仓库内只保留 Wiki 的**源配置**（`wiki-purpose.md`、`wiki-schema.md`、本指南），通过同步脚本复制到 Wiki 项目生效。这样配置受 git 管理，运行时派生物不污染仓库。

### 3.2 安装 LLM Wiki

从 [GitHub Releases](https://github.com/nashsu/llm_wiki/releases) 下载预编译二进制（macOS `.dmg`，Apple Silicon）。
本机环境是 Command Line Tools 无完整 Xcode，**不建议从源码构建**（Tauri 编译会折腾）。

### 3.3 创建 Wiki 项目

在 LLM Wiki 桌面应用中：
1. 「新建项目」→ 任选模板（模板只影响初始 purpose/schema，会被我们的自定义文件覆盖）
2. 项目名称：`MetalRenderingEngine`
3. 项目目录：`/Users/liliang/MyGameRender-wiki`

### 3.4 配置 LLM 提供商（多 provider，跨平台复用）

设置 → LLM 提供商。目标是让 **Claude Code 和 Codex 都能用**，所以配成可切换：
- 至少配一个对话模型用于摄入/分析（Claude 或 OpenAI 均可）
- 模型选择按你的配额和偏好，不写死——切换 provider 不影响 Wiki 内容

### 3.5 开启向量搜索

设置 → 向量搜索：
- 开启（不开也能用，但召回率从 ~58% 升到 ~71%，对跨术语检索价值大）
- Embedding 端点：任一 OpenAI 兼容 embedding 端点
- 模型与维度按端点默认即可（如 `text-embedding-3-small` / 512）

### 3.6 同步自定义 purpose.md / schema.md

仓库内的 `wiki-purpose.md` / `wiki-schema.md` 是源（受 git 管理）。首次建项目后，把它们复制为 Wiki 项目的 `purpose.md` / `schema.md`：

```bash
WIKI=/Users/liliang/MyGameRender-wiki
REPO=/Users/liliang/MyGameRender
cp "$REPO/wiki-purpose.md" "$WIKI/purpose.md"
cp "$REPO/wiki-schema.md" "$WIKI/schema.md"
```

后续修改配置：改仓库里的源文件 → 跑同步脚本（见 §8.1）→ 在 LLM Wiki 里重新摄入。

### 3.7 开启 API Server + MCP

设置 → API + MCP：
- 「启用本地 HTTP API」：✅（`http://127.0.0.1:19828`）
- 「生成 Token」：点击，复制保存（写入 `~/.zshrc` 的 `LLM_WIKI_API_TOKEN`）
- 「允许本机无鉴权访问」：✅（仅本地开发环境）
- MCP Server：按应用内提示构建（通常 `npm run mcp:build`，路径以应用显示为准）

---

## 四、Wiki 结构设计

完整结构规则见 `wiki-schema.md`，这里只放目录骨架。页面类型为 6 种：feature / phase / decision / pitfall / dxmt-ref / source-summary。

```
MyGameRender-wiki/
├── purpose.md              # 从仓库 wiki-purpose.md 同步
├── schema.md               # 从仓库 wiki-schema.md 同步
├── raw/
│   └── sources/
│       ├── blueprints/     # 蓝图文档
│       ├── assessments/    # 评估报告
│       ├── roadmaps/       # 路线图
│       ├── research/       # 技术调研
│       ├── decisions/      # 设计决策 + 踩坑记录（摄入时生成 pitfall）
│       └── dxmt-docs/      # references/dxmt/docs 的副本（摄入时生成 dxmt-ref）
├── wiki/
│   ├── index.md            # 全局目录（LLM 自动维护）
│   ├── log.md              # 操作日志
│   ├── overview.md         # 引擎全貌（自动更新）
│   ├── phases/             # 各阶段状态
│   ├── features/           # 功能/组件
│   ├── decisions/          # 设计决策
│   ├── pitfalls/           # 踩坑记录（高价值资产）
│   ├── dxmt-refs/          # DXMT 借鉴参考（借鉴与禁止两面写）
│   ├── sources/            # 资料摘要
│   └── queries/            # 有价值的查询归档
└── .llm-wiki/              # 应用配置（自动生成）
```

---

## 五、文档迁移计划

按仓库真实结构迁移。先做仓库卫生，再分批喂料。

### 5.0 仓库卫生（先做）

处理 git status 里的悬空文件，避免污染 Wiki：
- `0002-feat-Command-Decorator-Builder-Memento.patch` → 归档或删除，不进 Wiki（patch 不是知识文档）
- `command-recorder-architecture.html` → 建议转成 md 后再导入，或忽略（HTML 摄入效果不如 md）
- `Phase10ArgEncoderIndexDiagTests.cs` → 测试代码，不进 Wiki（只记录到 feature 页面的 test_path）

### 5.1 第一批：核心蓝图与路线图

```bash
WIKI=/Users/liliang/MyGameRender-wiki
REPO=/Users/liliang/MyGameRender
S=$WIKI/raw/sources

cp "$REPO/csharp-metal-rendering-blueprint.md"      "$S/blueprints/"
cp "$REPO/metal-engine-blueprint-phase7-12.md"      "$S/blueprints/"
cp "$REPO/csharp-metal-engine-gap-analysis.md"      "$S/assessments/"
cp "$REPO/docs/phase-1-6-roadmap.md"                "$S/roadmaps/"
cp "$REPO/docs/phase-7-11-roadmap.md"               "$S/roadmaps/"
```

### 5.2 第二批：专项设计与调研

```bash
cp "$REPO/docs/slang-reflection-binding-design.md"  "$S/research/"
cp "$REPO/docs/shadergen-support-matrix.md"         "$S/research/"
cp "$REPO/docs/knowledge.md"                        "$S/research/"
cp "$REPO/docs/perf.md"                             "$S/assessments/"
cp "$REPO/docs/command-recorder-boundaries.md"      "$S/decisions/"
cp "$REPO/docs/argument-buffer-layout.md"           "$S/decisions/"   # 踩坑，摄入时生成 pitfall
```

### 5.3 第三批：DXMT 借鉴参考

```bash
cp "$REPO"/references/dxmt/docs/*.md "$S/dxmt-docs/"
```
摄入时每个文档生成一个 `dxmt-ref` 页面，**必须同时写借鉴与禁止两面**（见 schema.md §第5类）。

### 5.4 不进 Wiki 的内容（重要）

- `AGENTS.md`、`CLAUDE.md`、`BLUEPRINT.md` → 约束层/入口，强制读取，不进 Wiki（见 §二）
- 代码文件（`.cs`/`.m`/`.slang`/`.h`）→ 只记录到 feature 页面的 `code_path`
- `venv/`、`references/dxmt/` 源码 → 体积大且语义重复，只进 `references/dxmt/docs/`
- `llm-wiki-integration-guide.md`、`wiki-purpose.md`、`wiki-schema.md` → 元文档，不自我摄入

---

## 六、Agent 接入

### 6.1 安装 Agent Skill（Claude Code / Codex 等 skills 兼容运行时）

```bash
# 以 llm_wiki_skill 仓库 README 为准
npx skills add https://github.com/nashsu/llm_wiki_skill.git --skill llm_wiki_skill
```

该 skill **默认只读、带触发纪律**：只有你明确提到 "LLM Wiki"/"my wiki"/"知识库" 才触发，不污染普通对话。

### 6.2 环境变量

在 `~/.zshrc` 中添加：

```bash
export LLM_WIKI_API_TOKEN="<从 LLM Wiki 设置中生成的 Token>"
```

### 6.3 验证接入

```bash
# 1. 确认 API 在线
curl -s http://127.0.0.1:19828/api/v1/health

# 2. 列出项目，拿到 project id
curl -s -H "Authorization: Bearer $LLM_WIKI_API_TOKEN" \
  http://127.0.0.1:19828/api/v1/projects

# 3. 用上一步的 id 做搜索测试
curl -s -H "Authorization: Bearer $LLM_WIKI_API_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"query":"Argument Buffer 多资源布局","topK":5}' \
  http://127.0.0.1:19828/api/v1/projects/<project-id>/search
```

### 6.4 跨平台一致性

Claude Code 和 Codex 复用的不是"各自的配置"，而是**同一个 MCP 端点背后的同一个 Wiki**。一次建库，两个 Agent 都走 MCP/skill，共享同一份带引用的知识。无需为每个平台写不同集成。

---

## 七、日常工作流

### 7.1 完成一个设计审查/蓝图修订后

```
1. 把产出文档放入 raw/sources/<category>/
2. 在 LLM Wiki 中点击「重新扫描资料源」
3. LLM 自动摄入 → 更新受影响页面 → 更新 index.md → 追加 log.md
4. Agent 下次查询时自动拿到最新知识
```

### 7.2 当 Agent 需要了解项目状态时

```
Agent: "我的 wiki 里关于 Phase 10 argument buffer PoC 的最新状态是什么？"
→ LLM Wiki Hybrid 检索 → 返回结构化答案 + 来源链接
→ Agent 不需要逐个读 5 个文件
```

### 7.3 踩坑结论回流（最高价值工作流）

像 `argument-buffer-layout.md` 这种"踩坑结论"，养成习惯落进 `docs/`，同步进 Wiki 后自动生成 `pitfall` 页面。下次任何 Agent 检索该主题都能命中——**这是真正消除"每个 Agent 重新踩一遍坑"的关键**。

### 7.4 发现知识空白

LLM Wiki 的「图谱洞察」会自动检测：
- 孤立页面（没有交叉引用）
- 稀疏社区（某知识领域内部链接薄弱）
- 桥接节点（连接多个领域的枢纽页面）

点击「Deep Research」→ LLM 生成搜索主题 → 联网搜索 → 摄入结果。

### 7.5 定期 Review（替代 lint）

LLM Wiki 没有独立的 "Lint 按钮"；用 **Review 队列 + 图谱洞察** 做周期性体检：
- 处理 ingest 时 LLM 标记的 review item（Create Page / Deep Research / Skip）
- 检查图谱里的孤立页和低凝聚社区，补交叉引用
- `csharp-metal-engine-gap-analysis.md` 本身就是 gap 列表，可作为 Deep Research 种子

---

## 八、高级用法

### 8.1 自动化同步脚本

```bash
#!/bin/bash
# sync-wiki-sources.sh — 从引擎仓库同步文档到 LLM Wiki
# 用法: ./sync-wiki-sources.sh

set -e
WIKI=/Users/liliang/MyGameRender-wiki
REPO=/Users/liliang/MyGameRender
S=$WIKI/raw/sources

mkdir -p "$S"/{blueprints,assessments,roadmaps,research,decisions,dxmt-docs}

# 1. 同步 Wiki 配置源文件（仓库受 git 管理 → wiki 项目生效）
cp "$REPO/wiki-purpose.md" "$WIKI/purpose.md"
cp "$REPO/wiki-schema.md"  "$WIKI/schema.md"

# 2. 同步知识性文档（注意：AGENTS.md/CLAUDE.md/BLUEPRINT.md 不同步，属约束层）
cp "$REPO/csharp-metal-rendering-blueprint.md"      "$S/blueprints/"
cp "$REPO/metal-engine-blueprint-phase7-12.md"      "$S/blueprints/"
cp "$REPO/csharp-metal-engine-gap-analysis.md"      "$S/assessments/"
cp "$REPO/docs/phase-1-6-roadmap.md"                "$S/roadmaps/"
cp "$REPO/docs/phase-7-11-roadmap.md"               "$S/roadmaps/"
cp "$REPO/docs/slang-reflection-binding-design.md"  "$S/research/"
cp "$REPO/docs/shadergen-support-matrix.md"         "$S/research/"
cp "$REPO/docs/knowledge.md"                        "$S/research/"
cp "$REPO/docs/perf.md"                             "$S/assessments/"
cp "$REPO/docs/command-recorder-boundaries.md"      "$S/decisions/"
cp "$REPO/docs/argument-buffer-layout.md"           "$S/decisions/"
cp "$REPO"/references/dxmt/docs/*.md                "$S/dxmt-docs/"

echo "✅ Sources synced. LLM Wiki will auto-detect changes (SHA256 增量缓存) and re-ingest."
echo "   若改了 purpose.md/schema.md，需在 LLM Wiki 中手动触发重新摄入。"
```

可加到 Git post-commit hook 或定期执行。`docs/` 目录开启 source folder auto-watch 后，新文档落盘即自动摄入。

### 8.2 从聊天会话导出有价值的知识

每次重要会话结束后，把有价值的洞察导出为 `docs/` 下的 md，再同步进 Wiki：

```bash
# 例：G-Buffer 暂缓决策
cat > "$REPO/docs/2026-06-19-gbuffer-postponed.md" << 'EOF'
# G-Buffer/延迟渲染暂缓决策
状态：暂缓（MRT 基础设施已就绪，但当前场景复杂度不需要）
理由：引擎目前只有 1 个方向光，G-Buffer 的 O(几何×灯光)→O(像素×灯光) 优势不成立
前置条件：Shadow Map（Phase 11B）引入多 Pass 架构后，G-Buffer 是自然中间产物
EOF
```

### 8.3 知识图谱驱动的开发导航

图谱建起来后可发现：
- 哪些组件修改影响面最大（度数最高的节点）
- 哪些概念缺少验证（有设计文档但无测试覆盖）
- 哪些审查看似独立但实际关联（跨社区边）

### 8.4 多 Agent 协作

多个 Agent 协作时（一个负责 bridge、一个负责 C# 封装、一个负责着色器），LLM Wiki 是共享知识总线：

```
Agent A (bridge 层) → 更新 "MTLDevice_newLibraryWithSource 已添加"
Agent B (Shader 层) → 查询 "newLibraryWithSource 状态" → 立即知道可用
```

### 8.5 Obsidian 双开模式

Wiki 目录天然兼容 Obsidian：
1. 用 Obsidian 打开 `MyGameRender-wiki/wiki/` 目录（应用会自动生成 `.obsidian/` 配置）
2. 左侧 Obsidian 浏览知识图谱、阅读页面
3. 右侧 LLM Wiki 桌面应用聊天查询
4. LLM 自动维护，你在 Obsidian 中实时看到更新

---

## 九、GPLv3 合规与边界

LLM Wiki 采用 **GPLv3**（强 copyleft）。本项目的使用边界：

- ✅ **纯本地当工具用**：下载安装、运行、喂自己的文档、让 Agent 查询——完全不触发 GPL 传染条款。
- ✅ **不分发 llm_wiki**：只在本地开发机运行，不分发二进制、不提供网络服务。
- ❌ **禁止把 llm_wiki 代码搬进引擎仓库**：不得把它的源码片段复制进 `src/`、`native/` 或任何引擎代码。Agent 协作时需留意，避免误把 llm_wiki 的实现代码当成参考搬进引擎。
- ❌ **禁止静态/动态链接 llm_wiki**：引擎不依赖它运行，它只是开发期知识工具。

引擎本身（`MetalRenderingEngine`）不引入、不链接、不分发 llm_wiki，合规上无风险。

---

## 十、ROI 估算

| 投入 | 耗时 |
|------|------|
| 安装 LLM Wiki | 5 分钟 |
| 创建项目 + 配置 provider/向量/MCP | 15 分钟 |
| 同步 purpose.md + schema.md | 2 分钟 |
| 迁移文档（约 16 个文件） | 15 分钟 |
| 首次摄入 + 审阅 | 30 分钟（自动运行，你只需审阅） |
| 安装 Agent Skill + 验证 | 5 分钟 |
| 写同步脚本 | 10 分钟 |
| **总计投入** | **约 1.5 小时** |

| 回报 | 频率 |
|------|------|
| Agent 每次会话省去通读 13+ 份 markdown | 每次会话 |
| 不再需要手动判断文档时效 | 每次任务切换 |
| 踩坑记录沉淀，避免重复踩 | 每次涉及历史坑的任务 |
| 自动发现知识矛盾/空白 | 每次摄入 + 周期性 Review |
| 新 Agent 上手秒级获取项目全貌 | 每次新会话/新 Agent |

**频繁 Agent 协作中，一周内回本。** 长期价值随 Phase 推进和文档累积持续放大。

---

## 十一、启动清单

- [ ] 下载并安装 LLM Wiki 桌面应用（Release `.dmg`）
- [ ] 创建项目：`MetalRenderingEngine`，目录 `/Users/liliang/MyGameRender-wiki`
- [ ] 配置 LLM 提供商（多 provider 可切换）
- [ ] 开启向量搜索（推荐）
- [ ] 同步 `wiki-purpose.md` → `purpose.md`，`wiki-schema.md` → `schema.md`
- [ ] 仓库卫生：处理 `0002-*.patch`、`command-recorder-architecture.html`、`Phase10*Tests.cs`
- [ ] 迁移第一批文档（蓝图 + 路线图）到 `raw/sources/`
- [ ] 触发首次摄入，审阅生成的 Wiki 页面（确认 feature/phase/pitfall 类型正确）
- [ ] 迁移第二、三批文档，分批摄入
- [ ] 开启 API Server + 生成 Token
- [ ] 安装 Agent Skill：`npx skills add ...`
- [ ] 设置 `LLM_WIKI_API_TOKEN` 环境变量
- [ ] 验证接入：`curl /api/v1/health` + 搜索测试
- [ ] 写 `sync-wiki-sources.sh` 同步脚本
- [ ] 首次查询测试："我的 wiki 里 MetalCommandList 的架构是怎样的？"
- [ ] 确认 `AGENTS.md` 第 11 节"项目知识库（可选检索层）"已就位（职责分离落地）
