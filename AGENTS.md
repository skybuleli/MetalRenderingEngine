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
| 着色器语言 | Slang（HLSL 超集），未来通过 C# 源生成器生成 |
| 着色器编译路径 | `slangc -target dxil -profile sm_6_0` → `metal-shaderconverter` → `.metallib` |
| Metal 接口方式 | 自定义 `bridge.m`（C ABI）→ C# P/Invoke → SafeHandle |
| 开发平台 | macOS 26.4.1，Apple Silicon (M1, 8GB) |
| Xcode | 仅 Command Line Tools（无完整 Xcode.app） |
| 内存模型 | M1 统一内存（UMA），首选 `StorageModeShared` |

---

## 2. 项目结构

```
MetalRenderingEngine/
├── AGENTS.md                         # ← 本文件
├── BLUEPRINT.md                      # 技术方案蓝图（必读）
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
│   └── MetalRenderingEngine.Demo/    # 示例/测试项目
│       └── Program.cs
├── build/
│   ├── compile_shaders.sh            # 着色器编译脚本
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
- 架构决策记录在 BLUEPRINT.md 中，不在代码中重复

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

# 编译着色器
./build/compile_shaders.sh

# 构建 .NET 项目
dotnet build MetalRenderingEngine.sln
```

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

- `System.Numerics.Vectors` — SIMD 数学类型
- `System.Runtime.CompilerServices.Unsafe` — 指针操作辅助
- 用户界面框架（如 Avalonia）— 仅用于 Demo 项目

**任何新增 NuGet 依赖必须在 AGENTS.md 的此节中记录。**

---

## 8. 工作流

### 8.1 Agent 启动流程

每次新 Agent 启动时：

1. 读取本 `AGENTS.md`
2. 读取 `BLUEPRINT.md` 了解技术方案
3. 检查工具链可用性：`slangc --version`、`metal-shaderconverter --version`、`clang --version`
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

### 8.3 添加新着色器

1. 在 `src/MetalRenderingEngine.Shaders/` 下创建 `.slang` 文件
2. 运行 `build/compile_shaders.sh`
3. 将生成的 `.metallib` 添加到 C# 项目（嵌入资源或复制到输出）

---

## 9. 测试要求

### 9.1 单元测试

- `MetalDevice` 生命周期测试
- `MetalBuffer` 创建/读写/释放测试
- `MetalLibrary` 加载 `.metallib` 测试
- `MetalComputePipeline` 创建和调度测试
- Bridge 引用计数泄漏测试

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

## 10. 版本与变更记录

| 日期 | 版本 | 变更 |
|------|------|------|
| 2026-06-17 | 1.0 | 初始版本，定义核心原则、架构约束、禁止事项 |
