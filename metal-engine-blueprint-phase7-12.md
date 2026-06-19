# MetalRenderingEngine 路线图 Phase 7–11

> 审查日期: 2026-06-18 · 仓库 `skybuleli/MetalRenderingEngine`
> 源代码位置: `src/MetalRenderingEngine.Core/` · Bridge: `native/bridge.h` + `native/bridge.m`

---

## 当前状态总览

Phase 1–6 已完成。引擎可在 Apple Silicon 上运行 compute shader、渲染三角形/纹理四边形、Mandelbrot 生成、ImGui 调试 UI、instanced 批量绘制。

| 指标 | 数值 |
|------|------|
| C# 代码行数 | 9,855 |
| Bridge C 函数 | 80 |
| C# P/Invoke | 77 |
| 自动化测试 | 57 xUnit [Fact] |
| 可运行 Demo | 8 个 |
| ShaderGen 源生成器 | 2,530 行 |

### 已完成能力清单

| 层级 | 能力 |
|------|------|
| **Metal 设备** | Device 创建/查询、UMA 检测、推荐显存上限 |
| **Buffer** | 创建 (Shared/Private/Managed)、填充、回读、GPU 地址、Span 视图 |
| **Texture** | 创建 (2D/Depth)、`replaceRegion` 上传、`getBytes` 回读、drawable 包装 |
| **Sampler** | 创建、min/mag/mip filter、address mode |
| **Library** | 从 .metallib 字节流加载、按名称取 Function |
| **Compute Pipeline** | PSO 创建、`SetComputePipelineState`、`SetBuffer`、`SetBytes`、`SetTexture`、`DispatchThreadgroups`、`UseResource` |
| **Render Pipeline** | PSO 创建、`SetRenderPipelineState`、`SetVertexBuffer`、`SetFragmentBuffer`、`SetFragmentTexture`、`SetFragmentSamplerState`、`SetViewport`、`SetScissorRect`、`DrawPrimitives`、`DrawIndexedTriangles`、`SetVertexBytes`、`SetFragmentBytes` |
| **同步** | MTLSharedEvent (CPU-GPU signal/wait/notify)、SharedEventPool、GpuFence (AsyncCallback+BlockingWait 双策略)、MTLFence (GPU-only)、`EncodeWaitForEvent` |
| **窗口** | Cocoa NSWindow + CAMetalLayer (bridge 直连)、SDL3 + SDL_Metal_CreateView |
| **命令批处理** | `MetalCommandList` — ring buffer + 链表结构体 + 单次 P/Invoke 回放 |
| **资源池** | BufferPool (2ⁿ-bucketed)、TexturePool (key-based)、TransientBufferAllocator (ring-buffer 帧分配) |
| **源生成器** | `ShaderGen` — C# `partial struct` + `[Shader]` → Slang 源码 → slangc → DXIL → MSC → metallib |
| **着色器加载** | `MetalShaderLoader` — `shaders/*.metallib` → ConcurrentDictionary 缓存 |

---

## 缺口总览

| 优先级 | 类别 | 缺失项 | 为什么必须做 |
|--------|------|--------|-------------|
| **P0** | 3D 渲染基础 | Depth/Stencil、CullMode、VertexDescriptor、Instanced/Indirect Draw、MRT | 不实现就无法渲染任何 3D 场景 |
| **P1** | 命令抽象层 | ICommandRecorder + Rendering 命名空间（已设计，未合入仓库） | 没有它就没有统一的命令模型 |
| **P1** | 着色器编译器 | IShaderCompiler 接口 + SpirvCrossCompiler + 运行时 MSL 编译 | 缺少运行时接纳外部着色器的能力 |
| **P1** | 资源绑定层 | Argument Buffer 封装 + 源生成器反射集成 | MSC 4.0 的输出不兼容直接 `setBuffer` |
| **P2** | 引擎自洽 | Shader Cache 磁盘持久化 + 复杂场景 Demo + 回归测试套件 | 离独立引擎还差最后一块拼图 |

---

## Phase 7: 3D 渲染基础补齐 (P0)

**目标:** 能渲染一个带深度测试、背面剔除、实例化、双 MRT 的 3D 场景。

**预估工时:** 3–5 h · **代码量:** ~230 行 C + ~200 行 C#

### 7.1 Depth/Stencil 状态对象

新增文件修改: `native/bridge.h` + `native/bridge.m` + `Metal/Interop/MetalBridge.cs`

```c
struct WMTStencilDescriptor {
    uint32_t stencilFailureOperation;    // MTLStencilOperation
    uint32_t depthFailureOperation;
    uint32_t depthStencilPassOperation;
    uint32_t stencilCompareFunction;     // MTLCompareFunction
    uint32_t readMask;
    uint32_t writeMask;
};

struct WMTDepthStencilDesc {
    uint32_t depthCompareFunction;
    uint8_t  depthWriteEnabled;
    uint8_t  _pad[3];
    struct WMTStencilDescriptor frontFaceStencil;
    struct WMTStencilDescriptor backFaceStencil;
};
```

- `MTLDevice_newDepthStencilState(device, desc)` → `mtl_handle_t`
- `MetalDepthStencilState : MetalObject` SafeHandle 封装
- `MetalRenderEncoder.SetDepthStencilState(MetalDepthStencilState state)`
- `MetalRenderEncoder.SetStencilReference(uint front, uint back)`

### 7.2 Depth/Stencil 纹理 (RenderPass Attachment)

`WMTRenderPassDesc` 当前只有 Color attachment。需要加 Depth/Stencil：

```c
struct WMTRenderPassDesc {
    // 现有 Color
    struct WMTColorAttachmentBuffer8 colorAttachments;
    uint32_t colorCount;
    // 新增
    struct WMTDepthStencilAttachment depthAttachment;
    struct WMTStencilAttachment stencilAttachment;
};
```

- `MTLTexture` 创建支持 `MTLPixelFormat.Depth32Float` 和 `MTLPixelFormat.Depth24Unorm_Stencil8`
- `LoadAction` / `StoreAction` 独立设置

### 7.3 光栅化状态

`MetalRenderEncoder` 新增方法:

```csharp
void SetCullMode(MTLCullMode mode);              // None / Front / Back
void SetFrontFacing(MTLWinding winding);         // Clockwise / CounterClockwise
void SetDepthBias(float slope, float clamp, float bias);
void SetDepthClipMode(MTLDepthClipMode mode);    // Clip / Clamp
void SetTriangleFillMode(MTLTriangleFillMode);   // Fill / Lines
```

> **设计意图:** 这些状态在 Metal 中属于 `MTLRenderCommandEncoder`，每个 draw call 前可动态切换，不像 Vulkan 那样全部编码到 PSO 里。Metal 的 PSO 只锁定 shader + attachment format + blend。因此 `SetCullMode` 等必须暴露在 encoder 级别。

### 7.4 VertexDescriptor

当前 `WMTRenderPipelineDesc` 不接受顶点布局描述——shader 的 `[[attribute(N)]]` 无法与 buffer layout 对应。

```c
struct WMTVertexAttributeDesc {
    uint32_t format;       // MTLVertexFormat
    uint32_t offset;
    uint32_t bufferIndex;
};

struct WMTVertexBufferLayoutDesc {
    uint32_t stride;
    uint32_t stepFunction; // MTLVertexStepFunction
    uint32_t stepRate;
};

// InlineArray 限制: 最多 8 attributes + 8 layouts
struct WMTVertexDescriptor {
    struct WMTVertexAttributeDescBuffer8 attributes;
    uint32_t attributeCount;
    struct WMTVertexBufferLayoutDescBuffer8 layouts;
    uint32_t layoutCount;
};
```

`WMTRenderPipelineDesc` 增加 `vertexDescriptor` 字段。

### 7.5 Instanced + Indirect Draw

```csharp
// Instanced
void DrawPrimitives(MTLPrimitiveType type, ulong vStart, ulong vCount, ulong instanceCount);
void DrawIndexedPrimitives(MTLPrimitiveType type, ulong iCount, MTLIndexType iType,
                           MetalBuffer ib, ulong ibOffset, ulong instanceCount);

// Indirect (骨架)
void DrawPrimitivesIndirect(MetalBuffer buffer, ulong offset);
void DrawIndexedPrimitivesIndirect(MetalBuffer buffer, ulong offset);
```

### 7.6 MRT (多渲染目标)

当前只有一个 color attachment。扩展 `WMTRenderPassDesc` 支持 ≤ 8 个：

```csharp
var rp = new WMTRenderPassDesc();
rp.SetColorAttachmentAt(0, new WMTColorAttachment { ... }); // albedo
rp.SetColorAttachmentAt(1, new WMTColorAttachment { ... }); // normal+depth
rp.SetColorAttachmentAt(2, new WMTColorAttachment { ... }); // motion vector
rp.SetDepthAttachment(new WMTDepthStencilAttachment { ... });
```

### 验证: 3D 场景 Demo (`ThreeDSceneDemo`)

渲染 100 个 instanced 旋转立方体，带深度测试、背面剔除、双 MRT（lit color + 轻量 G-buffer）。

```
场景参数:
- 100 个立方体，随机位置，instanced draw (1 call = 100 cubes)
- 单个 MTLBuffer 存 instance 数据 (model matrix × 100)
- 摄像机绕 Y 轴旋转
- 方向光 Blinn-Phong 光照
- 录制路径走 `MetalCommandRecorder` / `MetalCommandList` 批量回放

渲染目标:
- MRT0: BGRA8Unorm — 最终颜色 (albedo × lighting)
- MRT1: RGBA16Float — normal.xy 编码 + depth + roughness

断言 (测试):
- MRT0 每个像素 alpha = 1
- MRT1 的 z 分量 (深度) 在 [0, 1] 之间
- MRT1 的 w 分量 roughness 在 [0, 1] 之间
- 帧时间 < 5ms (100 instanced cubes, M1)
```

---

## Phase 8: 命令抽象层 — ICommandRecorder (P1)

**目标:** 建立渲染命令的统一入口，用四种设计模式解决引擎架构的三个核心问题 —— 多后端透明切换、无侵入调试日志、帧级回归测试。

**设计源码位置:** `0002-feat-Command-Decorator-Builder-Memento.patch`（8 文件，1,232 行，待合入仓库）  
**现有架构文档:** `command-recorder-architecture.html`

**预估工时:** 3–4 h · **代码量:** ~100 行增量（主要工作量在合入 + 适配 Phase 7 新 API）

### 8.0 架构设计意图

当前引擎的渲染命令直接调用 `MetalRenderEncoder` 的方法：

```csharp
// 现状: Demo 代码与 Metal 编码器强耦合
encoder.SetRenderPipelineState(pso);
encoder.SetVertexBuffer(vb, 0, 0);
encoder.SetFragmentBuffer(ub, 0, 0);
encoder.DrawTriangles(0, 3);
encoder.EndEncoding();
```

这带来三个问题:

1. **不可测试** — 无法在没有 GPU 的环境下验证命令序列正确性
2. **不可观测** — 出 bug 时不知道哪条命令出了问题（Metal 验证层错误信息与 C# 调用栈脱节）
3. **不可对比** — 无法对同一场景录制两次命令序列然后 diff

ICommandRecorder 的设计用 **Command + Decorator + Builder + Memento** 四种模式解决这三个问题。

### 8.1 命令模式 (Command Pattern) — 每个渲染操作是一个不可变值对象

```
IRenderCommand (接口)
    │
    ├── DrawCommand (vertexCount, instanceCount, firstVertex, firstInstance)
    ├── DrawIndexedCommand (indexCount, instanceCount, firstIndex, ...)
    ├── DispatchCommand (groupsX, groupsY, groupsZ)
    ├── SetProgramCommand (pso handle)
    ├── SetVertexBufferCommand (buffer, offset, index)
    ├── SetFragmentBufferCommand (buffer, offset, index)
    ├── SetTextureCommand (texture, index)
    ├── SetSamplerCommand (sampler, index)
    ├── SetViewportCommand (x, y, w, h, znear, zfar)
    ├── SetScissorCommand (x, y, w, h)
    ├── SetCullModeCommand (mode)
    ├── SetDepthStateCommand (state)
    ├── ClearColorCommand (rtIndex, color)
    ├── ClearDepthCommand (depth)
    ├── BarrierCommand ()
    ├── SetRenderTargetsCommand (colorTex[], depthTex)
    └── ... (共 24 种)
```

**全部是 `readonly struct`** — 零 GC 压力，可安全序列化，可在录制后复查。

```csharp
// 每个命令自带人类可读输出
var cmd = new DrawIndexedCommand(36, 100, 0, 0, 0);
cmd.AppendLog(sb);
// → "DrawIndexed idxCnt=36 inst=100 firstIdx=0 vertOff=0 firstInst=0"
```

### 8.2 装饰器模式 (Decorator) — 无侵入日志

`LoggingCommandRecorder` 透明包裹任何 `ICommandRecorder` 实现，在每次调用前后输出结构化日志：

```
// 实现原理
class LoggingCommandRecorder : ICommandRecorder {
    readonly ICommandRecorder _inner;
    readonly TextWriter _output;

    public void DrawIndexed(int iC, int inC, int fI, int vO, int fIn) {
        _output.WriteLine($"[{stopwatch.Elapsed}] DrawIndexed idxCnt={iC} inst={inC}");
        _inner.DrawIndexed(iC, inC, fI, vO, fIn);
    }
}
```

**意图:** 不改 `MetalCommandRecorder` 一行代码，通过组合实现日志。Debug 模式自动开启：

```csharp
#if DEBUG
var recorder = new LoggingCommandRecorder(
    new MetalCommandRecorder(device, layer), Console.Out);
#else
var recorder = new MetalCommandRecorder(device, layer);
#endif
```

### 8.3 备忘录模式 (Memento) — 录制并比对命令序列

`RecordingCommandRecorder` 把每条命令存入内存列表而非执行，用于回归测试：

```csharp
// 场景: 帧回归测试
// 录制参考帧
var refRecorder = new RecordingCommandRecorder();
RenderFrame(refRecorder);  // → 记录 142 条命令

// 修改着色器/管线代码后
var curRecorder = new RecordingCommandRecorder();
RenderFrame(curRecorder);

// 自动 diff
var diffs = RecordingCommandRecorder.Diff(refRecorder, curRecorder);
Assert.Empty(diffs, $"命令序列不一致: {string.Join("\n", diffs)}");
```

**意图:** 渲染引擎重构时，不需要肉眼检查画面——命令序列一致则画面一致。这比像素级 hash 对比更精确（像素 hash 会受到浮点精度、驱动差异影响）。

`CommandReplayer` 可将录制的命令序列回放到任意录制器：

```
RecordingCommandRecorder → CommandReplayer → MetalCommandRecorder   (执行)
                                            → RecordingCommandRecorder (二次录制对比)
                                            → LoggingCommandRecorder   (输出日志)
```

### 8.4 建造者模式 (Builder) — Pipeline 创建

`PipelineBuilder` 链式构建不可变的 `PipelineDescriptor`：

```csharp
var desc = new PipelineBuilder()
    .WithVertexShader(vsBytes)
    .WithFragmentShader(fsBytes)
    .WithVertexDescriptor(vd)
    .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
    .WithBlend(0, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha)
    .WithDepth(MTLPixelFormat.Depth32Float)
    .WithSampleCount(1)
    .WithDebugName("PbrPipeline")
    .Build();
```

**意图:** 管线创建参数多（shader ×2、color attachment ×N、blend ×N、depth、sample、label），Builder 避免构造函数参数爆炸，同时提供默认值和参数校验。

### 8.5 ICommandRecorder 接口（核心方法分组）

```csharp
public interface ICommandRecorder : IDisposable
{
    // ── 管线状态 ──
    void SetProgram(IShaderProgram program);
    void SetRenderTargets(ReadOnlySpan<MetalTexture> colors, MetalTexture? depthStencil);

    // ── 光栅化状态 ──
    void SetViewport(float x, float y, float w, float h, float znear, float zfar);
    void SetScissor(int x, int y, int w, int h);
    void SetCullMode(MTLCullMode mode);
    void SetFrontFace(MTLWinding winding);
    void SetDepthState(MetalDepthStencilState state);
    void SetStencilReference(uint front, uint back);
    void SetDepthBias(float slope, float clamp, float bias);

    // ── 混合状态 ──
    void SetBlendColor(float r, float g, float b, float a);

    // ── 资源绑定 ──
    void SetVertexBuffer(MetalBuffer buffer, ulong offset, uint index);
    void SetVertexBuffers(ReadOnlySpan<(MetalBuffer, ulong)> buffers, uint startIndex);
    void SetFragmentBuffer(MetalBuffer buffer, ulong offset, uint index);
    void SetFragmentTexture(MetalTexture texture, uint index);
    void SetFragmentSampler(MetalSamplerState sampler, uint index);

    // ── 绘制命令 ──
    void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance);
    void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int vertexOffset, int firstInstance);
    void DrawIndirect(MetalBuffer buffer, ulong offset);
    void DrawIndexedIndirect(MetalBuffer buffer, ulong offset);

    // ── Compute ──
    void SetComputeBuffer(MetalBuffer buffer, ulong offset, uint index);
    void SetComputeTexture(MetalTexture texture, uint index);
    void Dispatch(int groupsX, int groupsY, int groupsZ);
    void DispatchIndirect(MetalBuffer buffer, ulong offset);

    // ── 清除 ──
    void ClearColor(uint rtIndex, float r, float g, float b, float a);
    void ClearDepth(float depth);

    // ── 同步 ──
    void Barrier();
    void SignalFence(GpuFence fence);
    void WaitForFence(GpuFence fence);

    // ── 帧控制 ──
    void BeginFrame(MetalDrawable drawable);
    void EndFrame();
}
```

### 8.6 文件清单（补丁中）

| 文件 | 行数 | 说明 |
|------|------|------|
| `Rendering/Commands/RenderCommand.cs` | 43 | 24 种命令枚举 + `IRenderCommand` 接口 |
| `Rendering/Commands/CommandStructs.cs` | 251 | 24 个 `readonly struct` 命令体 |
| `Rendering/ICommandRecorder.cs` | 113 | 录制器接口 (~45 API) |
| `Rendering/MetalCommandRecorder.cs` | 370 | Metal 后端实现，委托到 MetalEncoder/Queue |
| `Rendering/LoggingCommandRecorder.cs` | 167 | 日志装饰器 |
| `Rendering/RecordingCommandRecorder.cs` | 121 | 命令捕获 + Diff |
| `Rendering/PipelineBuilder.cs` | 113 | 管线 Builder |
| `Rendering/CommandReplayer.cs` | 54 | 跨录制器回放 |

### 8.7 Phase 7 → Phase 8 适配清单

Phase 7 新增的 API 需要在 ICommandRecorder 中暴露：

- `SetCullMode` / `SetFrontFace` / `SetDepthBias` / `SetDepthClipMode` → 新增 `SetCullModeCommand` 等 4 个命令 struct
- `SetDepthState` → 新增 `SetDepthStencilStateCommand`
- `SetStencilReference` → 新增 `SetStencilReferenceCommand`
- `DrawPrimitives(instanced)` / `DrawIndexedPrimitives(instanced)` → 扩展现有 `DrawCommand` (已有 instanceCount 字段)
- `DrawIndirect` → 新增 `DrawIndirectCommand`

### 验证: 命令抽象层测试

**测试 1 — 装饰器透明性:**
```csharp
var inner = new RecordingCommandRecorder();  // 只记录不执行
var logged = new LoggingCommandRecorder(inner, new StringWriter());
logged.DrawIndexed(36, 1, 0, 0, 0);
Assert.Single(inner.Commands);  // 装饰器正确穿透
Assert.Equal("DrawIndexed", inner.Commands[0].Name);
```

**测试 2 — Memento 回合:**
```csharp
// 录制 3 个绘制命令
using var rec = new RecordingCommandRecorder();
rec.Draw(3, 1, 0, 0);
rec.DrawIndexed(6, 1, 0, 0, 0);
rec.Dispatch(8, 8, 1);

// 回放到另一个录制器
var replayTarget = new RecordingCommandRecorder();
var replayer = new CommandReplayer(replayTarget);
replayer.Replay(rec);

// 两个录制器命令序列完全一致
Assert.Empty(RecordingCommandRecorder.Diff(rec, replayTarget));
```

**测试 3 — PipelineBuilder 参数校验:**
```csharp
// 缺少 VertexShader → Build() 应抛
Assert.Throws<InvalidOperationException>(() =>
    new PipelineBuilder().WithFragmentShader(fs).Build());
// 正常路径
var desc = new PipelineBuilder()
    .WithVertexShader(vs)
    .WithFragmentShader(fs)
    .Build();
Assert.NotNull(desc);
```

---

## Phase 9: 着色器编译器 (P1)

**目标:** 建立 IShaderCompiler 接口，支持三种输入格式编译为 Metal 可执行程序。

**预估工时:** 8–12 h · **代码量:** ~500 行 C#

### 9.1 IShaderCompiler 接口设计

```csharp
public enum ShaderFormat { Spirv, Slang, MslSource, Metallib }

public enum ShaderStage { Vertex, Fragment, Compute }

public interface IShaderCompiler
{
    /// <summary>编译着色器为 Metal 管线状态</summary>
    IShaderProgram Compile(byte[] data, ShaderFormat format, ShaderStage stage,
                           ShaderCompileOptions? options = null);

    /// <summary>链接 vertex + fragment 为完整 render pipeline</summary>
    IShaderProgram Link(IShaderProgram vertex, IShaderProgram fragment,
                        PipelineDescriptor pipelineDesc);
}

public interface IShaderProgram : IDisposable
{
    ShaderStage Stage { get; }
    MetalComputePipelineState? ComputePipeline { get; }  // compute shader
    MetalFunction? VertexFunction { get; }                // render shader vertex
    MetalFunction? FragmentFunction { get; }              // render shader fragment
    ShaderReflection Reflection { get; }                  // 绑定元数据
}
```

### 9.2 SpirvCrossCompiler — 外部着色器入口

**流转:** SPIR-V 二进制 → spirv-cross → MSL 源码 → `newLibraryWithSource` → MTLLibrary

设计决策: 不嵌入 spirv-cross 源码，而是 P/Invoke 预先编译的 `libspirv-cross.dylib`。

```
spirv-cross 的 C API 精简子集 (需要 P/Invoke 封装):
  spvc_context_create()
  spvc_context_parse_spirv(ctx, spirv_data, spirv_size)
  spvc_context_compile(ctx, "msl", &msl_source)
  spvc_context_get_msl_output(ctx, &msl_text, &msl_size)
  spvc_context_destroy()
```

bridge 端新增:
```c
// native/bridge.h 新增
mtl_handle_t MTLDevice_newLibraryWithSource(
    mtl_handle_t device,
    const char* source,     // MSL 源码 (UTF-8 C string)
    uint64_t sourceLength,
    mtl_handle_t* err_out
);
```

### 9.3 SlangCompiler — 引擎自有着色器

**流转:** Slang 源码 → slangc 子进程 → DXIL → metal-shaderconverter 子进程 → metallib → `newLibraryWithData`

复用已有的 `build/compile_shaders.sh` 流程。作为子进程调用（非 P/Invoke），因为 slangc 和 MSC 都是独立的命令行工具：

```csharp
class SlangCompiler : IShaderCompiler
{
    public IShaderProgram Compile(byte[] data, ShaderFormat format, ShaderStage stage, ...)
    {
        // 1. Slang 源码写入临时文件
        // 2. 调用 slangc -target dxil -profile sm_6_0 -entry XXX -stage XXX
        // 3. 调用 metal-shaderconverter input.dxil -o output.metallib
        // 4. 读取 .metallib 二进制
        // 5. MetalDevice.NewLibrary(metallibBytes)
        // 6. library.NewFunction("main")
    }
}
```

### 9.4 ShaderCache — 两级缓存

```
L1: ConcurrentDictionary<SHA256, CachedShader>  内存 (无限制, 进程生命周期)
L2: ~/.metal-rendering-engine/shader-cache/      磁盘 (LRU, 256MB 上限)

CacheKey = SHA256(输入字节 + 编译选项JSON + ShaderFormat + ShaderStage)

CachedShader file:
  ├── input.hash         (32 bytes)
  ├── output.metallib    (可变)
  ├── reflection.json    (反射元数据)
  └── timestamp.txt      (最后访问时间, 用于 LRU)
```

命中流程: L1 命中 → 直接返回。L1 未命中 → L2 查找 → 命中则加载到 L1。两级都未命中 → 编译 → 同时写入 L1 + L2。

LRU 淘汰: 定时扫描 L2 目录，删除超过 256MB 的最旧条目。

### 验证: 着色器编译器测试

**测试 1 — Metallib 直接加载 (已有，保持兼容):**
```csharp
var compiler = new SlangCompiler();
var program = compiler.Compile(precompiledMetallib, ShaderFormat.Metallib, ShaderStage.Compute);
Assert.NotNull(program.ComputePipeline);
```

**测试 2 — SPIR-V → Metal 端到端:**
```csharp
// 等价于 PoC 验证过的路径
byte[] spirv = GlslangValidator.CompileToSpirv(glslSource, ShaderStage.Vertex);
var program = new SpirvCrossCompiler().Compile(spirv, ShaderFormat.Spirv, ShaderStage.Vertex);
Assert.NotNull(program.VertexFunction);
Assert.NotNull(program.Reflection);
// 反射信息应包含 binding 信息
Assert.NotEmpty(program.Reflection.UniformBuffers);
```

**测试 3 — ShaderCache 命中:**
```csharp
// 首次编译
var p1 = compiler.Compile(slangSource, ShaderFormat.Slang, ShaderStage.Compute);
// 二次调用应命中缓存
var sw = Stopwatch.StartNew();
var p2 = compiler.Compile(slangSource, ShaderFormat.Slang, ShaderStage.Compute);
sw.Stop();
Assert.True(sw.ElapsedMilliseconds < 5); // 缓存命中 < 5ms
Assert.Equal(p1.ComputePipeline!.Handle, p2.ComputePipeline!.Handle);
```

**测试 4 — Vertex+Fragment 链接:**
```csharp
var vs = compiler.Compile(vsSpirv, ShaderFormat.Spirv, ShaderStage.Vertex);
var fs = compiler.Compile(fsSpirv, ShaderFormat.Spirv, ShaderStage.Fragment);
var pipeline = compiler.Link(vs, fs, pipeDesc);
Assert.NotNull(pipeline.RenderPipeline);
```

---

## Phase 10: 资源绑定层 (P1)

**目标:** 建立类型安全的资源绑定抽象，兼容 MSC 4.0 argument buffer，源生成器根据反射自动产出绑定代码。

**预估工时:** 6–8 h · **代码量:** ~500 行 C#

### 10.1 背景: MSC 4.0 的 Argument Buffer 模型

已知的关键发现 (来自 `docs/knowledge.md`):

| 知识 | 值 |
|------|-----|
| MSC 输出的 metallib 不能直接 `setBuffer(buf, slot)` 绑资源 | ❌ 不行 |
| 替代方式 | `UseResource + SetBytes({gpuAddress, len, stride}, index:2)` |
| `RWStructuredBuffer<T>` 的 UAV 描述符 | 24 字节 = gpuAddress(u64) + length(u64) + stride(u64) |
| buffer 索引偏移 | **+2**（buffer(0) = 根表, buffer(1) = 采样器, buffer(2) = per-draw 数据） |

这决定了引擎的资源绑定层不能简单地把 C# 对象映射到 `[[buffer(N)]]`。

### 10.2 ResourceTable

```csharp
/// <summary>
/// 两级资源表: PerFrame (vertex+fragment 共用 UBO、shadow map) +
///            PerMaterial (albedo、normal、roughness 纹理、材质 UBO)
/// Apply 时按名称查找并编码到当前 encoder。
/// </summary>
public sealed class ResourceTable
{
    public void BindBuffer(string name, MetalBuffer buffer, ulong offset);
    public void BindTexture(string name, MetalTexture texture);
    public void BindSampler(string name, MetalSamplerState sampler);

    /// <summary>将表中所有资源编码到 ICommandRecorder</summary>
    public void Apply(ICommandRecorder recorder, ShaderReflection reflection);
}

// 使用示例
var perFrame = new ResourceTable();
perFrame.BindBuffer("PerFrame", viewProjBuffer, 0);
perFrame.BindTexture("ShadowMap", shadowMap);

var perMaterial = new ResourceTable();
perMaterial.BindBuffer("Material", materialBuffer, 0);
perMaterial.BindTexture("AlbedoMap", albedoTexture);
perMaterial.BindTexture("NormalMap", normalTexture);
perMaterial.BindSampler("LinearSampler", linearSampler);

// 编码
perFrame.Apply(recorder, shader.Reflection);
perMaterial.Apply(recorder, shader.Reflection);
```

### 10.3 Argument Buffer 编码器

对 MSC 4.0 编译的 metallib（使用了 argument buffer 间接寻址）:

```csharp
public readonly struct UavDescriptor // 24 bytes
{
    public readonly ulong GpuAddress;
    public readonly ulong Length;
    public readonly ulong Stride;
}

public class ArgumentBufferBuilder
{
    /// <summary>从 ResourceTable 构建 argument buffer 的字节内容</summary>
    public static byte[] Build(ResourceTable table, ShaderReflection reflection);

    /// <summary>编码到 encoder (UseResource + SetBytes)</summary>
    public static void Encode(ICommandRecorder recorder, byte[] argumentData,
                              ReadOnlySpan<MetalObject> residentResources);
}
```

### 10.4 源生成器反射集成

扩展 `ShaderGen/BindingLayoutEmitter` 消费 slung-reflection JSON，自动生成:

```csharp
// ═══════════════════════════════════════════
// 自动生成, 源自 PbrShader 的 slang-reflection JSON
// ═══════════════════════════════════════════
[StructLayout(LayoutKind.Sequential)]
public unsafe struct PbrShader_PerFrameUBO
{
    public Matrix4x4 ViewProjection; // offset 0, size 64
    public Vector4 CameraPosition;   // offset 64, size 16
    public Vector4 LightDirection;   // offset 80, size 16
    public float Time;               // offset 96, size 4
}

public sealed class PbrShaderBindingLayout : ShaderBindingLayout
{
    public ConstantBuffer<PbrShader_PerFrameUBO> PerFrame; // buffer(0) set(0)
    public ConstantBuffer<MaterialUBO> Material;            // buffer(0) set(1)
    public Texture2D<float4> AlbedoMap;                     // texture(1) set(1)
    public SamplerState LinearSampler;                      // sampler(4) set(1)

    public void Apply(ICommandRecorder rec, ResourceTable table)
    {
        rec.SetVertexBuffer(table.Get("PerFrame"), 0, 0);
        rec.SetFragmentBuffer(table.Get("PerFrame"), 0, 0);
        rec.SetFragmentTexture(table.Get("AlbedoMap"), 0);
        rec.SetFragmentSampler(table.Get("LinearSampler"), 0);
    }
}
```

> **意图:** shader 代码是一切的真相来源。改了 shader 里的 binding 声明，重新编译，源生成器自动更新 C# 绑定代码。编译期就能捕获 binding 不匹配——“shader 期望 texture(3) 但 C# 只绑了 texture(0)”。

### 验证: 资源绑定测试

**测试 1 — ResourceTable Apply 正确编码:**
```csharp
var table = new ResourceTable();
table.BindBuffer("PerFrame", ub, 0);
var recorder = new RecordingCommandRecorder();
table.Apply(recorder, reflection);

// 验证录制的命令包含 SetVertexBuffer 和 SetFragmentBuffer
var cmds = recorder.Commands;
Assert.Contains(cmds, c => c is SetVertexBufferCommand { Index: 0 });
Assert.Contains(cmds, c => c is SetFragmentBufferCommand { Index: 0 });
```

**测试 2 — 源生成器 BindingLayout 编译期检查:**
```csharp
// 这段代码如果 shader reflection 中 buffer(0) 的名字不是 "PerFrame"，编译期 CS1061
var layout = new PbrShaderBindingLayout();
layout.PerFrame = ...;  // 属性名来自 reflection JSON，编译期绑定
```

**测试 3 — Argument Buffer 24 字节描述符:**
```csharp
var desc = new UavDescriptor { GpuAddress = 0x1234, Length = 4096, Stride = 4 };
var bytes = MemoryMarshal.AsBytes(new[] { desc });
Assert.Equal(24, bytes.Length);
// 小端编码: gpuAddr[0..8] + length[8..16] + stride[16..24]
```

---

## Phase 11: 引擎自洽与完善 (P2)

**目标:** 让引擎成为一个可以独立使用的渲染库：磁盘 shader 缓存、CLI 编译工具、复杂场景 Demo 套件、性能基准。

**预估工时:** 6–10 h · **代码量:** ~450 行 C#

### 11.1 Shader CLI 编译工具

将 `build/compile_shaders.sh` 升级为 C# dotnet tool：

```bash
dotnet run --project src/MetalRenderingEngine.ShaderCompiler \
    --input Shaders/ --output out/shaders/ --target metallib
```

批量扫描 `.slang` 文件 → `ShaderGen` 输出 → slangc → MSC → metallib → 写入 `out/shaders/`。

### 11.2 复杂场景 Demo 套件

| Demo | 验证能力 | 复杂度 |
|------|---------|--------|
| `ThreeDSceneDemo` | Phase 7: 100 instanced cubes + depth + MRT | ⭐⭐⭐ |
| `ShaderCompilerDemo` | Phase 9: SPIR-V → Metal 端到端 | ⭐⭐ |
| `BindingLayoutDemo` | Phase 10: 源生成器自动绑定 + ResourceTable | ⭐⭐⭐ |
| `PbrDemo` | PBR 管线: albedo/normal/roughness 纹理 + IBL | ⭐⭐⭐⭐ |
| `ParticleDemo` | Compute + Render 混合: GPU 粒子物理 | ⭐⭐⭐⭐ |
| `ShadowDemo` | 阴影贴图: RPass1→depth, RPass2→scene+shadow | ⭐⭐⭐⭐ |
| `BatchBenchDemo` | 10000 draw calls, 对比逐次 P/Invoke vs MetalCommandList | ⭐⭐ |

### 11.3 性能基准套件

```csharp
// 基准: 1000 instanced cubes, M1, BGRA8Unorm + Depth32Float
[Fact] public void Instanced1000_FrameTimeUnder8ms() { ... }
[Fact] public void Compute1M_DispatchUnder2ms() { ... }
[Fact] public void ShaderCacheHit_Under1ms() { ... }
[Fact] public void MetalCommandList_10000Draws_Under50us_Encoding() { ... }
```

### 11.4 整体集成测试

```csharp
/// <summary>
/// 端到端: C# PBR shader → ShaderGen → slangc → MSC → metallib
///         → 加载 → 创建 pipeline → 渲染 → 回读像素 → 验证
/// </summary>
[Fact]
public void PbrPipeline_EndToEnd_RendersNonBlackPixels()
{
    // 1. ShaderGen 生成 Slang 源码
    var slangSource = PbrShaderSlangSource.Source;

    // 2. Slang → DXIL → MSC → metallib
    var compiler = new SlangCompiler();
    var program = compiler.Compile(
        Encoding.UTF8.GetBytes(slangSource),
        ShaderFormat.Slang, ShaderStage.Fragment);

    // 3. 创建 pipeline + 资源
    using var device = MetalDevice.CreateSystemDefault();
    var pipeDesc = new PipelineBuilder()
        .WithVertexShader(...).WithFragmentShader(...)
        .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm).Build();

    // 4. 渲染一帧 (白色球体 PBR 材质)
    var pixels = RenderFrame(device, program, pipeDesc, Width, Height);

    // 5. 验证: 至少 30% 像素非黑色
    float nonBlack = pixels.Count(p => p is { R: > 0, G: > 0, B: > 0 });
    Assert.True(nonBlack / pixels.Length > 0.3f);
}
```

---

## 工作量估算

| 阶段 | 优先级 | 预估工时 | 新增代码 | 关键依赖 |
|------|--------|----------|---------|----------|
| Phase 7: 3D 渲染基础 | P0 | 3–5 h | ~230 C + ~200 C# | 无 |
| Phase 8: 命令抽象层 | P1 | 3–4 h | ~100 C# (合入+适配) | Phase 7 新 API |
| Phase 9: 着色器编译器 | P1 | 8–12 h | ~500 C# | 无 |
| Phase 10: 资源绑定层 | P1 | 6–8 h | ~500 C# | Phase 9 |
| Phase 11: 引擎自洽 | P2 | 6–10 h | ~450 C# | Phase 8+9+10 |
| **总计** | | **26–39 h** | **~1,980** | |

```
已完成 (Phase 1–6)  ████████████████░░░░  9,855 行 (83%)
Phase 7–11 新增      ░░░░░░░░░░░░░░░███░  ~1,980 行 (17%)
```

---

## 依赖关系图

```
Phase 7 (3D 基础) ─────────────────────────────┐
    │                                            │
    ├──→ Phase 8 (命令抽象层) ──────────────────┤
    │                                            │
    │    Phase 9 (着色器编译器) ──→ Phase 10 ────┤
    │                                            │
    └────────────────→ Phase 11 (引擎自洽) ←─────┘
```

- **Phase 7 独立可做** — 无前置依赖，做完就能渲染 3D 场景
- **Phase 8** 依赖 Phase 7 新增 API（DepthState,CullMode 等需暴露到 ICommandRecorder）
- **Phase 9 独立可做** — 与 Phase 8 可并行
- **Phase 10** 依赖 Phase 9 的 ShaderReflection 产出
- **Phase 11** 依赖 Phase 8+9+10

---

## 关键架构决策

| # | 决策 | 依据 |
|---|------|------|
| D1 | ICommandRecorder 使用 Command+Decorator+Builder+Memento 四种模式 | 解决可测试性、可观测性、可对比性三个核心问题 |
| D2 | 命令全部使用 `readonly struct` | 零 GC 压力，可序列化，可安全跨录制器传递 |
| D3 | 资源绑定用 argument buffer 布局 + 编译期源生成 | MSC 4.0 实测: buffer 索引偏移 +2；编译期生成避免运行时反射开销 |
| D4 | 着色器使用 SPIR-V 作为外部交换格式 | 语义完整、附带反射元数据、标准化 |
| D5 | SPIR-V → spirv-cross → MSL → Metal（当前） | PoC 已验证可用；slangc CLI 不支持 .spv 输入 |
| D6 | 引擎自有着色器走 Slang → DXIL → MSC → metallib（当前） | Path A 全链路已验证端到端可用 |
| D7 | Metal Bridge P/Invoke 待迁移至 ClangSharp 自动生成 | 借鉴 s&box InteropGen .def 模式；77 个手写 DllImport 可被替换 |
| D8 | ShaderCache 使用 SHA256 + LRU 两级缓存 | 编译结果与输入字节精确绑定；256MB 上限适配 8GB 内存设备 |

---

## 参考

- 仓库: [skybuleli/MetalRenderingEngine](https://github.com/skybuleli/MetalRenderingEngine)
- Phase 1–6 原始路线图: `docs/phase-1-6-roadmap.md`
- 项目知识索引: `docs/knowledge.md`
- Slang 反射绑定设计: `docs/slang-reflection-binding-design.md`
- 命令录制器架构: `command-recorder-architecture.html`
- 命令录制器补丁: `0002-feat-Command-Decorator-Builder-Memento.patch` (8 文件, 1,232 行)
- PoC 着色器包: `poc_verify.zip`
- 技术栈: .NET 10 · Metal 4 · Slang · SPIR-V · macOS 26.4 · Apple M1
