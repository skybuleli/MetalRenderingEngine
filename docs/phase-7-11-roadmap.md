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

## 三、详细任务拆分

### Phase 7: 3D 渲染基础补齐（修正版）

> 目标：带深度测试、背面剔除、GPU 实例化、MRT 的 3D 场景，且命令走 `MetalCommandList` 批量回放。

#### 7A. Depth/Stencil 状态对象
- [ ] `bridge.h` 新增 `WMTStencilDescriptor` + `WMTDepthStencilDesc` 结构体；`MTLDevice_newDepthStencilState(device, desc) → handle`
- [ ] `bridge.m` 实现（≤20 行，按现有模式 `__bridge_retained`）
- [ ] `MetalBridge.cs` 新增 DllImport；`MetalEnums.cs` 新增 `MTLCompareFunction` / `MTLStencilOperation` 枚举（注：`WMTCompareFunction` 已存在 `bridge.h:383`，C# 端需补对应枚举或复用）
- [ ] `MetalDepthStencilState : MetalObject` SafeHandle 封装
- [ ] 测试：`DepthStencilStateTests` — 创建 state、查询 handle 非 0、确定性释放

#### 7B. 修复 RenderPass depth/stencil 完整性
- [ ] `bridge.m` `MTLCommandBuffer_renderCommandEncoder` — 补全 depth/stencil attachment 的 `loadAction`/`storeAction`/`clearDepth`/`clearStencil` 传播
- [ ] 验证：depth clear 实际生效（用 `ThreeDSceneDemo` 验证深度测试行为）
- [ ] 注：`WMTRenderPassDesc` 结构体字段已存在，无需新增字段

#### 7C. Depth/Stencil 像素格式补全
- [ ] `MetalEnums.cs` / `bridge.h` `WMTPixelFormat` 新增 `Depth24Unorm_Stencil8 = 355`
- [ ] `bridge.m` `pixel_format_bytes_per_pixel()` 新增该格式分支（4 bpp）
- [ ] `MetalTexture.cs` 增加 `IsDepthFormat` 辅助属性
- [ ] 测试：创建 `Depth32Float` 与 `Depth24Unorm_Stencil8` 纹理，验证不崩溃

#### 7D. 光栅化状态 setters（5 个）
- [ ] `bridge.h`/`bridge.m`：`setCullMode` / `setFrontFacing` / `setDepthBias` / `setDepthClipMode` / `setTriangleFillMode`
- [ ] `MetalBridge.cs` DllImport；`MetalEnums.cs` 新增 `MTLCullMode` / `MTLWinding` / `MTLDepthClipMode` / `MTLTriangleFillMode` 枚举
- [ ] `MetalRenderEncoder.cs` 暴露 5 个公开方法
- [ ] **`MetalCommandList` 扩展**：`MetalCommandTypes.cs` 新增 `WMTRenderSetRasterState` 结构体（5 字段 + opcode）；`MetalCommandList.RecordSetRasterState(...)`；`bridge.m` `replay_render_cmd` 新增分支
- [ ] 测试：`RasterStateCommandListTests` — 录制 + 回放验证

#### 7E. DepthStencil/StencilReference setters
- [ ] `bridge.h`/`bridge.m`：`setDepthStencilState` / `setStencilReferenceValue`
- [ ] `MetalBridge.cs` + `MetalRenderEncoder.cs` 方法
- [ ] **`MetalCommandList` 扩展**：`WMTRenderSetDepthStencilState` + `WMTRenderSetStencilRef` 结构体 + Record + replay 分支
- [ ] 测试：录制 + 回放

#### 7F. VertexDescriptor
- [ ] `bridge.h`：`WMTVertexAttributeDesc` + `WMTVertexBufferLayoutDesc` + `WMTVertexDescriptor`（InlineArray 8）；`WMTRenderPipelineDesc` 增 `vertexDescriptor` 字段
- [ ] `bridge.m` `MTLDevice_newRenderPipelineState` 消费 `pd.vertexDescriptor`
- [ ] `MetalTypes.cs` 镜像结构体；`MetalRenderPipelineState.cs` builder 接受 vertex descriptor
- [ ] 测试：带 `[[attribute(0)]] position, [[attribute(1)]] color` 的顶点布局创建 PSO

#### 7G. Instanced Draw
- [ ] `bridge.h`/`bridge.m`：`drawPrimitives` 增 `instanceCount`；`drawIndexedPrimitives` 增 `instanceCount`（修改现有签名，同步更新所有调用方）
- [ ] `MetalBridge.cs` 签名更新；`MetalRenderEncoder.cs` 增 instanceCount 重载（保留旧重载默认=1）
- [ ] **`MetalCommandList` 扩展**：`WMTRenderDraw` 增 `InstanceCount` 字段；`WMTRenderDrawIndexed` 新结构体；replay 分支调用 instanced 变体
- [ ] 测试：`InstancedDrawTests` — 100 实例一次 draw，验证像素

#### 7H. Indirect Draw
- [ ] `bridge.h`/`bridge.m`：`drawPrimitivesIndirect` / `drawIndexedPrimitivesIndirect`
- [ ] `MetalBridge.cs` + `MetalRenderEncoder.cs` 方法
- [ ] **`MetalCommandList` 扩展**：`WMTRenderDrawIndirect` 结构体 + Record + replay
- [ ] 测试：`IndirectDrawTests`

#### 7I. MRT helper
- [x] 注：`WMTRenderPassDesc.colors[8]` 与 `WMTRenderPipelineDesc.colors[8]` 已存在，无需改 bridge
- [x] `MetalRenderPipelineState.cs` 增加 fluent builder `.WithColorAttachment(index, format)` `.WithDepth(format)`
- [x] 验证：`ThreeDSceneDemo` 双 MRT

#### 7J. ThreeDSceneDemo
- [ ] 100 个 instanced 旋转立方体，单次 `DrawIndexedPrimitives(instanceCount:100)`
- [x] 深度测试 + 背面剔除 + 双 MRT（BGRA8 lit color + RGBA16Float normal.xy/depth/roughness）
- [x] Blinn-Phong 方向光
- [x] 命令经 `MetalCommandList` 批量回放（当前以 `ThreeDSceneDemo` / `ThreeDSceneWindow` + `CommandRecorderTests` 验证）
- [ ] 断言：MRT0 alpha=1；MRT1 深度∈[0,1]；帧时 < 5ms；单帧 P/Invoke 数 ≤ 固定常数

---

### Phase 8: 命令抽象层 ICommandRecorder（重写版）

> 目标：统一命令入口 + 可测试/可观测，**且执行路径走 `MetalCommandList` 批量回放**。

#### 8A. 接口定型
- [x] `Rendering/ICommandRecorder.cs` — 采用蓝图 §8.5 类型化版本（`MetalBuffer`/`MetalTexture`/`MTLCullMode`/`DrawIndirect`/`WaitForFence` 等已落地能力）
- [x] 方法分组：管线状态 / 光栅化 / 混合 / 资源绑定 / 绘制 / Compute / 清除 / 同步 / 帧控制
- [x] 决策：接口只覆盖 Phase 7 已实现能力，不超前声明

#### 8B. MetalCommandRecorder 重写（关键修正）
- [x] 内部持有 `MetalCommandList _renderList`（render pass 级），不直接走逐命令 `MetalBridge.*`
- [x] 每个高频方法 → `MetalCommandList.RecordXxx(...)`
- [x] `EndRenderPass()` → `_renderList.ReplayRender(encoder)` + `Dispose()`（单次回放）
- [ ] `EndFrame()` → compute list 回放 + 提交
- [x] `BeginRenderPass(WMTRenderPassDesc)` 接收结构体（修正补丁类型不匹配）
- [x] 删除补丁 `_commands = List<IRenderCommand>` 装箱路径（执行路径零 GC）

#### 8C. 可观测/捕获层（降级用途）
- [ ] `Rendering/Commands/CommandStructs.cs` — 24 个 `readonly struct`，仅供 `RecordingCommandRecorder`
- [x] `RecordingCommandRecorder` — 录制到内存列表（仅测试/调试），支持 `Diff` + `CommandReplayer`
- [x] `LoggingCommandRecorder` — Decorator
- [x] `CommandReplayer` — 跨录制器回放

#### 8D. PipelineBuilder
- [x] `Rendering/PipelineBuilder.cs` — 链式构建不可变 `PipelineDescriptor`，覆盖 vertex descriptor / color attachments / blend / depth / sample / label
- [ ] 参数校验：缺 vertex shader 抛 `InvalidOperationException`
- [x] `Build()` 产出 `MetalRenderPipelineState`

#### 8E. 适配 Phase 7 全部新 API
- [x] `ICommandRecorder` 暴露 7D/7E/7G/7H 全部新方法
- [ ] `MetalCommandRecorder` 每个方法 → 对应 `MetalCommandList.RecordXxx`

#### 8F. 测试
- [ ] 装饰器透明性
- [ ] Memento 回合
- [x] **批量回放保真**：经 `MetalCommandList` 回放路径已由 `CommandRecorderTests` + `ThreeDSceneIntegrationTests` 覆盖
- [ ] **性能回归**：1000 instanced draw 经 `ICommandRecorder` 的 P/Invoke 数 ≤ 固定常数
- [ ] PipelineBuilder 参数校验
- [ ] 迁移现有 demo（TexturedApp/InstancedTrianglesDemo/ImGuiApp）验证无回归

---

### Phase 9: 着色器编译器（修正版）

#### 9A. 放宽 AGENTS.md（前置审批）
- [ ] §5.1 修订：禁止运行时 MSL 编译增加例外——"仅 `SpirvCrossCompiler` 路径允许 `newLibraryWithSource`；Slang 着色器仍必须预编译为 .metallib"
- [ ] §7.1 依赖清单记录 `libspirv-cross.dylib`
- [ ] §5.2 记录：新增 bridge 函数 `MTLDevice_newLibraryWithSource`

#### 9B. IShaderCompiler 接口
- [ ] `Shader/IShaderCompiler.cs` / `IShaderProgram.cs` / `ShaderFormat` / `ShaderCompileOptions`

#### 9C. SlangCompiler（合规路径）
- [ ] `Shader/Compilers/SlangCompiler.cs` — 提炼 `ComputeShaderDemo.cs:122-188` 子进程模式
- [ ] slangc 增 `--output-reflection-file`；MSC 增 `--output-reflection-file`（Phase 10 前置）
- [ ] 错误处理：stderr → `ShaderCompileException`
- [ ] 测试：`SlangCompilerTests`

#### 9D. ShaderCache（两级缓存）
- [ ] L1 `ConcurrentDictionary<SHA256, CachedShader>`；L2 `~/.metal-rendering-engine/shader-cache/`（LRU 256MB）
- [ ] 命中流程：L1 → L2 → 编译双写
- [ ] 测试：首次 vs 命中（<5ms）；跨进程复用；LRU 淘汰

#### 9E. SpirvCrossCompiler（需 9A 放宽）
- [ ] 引入 `libspirv-cross.dylib`
- [ ] `Shader/Interop/SpirvCrossBridge.cs` P/Invoke
- [ ] `bridge.h`/`bridge.m` 新增 `MTLDevice_newLibraryWithSource`
- [ ] `MetalBridge.cs` + `MetalDevice.NewLibraryWithSource`
- [ ] 测试：`SpirvCrossCompilerTests`

#### 9F. ShaderReflection 数据模型
- [ ] `Shader/Reflection/ShaderReflection.cs` / `MscReflectionParser.cs`
- [ ] `IShaderProgram.Reflection` 由 `MscReflectionParser` 填充
- [ ] 测试：解析 `Multiply.reflect.json`，断言 UAV@buffer(2)、24 字节

---

### Phase 10: 资源绑定层（修正版）

#### 10A. 多资源 argument buffer PoC（前置，决策门）
- [ ] 测试 shader：1 CBV + 2 UAV + 2 texture + 1 sampler
- [ ] `MTL_SHADER_VALIDATION=1` 抓 MSC 实际绑定
- [ ] 记录 CBV/texture/sampler 布局、EltOffset 对齐、vertex/fragment 是否共享
- [ ] 产出 `docs/argument-buffer-layout.md`
- [ ] **决策门**：若不稳定，回退 10E 半手动

#### 10B. ArgumentBufferEncoder
- [ ] `Binding/ArgumentBufferEncoder.cs` — 按 10A 布局序列化 `ResourceTable`
- [ ] 扩展 `CBufferDescriptor` / `TextureDescriptor` / `SamplerDescriptor`
- [ ] `Encode(...)` — `UseResource` + `SetBytes(argData, index:2)`
- [ ] 测试：字节数与偏移符合 10A 规格

#### 10C. ResourceTable
- [ ] `Binding/ResourceTable.cs` — `BindBuffer/Texture/Sampler(name, resource)`；`Apply(ICommandRecorder, ShaderReflection)`
- [ ] 两级表：PerFrame + PerMaterial
- [ ] 测试：Apply 录制正确命令

#### 10D. 编译后 MSBuild 反射生成
- [ ] `build/targets/BindingGen.targets` — `AfterTargets="CopyGeneratedMetallibs"`
- [ ] 读 `*.reflect.json` → 生成 `*.bindings.json` → 嵌入资源
- [ ] `Binding/ReflectionLoader.cs` 运行时加载
- [ ] 测试：`ReflectionLoaderTests`

#### 10E. 半自动 BindingLayout（可选便利层）
- [ ] `Binding/ShaderBindingLayout.cs` 基类
- [ ] 手写/脚本生成 `*BindingLayout` 子类
- [ ] 测试：`PbrShaderBindingLayout` + ResourceTable 联动

---

### Phase 11: 引擎自洽与完善

#### 11A. Shader CLI 编译工具
- [ ] `src/MetalRenderingEngine.ShaderCompiler/` dotnet tool
- [ ] 替代 `compile_shaders.sh` 核心逻辑

#### 11B. 复杂场景 Demo 套件
- [ ] ThreeDSceneDemo（7J）/ ShaderCompilerDemo / BindingLayoutDemo / PbrDemo / ParticleDemo / ShadowDemo / BatchBenchDemo

#### 11C. 性能基准套件
- [ ] `Instanced1000_FrameTimeUnder8ms` / `Compute1M_DispatchUnder2ms` / `ShaderCacheHit_Under1ms` / `MetalCommandList_10000Draws_Under50us_Encoding` / `ICommandRecorder_PInvokeCountConstant`（新增，守护 Phase 6/8）

#### 11D. 整体集成测试
- [ ] `PbrPipeline_EndToEnd_RendersNonBlackPixels`
- [ ] 多帧稳定性（1000 帧无崩溃）
- [ ] 回归：现有 demo 经 ICommandRecorder 无行为变化

#### 11E. 文档与 AGENTS.md 更新
- [ ] AGENTS.md §10 变更记录；§5.1/§7.1 反映 9A 放宽

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
