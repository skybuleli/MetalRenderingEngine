# C# → Metal 渲染引擎 — 全量任务方案（Phase 1–6）

## Context

仓库当前只有两份文档（`AGENTS.md` + `csharp-metal-rendering-blueprint.md`），没有任何源码。本方案根据蓝图把 6 个阶段拆成可逐步落地的任务清单，覆盖从 **C# → bridge.m → Metal compute kernel 走通** 一直到 **Roslyn 源生成器 + 引擎抽象层**。

用户确认的关键决策：
- **窗口/输入：SDL3**（macOS 上通过 `SDL_Metal_GetLayer` 拿到 `CAMetalLayer`，交给我们的 bridge）；**ImGui** 推迟到 Phase 3+ 作为调试 UI，绘制完全走我们自己的 render encoder（不引入 `imgui_impl_metal.mm`，避免违反 AGENTS.md §5.1）
- **项目结构：单 Core 起步**（`MetalRenderingEngine.Core` + `MetalRenderingEngine.Demo` + Shaders 子目录），Tests 与 Avalonia 抽象层延后到 Phase 6 拆分
- **范围：Phase 1–6 全量路线图**

所有阶段严格遵守 AGENTS.md 的禁止事项：禁止 metal-cpp / MoltenVK / 第三方 Metal 绑定 / 运行时 shader 编译 / bridge.m 之外写 ObjC。

---

## DXMT 参考映射（核心读图）

DXMT 已 clone 到 `references/dxmt/`（已加 .gitignore）。下面是我扫完后逐条对应的"借鉴清单"，每一条 Phase 都会在任务里复用：

### A. 桥接层形态 — 走 `nativemetal` 风格，不走 `winemetal` 的 unixcall

DXMT 有两套桥：
- **`src/winemetal/`**（约 3100 行）：为 Wine unixcall 设计，每个函数把参数打包成 `struct unixcall_generic_obj_uint64_obj_ret`，C 侧 thunk 调 `WINE_UNIX_CALL(code, params)`，ObjC 侧用 `static NTSTATUS _func(void *obj)` 包装（`src/winemetal/unix/winemetal_unix.c:62-72`）。**这是 Wine 专属机制，我们不应该照搬**——会引入大量样板代码而没有任何收益。
- **`src/nativemetal/`**：基本只是占位 + `meson.build`，未实质实现；说明 DXMT 也认为非-Wine 场景应该走"直接 C 函数"。

我们的 bridge.m 选 **"直接 C 函数 + 返回值"** 模式（蓝图 §2.2 的判断没变），但 **API 命名风格借鉴 winemetal.h**：`MTLDevice_newCommandQueue / MTLBuffer_contents / MTLComputeCommandEncoder_setBuffer`——这种 `类_方法` 命名比蓝图原案的 `metal_compute_encoder_set_buffer` 更对应官方 ObjC 接口、可读性更好、grep 更准。我们采用之，并把蓝图 §4.2 的命名作为兼容别名废弃掉。

参考：`references/dxmt/src/winemetal/winemetal.h:36-105`（C 头声明风格）、`references/dxmt/src/winemetal/Metal.hpp:13-122`（C++ 端 RAII wrapper Reference<T>）。

### B. `obj_handle_t` vs `void*` — 用 uint64_t

DXMT 把 Metal 对象一律表示为 `typedef uint64_t obj_handle_t`（`winemetal.h:28`），而不是 `void*`。理由：

1. P/Invoke marshalling 更简单，C# 端就是 `ulong` / `nuint`
2. 跨 ABI（32→64 位、Wine→Mac）时不容易出错
3. 0 = NULL_OBJECT_HANDLE，所有比较一致

**我们采用：** bridge.h 改成 `typedef uintptr_t mtl_handle_t;`（macOS 上 = uint64），C# 端用 `nuint` 接，SafeHandle 内部仍用 `IntPtr`（与 `nuint` 等价）。蓝图原案的 `void*` 改为这个。

### C. SafeHandle ↔ DXMT 的 `Reference<T>`

`references/dxmt/src/winemetal/Metal.hpp:62-122` 的 `Reference<Class>` 模板就是 C# `SafeHandle` 的 C++ 等价物：
- 拷贝构造 = `retain()`
- 移动构造 = 转移所有权（move.handle = NULL）
- 析构 = `release()`
- `operator=(nullptr)` = 显式释放

我们的 `MetalObject : SafeHandle` 完全是同构的，只是用 C# 语法表达。**Phase 1 实现时直接对照 Metal.hpp:80-120 的语义复刻**——它已经把 retain/release 该在哪个生命周期点触发都验证过了。

### D. 命令编码批处理 — 蓝图原本说"单次调用 优先简单"，DXMT 给出了关键反证

DXMT 内部走的是 **两级 batch**：
1. C++ 端用 `CommandList<Context>` 把每个 draw call 编码成一个 lambda command 链表（`references/dxmt/src/dxmt/dxmt_command_list.hpp:48-112`），通过 `emit(Fn, buffer)` placement-new 到 `CommandChunk` 的 ring bump allocator 上（`dxmt_command_queue.hpp:95-145`）。
2. 真正调 Metal API 时，再把多个命令打包成 `wmtcmd_*` C struct 单链表，**一次 unixcall 完成整个 encoder pass 的所有命令**（`winemetal_unix.c:785-868` 的 `_MTLComputeCommandEncoder_encodeCommands` 大 switch）。

对我们的启示：
- **Phase 1-2 保持"单次 P/Invoke 一个 Metal 调用"** 的简单模式（每帧几十次 P/Invoke 在 M1 上完全可承受）
- **Phase 6（性能优化阶段）引入 batch encoder**：参考 `winemetal.h:876-1110` 定义一组 `wmtcmd_compute_*` / `wmtcmd_render_*` POD struct + `wmtcmd_base { uint16_t type; ptr next; }` 链表头，bridge.m 用 `switch(type)` 一次回放整个 pass。1000 个 draw call 从 1000 次 P/Invoke 降到 1 次。**这条预先写进 Phase 6 任务，不在 Phase 1-3 提前优化**。

### E. Shader Converter — 我们走的路与 DXMT **完全不同**，不照搬

DXMT 自己写了一个 `airconv`：用 LLVM IR 直接把 DXBC → AIR（Apple GPU bitcode），最后用 `MetallibWriter` 拼装成 `.metallib`（`references/dxmt/src/airconv/metallib_writer.cpp:1-50`）。这条路线对我们 **完全不适用**：

| 项 | DXMT airconv | 我们的 Path A |
|----|-------------|---------------|
| 输入 | DXBC (D3D 字节码) | Slang HLSL |
| 中间 | 自实现 LLVM IR | slangc 输出 DXIL |
| 工具 | 自实现 metallib_writer + LLVM bitcode writer | Apple 的 `metal-shaderconverter` 二进制 |
| 维护成本 | 极高（自己写一个 shader 编译器） | 低（调命令行） |

**判断：不借鉴 airconv 的任何代码。** 但有一点可参考：airconv 定义的 binding 索引常量 `SM50_BINDING_INDEX_*`（`airconv_public.h:21-31`）说明 metal-shaderconverter 输出后 buffer index 需要重映射——Phase 4 工具链集成时，我们要从 MSC 的 reflection 输出读 binding 表传给 C#，避免硬编码 `register(b0)` → `buffer(0)`。

### F. Fence / SharedEvent / 帧同步

DXMT 用 **MTLSharedEvent + 后台 listener 线程** 做帧同步（`dxmt_command_queue.cpp:41-63`、`209-210`），有 `signal_frame_latency_fence_` 和 `frame_latency_fence_` 两层 fence。

对我们的启示：
- Phase 3 双缓冲先用 **简单的 MTLFence**（`MTLDevice newFence` + encoder updateFence/waitForFence；DXMT API 见 `winemetal.h:1856`、`winemetal_unix.c:850-859`）
- Phase 6 高级阶段再引入 MTLSharedEvent + CPU fence（参考 `dxmt_command_queue.cpp:31-72`，但简化掉 Wine 相关部分）

### G. 不能照搬的部分（明确划线）

- ❌ `src/winemetal/unix/` 的整个 `_NSObject_retain/release` unixcall 包装层（Wine 专属）
- ❌ `src/airconv/`（完整 LLVM-based shader compiler，超出本项目范围）
- ❌ `src/d3d11/` `src/d3d10/` `src/dxgi/`（DirectX API 翻译，我们不做）
- ❌ `Metal.hpp` 的 C++ 模板代码（C# 端直接用 SafeHandle，不需要 C++ 中间层）
- ❌ `src/util/util_win32_compat.h` 之类 Wine/Win32 兼容文件

### H. 可直接照抄的代码段索引（Phase 1 用）

| 我们要做的事 | 直接参考的 DXMT 文件 | 模式 |
|------------|-------------------|------|
| bridge.h 函数命名规范 | `winemetal.h:84,90,109,204,512,602,679` | `MTLClass_method` |
| WMTBufferInfo / WMTTextureInfo / WMTSamplerInfo struct 定义 | `winemetal.h:204-293`、`winemetal.h:263-293` | C 标准布局 struct，C# 端 LayoutKind.Sequential |
| Library 加载 + NSError 回传 | `winemetal.h:583`（`obj_handle_t *err_out`） | err 通过 out 参数返回，0 = success |
| Compute encoder API 集合 | `winemetal.h:608`、`winemetal_unix.c:785-868` | 一组 setPSO/setBuffer/setBytes/setTexture/dispatch |
| MTLFence 双缓冲 | `winemetal.h:1856`、`winemetal_unix.c:850-859` | updateFence/waitForFence per encoder |
| (Phase 6 性能) batched cmd struct + 链表回放 | `winemetal.h:876-1110`、`winemetal_unix.c:785-868` | `wmtcmd_base { type; next; }` + switch |

---

## 总体目录最终形态

```
MyGameRender/
├── AGENTS.md                              # 已存在
├── csharp-metal-rendering-blueprint.md    # 已存在
├── native/
│   ├── bridge.h
│   ├── bridge.m
│   └── sdl_metal_bridge.m                 # Phase 2 新增：SDL_Metal_GetLayer 封装
├── src/
│   ├── MetalRenderingEngine.Core/
│   │   ├── MetalRenderingEngine.Core.csproj
│   │   ├── Metal/
│   │   │   ├── Interop/{MetalBridge.cs, MetalTypes.cs, MetalEnums.cs}
│   │   │   ├── MetalObject.cs              # SafeHandle 基类
│   │   │   ├── MetalDevice.cs
│   │   │   ├── MetalLibrary.cs, MetalFunction.cs
│   │   │   ├── MetalBuffer.cs, MetalTexture.cs, MetalSampler.cs
│   │   │   ├── MetalCommandQueue.cs, MetalCommandBuffer.cs
│   │   │   ├── MetalComputeEncoder.cs, MetalRenderEncoder.cs
│   │   │   ├── MetalComputePipeline.cs, MetalRenderPipeline.cs
│   │   │   ├── MetalFence.cs
│   │   │   ├── MetalLayer.cs, MetalDrawable.cs   # Phase 2
│   │   │   └── MetalException.cs, MetalError.cs
│   │   ├── Graphics/                       # Phase 6：抽象层
│   │   │   └── IGraphicsDevice.cs / IBuffer.cs / ...
│   │   └── ShaderGen/                      # Phase 5：Roslyn 源生成器（独立 csproj）
│   ├── MetalRenderingEngine.Demo/
│   │   ├── MetalRenderingEngine.Demo.csproj
│   │   └── Program.cs (Phase 1) / TriangleApp.cs (P2) / TexturedApp.cs (P3) / ImGuiApp.cs (P4)
│   └── MetalRenderingEngine.Shaders/
│       ├── Compute/Mandelbrot.slang
│       └── Render/Triangle.vert.slang, Triangle.frag.slang
├── build/
│   ├── build_bridge.sh
│   ├── compile_shaders.sh
│   └── targets/MetalShaders.targets        # Phase 4 MSBuild 集成
├── out/
│   ├── libmetal_bridge.dylib
│   └── shaders/*.metallib
├── tests/MetalRenderingEngine.Tests/       # Phase 6 添加
└── MetalRenderingEngine.sln
```

---

## Phase 1 — 最小可行链路（1–2 天，可独立验证）

**目标：C# 在 M1 上调度一个 compute kernel 把 buffer 每元素 ×2，写回内存校验通过。**

### 任务

1. **bridge.h / bridge.m（仅 compute 子集）** — **采用 DXMT winemetal.h 风格命名 + `mtl_handle_t` (uint64) 句柄**
   - 类型定义：`typedef uintptr_t mtl_handle_t;` `#define MTL_NULL_HANDLE 0`
   - 引用计数：`NSObject_retain / NSObject_release`（CFRetain/CFRelease）
   - Device：`MTLCreateSystemDefaultDevice`、`MTLDevice_name`、`MTLDevice_hasUnifiedMemory`、`MTLDevice_recommendedMaxWorkingSetSize`
   - Library：`MTLDevice_newLibrary(device, data, size, err_out)` —— 错误通过 `mtl_handle_t *err_out` 回传（DXMT 模式，`winemetal.h:583`）
   - Function：`MTLLibrary_newFunctionWithName`
   - ComputePipeline：`MTLDevice_newComputePipelineState`、`MTLComputePipelineState_maxTotalThreadsPerThreadgroup`、`MTLComputePipelineState_threadExecutionWidth`
   - Buffer：`MTLDevice_newBuffer(device, WMTBufferInfo*)`、`MTLBuffer_contents`、`MTLBuffer_didModifyRange`
   - Queue/CmdBuf：`MTLDevice_newCommandQueue`、`MTLCommandQueue_commandBuffer`、`MTLCommandBuffer_commit / waitUntilCompleted / status / error`
   - ComputeEncoder：`MTLCommandBuffer_computeCommandEncoder` + `MTLComputeCommandEncoder_setComputePipelineState / setBuffer / setBytes / setTexture / dispatchThreadgroups / endEncoding`
   - NSError：`NSError_localizedDescription`（返回 `const char*`，调用方拷走，bridge.m 维护一个 thread-local autoreleased NSString）
   - **每个函数 ≤ 20 行**（AGENTS.md §4.2）；用 `__bridge_retained` 转移所有权
   - 结构体 `WMTBufferInfo` 严格对齐 `winemetal.h:204-...` 的字段顺序
2. **build/build_bridge.sh**：`clang -dynamiclib -framework Metal -framework Foundation native/bridge.m -o out/libmetal_bridge.dylib`
3. **`MetalRenderingEngine.sln` + Core + Demo csproj**（.NET 10，`<AllowUnsafeBlocks>true`）
   - csproj 中以 `<Content Include="../../out/libmetal_bridge.dylib" CopyToOutputDirectory="PreserveNewest"/>` 把 dylib 复制到 bin
4. **`Metal/Interop/MetalBridge.cs`**：所有 `[DllImport("libmetal_bridge")]` 集中声明（AGENTS.md §4.1）
5. **`Metal/Interop/MetalTypes.cs`**：`MetalBufferInfo`（`[StructLayout(LayoutKind.Sequential)]`）
6. **`Metal/Interop/MetalEnums.cs`**：`MTLResourceOptions`、`MTLCommandBufferStatus`
7. **`Metal/MetalObject.cs`**：`abstract class MetalObject : SafeHandle`，统一封装 `Retain/ReleaseHandle`
8. **SafeHandle 子类**：`MetalDevice / MetalLibrary / MetalFunction / MetalComputePipelineState / MetalBuffer / MetalCommandQueue / MetalCommandBuffer / MetalComputeEncoder`
9. **`Metal/MetalException.cs` + `MetalError.cs`**：从 `NSError*` 句柄拉描述字符串
10. **`src/MetalRenderingEngine.Shaders/Compute/Multiply.slang`**：
    ```hlsl
    RWStructuredBuffer<float> Data : register(u0);
    [numthreads(64,1,1)]
    void main(uint3 id : SV_DispatchThreadID) { Data[id.x] *= 2.0; }
    ```
11. **`build/compile_shaders.sh`**：遍历 `src/**/Shaders/**/*.slang` → `slangc -target dxil -profile sm_6_0` → `metal-shaderconverter` → `out/shaders/*.metallib`
12. **`Demo/Program.cs`**：加载 `Multiply.metallib` → 创建 1024 元素 buffer 填 1..1024 → dispatch → `WaitUntilCompleted` → 断言每个元素都翻倍

### 验证

```bash
./build/build_bridge.sh
./build/compile_shaders.sh
dotnet run --project src/MetalRenderingEngine.Demo
# 期望输出：✅ All 1024 elements doubled correctly. Device: Apple M1
```

### 关键参考（已写好的代码片段）

- bridge.m 核心实现：blueprint §4.3（行 349–470）已给出可直接照抄的模式
- C# SafeHandle 模式：blueprint §5.1（行 491–564）
- compute 调度：blueprint §5.3（行 602–652）

---

## Phase 2 — 渲染管线 + SDL3 窗口（2–3 天）

**目标：SDL3 窗口里出现 Metal 渲染的彩色三角形。**

### 任务

1. **bridge.m 扩展**
   - RenderPipeline：`MetalColorAttachment / MetalRenderPipelineDesc`（blueprint §4.2 行 215–233）+ `metal_new_render_pipeline_state`
   - RenderEncoder：`metal_render_command_encoder` + `set_pipeline/set_vertex_buffer/set_viewport/set_scissor/draw_primitives/draw_indexed_primitives/end_encoding`
   - RenderPass：`MetalRenderPassAttachment / MetalRenderPassDesc`（blueprint §4.2 行 300–313）
   - CAMetalLayer：`metal_layer_set_device/set_pixel_format/set_drawable_size`、`metal_layer_next_drawable`、`metal_drawable_texture`、`metal_command_buffer_present_drawable`
2. **新增 `native/sdl_metal_bridge.m`**（一个文件、3 个函数，仍属于 bridge 范畴）
   - `bridge_sdl_get_metal_layer(SDL_Window*)` → 调 `SDL_Metal_CreateView` + `SDL_Metal_GetLayer` 返回 `void*`
   - 把 libSDL3 链接进 dylib：`clang -dynamiclib ... -lSDL3 native/bridge.m native/sdl_metal_bridge.m`
3. **C# 侧 SDL3 绑定**：用 `Silk.NET.SDL` 或最小手写 P/Invoke（仅 init/createWindow/pollEvent/quit ~10 个函数）—— **新增 NuGet 必须在 AGENTS.md §7.3 登记**（推荐手写以零依赖）
4. **C# 新 SafeHandle**：`MetalRenderPipelineState / MetalRenderEncoder / MetalLayer / MetalDrawable`
5. **`MetalRenderPipelineDescriptor`**（C# 构造器对应 C 结构体）
6. **Slang 着色器**：`Triangle.vert.slang`（`[[vertex]]`，硬编码 3 个顶点）+ `Triangle.frag.slang`（`[[fragment]]`，按重心色）
7. **`Demo/TriangleApp.cs`**：SDL 初始化 → 创建 Metal 窗口 → 主循环（poll event / next drawable / render pass clear+draw / present / commit）

### 验证

`dotnet run --project src/MetalRenderingEngine.Demo -- triangle` → 弹窗显示 RGB 三角形，关闭窗口正常退出，无 leak。

---

## Phase 3 — 资源系统（2–3 天）

**目标：贴图三角形 + CPU 每帧动态更新 uniform；Fence 双缓冲。**

### 任务

1. **bridge.m 扩展**
   - Texture：`MetalTextureInfo`（blueprint §4.2 行 246–257）+ `metal_new_texture / replace_region`
   - Sampler：`MetalSamplerInfo`（行 264–276）+ `metal_new_sampler`
   - RenderEncoder：`set_fragment_buffer / set_fragment_texture / set_fragment_sampler`
   - Fence：`metal_new_fence / encoder_wait_for_fence / encoder_update_fence`
2. **C# SafeHandle**：`MetalTexture / MetalSampler / MetalFence` + descriptor 类
3. **`MTLPixelFormat`、`MTLTextureUsage`、`MTLStorageMode`、`MTLSamplerMinMagFilter`、`MTLSamplerAddressMode`** 枚举（blueprint §5.2 行 580–588）
4. **`Demo/TexturedApp.cs`**
   - 加载 PNG（用 `System.Drawing.Common` 或 `StbImageSharp` 或手写解码 PPM 用于零依赖）→ `MetalTexture.ReplaceRegion`
   - 每帧通过 `SetBytes` 传 MVP 矩阵 + 时间
   - Triple-buffer：3 个 `MetalBuffer` + `MetalFence` 循环避免 CPU 覆写 GPU 正在读的内存
5. **shader 升级**：`Triangle.vert.slang` 接收 MVP；`Triangle.frag.slang` 采样纹理

### 验证

窗口里 3D 旋转的纹理四边形，1000 帧不掉帧不崩，`leaks` 检查 Metal 对象计数稳定。

---

## Phase 3.5 — ImGui 调试 UI 接入（1–2 天，可选但强烈建议）

**目标：ImGui 浮窗显示 FPS / GPU 时间 / 资源计数；完全走我们自己的 render encoder。**

- 引入 `ImGui.NET` NuGet（→ 登记到 AGENTS.md §7.3）
- C# 端：`ImGui.NewFrame / Render` 拿到 `ImDrawData` → 把 vtx/idx 写入两个 `MetalBuffer` → 用我们的 vertex+fragment shader（带 texture + scissor）绘制
- font atlas 上传到一个 `MetalTexture`
- 不引入 `imgui_impl_metal.mm`（违反 AGENTS.md §5.1）
- 这一步同时是 Phase 1-3 渲染层"是否够用"的天然集成测试

---

## Phase 4 — 着色器工具链 MSBuild 集成（2–3 天）

**目标：开发者写完 `.slang` 直接 `dotnet build`，IDE 报错指向源行。**

1. **`build/targets/MetalShaders.targets`**
   - `<Target Name="CompileMetalShaders" BeforeTargets="BeforeBuild">`：枚举 `@(MetalShader)`，调 `slangc` + `metal-shaderconverter`
   - 输出到 `$(IntermediateOutputPath)shaders/`，自动 `<EmbeddedResource>` 或 `<Content CopyToOutput>`
   - 实现 incremental build：用 `Inputs/Outputs` 让 MSBuild 只编译变更文件
2. **Core.csproj 导入 targets**，约定 `*.slang` 默认 build action = `MetalShader`
3. **错误重映射**：把 `slangc` stderr 解析为 MSBuild `error` 格式（`file(line,col): error CODE: message`）让 VS Code/Rider 点击跳转
4. **`MetalShaderLoader`** 工具类：从程序集嵌入资源加载 `.metallib`，按 entry name 缓存 `MetalFunction`

### 验证

`dotnet build` 自动产出所有 .metallib；改一个 .slang 重新 build 只重编那一个；语法错故意写错 → IDE 中点击错误跳到源代码对应行。

---

## Phase 5 — C# 源生成器：C# struct → Slang（2–3 周）

**目标：用户写 `IComputeShader` C# struct，build 自动生成 Slang 并走 Phase 4 管线。**

1. **新 csproj `MetalRenderingEngine.ShaderGen`**（`<TargetFramework>netstandard2.0</TargetFramework>`，Roslyn 源生成器要求）
2. **核心接口**（在 Core 中定义）
   - `IComputeShader`：`void Execute(ThreadId id)`
   - `IVertexShader<TIn,TOut>` / `IFragmentShader<TIn,TOut>`
   - `[Shader]`、`[ThreadGroupSize(x,y,z)]` 特性
   - 资源类型：`ReadOnlyBuffer<T> / ReadWriteBuffer<T> / Texture2D<T> / SamplerState / ConstantBuffer<T>`
3. **`IIncrementalGenerator` 实现**（参考 ComputeSharp 的 `HlslKnownTypes` / `HlslKnownMethods`）
   - 扫描带 `[Shader]` 的 `partial struct`
   - 把字段映射成 Slang resource binding（`register(u0)` / `register(b0)` 顺序自动分配）
   - 把方法体翻译为 Slang/HLSL：表达式树、操作符、内建函数（`float3 / dot / cross / mul`...）映射表
   - 生成 `.slang` 写到 `obj/Generated/Shaders/`，并通过 `AdditionalFiles` 反馈给 Phase 4 的 MSBuild target
4. **C# 类型映射表**：`float2/3/4` ↔ Slang `float2/3/4`，`int2/3/4`，`Matrix4x4` → `float4x4`
5. **不支持的 C# 特性显式报错**：`async / throw / try / class / virtual / reflection` → diagnostic `MSGEN001..N`
6. **回归测试**：把 Phase 1 的 `Multiply.slang` 替换为 C# struct 版，结果一致

### 验证

写一个 `MandelbrotShader : IComputeShader`（blueprint §3.2 行 124–140）→ build → 产物 `.metallib` 与手写 Slang 版字节级别一致或 GPU 输出一致。

---

## Phase 6 — 引擎抽象层 + 性能优化（持续迭代，首版 1 周）

**目标：为未来 Vulkan/DX12 后端预留抽象，且不损失 Metal 后端性能。**

1. **`Graphics/` 接口层**
   - `IGraphicsDevice / IBuffer / ITexture / ISampler / IShader / IPipelineState / ICommandQueue / ICommandList / IRenderPass`
   - 接口设计参考 wgpu / Sokol GFX（命令式 + 不可变 pipeline state）
2. **Metal 后端**：`MetalGraphicsDevice : IGraphicsDevice`，把 Phase 1-3 的 `MetalXxx` 封装成接口实现
3. **资源池**：`BufferPool` / `TexturePool` / `TransientBufferAllocator`（每帧重置的 ring buffer，参考 `dxmt_command_queue.hpp:184-189` 的 `RingBumpState` 设计）
4. **批量命令编码器（P/Invoke 削减）** —— **DXMT 已验证的关键优化**
   - bridge.h 新增 `wmtcmd_base { uint16_t type; uint16_t reserved[3]; mtl_handle_t next_ptr; }` + 一组 `wmtcmd_compute_*` / `wmtcmd_render_*` POD struct，原样对应 `references/dxmt/src/winemetal/winemetal.h:876-1110`
   - bridge.m 新增 `MTLComputeCommandEncoder_encodeCommands(encoder, wmtcmd_base*)` 和对应 render/blit 版本，照 `winemetal_unix.c:785-868` 的 switch 模板实现
   - C# 端：`MetalCommandList` 内部维护一个 `MemoryPool` + `Span<byte>` ring buffer，每次 `Draw/Dispatch/SetBuffer` 调用 placement-write 一个 cmd struct，链表挂上；提交时一次 P/Invoke 回放整段
   - **量级目标：** 1000 draw call/帧从 ~1000 次 P/Invoke 降到 1 次，预期帧时间下降 0.5-2ms（M1 上 P/Invoke ~1-2μs）
5. **MTLSharedEvent + CPU fence**（替代 Phase 3 的简单 MTLFence）
   - 参考 `references/dxmt/src/dxmt/dxmt_command_queue.cpp:31-72` 的 `SharedEventListener` + 后台线程模式
   - 简化掉 Wine `unixcall` 包装，直接用 dispatch queue + completion handler
6. **测试拆出**：`tests/MetalRenderingEngine.Tests/`（xUnit），M1-only：device 生命周期、buffer 引用计数（用 `Instruments leaks` 自动化）、compute 数值正确性、render 渲染像素 hash 比对
7. **CI**：GitHub Actions self-hosted runner（M1）跑 `dotnet test` + `clang` 编译 bridge
8. **性能 baseline**：单帧 1000 draw call、单帧 100 万顶点的吞吐量基准（DXMT 在 Wine D3D11 游戏中能跑可玩帧率，是我们的下限参照），写入 `docs/perf.md`

---

## 关键修改/新增文件一览（仅列代表性路径）

| 阶段 | 关键文件 |
|------|---------|
| P1 | `native/bridge.{h,m}`、`build/build_bridge.sh`、`build/compile_shaders.sh`、`src/MetalRenderingEngine.Core/Metal/{Interop/MetalBridge.cs, MetalObject.cs, MetalDevice.cs, MetalBuffer.cs, MetalLibrary.cs, MetalComputePipeline.cs, MetalCommandQueue.cs, MetalCommandBuffer.cs, MetalComputeEncoder.cs, MetalException.cs}`、`src/MetalRenderingEngine.Shaders/Compute/Multiply.slang`、`src/MetalRenderingEngine.Demo/Program.cs`、`MetalRenderingEngine.sln` |
| P2 | `native/sdl_metal_bridge.m`、bridge.{h,m} 扩展 render/layer 段、`Metal/Metal{RenderPipeline,RenderEncoder,Layer,Drawable}.cs`、`Demo/TriangleApp.cs`、`Shaders/Render/Triangle.{vert,frag}.slang` |
| P3 | bridge.{h,m} 扩展 texture/sampler/fence、`Metal/Metal{Texture,Sampler,Fence}.cs`、`Demo/TexturedApp.cs` |
| P3.5 | `Demo/ImGuiApp.cs`、`Metal/UI/ImGuiMetalRenderer.cs`、ImGui font shader |
| P4 | `build/targets/MetalShaders.targets`、`MetalShaderLoader.cs`、Core.csproj 导入 targets |
| P5 | `src/MetalRenderingEngine.ShaderGen/` 整个新项目、Core 中的 `IComputeShader` / `[Shader]` / `ReadWriteBuffer<T>` 等公开接口 |
| P6 | `src/MetalRenderingEngine.Core/Graphics/I*.cs`、`tests/MetalRenderingEngine.Tests/`、`.github/workflows/ci.yml`、`docs/perf.md` |

---

## 复用与遵循

- **DXMT 参考映射**：见上方"DXMT 参考映射"章节。Phase 1 的 bridge.h 命名与 struct 布局直接对照 `references/dxmt/src/winemetal/winemetal.h`；Phase 3 fence、Phase 6 batched encoder 也都有具体 file:line。
- **不照搬蓝图原文 API 命名**：蓝图 §4.2/§4.3 用的是 `metal_xxx_yyy` snake_case，与 DXMT 的 `MTLXxx_yyy` 不一致。**最终采用 DXMT 风格**（更对应 Apple ObjC 接口、grep 友好）；蓝图代码段作为实现逻辑模板使用，命名做替换。
- **C# SafeHandle 模板**：blueprint 行 491-564 给的代码可直接照抄，仅把 `metal_xxx` 改成 `MTLXxx_yyy`。
- **AGENTS.md §4.1 命名约定**：`MetalXxx` 类必须 `public sealed`，`DllImport` 必须 `internal static extern` 且集中在 `MetalBridge.cs`；注释中文；公开 API 加 XML doc。
- **AGENTS.md §5.1 红线**：不引入 metal-cpp / MoltenVK / Veldrid / Metal.NET / SharpMetal / Silk.NET.Metal；不直接 P/Invoke `objc_msgSend`；bridge.m 之外不写 ObjC（含 ImGui 后端、含 SDL Metal view 创建——SDL3 自带 ObjC 实现在 libSDL3 内部）。
- **每新增 NuGet 依赖必须更新 AGENTS.md §7.3**：本方案唯一新增 `ImGui.NET`（P3.5），如选择手写 SDL3 P/Invoke 则零额外 NuGet。

---

## 端到端验证清单

| 阶段 | 命令 | 期望 |
|------|------|------|
| P1 | `./build/build_bridge.sh && ./build/compile_shaders.sh && dotnet run --project src/MetalRenderingEngine.Demo` | 控制台打印 `✅ All 1024 elements doubled correctly. Device: Apple M1` |
| P2 | `dotnet run -- triangle` | SDL3 窗口出现彩色三角形，关闭无 crash |
| P3 | `dotnet run -- textured` | 旋转的贴图四边形，连续运行 1 分钟 60fps，`leaks <pid>` 无 Metal 对象泄漏 |
| P3.5 | `dotnet run -- imgui` | ImGui 调试浮窗显示 FPS/帧时间/资源计数，可交互 |
| P4 | 修改 `.slang` 后 `dotnet build` | 仅重编该文件；故意改错语法 → IDE 错误可点击跳转到 .slang 源行 |
| P5 | `dotnet build` Mandelbrot C# 版 | 自动产出 .metallib；运行结果与手写 Slang 版逐像素相同 |
| P6 | `dotnet test` | 所有单元 + 集成测试通过，CI 在 M1 self-hosted runner 上绿灯 |

---

## 风险与缓解

| 风险 | 缓解 |
|------|------|
| SDL3 在某些 macOS 版本上 `SDL_Metal_GetLayer` 返回 nil | P2 添加回退：手写极简 NSWindow+CAMetalLayer 创建函数到 bridge.m（≤30 行） |
| `metal-shaderconverter` 对某些 DXIL 特性不支持（如 wave intrinsics） | P1 起就让 `compile_shaders.sh` 捕获 MSC stderr 并输出到日志；建立"已知不兼容特性"清单；参考 `references/dxmt/src/airconv/airconv_public.h:21-44` 看 DXMT 是怎么处理 binding 重映射的 |
| MSC 输出后 buffer index 与 Slang `register(b0)` 不一致 | Phase 4 工具链集成时读 MSC reflection；如果 MSC 不输出 reflection，从 `.metallib` 用 `MTLLibrary functionNames` + `MTLFunction stageInputAttributes` 反查 |
| Roslyn 源生成器复杂度爆炸（P5） | 先只支持 compute + 标量/向量算术 + buffer 读写；vertex/fragment + texture 采样作为子里程碑 |
| 8GB RAM 在大型项目中编译卡顿 | Core / ShaderGen / Demo 分项目让 IDE 增量编译；`<EnableSourceGenerator>false</EnableSourceGenerator>` 在 IDE 调试时可临时关 |
| ImGui.NET 与我们 unsafe pipeline 的版本兼容 | 锁定 ImGui.NET 1.91.x；font atlas 上传走我们的 `MetalTexture.ReplaceRegion` |
| 单次 P/Invoke 在大规模场景下成本累积 | Phase 6 batched encoder 已规划，DXMT 已验证模式（`winemetal.h:876-1110` + `winemetal_unix.c:785-868`） |

---

## 同步更新文档

实施前/中需要更新现有文档：

1. **`.gitignore`**：添加 `references/`（用户已做）、`out/`、`bin/`、`obj/`、`*.metallib`（构建产物）
2. **`AGENTS.md` §2 项目结构表**：把 `bridge.m` 之外的 `sdl_metal_bridge.m`（Phase 2）登记；把 `references/dxmt/` 标注为"只读参考，不构建"
3. **`AGENTS.md` §7.3 NuGet 依赖**：Phase 3.5 加 `ImGui.NET`；如选择不手写 SDL3 P/Invoke 也登记 `Silk.NET.SDL`
4. **`AGENTS.md` §5.2 需要审批**：明确"参考 DXMT 代码逻辑可以，复制 DXMT 源文件到我们 src/ 需要审批"
5. **`csharp-metal-rendering-blueprint.md` §4.2/§4.3**：API 命名从 `metal_xxx_yyy` 改为 DXMT 风格 `MTLXxx_yyy`（或在蓝图新增一节"实施细化：采用 DXMT 命名风格"，保留原文作为设计史）
