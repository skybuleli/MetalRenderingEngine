---
type: schema
created: 2026-06-19
updated: 2026-06-19
---

# Wiki 结构与维护规则

## 页面类型（6 种）

类型取舍原则：基础 4 种（feature/phase/decision/source-summary）覆盖"能力/阶段/决策/资料"四个通用维度；额外 2 种（pitfall/dxmt-ref）针对本项目的两类高价值特化知识——"踩过的坑"和"借鉴但不依赖的外部参考"。pitfall 和 dxmt-ref 不与 decision 混用，因为它们的查询模式不同：Agent 问"为什么这么设计"查 decision，问"这里有什么坑"查 pitfall，问"DXMT 这个模式能不能抄"查 dxmt-ref。

### 1. feature（功能/组件）
页面描述引擎的一个可识别功能或模块。Agent 查"某能力是否可用"时首先命中此类页面。

- 位置：`wiki/features/<slug>.md`
- Frontmatter：
  ```yaml
  type: feature
  layer: bridge | metal-api | rendering | shader | platform
  status: stable | in-progress | planned | deprecated
  phase: <N>               # 在哪个 Phase 引入的
  code_path: <src/...>     # 主要代码位置
  test_path: <tests/...>   # 测试位置（可选）
  since_commit: <hash>     # 引入此功能的 commit（可选）
  ```
- 内容模板：
  ```markdown
  # <名称>
  一句话职责描述。

  ## API 表面
  | 方法/函数 | 说明 | 状态 |
  |-----------|------|------|

  ## 依赖
  - 依赖：[[features/xxx]]
  - 被依赖：[[features/yyy]]

  ## 已知限制
  - ...

  ## 相关决策
  - [[decisions/...]]
  ```
- 示例：`features/metal-command-list`、`features/spirv-cross-compiler`、`features/shader-cache`

### 2. phase（阶段）
页面描述一个 Phase 的完整状态。Agent 查"某 Phase 是否完成"时命中。

- 位置：`wiki/phases/phase-<N>.md`
- Frontmatter：
  ```yaml
  type: phase
  number: <N>
  status: complete | in-progress | pending
  started: <YYYY-MM-DD>
  completed: <YYYY-MM-DD>  # 仅 status=complete 时
  delivers:                # 产出的 feature 列表
    - features/xxx
    - features/yyy
  ```
- 内容模板：
  ```markdown
  # Phase <N>: <标题>

  ## 状态：<complete/in-progress/pending>
  完成日期：<YYYY-MM-DD>（如适用）

  ## 目标
  - [x] 已完成项
  - [ ] 待完成项

  ## 产出
  | Feature | 代码位置 |
  |---------|---------|

  ## 关键决策
  - [[decisions/...]]
  ```
- 示例：`phases/phase-7`、`phases/phase-9`

### 3. decision（设计决策 + 已知问题）
两类知识合并：一个设计为什么这样选 + 一个已知问题是什么状态。够用了。

- 位置：`wiki/decisions/<YYYY-MM-DD>-<slug>.md`
- Frontmatter：
  ```yaml
  type: decision
  date: <YYYY-MM-DD>
  kind: architecture | api-design | toolchain | process
  status: accepted | superseded | open-issue | resolved
  affects:
    - features/xxx
  supersedes: <旧决策 slug>   # 可选
  superseded_by: <新决策 slug> # 可选
  ```
- 内容模板：
  ```markdown
  # <标题>

  ## 背景
  为什么需要做这个决定。

  ## 选项
  | 选项 | 优点 | 缺点 |
  |------|------|------|

  ## 决策
  选了哪个，核心理由一句话。

  ## 后果
  这个决定影响了什么。

  ## 来源
  来自：[[sources/xxx]] / 审查发现 / 会话讨论
  ```
- 示例：`decisions/2026-06-18-cmdlist-as-bottleneck`（Phase 8 审查发现）
- 示例：`decisions/2026-06-18-spirvcross-newlibrary`（AGENTS.md 放宽）

### 4. pitfall（踩坑记录）
记录"我们花了时间才搞清楚"的结论——这类知识是 Wiki 对 Agent 价值最高的资产，直接消除"每个 Agent 重新踩一遍坑"的浪费。与 decision 的区别：decision 回答"为什么这么选"，pitfall 回答"这里有什么坑、怎么绕过"。

- 位置：`wiki/pitfalls/<YYYY-MM-DD>-<slug>.md`
- Frontmatter：
  ```yaml
  type: pitfall
  date: <YYYY-MM-DD>            # 首次发现日期
  status: active | worked-around | fixed
  severity: p0 | p1 | p2        # p0=阻塞、p1=需绕过、p2=可忽略
  affects:
    - features/xxx              # 受影响的 feature
  root_cause: <一句话根因>
  ```
- 内容模板：
  ```markdown
  # <坑的标题>

  ## 现象
  观察到什么、在什么场景下触发。

  ## 根因
  为什么会这样（Metal/Slang/MSC/bridge 的哪一层导致的）。

  ## 规避/修复
  当前是怎么绕过的，或已经怎么修的。

  ## 验证
  怎么确认规避有效（对应的测试或复现脚本）。

  ## 来源
  - 原始调研：[[sources/xxx]]
  - 受影响 feature：[[features/yyy]]
  - 相关决策（如有）：[[decisions/...]]
  ```
- 示例：`pitfalls/2026-06-19-argbuffer-vertex-use-resource`（MSC argument buffer 在 vertex shader 中需要 UseResource）
- 示例：`pitfalls/2026-06-19-msc-reflection-encoder-index`（MSC 反射的 encoder index 与预期不一致）

### 5. dxmt-ref（DXMT 借鉴参考）
记录从 `references/dxmt/docs/` 提炼的、对本项目有参考价值的架构模式。**每个页面必须同时写"借鉴了什么"和"AGENTS.md 禁止了什么"两面**，避免 Agent 误以为可以引入 DXMT 代码或依赖。

- 位置：`wiki/dxmt-refs/<slug>.md`
- Frontmatter：
  ```yaml
  type: dxmt-ref
  source_doc: <references/dxmt/docs/下的相对路径>
  pattern: <一句话模式名>
  adopted: true | false        # 是否被本项目借鉴
  ingested: <YYYY-MM-DD>
  ```
- 内容模板：
  ```markdown
  # <模式名>

  ## DXMT 中的做法
  DXMT 如何实现这个模式（只描述架构，不贴代码）。

  ## 本项目的借鉴/不借鉴
  - ✅ 借鉴了：……（具体到我们的哪个 feature/decision）
  - ❌ 不借鉴：……（对应 AGENTS.md 的哪条禁令）

  ## 边界
  明确"参考"与"依赖"的界限：本项目只借鉴设计思想，绝不引入 DXMT 代码或依赖（AGENTS.md §1.2）。

  ## 来源
  - DXMT 文档：[[sources/xxx]]
  - 本项目对应：[[features/yyy]] / [[decisions/...]]
  ```
- 示例：`dxmt-refs/handle-based-resource`（handle-based 架构模式：借鉴 SafeHandle 思路，禁止引入 DXMT runtime）

### 6. source-summary（资料摘要）
LLM 自动生成，不手写。每个 `raw/sources/` 下的文档对应一个。

- 位置：`wiki/sources/<source-filename>.md`
- Frontmatter：
  ```yaml
  type: source-summary
  source_file: <raw/sources/下的相对路径>
  ingested: <YYYY-MM-DD>
  version: <v1|v2|...>     # 如果文档有明确版本
  supersedes: <旧摘要 slug> # 如果此文档是新版本
  ```
- 内容：LLM 自动提取的关键要点 + 交叉引用

## 目录与索引

### index.md（自动维护）
Agent 检索的入口。LLM 在每次摄入后更新。结构：
```markdown
# 目录

## Phases
- [[phases/phase-7]] — 3D 渲染基础 · ✅ complete
- [[phases/phase-8]] — 命令抽象层 · ✅ complete
- ...

## Features（按层分组）
### Bridge 层
- [[features/xxx]] — 一句话

### Metal API 层
- ...

## Decisions（按时间倒序）
- [[decisions/2026-06-18-xxx]] — ...

## Pitfalls（按严重度 p0→p2，再按日期倒序）
- 🔴 [[pitfalls/2026-06-19-xxx]] — p0 · 一句话根因
- 🟡 [[pitfalls/2026-06-19-yyy]] — p1 · 一句话根因

## DXMT 借鉴参考
- [[dxmt-refs/xxx]] — adopted: 借鉴了 …
- [[dxmt-refs/yyy]] — adopted: false（仅参考，未引入）

## Sources（最近摄入）
- [[sources/xxx]] — ingested 2026-06-19
```

### log.md（自动追加）
每次摄入/查询后追加一行。格式：
```markdown
## [2026-06-19] ingest | <source-filename> | touched: features/xxx decisions/yyy
## [2026-06-19] lint  | found: 2 orphan pages 1 contradiction
```
**重要：** log 条目不以 `# ` 开头（避免被 Obsidian 当作顶级标题），用 `## [date]` 格式。

## 摄入规则（针对我们的工作流定制）

### 新文档摄入
1. LLM 先读 `purpose.md` 获取上下文（特别注意"核心立场"节的约束层/知识层职责分离）
2. 第一步（分析）：提取关键实体/概念/决策/矛盾/踩坑结论/借鉴边界 → 不写文件
3. 第二步（生成）：
   a. 必做：生成 `sources/<name>.md` 摘要
   b. 如发现新 feature：创建 `features/<slug>.md`
   c. 如发现新决策：创建 `decisions/<date>-<slug>.md`
   d. 如发现"踩过的坑/非显而易见的结论/调试发现"：创建 `pitfalls/<date>-<slug>.md`
   e. 如文档来自 `references/dxmt/docs/`：创建 `dxmt-refs/<slug>.md`，必须同时写借鉴与禁止两面
   f. 如与已有页面矛盾：创建 `decisions/<date>-conflict-<topic>.md`（kind=open-issue）或 `pitfalls/<date>-conflict-<topic>.md`
   g. 如影响已有 feature：更新其状态和 API 表面
   h. 必做：更新 `index.md` 追加新页面（pitfall 进 Pitfalls 分组，dxmt-ref 进 DXMT 借鉴参考分组）
   i. 必做：更新 `log.md`

### 文档版本更新（重要：我们常修订蓝图）
当 `raw/sources/` 下出现同一主题的新版本文档时：
1. 识别旧版对应的 `sources/<name>.md`
2. 更新其 `supersedes` 指向旧版，`version` 加 1
3. 对比新旧内容，**只更新变化的部分**，不重写整个 feature 页面
4. 如果新版移除了某个能力声明，在对应 feature 页面标注 `status: deprecated`
5. 如果新版与旧版的设计方向矛盾，创建 decision 而非静默覆盖

### 不摄入的内容
- 纯代码文件（.cs、.h、.m）→ 只记录到 feature 页面的 `code_path` 字段
- 二进制文件（.dylib、.metallib）→ 忽略
- 第三方库文档 → 忽略（除非我们 fork 或深度定制了它）

## 交叉引用规范

- 语法：`[[相对路径/页面名]]`，不带 `.md` 扩展名
- 每个 feature 页面必须至少有 1 个入链（从 phase 页面或 decision 页面来）
- 每个 decision 页面必须链接到它影响的 feature
- 每个 phase 页面必须链接到它产出的 feature 和 decision
- 交叉引用的目的：Agent 通过图谱 2 跳遍历发现相关知识

## 查询响应规则

Agent 查询时，LLM 应：
1. 先读 `index.md` 定位候选页面（节省 token）
2. 通过交叉引用图谱扩展 1–2 跳
3. 按相关性排序后读取目标页面全文
4. 回答时引用页面路径：`[[path]]`
5. 如果检索结果不充分，在回答中明确说明"Wiki 中缺少 X 相关信息"
6. 有价值的查询结果可归档到 `sources/queries/` 供后续摄入
