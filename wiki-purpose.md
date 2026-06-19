---
type: purpose
created: 2026-06-19
updated: 2026-06-19
---

# MetalRenderingEngine 知识库

## 核心立场（必须首先读）

这个 Wiki 是项目的**知识检索层**，不是**约束层**。两者职责严格分离：

| 层 | 文件 | 何时读 | 权威性 |
|----|------|--------|--------|
| 约束层 | `AGENTS.md`（项目宪法） | 每次 Agent 会话**强制必读** | **最高，冲突时以此为准** |
| 知识层 | 本 Wiki | Agent 按需检索时读取 | 可引用、可回溯，但可能过时或失真 |

**冲突仲裁规则**：当 Wiki 内容与 `AGENTS.md` 的硬性约束（第 3 节架构约束、第 5 节禁止事项）矛盾时，一律以 `AGENTS.md` 为准，Wiki 内容视为待修正的过时信息。

典型例子：Wiki 某个 feature 页面说 `newLibraryWithSource` 可用，但 `AGENTS.md` §5.1 禁止运行时 MSL 编译（仅 SpirvCross 例外）——以 `AGENTS.md` 为准。

## 这个 Wiki 是给谁用的

**主要读者是 AI Agent**（Claude Code、Codex 等 skills/MCP 兼容运行时），不是人类。人类通过 Obsidian 浏览知识图谱，但日常消费这个 Wiki 的是 Agent。因此：
- 每个页面必须独立可读——Agent 可能通过检索直接跳入，不会从头读
- 状态必须明确——Agent 需要知道某个能力是"已完成"还是"设计阶段"
- 来源必须可追溯——Agent 需要能跳回原始文档验证细节

## Agent 最常问的 8 类问题

1. **能力查询**："MetalCommandList 支持哪些命令类型？bridge 里有没有 setCullMode？"
2. **状态查询**："Phase 10 argument buffer PoC 做完了吗？当前卡在哪里？"
3. **依赖查询**："SpirvCrossCompiler 依赖 bridge 层的哪些函数？改了 bridge.h 会影响哪些 C# 类？"
4. **决策查询**："为什么 Phase 8 的 ICommandRecorder 必须走 MetalCommandList？谁做的这个决定？"
5. **问题查询**："PipelineBuilder 两套 API 统一了吗？有没有已知的竞态条件？"
6. **测试查询**："ShaderCache 的测试覆盖了什么？有没有跨进程复用测试？"
7. **架构查询**："从 Slang 源码到 metallib 的完整路径是什么？哪些步骤是可缓存的？"
8. **差异查询**："v2 和 v3 蓝图在 Phase 8 设计上有什么不同？"
9. **借鉴边界查询**："DXMT 的 handle-based 架构里，哪些模式我们借鉴了？哪些被 AGENTS.md 明确禁止依赖？"

## 信息优先级

Agent 上下文窗口有限。摄入和查询时，LLM 应按以下优先级分配注意力：

1. **状态与决策**（最高）— 某能力当前是否可用、某个设计为什么这样选
2. **依赖与接口**（高）— 组件间的调用关系、bridge 函数的签名和位置
3. **已知限制**（中）— 当前不工作、待修复、待验证的事项
4. **历史背景**（低）— 为什么曾经是另一种设计、被废弃的路径

## 范围边界

**在范围内：**
- Metal API 封装层（bridge.h/m → MetalBridge.cs → Metal* 类）
- 渲染管线（RenderEncoder、ComputeEncoder、ICommandRecorder）
- 着色器工具链（Slang→DXIL→MSC、SpirvCross→MSL、ShaderCache）
- 资源管理（BufferPool、TexturePool、TransientBufferAllocator）
- 命令批处理（MetalCommandList、命令类型体系）
- 引擎架构（Phase 1-11 路线图、各阶段交付物）
- 设计决策与审查发现
- **踩坑记录**（argument buffer 布局、MSC 反射陷阱、引用计数泄漏等已踩过的坑——此类知识最该沉淀，避免每个 Agent 重新踩一遍）
- **DXMT 借鉴参考**（仅 `references/dxmt/docs/` 下的文档；记录"借鉴了什么模式"和"哪些被 AGENTS.md 禁止引入"的边界，不记录 DXMT 代码实现细节）

**不在范围内：**
- 龙神模拟器（Ryujinx/Ryubing）集成细节（下游消费者，非引擎本身）
- C# 语言特性、Roslyn 原理（除非与 ShaderGen 直接相关）
- macOS 系统 API（除非与窗口/Metal 层创建直接相关）
- 第三方库的使用细节（Hexa.NET.ImGui、System.Numerics 等）
- **DXMT 的源码实现**（只借鉴架构模式，AGENTS.md §1.2 明确"绝不引入 DXMT 代码或依赖"）

## 工作原则

1. **一个页面只讲一件事。** 如果一个组件有两个不相关的职责，拆成两个页面。
2. **状态比历史重要。** Agent 首先需要知道"现在能不能用"，其次才是"为什么这样设计"。
3. **交叉引用是命脉。** 每个页面至少有一个入链和一个出链。孤立页面是知识浪费。
4. **矛盾必须浮出水面。** 如果新文档与已有页面冲突，创建 `pitfall` 或 `decision` 页面记录矛盾，而非静默覆盖。
5. **文档版本是元数据。** 当同一主题有多个版本的蓝图时，在 frontmatter 中记录版本号，在正文中标注差异。
6. **踩坑优先沉淀。** 任何"我们花了时间才搞清楚"的结论（argument buffer 布局、MSC 反射的坑、引用计数泄漏点），都必须落成 `pitfall` 页面。这是 Wiki 对 Agent 价值最高的资产类型——它直接消除"每个 Agent 重新踩一遍坑"的浪费。
7. **借鉴边界要显式。** 涉及 DXMT 等"参考但不依赖"的外部项目时，`dxmt-ref` 页面必须同时记录"借鉴了什么"和"AGENTS.md 禁止了什么"，两面都要写，避免 Agent 误以为可以引入。
