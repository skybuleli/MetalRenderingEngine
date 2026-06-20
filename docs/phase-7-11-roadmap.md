# MetalRenderingEngine Phase 7–11 修正路线图

> 审查/定稿日期: 2026-06-18
> 本文是对 `metal-engine-blueprint-phase7-12.md` 的审查结论 + 修正后的执行路线图。

---

## 一、对原蓝图的修正总览

| Phase | 原蓝图问题 | 修正方案 |
|-------|-----------|---------|
| 7 | 未提及 `MetalCommandList`；2 处过时声明（depth/stencil 结构体、MRT 已存在） | 新命令**同时**录入 `MetalCommandList`（`WMT*` 结构体 + `bridge.m` replay 分支），保住 99.5% P/Invoke 优化；删去过时项 |
| 8 | 补丁绕过 `MetalCommandList` 回退逐命令 P/Invoke；不编译；与自身蓝图不符；与 Phase 6 命令结构体重复 | **重写** `MetalCommandRecorder`：记录进 `MetalCommandList`，`EndRenderPass`/`EndFrame` 单次回放。24 个 `readonly struct` 降级为**仅 `RecordingCommandRecorder` 可观测/捕获层**，不进执行路径 |
| 9 | SpirvCross 路径被 `AGENTS.md §5.1/§3.3` 禁止；spirv-cross 未安装；反射完全缺失 | **放宽 AGENTS.md**：允许 `newLibraryWithSource` **仅限 SpirvCross 路径**；新增 bridge 函数；引入 `libspirv-cross.dylib`（需审批记录到依赖清单）。先做可复用运行时 `SlangCompiler` + 两级 `ShaderCache`，SpirvCross 作为子阶段 |
| 10 | 源生成器消费反射在构建顺序上不可能；MSC 重写索引与蓝图矛盾；多资源 arg buffer 布局未验证 | (1) **先做 PoC** 验证 CBV/texture/sampler 在 argument buffer 的布局；(2) 反射驱动生成移到**编译后 MSBuild pass**：读 MSC `*.reflect.json` → 生成 `*.bindings.json` 嵌入资源 → 运行时 `ReflectionLoader` 构建绑定表。绕开 Roslyn 源生成器构建顺序冲突 |
| 11 | 依赖前三者，本身无大问题 | 保留，但 demo 套件中涉及反射绑定的项改为依赖 Phase 10 的运行时绑定表 |

**跨阶段核心原则**：`MetalCommandList` 是执行路径的单一瓶颈点。Phase 7 的新命令、Phase 8 的抽象层都必须接入它，否则性能回退。

---

## 二、审查发现的关键问题（存档）

### Phase 7 — 准确但有 2 处过时，且漏了关键批量路径
- §7.2 "RenderPass 只有 Color attachment" 过时：`WMTRenderPassDesc`（`bridge.h:272`）已有 depth/stencil 字段。但 `bridge.m:337-364` 创建 render encoder 时丢弃 depth/stencil 的 load/store/clear 值（只传 texture）。意图正确，实现缺口更具体。
- §7.6 "只有一个 color attachment" 过时：`colors[8]` + `color_count` 已支持，MRT 描述符层已可用。
- 缺失（准确）：DepthStencilState 对象、5 个光栅化状态 setter、VertexDescriptor、instanced/indirect draw、`Depth24Unorm_Stencil8` 格式。
- **遗漏的关键点**：蓝图未提 `MetalCommandList`。批量系统只支持 7 种 render 命令（`MetalCommandTypes.cs`）。若新 API 只加到 `MetalRenderEncoder`（逐命令 P/Invoke），1000 instanced draw 会退回 1000 次 P/Invoke，抵消 Phase 6 成果。

### Phase 8 — 最严重：补丁绕过批量层
1. `MetalCommandRecorder` 直接调 `MetalRenderEncoder`/`MetalBridge.*`，**全程零引用 `MetalCommandList`**——回退到 Phase 6 之前的逐命令 P/Invoke。
2. 补丁不编译：引用不存在的 `setCullMode`/`setIndexBuffer`；`drawPrimitives` 传 6 参但签名 4 参；`RenderCommandEncoder()` 类型不匹配。
3. 补丁与自身蓝图 §8.5 不符：蓝图用类型化对象，补丁用裸 `nint` 且缺 fences/indirect-draw/render-targets。
4. 两套命令结构体重复：Phase 6 的 `WMT*`（blittable、链表、喂原生回放）vs Phase 8 的 24 个 `readonly struct`（非 blittable、`List<IRenderCommand>` 装箱，违背"零 GC"宣称）。
5. 补丁未应用（`git status` 确认）。

### Phase 9 — SpirvCross 路径违反 AGENTS.md
- `SpirvCrossCompiler` 流转 SPIR-V → spirv-cross → MSL → `newLibraryWithSource`。但 `AGENTS.md §3.3/§5.1` 明确禁止运行时 MSL 编译。bridge 无 `newLibraryWithSource`。
- spirv-cross 未安装、无引用、无 PoC。蓝图测试 2 aspirational。
- 现状：只有 `ComputeShaderDemo.cs:122-188` ad-hoc 子进程 PoC，不可复用。`MetalShaderLoader` 仅内存缓存。
- 反射完全缺失：`slangc -reflection-json` 从未调用；MSC 的 `--output-reflection-file` 生成的 `*.reflect.json` 存在但无 C# 代码读取，未复制到输出目录。

### Phase 10 — 源生成器消费反射在构建顺序上不可能
- 构建顺序冲突：Roslyn `IIncrementalGenerator` 在 `CoreCompile` 运行，此时 slangc/MSC 还没跑（`MetalShaders.targets` 是 `AfterTargets="CoreCompile"`）。源生成器无法消费它自己生成的着色器的反射。
- MSC 重写索引：`docs/slang-reflection-binding-design.md §3.5.1` 已验证 MSC 重写所有 buffer 索引。蓝图 §10.4 仍依赖 Slang 级反射推导 slot，与该发现矛盾。
- 唯一已验证：单 UAV @ `[[buffer(2)]]` + 24 字节描述符（`Program.cs:80-89`，`UavDescriptor` in `MetalTypes.cs:204`）。多资源/CBV/texture/sampler 布局均未验证（CBV 实测 Size=24，与文档"8 字节猜测"矛盾）。

---

## 三、当前真实状态（2026-06-20）

> 本节覆盖原“详细任务拆分”的完成状态。代码与测试已经推进到 Phase 10，
> 后续维护以本节为准；上方“审查发现”保留为历史问题存档。

### Phase 7: 3D 渲染基础

**已完成：**
- Depth/Stencil 状态对象：`WMTDepthStencilDesc` / `MetalDepthStencilState` / bridge / DllImport / 生命周期测试。
- RenderPass depth/stencil 完整性：depth/stencil attachment 的 load/store/clear 已传播，并有深度写入/像素测试覆盖。
- Depth/Stencil 像素格式：`Depth32Float` 与 `Depth24Unorm_Stencil8` 可创建，深度格式辅助已落地。
- 光栅化状态：cull/front-facing/depth-bias/depth-clip/fill-mode 已接入 bridge、`MetalRenderEncoder`、`MetalCommandList` replay。
- DepthStencil/StencilReference setters：已接入 bridge、encoder、`MetalCommandList` replay。
- VertexDescriptor：`WMTVertexDescriptor` 已进入 pipeline descriptor 与 builder。
- Instanced/Indexed/Indirect Draw：直接 encoder 与 `MetalCommandList` 路径均已实现。
- MRT helper：`PipelineBuilder.WithColorAttachment(index, ...)` / `.WithDepth(...)` 已用于 demo。
- ThreeDScene：100 instanced cubes、深度测试、背面剔除、双 MRT、Blinn-Phong、`ICommandRecorder` 批量回放路径已落地。

**仍缺/待加强：**
- ThreeDScene 的自动化断言还可补强：MRT0 alpha=1、MRT1 depth∈[0,1]、帧时阈值、P/Invoke 固定上限。
- `MetalCommandList` ring buffer 仍不支持非空扩容；大场景需显式传更大容量或后续实现指针重定位 Grow。

### Phase 8: ICommandRecorder 命令抽象层

**已完成：**
- `ICommandRecorder` 类型化接口已定型，覆盖当前 Phase 7 能力。
- `MetalCommandRecorder` 执行路径以 `MetalCommandList` 为主：render pass 内高频命令录入 list，`EndRenderPass()` 单次 replay。
- `RecordingCommandRecorder` / `LoggingCommandRecorder` / `CommandReplayer` 已用于测试与调试。
- `PipelineBuilder` 已支持 color/depth/stencil/sample/vertex descriptor/blend/label，并已有参数校验测试。
- 装饰器透明性、Memento diff、批量回放保真、多 draw 回放测试已覆盖。
- 当前真实边界已单独记录在 `docs/command-recorder-boundaries.md`。

**当前显式折中：**
- `SetScissor` / `SetVertexBuffer` / `SetFragmentBuffer` / `SetFragmentTexture` 暂时直通 encoder；它们是低频路径，已在边界文档中记录。
- `EndFrame()` 目前提交并等待完成；compute list 统一回放尚未纳入 `ICommandRecorder` 执行路径。

**最高优先级剩余项：**
- 补 `ICommandRecorder_PInvokeCountConstant` 或等价性能/回放次数守护，防止 1000 draw 退回 1000 次 P/Invoke。

### Phase 9: 着色器编译器

**已完成：**
- `AGENTS.md` 已加入 `SpirvCrossCompiler` 的 `newLibraryWithSource` 限定例外。
- `IShaderCompiler` / `IShaderProgram` / `ShaderFormat` / `ShaderCompileOptions` 已落地。
- `SlangCompiler` 已提炼为可复用编译器，支持 slangc→DXIL→MSC→metallib，并产出 reflection json。
- `ShaderCache` + `CachingShaderCompiler` 两级缓存已实现并有测试。
- `SpirvCrossCompiler` 已实现 SPIR-V→spirv-cross→MSL→`MetalDevice.NewLibraryWithSource` 路径；测试在工具缺失时跳过。
- `MscReflection` / `MscReflectionParser` 数据模型已实现并有解析测试。

**仍缺/待加强：**
- `libspirv-cross.dylib` 依赖尚未成为必需构建依赖；当前实现依赖系统 `spirv-cross` CLI 可用性。
- `IShaderProgram.Reflection` 当前主要通过 reflection json 字段/解析器消费，后续可统一成强类型属性。

### Phase 10: 资源绑定层

**已完成：**
- 多资源 argument buffer PoC 已形成测试与 `docs/argument-buffer-layout.md`：覆盖 CBV/UAV/texture/sampler、EltOffset、vertex/fragment 差异。
- `ArgumentBufferEncoder` 已按 MSC reflection 的 `TopLevelArgumentBuffer` 编码混合资源描述符，并返回需要 residency 的资源列表。
- `ResourceTable` 已支持按 slot/type 绑定 buffer/texture/sampler，并通过 `ICommandRecorder` Apply 到 buffer(2)。
- `ReflectionLoader` 已支持从输出目录加载 `*.reflect.json` 并缓存。
- `ShaderBindingLayout` 半自动便利层已落地，并有测试。
- Textured/MultiTexture cube demo 已开始使用 Phase 10 绑定层。

**仍缺/待加强：**
- 编译后 `BindingGen.targets` 生成 `*.bindings.json` 尚未完成；当前主要直接消费 `*.reflect.json`。
- `ResourceTable.Bind*(name, ...)` 语义名绑定仍需处理 MSC 反射 name 缺失/重写问题，目前更可靠的是 slot/type 绑定或手写 BindingLayout。

### Phase 11: 引擎自洽与完善

**已完成一部分：**
- Demo 套件已有 ThreeDScene、TexturedCube、MultiTextureCube、GpuParticle 等覆盖 7–10 的样例。
- 单元/集成测试规模已到 125 个，并覆盖 Metal bridge、CommandList、ICommandRecorder、Shader compiler/cache/reflection、argument buffer/resource table。

**下一批优先项：**
1. 性能回归守护：`ICommandRecorder_PInvokeCountConstant` / `Instanced1000` / `MetalCommandList_10000Draws`。
2. 整理当前未提交 demo/test 文件，确认是否纳入 Phase 10 正式成果。
3. Shader CLI 工具与 BindingGen MSBuild pass。
4. PBR/Shadow/BatchBench 等更完整 demo。

---

## 四、依赖与执行顺序

```
Phase 7 (3D 基础，含 CommandList 扩展) ──────┐
   │                                         │
   ├──→ Phase 8 (ICommandRecorder 重写)      │
   │       ↑ 依赖 7D/7E/7G/7H 的命令结构体    │
   │                                         │
   │   Phase 9A (放宽 AGENTS.md)             │
   │       ↓                                 │
   │   Phase 9B-9F (编译器 + 缓存 + 反射)     │
   │       │                                 │
   │       ├──→ Phase 10A (arg buffer PoC) ←─┘ 反射依赖
   │       │       ↓ (决策门)
   │       │   Phase 10B-10E (绑定层)
   │       │                                 │
   └───────┴──────────→ Phase 11 (自洽) ←─────┘
```

- **可并行**：Phase 7 与 Phase 9A-9D（SlangCompiler 部分）无耦合。
- **关键路径**：7 → 8（8 依赖 7 的命令结构体）；9F → 10A → 10B-10E。
- **决策门**：Phase 10A 的 PoC 结果决定 10B-10E 走全自动还是半手动。

---

## 五、工作量重估

| 阶段 | 原估 | 修正估 | 变化原因 |
|------|------|--------|---------|
| 7 | 3–5h | 4–6h | 新增 `MetalCommandList` 命令结构体扩展 + replay 分支 |
| 8 | 3–4h | 5–7h | 重写 `MetalCommandRecorder` 接入批量层 + demo 回归 |
| 9 | 8–12h | 8–12h | SpirvCross 需引入 libspirv-cross + bridge 新函数 |
| 10 | 6–8h | 7–10h | 新增 10A PoC；MSBuild 反射生成替代源生成器 |
| 11 | 6–10h | 6–10h | 基本不变 |
| **总计** | **26–39h** | **30–45h** | 增量在 Phase 7/8 批量层集成与回归守护 |

---

## 六、关键风险与缓解

| 风险 | 缓解 |
|------|------|
| Phase 8 重写后批量回放与逐命令路径行为不一致 | 8F 加"批量回放保真"像素对比回归测试 |
| Phase 10A PoC 发现 MSC arg buffer 布局不稳定 | 10A 设决策门，回退 10E 半手动 |
| Phase 9E 引入 libspirv-cross 增加构建复杂度 | 仅 SpirvCross 路径依赖；Slang 路径不受影响 |
| 放宽 AGENTS.md 削弱着色器管线锁定原则 | 严格限定例外（仅 newLibraryWithSource + 仅 SpirvCross） |
| `MetalCommandList` ring buffer 不支持运行时扩容 | Phase 7 评估是否顺带实现 `Grow` 重定位 |

---

## 七、执行建议

1. 先做 Phase 7 + 8（地基，耦合最深，问题最严重），完成后再规划 9/10/11 细节
2. Phase 7 每个新命令遵循"bridge → C# encoder → `MetalCommandList` 结构体 → replay 分支 → 测试"五步闭环
3. Phase 8 完成后，所有 demo 迁移到 `ICommandRecorder`，删除直接调 `MetalRenderEncoder` 的旧路径
4. Phase 10A PoC 在写任何绑定层代码之前完成
