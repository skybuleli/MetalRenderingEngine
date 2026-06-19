# AGENTS.md — C# → Metal 原生渲染引擎项目

> 本文件为 AI Agent 提供项目级别的约束与指导。  
> 所有 Agent 在开始任何工作前必须先完整阅读本文件。

---

## 1. 项目总纲

### 1.1 项目目标

构建一套以 **C# 为主要生产力语言** 的渲染引擎，最终在 **Apple Silicon (M1+) 上通过 Metal 原生渲染**。用户用 C# 编写游戏逻辑和着色器，运行时直接驱动 Metal GPU。

### 1.2 核心原则

| 原则 | 说明 |
|------|------|
| **C# 优先** | 所有业务逻辑、引擎核心、资源管理必须在 C# 中完成 |
| **C/ObjC 仅用于桥接** | C/ObjC 代码仅限于 `bridge.m`，将 Metal.framework 的 ObjC API 暴露为 C ABI |
| **零第三方 Metal 绑定** | 禁止引入 Veldrid、Metal.NET (qian-o)、SharpMetal、Silk.NET Metal 等任何 Metal 绑定库 |
| **着色器管线锁定** | 着色器编译唯一路径：Slang → DXIL → metal-shaderconverter → .metallib（Path A） |
| **DXMT 是参考，不是依赖** | 借鉴 DXMT 的 handle-based 架构模式，但绝不引入 DXMT 代码或依赖 |
| **macOS / Apple Silicon 唯一目标** | 不考虑 Windows/Linux 后端（未来可通过抽象接口扩展，但不作为当前目标） |

### 1.3 技术约束

| 项 | 锁定值 |
|----|--------|
| .NET 版本 | 10.0+ |
| 着色器语言 | Slang（HLSL 超集），通过 C# 源生成器（`[Shader]` partial struct）生成 |
| 着色器编译路径 | `[Shader]` C# struct →（源生成器）→ Slang 源码/ Binding 类 → `slangc -target dxil -profile sm_6_0` → `metal-shaderconverter` → `.metallib` |
| Metal 接口方式 | 自定义 `bridge.m`（C ABI）→ C# P/Invoke → SafeHandle |
| 开发平台 | macOS 26.4.1，Apple Silicon (M1, 8GB) |
| Xcode | 仅 Command Line Tools（无完整 Xcode.app） |
| 内存模型 | M1 统一内存（UMA），首选 `StorageModeShared` |

---

## 2. 项目结构

```
MetalRenderingEngine/
├── AGENTS.md                         # ← 本文件
├── BLUEPRINT.md                      # 技术方案蓝图入口（指向实际蓝图文档，必读）
├── native/
│   ├── bridge.h                      # Metal 桥接层 C ABI 头文件
│   └── bridge.m                      # Metal 桥接层 ObjC 实现
├── src/
│   ├── MetalRenderingEngine.Core/    # 引擎核心库
│   │   ├── Metal/
│   │   │   ├── Interop/
│   │   │   │   ├── MetalBridge.cs    # 所有 DllImport 声明
│   │   │   │   └── MetalTypes.cs     # C 结构体 C# 对应定义
│   │   │   ├── MetalDevice.cs
│   │   │   ├── MetalLibrary.cs
│   │   │   ├── MetalFunction.cs
│   │   │   ├── MetalBuffer.cs
│   │   │   ├── MetalTexture.cs
│   │   │   ├── MetalSampler.cs
│   │   │   ├── MetalFence.cs
│   │   │   ├── MetalCommandQueue.cs
│   │   │   ├── MetalCommandBuffer.cs
│   │   │   ├── MetalComputeEncoder.cs
│   │   │   ├── MetalRenderEncoder.cs
│   │   │   ├── MetalRenderPipeline.cs
│   │   │   └── MetalComputePipeline.cs
│   │   └── Graphics/                 # 引擎抽象层（为未来多后端预留）
│   │       ├── IGraphicsDevice.cs
│   │       ├── IBuffer.cs
│   │       ├── ITexture.cs
│   │       ├── ICommandQueue.cs
│   │       └── IPipelineState.cs
│   ├── MetalRenderingEngine.Shaders/ # 着色器源代码
│   │   ├── Compute/
│   │   └── Render/
│   ├── MetalRenderingEngine.Demo/    # 示例/测试项目
│   │   ├── Program.cs
│   │   ├── ImGuiMetalRenderer.cs     # Phase 3.5: ImGui Metal 渲染器
│   │   ├── ImGuiApp.cs               # Phase 3.5: ImGui 调试 UI Demo
│   │   └── Shaders/                  # Phase 5: C# Shader struct 定义（源生成器输入）
│   │       ├── MultiplyShader.cs
│   │       ├── TriangleShader.cs
│   │       └── MandelbrotShader.cs
│   └── MetalRenderingEngine.ShaderGen/  # Phase 5: C# 源生成器
│       ├── ShaderGenerator.cs           # IIncrementalGenerator 入口
│       ├── Models/ShaderModel.cs        # Shader 信息模型
│       ├── Translation/
│       │   ├── TypeMapper.cs            # C#→Slang 类型映射
│       │   ├── ExpressionTranslator.cs  # 表达式翻译
│       │   └── StatementTranslator.cs   # 语句翻译
│       └── Emit/
│           ├── SlangEmitter.cs          # Slang 着色器代码生成
│           └── BindingClassEmitter.cs   # C# Binding 类生成
├── build/
│   ├── compile_shaders.sh            # 着色器编译脚本
│   ├── compile_generated_shaders.sh  # Phase 5: 从源生成器输出编译着色器
│   ├── targets/
│   │   └── MetalShaders.targets      # Phase 5: MSBuild 后处理目标
│   └── build_bridge.sh              # bridge.m 编译脚本
├── tests/
│   └── MetalRenderingEngine.Tests/   # 单元测试
└── MetalRenderingEngine.sln
```

---

## 3. 架构约束

### 3.1 Metal 调用路径（唯一允许的方式）

```
C# SafeHandle 子类
    ↓ P/Invoke [DllImport("libmetal_bridge")]
bridge.m 中的 C 函数
    ↓ [(id<MTLXXX>)handle method]
Metal.framework
```

**禁止：** 直接从 C# 调用 `objc_msgSend`、引入 metal-cpp、使用任何第三方 Metal 封装库。

### 3.2 资源生命周期

- 所有 Metal 对象必须通过 `MetalObject`（SafeHandle 子类）管理
- 使用 `using` 语句确保确定性释放
- 共享引用场景使用 `Retain()` / `Dispose()` 手动管理
- **禁止** 直接使用 `IntPtr` 持有 Metal 对象（必须封装在 SafeHandle 中）

```csharp
// ✅ 正确
using var buffer = device.NewBuffer(1024, MTLResourceOptions.StorageModeShared);

// ❌ 错误
IntPtr bufferPtr = metal_new_buffer(device.Handle, ...);
// 手动调 metal_release(bufferPtr); // 容易忘记
```

### 3.3 着色器编译流程（不可绕过）

```
.slang 源文件
    ↓ slangc -target dxil -entry <entry> -stage <stage> -profile sm_6_0
.dxil
    ↓ metal-shaderconverter
.metallib
    ↓ 嵌入资源或复制到输出目录
C# 运行时通过 metal_new_library_from_data 加载
```

**禁止：** 运行时编译着色器（`newLibraryWithSource:`）、跳过 MSC 步骤、直接嵌入 DXIL。

> **Phase 9 例外：** `SpirvCrossCompiler` 外部着色器路径允许运行时 `newLibraryWithSource:`
> （SPIR-V → spirv-cross → MSL 源 → Metal 编译）。引擎自有 Slang 着色器仍必须预编译为 `.metallib`。
> 此例外仅适用于 `Phase 9E` 的 SpirvCrossCompiler，其他代码路径仍禁止运行时 MSL 编译。

### 3.4 错误处理

```csharp
// 桥接层函数涉及 NSError 时必须检查
public MetalLibrary NewLibrary(byte[] data)
{
    unsafe
    {
        IntPtr errorPtr = IntPtr.Zero;
        fixed (byte* p = data)
        {
            IntPtr lib = metal_new_library_from_data(handle, p, (nuint)data.Length, &errorPtr);
            if (lib == IntPtr.Zero)
            {
                var error = new MetalError(errorPtr);
                throw new MetalException($"Failed to create library: {error.Description}");
            }
            return new MetalLibrary(lib);
        }
    }
}
```

---

## 4. 代码规范

### 4.1 C# 规范

- **命名空间：** `MetalRenderingEngine.Metal` 用于 Metal 直接绑定，`MetalRenderingEngine.Graphics` 用于抽象层
- **类型命名：** Metal 对象以 `Metal` 前缀（`MetalDevice`、`MetalBuffer`），抽象接口以 `I` 前缀（`IGraphicsDevice`）
- **P/Invoke 声明：** 全部集中在 `MetalBridge.cs` 中，使用 `internal static extern` 
- **注释语言：** 中文
- **访问修饰符：** Metal 绑定类为 `public sealed class`，DllImport 方法为 `private/internal static extern`
- **unsafe 代码：** 仅在需要直接操作内存（buffer contents、结构体指针）时使用

### 4.2 C/ObjC 规范

- **bridge.m 中的函数全部以 `metal_` 为前缀**
- **每个函数不超过 20 行**（仅做类型转换和 ObjC 调用）
- **使用 `__bridge_retained` 转移所有权，`CFRetain`/`CFRelease` 管理引用计数**
- **结构体使用 C 标准布局，在 bridge.h 中定义**

### 4.3 着色器规范

- **着色器使用 Slang 语法编写（HLSL 超集）**
- **入口函数统一命名为 `main`**
- **文件名后缀：** 顶点着色器 `.vert.slang`，片元着色器 `.frag.slang`，计算着色器 `.slang`
- **常量缓冲区使用 `ConstantBuffer<T>` + `[[buffer(n)]]`**

### 4.4 注释与文档

- 所有注释使用 **中文**
- 公开 API 必须有三斜线 XML 文档注释
- bridge.m 中每个函数上方注释说明对应的 Metal ObjC 方法
- 架构决策记录在 `BLUEPRINT.md` / `csharp-metal-rendering-blueprint.md` 中，不在代码中重复

---

## 5. 禁止事项

### 5.1 绝对禁止

- ❌ 引入任何第三方 Metal 绑定 NuGet 包（Veldrid、Metal.NET、SharpMetal、Silk.NET.Metal 等）
- ❌ 在 C# 中直接使用 `objc_msgSend` 或 DllImport `libobjc.A.dylib`
- ❌ 在 bridge.m 之外写 ObjC 代码
- ❌ 使用 `metal-cpp` 或任何 Apple C++ Metal 封装
- ❌ 引入 MoltenVK
- ❌ 在 bridge.m 中引入复杂逻辑（超过 20 行的函数需要评审）
- ❌ 依赖仅存在于完整 Xcode 中的工具（如 `xcrun metal`）
- ❌ 将着色器编译为 MSL 并运行时编译（必须预编译为 .metallib）
  > **Phase 9 例外：** SpirvCrossCompiler 外部着色器路径允许 `newLibraryWithSource:`，
  > 仅用于 SPIR-V → MSL 运行时编译。引擎自有 Slang 着色器仍必须预编译。

### 5.2 需要审批

- ⚠️ 引入任何新的 NuGet 依赖
- ⚠️ 修改 bridge.h 的 API
- ⚠️ 改变着色器编译路径
- ⚠️ 添加超过 1 个 unsafe class
- ⚠️ 在 bridge.m 外添加 C/C++/ObjC 源文件

---

## 6. 构建与调试

### 6.1 编译 bridge.m

```bash
cd native
clang -dynamiclib \
  -o ../out/libmetal_bridge.dylib \
  -framework Metal -framework Foundation \
  bridge.m
```

编译产物 `libmetal_bridge.dylib` 必须放在 C# 项目的输出目录或通过 `DLLIMPORT_RESOLVER` 可发现的位置。

### 6.2 编译着色器

```bash
# 每个 .slang 文件：
slangc <file> -target dxil -entry main -stage <compute|vertex|fragment> \
  -profile sm_6_0 -o <out>.dxil

metal-shaderconverter <out>.dxil -o <out>.metallib
```

### 6.3 构建项目

```bash
# 先编译 bridge
./build/build_bridge.sh

# 编译手动编写的 .slang 着色器
./build/compile_shaders.sh

# 构建 .NET 项目（包含源生成器 + MSBuild 后处理编译生成的着色器）
dotnet build MetalRenderingEngine.sln
```

构建流程说明：
1. 编译 C# → Slang 源生成器 `MetalRenderingEngine.ShaderGen`
2. Roslyn `IIncrementalGenerator` 扫描所有标记 `[Shader]` 的 partial struct
3. 生成 Slang 源码（const string 嵌入 C# Binding 类）和类型安全的资源绑定类
4. `MetalShaders.targets` 在 CoreCompile 后自动调用 `compile_generated_shaders.sh`
5. 脚本从生成的 Binding 类中提取 Slang 源码，编译为 `.metallib`

### 6.4 调试

- Metal 验证层：编辑 Scheme → Run → Options → GPU Frame Capture → Metal → Enable Validation
- 着色器调试：检查 slangc 输出、MSC 转换日志
- Crash 排查：检查 NSError 返回值、确认 Bridge 保持引用计数正确

---

## 7. 依赖清单

### 7.1 运行时依赖

| 依赖 | 来源 | 说明 |
|------|------|------|
| .NET 10.0+ | dotnet SDK | C# 运行时 |
| Metal.framework | macOS 系统 | GPU API（`/System/Library/Frameworks/Metal.framework`） |
| libobjc.A.dylib | macOS 系统 | ObjC 运行时（`/usr/lib/libobjc.A.dylib`） |
| libmetal_bridge.dylib | 本项目编译 | Metal 桥接层 |

### 7.2 构建时依赖

| 依赖 | 来源 | 说明 |
|------|------|------|
| slangc | PATH（用户已安装） | Slang 着色器编译器 |
| metal-shaderconverter | `/usr/local/bin/metal-shaderconverter` | DXIL → metallib |
| clang | `/usr/bin/clang` | 编译 bridge.m |

### 7.3 允许的 NuGet 依赖

- `Microsoft.CodeAnalysis.CSharp` 4.12.0 — ShaderGen 源生成器（Roslyn 分析框架）
- `Microsoft.CodeAnalysis.Analyzers` 3.3.4 — ShaderGen 分析器开发包
- `System.Numerics.Vectors` — SIMD 数学类型
- `System.Runtime.CompilerServices.Unsafe` — 指针操作辅助
- `ImGui.NET` 1.91.6.1 — 调试 UI（Phase 3.5，仅 Demo 项目）
- 用户界面框架（如 Avalonia）— 仅用于 Demo 项目

**任何新增 NuGet 依赖必须在 AGENTS.md 的此节中记录。**

---

## 8. 工作流

### 8.1 Agent 启动流程

每次新 Agent 启动时：

1. 读取本 `AGENTS.md`
2. 读取 `BLUEPRINT.md` 了解技术方案（仓库内入口文件会指向实际蓝图 `csharp-metal-rendering-blueprint.md`）
3. 检查工具链可用性：`slangc -h`、`metal-shaderconverter --help`、`clang --version`
4. 确认 `libmetal_bridge.dylib` 是最新的（如需要则重新编译）
5. 确认着色器 `.metallib` 是最新的（如需要则重新编译）
6. 运行 `dotnet test` 确认测试通过
7. 开始任务

### 8.2 添加新 Metal API

当需要当前 bridge.m 未暴露的 Metal 函数时：

1. 在 `bridge.h` 中添加函数声明
2. 在 `bridge.m` 中实现（参考已有模式）
3. 在 `MetalBridge.cs` 中添加 DllImport
4. 在对应的 SafeHandle 类中添加公开方法
5. 重新编译 bridge.m 和 C# 项目
6. 写单元测试验证

### 8.3 添加新的 Slang 着色器（传统方式）

1. 在 `src/MetalRenderingEngine.Shaders/` 下创建 `.slang` 文件
2. 运行 `build/compile_shaders.sh`
3. 将生成的 `.metallib` 添加到 C# 项目（嵌入资源或复制到输出）

### 8.4 添加新的 C# Shader（源生成器方式）

1. 在 Demo 项目的 `Shaders/` 目录下创建 `partial struct` 实现 `IComputeShader` / `IVertexShader` / `IFragmentShader`
2. 标记为 `[Shader]`
3. 源生成器自动生成 Slang 代码和 Binding 类
4. `dotnet build` 自动编译为 `.metallib`

---

## 9. 测试要求

### 9.1 单元测试

- `MetalDevice` 生命周期测试
- `MetalBuffer` 创建/读写/释放测试
- `MetalLibrary` 加载 `.metallib` 测试
- `MetalComputePipeline` 创建和调度测试
- Bridge 引用计数泄漏测试
- ShaderGen 类型映射测试
- ShaderGen 表达式翻译测试（算术、逻辑、成员访问、数组、函数调用）
- ShaderGen 语句翻译测试（for/while/if-else/break/return）
- ShaderGen 完整着色器生成验证（Multiply、Triangle、Mandelbrot）

### 9.2 集成测试

- 完整 compute kernel 调度 + 结果验证
- 渲染 → drawable 像素验证
- 多帧稳定性测试（1000 帧无崩溃）

### 9.3 测试运行

```bash
dotnet test MetalRenderingEngine.sln
```

所有测试必须在 M1 Mac 上通过（Metal 依赖于实际 GPU）。

---

## 11. 项目知识库（可选检索层）

本项目使用 [LLM Wiki](https://github.com/nashsu/llm_wiki) 维护一个跨 Phase 的技术知识库，供 Agent 在开发过程中按需检索。本节定义其**职责边界与使用规则**——约束层与知识层严格分离。

### 11.1 职责分离（核心）

| 层 | 文件 | 何时读 | 权威性 |
|----|------|--------|--------|
| **约束层** | 本 `AGENTS.md`（项目宪法） | 每次 Agent 会话**强制必读** | **最高，冲突时以此为准** |
| **知识层** | LLM Wiki | Agent 按需检索时读取 | 可引用、可回溯，但可能过时或失真 |

**冲突仲裁规则**：当 Wiki 内容与本文件的硬性约束（第 3 节架构约束、第 5 节禁止事项）矛盾时，一律以本文件为准，Wiki 内容视为待修正的过时信息。

典型例子：Wiki 某个 feature 页面说 `newLibraryWithSource` 可用，但本文件 §5.1 禁止运行时 MSL 编译（仅 SpirvCross 例外）——以本文件为准。

### 11.2 什么进 Wiki，什么不进

**进 Wiki（知识层，按需检索）：** 蓝图、roadmap、gap-analysis、Phase 调研、踩坑结论（如 `docs/argument-buffer-layout.md`）、`references/dxmt/docs/` 的借鉴参考。

**不进 Wiki（约束层/入口，强制读取）：** 本文件、`CLAUDE.md`、`BLUEPRINT.md`、架构禁令、技术约束锁定值、依赖白名单。代码文件本身（`.cs`/`.m`/`.slang`/`.h`）不进 Wiki，只记录到 feature 页面的 `code_path` 字段。

### 11.3 检索入口

Wiki 通过本地 MCP（`http://127.0.0.1:19828`）或 `llm_wiki_skill` 访问，只读、带引用。Claude Code 与 Codex 等平台共享同一个 Wiki，无需各自配置。

### 11.4 使用规则

1. 遇到"历史决策为什么这么做""某 Phase 的踩坑""DXMT 借鉴边界"这类跨文档追溯问题时，优先检索 Wiki，而非重新通读全部 markdown。
2. Wiki 返回的引用以 `[n]` 编号、可回溯到 wiki 页面路径，可信但需校验时效。
3. Wiki 不可用时，fallback 到 `BLUEPRINT.md` 指针链，不影响主流程。
4. **硬性架构约束（第 3、5 节）仍以本文件为准，Wiki 不得覆盖。**
5. 踩坑结论应落进 `docs/` 并同步进 Wiki 生成 `pitfall` 页面——避免每个 Agent 重新踩一遍坑。

### 11.5 合规边界

LLM Wiki 采用 GPLv3。本项目**纯本地当工具用**（不分发、不链接、不引入其代码），合规无风险。**禁止把 llm_wiki 代码搬进 `src/`/`native/`**，Agent 协作时需留意避免误搬。

### 11.6 配套文件

- `wiki-purpose.md` / `wiki-schema.md` — Wiki 配置源（受本仓库 git 管理，同步到 Wiki 项目生效）
- `llm-wiki-integration-guide.md` — 操作手册（安装、迁移、同步脚本、工作流）

Wiki 运行时数据位于仓库外（`/Users/liliang/MyGameRender-wiki`），不进引擎 git 历史。

---

## 12. 版本与变更记录

| 日期 | 版本 | 变更 |
|------|------|------|
| 2026-06-17 | 1.0 | 初始版本，定义核心原则、架构约束、禁止事项 |
| 2026-06-18 | 2.0 | Phase 5: 添加 C# Shader 源生成器（`IIncrementalGenerator`），支持 Compute/Vertex/Fragment 着色器自动生成 |
| 2026-06-18 | 3.0 | Phase 6: 批量命令编码器（`MetalCommandList` + wmtcmd 链表回放，P/Invoke 降 99.5%）、补全测试（21 个）、CI（macos-14）、性能 baseline |
| 2026-06-18 | 3.1 | Phase 6 扩展: MTLSharedEvent + CPU fence（bridge + C# 封装 + 异步 notifyListener 回调）、FenceBenchmarkDemo（MTLFence 阻塞 vs MTLSharedEvent 异步对比）、SharedEvent 测试（26 个总） |
| 2026-06-18 | 3.2 | Phase 6 扩展: SharedEventPool（预分配 event + signaledValue 复用，解决 Metal ≤64 上限）+ GpuFence 混合策略（AsyncCallback 帧间 / BlockingWait 数据依赖），三模式 benchmark（31 个测试） |
| 2026-06-19 | 4.0 | Phase 7-8: 3D 渲染基础（DepthStencil/RasterState/VertexDescriptor/InstancedDraw/IndirectDraw/MRT/MSAA）+ ICommandRecorder 抽象层（MetalCommandRecorder 走 MetalCommandList 批量回放）+ PipelineBuilder/RecordingCommandRecorder/LoggingCommandRecorder |
| 2026-06-19 | 5.0 | Phase 9: 着色器编译器——IShaderCompiler 接口 + SlangCompiler（slangc→DXIL→MSC→.metallib）+ SpirvCrossCompiler（spirv-cross→MSL→newLibraryWithSource）+ ShaderCache 两级缓存（L1 内存 + L2 磁盘 LRU 256MB）+ CachingShaderCompiler 装饰器 + MSC 反射数据模型与 MscReflectionParser + bridge 新增 MTLDevice_newLibraryWithSource（101 个测试） |
| 2026-06-19 | 6.0 | 接入 LLM Wiki 作为可选知识检索层：新增第 11 节定义约束层（本文件）与知识层（Wiki）职责分离，冲突以本文件为准；配套 `wiki-purpose.md`/`wiki-schema.md`/`llm-wiki-integration-guide.md`；Wiki 页面类型 6 种（feature/phase/decision/pitfall/dxmt-ref/source-summary）；GPLv3 纯本地使用边界 |
