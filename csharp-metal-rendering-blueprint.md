# C# → Metal 原生渲染技术方案蓝图

> 版本: 1.0  
> 日期: 2026-06-17  
> 目标: 以 C# 为生产力语言编写游戏引擎/渲染器，最终在 Apple Silicon 上通过 Metal 原生渲染  
> 核心参考: DXMT（3Shain/dxmt）的 Metal 桥接架构

---

## 1. 目标与约束

### 1.1 最终目标

- **着色器**：用户可以用 C# 语法编写 GPU 着色器（通过 Roslyn 源生成器翻译为 Slang/HLSL）
- **运行时**：所有 GPU 资源管理、命令调度、渲染管线控制全部在 C# 中完成
- **后端**：最终通过 Apple Metal.framework 原生渲染，不经 MoltenVK、WebGPU 等中间层
- **平台**：macOS 15+ / Apple Silicon (M1+)

### 1.2 关键约束

| 约束 | 说明 |
|------|------|
| 无成熟的 .NET Metal 绑定库 | Veldrid 停更、Metal.NET 无人维护、SharpMetal 未发布 |
| 用户偏好 C# 全栈 | 不接受 C++ 核心逻辑，C/ObjC 仅限于桥接层 |
| 着色器编译走 Path A | Slang → DXIL → MSC → metallib（已验证可用） |
| M1 Mac，8GB RAM，macOS 26.4.1 | 开发环境受限 |
| 仅安装 Xcode CLT | 无完整 Xcode.app，但 MSC 4.0 在 CLT 下可用 |

### 1.3 非目标（本期不覆盖）

- Vulkan/DX12 后端（未来可通过抽象层扩展）
- iOS/tvOS/visionOS 支持（Metal 本身支持，但暂不投入）
- 完整的 PBR/光照/后处理管线（引擎特性，非基础架构）

---

## 2. 总体架构

### 2.1 核心洞察：借鉴 DXMT 的 Handle-Based 架构

DXMT 的核心架构已经被验证可在大规模项目中工作（1095 stars，Wine D3D11 游戏可玩）：

```
┌─────────────────────────────────────────────────┐
│  C# 应用层（游戏/引擎）                            │
│  ┌───────────────────────────────────────────┐  │
│  │  着色器：C# struct → Roslyn SG → Slang/HLSL │  │
│  │  资源：Buffer<T>, Texture2D, Sampler       │  │
│  │  管线：ComputePipeline, RenderPipeline     │  │
│  │  调度：CommandList, CommandQueue           │  │
│  └───────────────────────────────────────────┘  │
│                      │ P/Invoke                   │
│  ┌───────────────────▼────────────────────────┐  │
│  │  bridge.m (ObjC, ~300-500行)               │  │
│  │  void* → [(id<MTLXXX>)handle method]        │  │
│  └───────────────────┬────────────────────────┘  │
│                      │                            │
│  ┌───────────────────▼────────────────────────┐  │
│  │  Metal.framework + libobjc.A.dylib         │  │
│  │  (macOS 系统自带，无需额外安装)               │  │
│  └───────────────────────────────────────────┘  │
└─────────────────────────────────────────────────┘
```

### 2.2 与 DXMT 的差异

| 维度 | DXMT | 我们的方案 |
|------|------|-----------|
| C→ObjC 通信 | Wine unixcall 层（NTSTATUS + struct param） | 直接 C 函数 + 返回值 |
| 目标 | D3D11 API 翻译（被动适配） | 原生 Metal API（主动设计） |
| 着色器转换 | DXBC→AIR（airconv） | Slang→DXIL→MSC（Path A） |
| C++ 封装 | Metal.hpp（RAII template） | C# SafeHandle（等效模式） |
| 命令编码 | 批量 struct 编码（性能优化） | 单次 API 调用（简单优先） |

### 2.3 为什么不用现有 .NET 库

| 库 | 问题 |
|----|------|
| Veldrid.MetalBindings | 2023年2月停更，Metal 版本停留在 2.x |
| Metal.NET (qian-o) | 9 stars，一人项目，无社区验证 |
| SharpMetal | 零发布包 |
| Silk.NET Metal | Issue 挂多年未完工 |

**结论：自己写一层薄桥接（~500行 ObjC + ~300行 C#），成本远低于依赖不成熟的三方库带来的风险。**

---

## 3. 着色器管线

### 3.1 当前方案：Slang 手写（即日可用）

```
手写 .slang 文件
    ↓ slangc -target dxil -profile sm_6_0
  .dxil
    ↓ metal-shaderconverter
  .metallib
    ↓ 嵌入 C# 资源或作为构建产物
  C# 运行时加载
```

**构建命令示例：**
```bash
# 编译 Slang → DXIL
slangc shaders/compute/mandelbrot.slang \
  -target dxil -entry main -stage compute \
  -profile sm_6_0 -o out/mandelbrot.dxil

# DXIL → metallib
metal-shaderconverter out/mandelbrot.dxil -o out/mandelbrot.metallib
```

### 3.2 未来方案：C# 源生成器 → Slang（3-4 周后）

```
C# struct (实现 IComputeShader/IFragmentShader)
    ↓ Roslyn Source Generator（参考 ComputeSharp）
  .slang 文件
    ↓ 与 3.1 相同的剩余管线
  .metallib
```

**目标 C# 语法示例：**
```csharp
[Shader]
public readonly partial struct MandelbrotShader : IComputeShader
{
    public float Time;
    public ReadWriteBuffer<float4> Output;

    public void Execute(ThreadId id)
    {
        float2 uv = (float2)id.XY / (float2)DispatchSize.XY;
        float3 col = Mandelbrot(uv * 3.0f - 2.0f);
        Output[id.XY] = new float4(col, 1.0f);
    }

    private float3 Mandelbrot(float2 c) { /* ... */ }
}
```

生成的 Slang 代码大致为：
```hlsl
struct MandelbrotShader_Constants {
    float Time;
};

RWBuffer<float4> Output : register(u0);
ConstantBuffer<MandelbrotShader_Constants> _constants : register(b0);

float3 Mandelbrot(float2 c) { /* ... */ }

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID) {
    float2 uv = float2(id.xy) / float2(dispatchSize.xy);
    float3 col = Mandelbrot(uv * 3.0 - 2.0);
    Output[id.xy] = float4(col, 1.0);
}
```

### 3.3 着色器类型覆盖计划

| 阶段 | 着色器类型 | Slang 特性 | Metal 对应 |
|------|-----------|-----------|-----------|
| P1 | Compute | `[numthreads]` | `MTLComputePipelineState` |
| P2 | Vertex + Fragment | `[[vertex]]`/`[[fragment]]`, `[[stage_in]]` | `MTLRenderPipelineState` |
| P3 | 常量缓冲区 | `[[buffer(n)]]` + `ConstantBuffer<T>` | `setVertexBuffer:` / `setFragmentBuffer:` |
| P4 | 纹理采样 | `Texture2D<T>`, `SamplerState` | `setFragmentTexture:` / `setFragmentSamplerState:` |
| P5 | Mesh Shader | `[[mesh]]` / `[[object]]` | `MTLMeshRenderPipelineState` (Metal 4+) |

---

## 4. Metal 桥接层：bridge.m

### 4.1 设计原则

1. **纯 C ABI**：所有函数返回 `void*`（不透明句柄），所有参数使用 C 基础类型
2. **手动引用计数**：`__bridge_retained` 转移所有权给调用方，`retain`/`release` 由 C# 层管理
3. **最小化 API 面**：只导出实际使用的 Metal 函数，不追求覆盖全部 Metal API
4. **错误通过 out 参数返回**：`NSError**` 模式

### 4.2 完整 API 设计

```objc
// ============================================================
// bridge.h — Metal 桥接层 C ABI
// ============================================================

#ifndef METAL_BRIDGE_H
#define METAL_BRIDGE_H

#include <stdint.h>
#include <stddef.h>

// ---- 引用计数 ----
void  metal_retain(void* obj);
void  metal_release(void* obj);

// ---- 设备 ----
void* metal_create_system_default_device(void);
const char* metal_device_name(void* device);
int   metal_device_has_unified_memory(void* device);
int   metal_device_supports_family(void* device, int gpu_family);

// ---- 着色器库 ----
void* metal_new_library_from_data(void* device, const void* data, size_t len, void** error);
void* metal_new_function_with_name(void* library, const char* name);

// ---- 计算管线 ----
void* metal_new_compute_pipeline_state(void* device, void* function, void** error);
int   metal_compute_pipeline_max_total_threads_per_threadgroup(void* pso);
int   metal_compute_pipeline_thread_execution_width(void* pso);

// ---- 渲染管线 ----
typedef struct {
    int pixel_format;
    int blending_enabled;
    int src_rgb_blend_factor;
    int dst_rgb_blend_factor;
    int src_alpha_blend_factor;
    int dst_alpha_blend_factor;
} MetalColorAttachment;

typedef struct {
    MetalColorAttachment colors[8];
    int depth_pixel_format;
    int stencil_pixel_format;
    int sample_count;
    int alpha_to_coverage;
} MetalRenderPipelineDesc;

void* metal_new_render_pipeline_state(void* device, void* vertex_func, void* fragment_func,
                                       MetalRenderPipelineDesc* desc, void** error);

// ---- 资源 ----
typedef struct {
    size_t length;
    int    storage_mode;   // 0=Shared, 1=Managed, 2=Private
    int    cpu_cache_mode; // 0=Default, 1=WriteCombined
} MetalBufferInfo;

void* metal_new_buffer(void* device, MetalBufferInfo* info);
void* metal_buffer_contents(void* buffer);
void  metal_buffer_did_modify_range(void* buffer, size_t start, size_t length);

typedef struct {
    int    texture_type;   // 0=1D, 1=2D, 2=3D, 3=Cube
    int    pixel_format;
    int    width;
    int    height;
    int    depth;
    int    mipmap_levels;
    int    array_length;
    int    sample_count;
    int    storage_mode;
    int    usage;          // bitmask: 1=ShaderRead, 2=ShaderWrite, 4=RenderTarget
} MetalTextureInfo;

void* metal_new_texture(void* device, MetalTextureInfo* info);
void  metal_texture_replace_region(void* texture, int level, int slice,
                                    int x, int y, int z, int w, int h, int d,
                                    const void* data, size_t bytes_per_row, size_t bytes_per_image);

typedef struct {
    int min_filter;       // 0=Nearest, 1=Linear
    int mag_filter;
    int mip_filter;       // 0=NotMipmapped, 2=Nearest, 3=Linear
    int s_address_mode;   // 0=ClampToEdge, 1=Repeat, 2=MirrorRepeat
    int t_address_mode;
    int r_address_mode;
    int max_anisotropy;
    int normalized_coords;
    int compare_function; // -1=Disabled
    int lod_min_clamp;
    int lod_max_clamp;    // MAXFLOAT = no clamp
} MetalSamplerInfo;

void* metal_new_sampler(void* device, MetalSamplerInfo* info);

// ---- 命令队列与命令缓冲 ----
void* metal_new_command_queue(void* device);
void* metal_new_command_buffer(void* queue);
void  metal_command_buffer_commit(void* cmd_buf);
void  metal_command_buffer_wait_until_completed(void* cmd_buf);
int   metal_command_buffer_status(void* cmd_buf); // 0=NotEnqueued, 1=Enqueued, 2=Committed, 3=Scheduled, 4=Completed, 5=Error
void* metal_command_buffer_error(void* cmd_buf);

// ---- 计算编码器 ----
void* metal_compute_command_encoder(void* cmd_buf);
void  metal_compute_encoder_set_pipeline(void* encoder, void* pso);
void  metal_compute_encoder_set_buffer(void* encoder, void* buffer, size_t offset, int index);
void  metal_compute_encoder_set_bytes(void* encoder, const void* bytes, size_t length, int index);
void  metal_compute_encoder_set_texture(void* encoder, void* texture, int index);
void  metal_compute_encoder_dispatch_threadgroups(void* encoder,
        int groups_x, int groups_y, int groups_z,
        int threads_x, int threads_y, int threads_z);
void  metal_compute_encoder_end_encoding(void* encoder);

// ---- 渲染编码器 ----
typedef struct {
    void* texture;
    int   load_action;   // 0=DontCare, 1=Load, 2=Clear
    int   store_action;  // 0=DontCare, 1=Store, 2=MultisampleResolve
    float clear_color[4];
    float clear_depth;
    int   clear_stencil;
} MetalRenderPassAttachment;

typedef struct {
    MetalRenderPassAttachment colors[8];
    MetalRenderPassAttachment depth;
    MetalRenderPassAttachment stencil;
} MetalRenderPassDesc;

void* metal_render_command_encoder(void* cmd_buf, MetalRenderPassDesc* desc);
void  metal_render_encoder_set_pipeline(void* encoder, void* pso);
void  metal_render_encoder_set_vertex_buffer(void* encoder, void* buffer, size_t offset, int index);
void  metal_render_encoder_set_fragment_buffer(void* encoder, void* buffer, size_t offset, int index);
void  metal_render_encoder_set_fragment_texture(void* encoder, void* texture, int index);
void  metal_render_encoder_set_fragment_sampler(void* encoder, void* sampler, int index);
void  metal_render_encoder_set_viewport(void* encoder, float x, float y, float w, float h,
                                          float znear, float zfar);
void  metal_render_encoder_set_scissor(void* encoder, int x, int y, int w, int h);
void  metal_render_encoder_draw_primitives(void* encoder, int primitive_type,
                                             int vertex_start, int vertex_count,
                                             int instance_count, int base_instance);
void  metal_render_encoder_draw_indexed_primitives(void* encoder, int primitive_type,
                                                     int index_count, int index_type,
                                                     void* index_buffer, int index_buffer_offset,
                                                     int instance_count, int base_vertex, int base_instance);
void  metal_render_encoder_end_encoding(void* encoder);

// ---- 呈现 ----
void* metal_create_layer(void* ns_window);
void* metal_layer_next_drawable(void* layer);
void* metal_drawable_texture(void* drawable);
void  metal_command_buffer_present_drawable(void* cmd_buf, void* drawable);

// ---- 同步 ----
void* metal_new_fence(void* device);
void  metal_encoder_wait_for_fence(void* encoder, void* fence, int before_stages);
void  metal_encoder_update_fence(void* encoder, void* fence, int after_stages);

#endif // METAL_BRIDGE_H
```

### 4.3 bridge.m 核心实现模式

```objc
// ============================================================
// bridge.m — Metal 桥接层实现
// ============================================================

#import <Metal/Metal.h>
#import "bridge.h"

// ---- 引用计数 ----
void metal_retain(void* obj)   { CFRetain((CFTypeRef)obj); }
void metal_release(void* obj)  { CFRelease((CFTypeRef)obj); }

// ---- 设备 ----
void* metal_create_system_default_device(void) {
    id<MTLDevice> device = MTLCreateSystemDefaultDevice();
    return (__bridge_retained void*)device;
}

int metal_device_has_unified_memory(void* device) {
    return [(id<MTLDevice>)device hasUnifiedMemory] ? 1 : 0;
}

// ---- 着色器库（从预编译的 .metallib 二进制加载）----
void* metal_new_library_from_data(void* device, const void* data, size_t len, void** error) {
    id<MTLDevice> dev = (__bridge id<MTLDevice>)device;
    dispatch_data_t dd = dispatch_data_create(data, len,
        dispatch_get_main_queue(), ^{ /* data copied, nothing to release */ });
    id<MTLLibrary> lib = [dev newLibraryWithData:dd error:(NSError**)error];
    dispatch_release(dd);
    return (__bridge_retained void*)lib;
}

void* metal_new_function_with_name(void* library, const char* name) {
    id<MTLLibrary> lib = (__bridge id<MTLLibrary>)library;
    NSString* nsName = [NSString stringWithUTF8String:name];
    id<MTLFunction> func = [lib newFunctionWithName:nsName];
    return (__bridge_retained void*)func;
}

// ---- 计算管线 ----
void* metal_new_compute_pipeline_state(void* device, void* function, void** error) {
    id<MTLDevice> dev = (__bridge id<MTLDevice>)device;
    id<MTLFunction> func = (__bridge id<MTLFunction>)function;
    id<MTLComputePipelineState> pso = [dev newComputePipelineStateWithFunction:func
                                                                         error:(NSError**)error];
    return (__bridge_retained void*)pso;
}

// ---- Buffer ----
void* metal_new_buffer(void* device, MetalBufferInfo* info) {
    id<MTLDevice> dev = (__bridge id<MTLDevice>)device;
    MTLResourceOptions opts = (MTLResourceOptions)(info->storage_mode << 4 | info->cpu_cache_mode);
    id<MTLBuffer> buf = [dev newBufferWithLength:info->length options:opts];
    return (__bridge_retained void*)buf;
}

void* metal_buffer_contents(void* buffer) {
    return [(id<MTLBuffer>)buffer contents];
}

void metal_buffer_did_modify_range(void* buffer, size_t start, size_t length) {
    [(id<MTLBuffer>)buffer didModifyRange:NSMakeRange(start, length)];
}

// ---- 计算编码器 ----
void* metal_compute_command_encoder(void* cmd_buf) {
    id<MTLCommandBuffer> buf = (__bridge id<MTLCommandBuffer>)cmd_buf;
    id<MTLComputeCommandEncoder> enc = [buf computeCommandEncoder];
    return (__bridge_retained void*)enc;
}

void metal_compute_encoder_set_pipeline(void* encoder, void* pso) {
    [(id<MTLComputeCommandEncoder>)encoder setComputePipelineState:(id<MTLComputePipelineState>)pso];
}

void metal_compute_encoder_set_buffer(void* encoder, void* buffer, size_t offset, int index) {
    [(id<MTLComputeCommandEncoder>)encoder setBuffer:(id<MTLBuffer>)buffer
                                              offset:offset
                                             atIndex:(NSUInteger)index];
}

void metal_compute_encoder_set_bytes(void* encoder, const void* bytes, size_t length, int index) {
    [(id<MTLComputeCommandEncoder>)encoder setBytes:bytes length:length atIndex:(NSUInteger)index];
}

void metal_compute_encoder_set_texture(void* encoder, void* texture, int index) {
    [(id<MTLComputeCommandEncoder>)encoder setTexture:(id<MTLTexture>)texture atIndex:(NSUInteger)index];
}

void metal_compute_encoder_dispatch_threadgroups(void* encoder,
        int groups_x, int groups_y, int groups_z,
        int threads_x, int threads_y, int threads_z) {
    MTLSize groups = {groups_x, groups_y, groups_z};
    MTLSize threads = {threads_x, threads_y, threads_z};
    [(id<MTLComputeCommandEncoder>)encoder dispatchThreadgroups:groups threadsPerThreadgroup:threads];
}

void metal_compute_encoder_end_encoding(void* encoder) {
    [(id<MTLComputeCommandEncoder>)encoder endEncoding];
}

// ---- 命令缓冲 ----
void* metal_new_command_queue(void* device) {
    id<MTLDevice> dev = (__bridge id<MTLDevice>)device;
    id<MTLCommandQueue> queue = [dev newCommandQueue];
    return (__bridge_retained void*)queue;
}

void* metal_new_command_buffer(void* queue) {
    id<MTLCommandQueue> q = (__bridge id<MTLCommandQueue>)queue;
    id<MTLCommandBuffer> buf = [q commandBuffer];
    return (__bridge_retained void*)buf;
}

void metal_command_buffer_commit(void* cmd_buf) {
    [(id<MTLCommandBuffer>)cmd_buf commit];
}

void metal_command_buffer_wait_until_completed(void* cmd_buf) {
    [(id<MTLCommandBuffer>)cmd_buf waitUntilCompleted];
}
```

### 4.4 编译 bridge.m

```bash
# 编译为动态库
clang -dynamiclib \
  -o libmetal_bridge.dylib \
  -framework Metal -framework Foundation \
  bridge.m

# 放置到 C# 项目的输出目录或系统路径
```

---

## 5. C# 运行时层

### 5.1 SafeHandle 封装模式

```csharp
// MetalObject.cs — 所有 Metal 对象的基类
using System.Runtime.InteropServices;

public abstract class MetalObject : SafeHandle
{
    [DllImport("libmetal_bridge")] static extern void metal_release(IntPtr obj);
    [DllImport("libmetal_bridge")] static extern void metal_retain(IntPtr obj);

    protected MetalObject(IntPtr handle, bool ownsHandle) 
        : base(IntPtr.Zero, ownsHandle) 
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            metal_release(handle);
            SetHandle(IntPtr.Zero);
        }
        return true;
    }

    public void Retain() => metal_retain(handle);
}

// MetalDevice.cs
public sealed class MetalDevice : MetalObject
{
    [DllImport("libmetal_bridge")]
    static extern IntPtr metal_create_system_default_device();

    [DllImport("libmetal_bridge")]
    static extern int metal_device_has_unified_memory(IntPtr device);

    private MetalDevice(IntPtr handle) : base(handle, true) { }

    public static MetalDevice Create() => new(metal_create_system_default_device());

    public bool HasUnifiedMemory => metal_device_has_unified_memory(handle) != 0;

    public MetalLibrary NewLibrary(byte[] metallibData) { /* ... */ }
    public MetalBuffer NewBuffer(nuint length, MTLResourceOptions options) { /* ... */ }
    public MetalTexture NewTexture(MetalTextureDescriptor desc) { /* ... */ }
    public MetalSamplerState NewSampler(MetalSamplerDescriptor desc) { /* ... */ }
    public MetalCommandQueue NewCommandQueue() { /* ... */ }
    public MetalComputePipelineState NewComputePipeline(MetalFunction function) { /* ... */ }
    public MetalRenderPipelineState NewRenderPipeline(
        MetalFunction vertex, MetalFunction fragment, MetalRenderPipelineDescriptor desc) { /* ... */ }
}

// MetalBuffer.cs
public sealed class MetalBuffer : MetalObject
{
    [DllImport("libmetal_bridge")]
    static extern IntPtr metal_new_buffer(IntPtr device, ref MetalBufferInfo info);

    [DllImport("libmetal_bridge")]
    static extern IntPtr metal_buffer_contents(IntPtr buffer);

    [DllImport("libmetal_bridge")]
    static extern void metal_buffer_did_modify_range(IntPtr buffer, nuint start, nuint length);

    internal MetalBuffer(IntPtr handle) : base(handle, true) { }

    public unsafe void* Contents => (void*)metal_buffer_contents(handle);

    public void DidModifyRange(nuint start, nuint length)
        => metal_buffer_did_modify_range(handle, start, length);
}
```

### 5.2 枚举和结构体（C# ↔ C 对应）

```csharp
// MTLResourceOptions.cs
public enum MTLResourceOptions : ulong
{
    StorageModeShared  = 0 << 4,
    StorageModeManaged = 1 << 4,
    StorageModePrivate = 2 << 4,
    CPUCacheModeDefault       = 0 << 0,
    CPUCacheModeWriteCombined = 1 << 0,
}

// MTLPixelFormat.cs（关键格式）
public enum MTLPixelFormat : uint
{
    Invalid = 0,
    RGBA8Unorm = 70,
    BGRA8Unorm = 80,
    RGBA32Float = 125,
    Depth32Float = 252,
    // ... 按需添加
}

// 注意：C# 结构体的内存布局必须与 bridge.h 中的 C 结构体严格一致
[StructLayout(LayoutKind.Sequential)]
public struct MetalBufferInfo
{
    public nuint Length;
    public int StorageMode;
    public int CPUCacheMode;
}
```

### 5.3 计算着色器调度示例

```csharp
public class MandelbrotComputeExample
{
    public void Run()
    {
        // 1. 创建设备
        using var device = MetalDevice.Create();
        
        // 2. 加载预编译的 metallib（从嵌入资源）
        byte[] metallib = File.ReadAllBytes("shaders/mandelbrot.metallib");
        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("mandelbrot_kernel");
        using var pso = device.NewComputePipeline(function);
        
        // 3. 创建输出 buffer
        int width = 1024, height = 768;
        using var outputBuffer = device.NewBuffer(
            (nuint)(width * height * 16), // float4 = 16 bytes
            MTLResourceOptions.StorageModeShared);
        
        // 4. 创建命令
        using var queue = device.NewCommandQueue();
        using var cmdBuf = queue.NewCommandBuffer();
        using var encoder = cmdBuf.CreateComputeEncoder();
        
        encoder.SetPipeline(pso);
        encoder.SetBuffer(outputBuffer, 0, 0);
        
        // 设置常量（通过 setBytes 传小数据）
        var constants = new MandelbrotConstants { Time = 1.5f, MaxIterations = 256 };
        encoder.SetBytes(constants, 1);
        
        // 调度
        int threadGroupSize = pso.MaxTotalThreadsPerThreadgroup;
        int groupsX = (width  + threadGroupSize - 1) / threadGroupSize;
        int groupsY = (height + threadGroupSize - 1) / threadGroupSize;
        encoder.DispatchThreadgroups(groupsX, groupsY, 1, threadGroupSize, 1, 1);
        
        encoder.EndEncoding();
        cmdBuf.Commit();
        cmdBuf.WaitUntilCompleted();
        
        // 5. 读取结果
        unsafe
        {
            float* data = (float*)outputBuffer.Contents;
            // data[0..width*height*4] 包含结果
        }
    }
}
```

---

## 6. 资源管理策略

### 6.1 内存模型

M1 是统一内存架构（UMA），CPU 和 GPU 共享物理内存。

```csharp
// 推荐策略：UMA 下始终使用 StorageModeShared
// 不需要 CPU↔GPU 显式拷贝
var buffer = device.NewBuffer(length, MTLResourceOptions.StorageModeShared);

// 仅对纯 GPU 端数据使用 Private
var gpuOnlyBuffer = device.NewBuffer(length, MTLResourceOptions.StorageModePrivate);
```

### 6.2 生命周期管理

```csharp
// C# using 语句自动调用 Dispose → ReleaseHandle → metal_release
using var buffer = device.NewBuffer(1024, MTLResourceOptions.StorageModeShared);
// ... buffer 离开作用域时自动释放

// 需要跨越作用域共享时手动 Retain/Release
public class TextureCache
{
    private MetalTexture _texture;
    
    public void Set(MetalTexture texture)
    {
        _texture?.Dispose();
        _texture = texture;
        texture?.Retain(); // 防止外部释放后被回收
    }
}
```

### 6.3 同步策略

```csharp
// GPU 完成回调
cmdBuf.AddCompletedHandler(buf => {
    // GPU 工作完成，安全读取 buffer
    ProcessResults(outputBuffer);
});

// Fence 同步（多队列/多缓冲）
using var fence = device.NewFence();
encoderA.UpdateFence(fence, afterStages: /* Fragment */);
// ... 在另一个 encoder 中
encoderB.WaitForFence(fence, beforeStages: /* Vertex */);
```

---

## 7. 渲染管线

### 7.1 最小渲染循环

```csharp
public class MetalRenderLoop
{
    private MetalDevice _device;
    private MetalCommandQueue _queue;
    private MetalRenderPipelineState _pso;
    private IntPtr _metalLayer; // CAMetalLayer

    public void RenderFrame(IntPtr windowHandle)
    {
        // 获取 drawable
        var drawable = _metalLayer.GetNextDrawable();
        var drawableTexture = drawable.GetTexture();

        // 设置 render pass
        var renderPass = new MetalRenderPassDesc();
        renderPass.colors[0].texture = drawableTexture.handle;
        renderPass.colors[0].load_action = 2;  // Clear
        renderPass.colors[0].clear_color = new[] { 0.2f, 0.3f, 0.4f, 1.0f };

        // 编码渲染命令
        using var cmdBuf = _queue.NewCommandBuffer();
        using var encoder = cmdBuf.CreateRenderEncoder(renderPass);

        encoder.SetPipeline(_pso);
        encoder.SetViewport(0, 0, 1920, 1080, 0, 1);
        encoder.DrawPrimitives(PrimitiveType.Triangle, 0, 3);
        encoder.EndEncoding();

        cmdBuf.PresentDrawable(drawable);
        cmdBuf.Commit();
    }
}
```

### 7.2 Avalonia 集成（用户的主要 UI 框架）

```csharp
// 方案：Avalonia 中嵌入原生 Metal 视图
// 通过 NSView 互操作
// 
// 1. 在 Avalonia 的 NativeControlHost 中创建 NSView
// 2. 在 NSView 上附加 CAMetalLayer
// 3. 每帧渲染到 CAMetalLayer 的 drawable

public class MetalView : NativeControlHost
{
    private IntPtr _nsView;
    private IntPtr _metalLayer;
    private MetalRenderLoop _renderLoop;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // 创建 NSView + CAMetalLayer
        _nsView = NativeCreateMetalView(parent.Handle);
        _metalLayer = NativeGetMetalLayer(_nsView);
        _renderLoop = new MetalRenderLoop(_metalLayer);
        return new PlatformHandle(_nsView, "NSView");
    }
}
```

---

## 8. 实施路线图

### Phase 1：最小可行链路（1-2 天）

**目标：** C# → bridge.m → Metal.framework → GPU 执行一个 compute kernel

- [ ] 编写 `bridge.h` + `bridge.m`（先只包含 compute 相关函数）
- [ ] 编译 `libmetal_bridge.dylib`
- [ ] 编写 C# SafeHandle 基类和 `MetalDevice`、`MetalLibrary`、`MetalComputePipelineState`
- [ ] 手写一个简单 Slang compute shader（例如：把 buffer 每个元素 ×2）
- [ ] 编译 shader：`slangc → DXIL → MSC → .metallib`
- [ ] C# 端加载 `.metallib` → 创建 pipeline → dispatch → 验证结果

**验证标准：** C# 程序在 M1 上成功调度 Metal compute shader 并得到正确结果。

### Phase 2：渲染管线（2-3 天）

- [ ] 扩展 `bridge.m`：增加 render pipeline、drawable、viewport 函数
- [ ] 编写 C# 渲染管线封装
- [ ] 手写 Slang vertex shader + fragment shader
- [ ] 实现最小渲染循环（清屏 + 三角形）
- [ ] 集成 CAMetalLayer 呈现到窗口

**验证标准：** 窗口中出现 Metal 渲染的彩色三角形。

### Phase 3：资源系统（2-3 天）

- [ ] 完善 Texture 支持（创建、上传、采样）
- [ ] 完善 Sampler 支持
- [ ] 常量缓冲区模式（setBytes / setBuffer）
- [ ] 基本的 Buffer 更新策略（双缓冲/环形缓冲）
- [ ] MTLFence 帧同步

**验证标准：** 纹理三角形 + CPU 动态更新 uniform。

### Phase 4：着色器工具链集成（2-3 天）

- [ ] MSBuild target：自动编译 `.slang` → `.metallib`
- [ ] 嵌入资源或内容文件自动管理
- [ ] Shader 编译错误 → C# 编译错误映射

### Phase 5：C# 源生成器（2-3 周）

- [ ] 参考 ComputeSharp 的 `HlslKnownTypes.cs`、`HlslKnownMethods.cs`
- [ ] 实现 Roslyn IIncrementalGenerator
- [ ] C# struct → Slang 语法映射
- [ ] 常量缓冲区自动布局（std140）
- [ ] 线程组大小自动推断

**验证标准：** 将 Phase 2 的手写 Slang 替换为 C# struct，功能一致。

### Phase 6：引擎抽象层（持续迭代）

- [ ] `IGraphicsDevice` 抽象（为未来 Vulkan/DX12 后端预留）
- [ ] `ICommandList` / `ICommandQueue`
- [ ] `IShader` / `IPipelineState`
- [ ] 资源池和缓存策略
- [ ] 性能分析和优化

---

## 9. 开发工作流与工具

### 9.1 项目结构建议

```
MyGameEngine/
├── src/
│   ├── MyGameEngine.Core/           # 引擎核心（C#）
│   │   ├── Metal/
│   │   │   ├── Interop/
│   │   │   │   ├── MetalBridge.cs   # DllImport 声明
│   │   │   │   └── MetalStructs.cs  # 结构体定义
│   │   │   ├── MetalDevice.cs
│   │   │   ├── MetalBuffer.cs
│   │   │   ├── MetalTexture.cs
│   │   │   ├── MetalLibrary.cs
│   │   │   ├── MetalPipeline.cs
│   │   │   ├── MetalCommandQueue.cs
│   │   │   ├── MetalCommandBuffer.cs
│   │   │   ├── MetalComputeEncoder.cs
│   │   │   └── MetalRenderEncoder.cs
│   │   ├── Graphics/
│   │   │   ├── IGraphicsDevice.cs   # 抽象接口
│   │   │   ├── IBuffer.cs
│   │   │   └── ITexture.cs
│   │   └── ShaderGen/               # 未来源生成器
│   │       └── ShaderSourceGenerator.cs
│   ├── MyGameEngine.Shaders/        # 着色器
│   │   ├── Compute/
│   │   │   └── Mandelbrot.slang
│   │   └── Render/
│   │       ├── FullscreenQuad.vert.slang
│   │       └── SimpleColor.frag.slang
│   └── MyGameEngine.Demo/           # 示例项目
│       └── Program.cs
├── native/
│   ├── bridge.h
│   └── bridge.m
├── build/
│   ├── compile_shaders.sh           # 着色器编译脚本
│   └── build_bridge.sh              # bridge 编译脚本
└── MyGameEngine.sln
```

### 9.2 构建脚本

```bash
#!/bin/bash
# build/compile_shaders.sh

SHADER_DIR="src/MyGameEngine.Shaders"
OUTPUT_DIR="out/shaders"

mkdir -p "$OUTPUT_DIR"

for slang_file in "$SHADER_DIR"/**/*.slang; do
    name=$(basename "$slang_file" .slang)
    
    # 判断 stage
    if [[ "$name" == *.vert ]]; then
        stage="vertex"
    elif [[ "$name" == *.frag ]]; then
        stage="fragment"
    else
        stage="compute"
    fi
    
    echo "Compiling $slang_file ($stage)..."
    
    slangc "$slang_file" \
        -target dxil -entry main -stage "$stage" \
        -profile sm_6_0 \
        -o "$OUTPUT_DIR/$name.dxil"
    
    metal-shaderconverter "$OUTPUT_DIR/$name.dxil" \
        -o "$OUTPUT_DIR/$name.metallib"
done

echo "Done. Output in $OUTPUT_DIR"
```

### 9.3 使用到的工具链（已验证）

| 工具 | 路径 | 用途 |
|------|------|------|
| slangc | PATH | Slang → DXIL 编译 |
| metal-shaderconverter | `/usr/local/bin/metal-shaderconverter` | DXIL → metallib |
| clang | `/usr/bin/clang` | 编译 bridge.m |
| dotnet | PATH (.NET 10.0.101) | C# 编译 |

---

## 10. 附录：关键设计决策记录

### 决策 1：为什么用 C ABI 而不是直接 P/Invoke objc_msgSend

**选型：** C 桥接函数

**理由：**
- `objc_msgSend` 是可变参数，P/Invoke 需要为每种参数组合写重载
- Xcode CLT 不包含完整 SDK 头文件时，C 桥接层更稳定
- 错误处理（NSError**）在 ObjC 侧处理比在 C# 侧容易
- DXMT 已验证此模式可行

### 决策 2：为什么用 Slang 而不是直接 MSL

**选型：** Slang → DXIL → MSC → metallib

**理由：**
- Slang 支持 HLSL 语法（C# 开发者熟悉）
- Slang 有 module、interface、泛型等现代特性
- MSC 是 Apple 官方工具，转换质量有保证
- 未来可以直接 Slang → MSL（当 Xcode 可用时），省去 MSC 步骤

### 决策 3：SafeHandle 而不是 IDisposable 裸指针

**选型：** SafeHandle 子类

**理由：**
- 自动处理 finalization（防止忘记释放）
- 与 .NET 资源管理模式一致
- 支持 `using` 语句
- 临界终结（CriticalFinalizerObject）防止进程退出时泄漏

---

## 11. 启动检查清单

新 Agent 启动时的推荐步骤：

1. **读取本蓝图**
2. **验证工具链：** `slangc --version`、`metal-shaderconverter --version`、`clang --version`
3. **编译 bridge：** `clang -dynamiclib -framework Metal -framework Foundation bridge.m -o libmetal_bridge.dylib`
4. **编译测试 shader：** 写一个简单 compute kernel → slangc → MSC → .metallib
5. **创建 C# 项目：** `dotnet new console`，引用 `libmetal_bridge.dylib`
6. **运行 Phase 1 验证：** C# 加载 .metallib → dispatch → 验证输出
7. **确认验证通过后，进入 Phase 2**
