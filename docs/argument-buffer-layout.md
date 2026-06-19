# Argument Buffer Layout

本文档记录 Phase 10A 的第一轮实测结果，目标不是设计最终绑定层，而是先把 `metal-shaderconverter` 的真实 `reflect.json` 行为钉死。

## 当前 PoC

测试位置：
- `tests/MetalRenderingEngine.Tests/Phase10ArgumentBufferLayoutTests.cs`

测试 shader 资源组合：
- `1 x CBV`
- `2 x StructuredBuffer SRV`
- `1 x RWStructuredBuffer UAV`
- `2 x Texture2D SRV`
- `1 x SamplerState`

编译方式：
- `SlangCompiler` with `ShaderStage.Fragment`
- `metal-shaderconverter --output-reflection-file`

## 实测结论

### 1. 所有条目都进入同一个 top-level argument buffer

这一组混合资源最终都出现在 `TopLevelArgumentBuffer` 内，没有出现“texture / sampler 走另一套外部绑定”的情况。

### 2. 条目大小当前统一为 24 字节

PoC 的 7 个资源条目大小全部为 `24`：

| Index | Type | Slot | EltOffset | Size |
|------:|------|-----:|----------:|-----:|
| 0 | SRV | 2 | 0 | 24 |
| 1 | SRV | 3 | 24 | 24 |
| 2 | SRV | 0 | 48 | 24 |
| 3 | SRV | 1 | 72 | 24 |
| 4 | UAV | 0 | 96 | 24 |
| 5 | CBV | 0 | 120 | 24 |
| 6 | Sampler | 0 | 144 | 24 |

当前可以安全假设：
- `TopLevelArgumentBuffer` 是按 24 字节步长线性排布
- `EltOffset` 比声明顺序更可信，后续 encoder 必须严格按它写

### 3. 顺序不是源码声明顺序

源码声明顺序是：
1. `paramsCb`
2. `inputA`
3. `inputB`
4. `outputBuffer`
5. `colorTex`
6. `normalTex`
7. `linearSampler`

实测 `TopLevelArgumentBuffer` 顺序却是：
1. `Texture SRV slot 2`
2. `Texture SRV slot 3`
3. `StructuredBuffer SRV slot 0`
4. `StructuredBuffer SRV slot 1`
5. `RWStructuredBuffer UAV slot 0`
6. `CBV slot 0`
7. `Sampler slot 0`

这意味着：
- 不能按源码声明顺序序列化 argument buffer
- 也不能只看 `register(t0/t1/u0/...)` 直接映射写入顺序
- 必须以 MSC 反射里的 `TopLevelArgumentBuffer + *Indices` 为唯一事实来源

### 4. 反射里名称目前为空

PoC 中 `TopLevelArgumentBuffer[*].Name` 全部为空字符串。

这意味着后续 `ResourceTable.BindBuffer("name", ...)` 不能指望直接靠 MSC 反射里的 `Name` 字段工作，至少要准备：
- 编译后附加元数据
- 或半自动 `BindingLayout`
- 或从 Slang 侧保留一份可命名映射

## 当前边界

这轮 PoC 只证明了：
- fragment shader 的混合资源会统一进入 top-level argument buffer
- `EltOffset` / `Size` / `*Indices` 可以作为运行时序列化依据
- 纯 `Texture2D + SamplerState` shader 的 MSC reflect.json 会产生 2 个顶层条目（SRV + Sampler）

还没有证明：
- 真实运行时 texture / sampler 描述符该怎么编码
- 从 MSC 产物取回的 `MTLArgumentEncoder` 是否能提供可用 `encodedLength`
- vertex / fragment 是否共享完全同构布局
- compute + render 混合时是否保持相同规则
- `MTL_SHADER_VALIDATION=1` 下的实际 Metal 绑定索引行为

## 根因确证：MTLArgumentEncoder 对 MSC 产物是死路（不是 blocker）

测试位置：
- `tests/MetalRenderingEngine.Tests/Phase10TextureSamplerRuntimeTests.cs`（旧）
- `tests/MetalRenderingEngine.Tests/Phase10ArgEncoderIndexDiagTests.cs`（诊断）

诊断实验结果（遍历 buffer index 0..2 调 `newArgumentEncoderWithBufferIndex`）：
```
buffer(0) encodedLength = 0
buffer(1) encodedLength = 0
buffer(2) encodedLength = 0   ← MSC 4.0 实际放 top_level_global_ab 的位置
```

> 注：`bufferIndex >= 3` 会触发 ObjC assert `bufferIndex N does not identify an argument buffer`
> 直接终止进程，无法 try-catch；故只测 0..2。

结论修正：之前“encodedLength==0 是 blocker”是误判。真实情况是——
**MSC 4.0 的 top-level argument buffer 不是 Metal 原生 argument buffer 结构**。
它由 MSC 自定义的描述符堆结构体组成（反编译可见 `struct.top_level_global_ab` /
`struct.res_desc_heap_ab` / `struct.smp_desc_heap_ab`），对 Metal 而言只是
一个普通 `[[buffer(2)]]` 传入的 struct 参数。因此 `newArgumentEncoderWithBufferIndex`
即使在正确的 buffer index 上也返回空 encoder（encodedLength==0）。

正确解法：**不走 MTLArgumentEncoder**，按 MSC 描述符堆的二进制布局手写描述符，
经 `setVertexBytes` / `setFragmentBytes` / `setBytes` 写入 `buffer(2)`。
buffer 路径（`UavDescriptor{GpuAddress, Length, Stride}`）已验证可用，
texture/sampler 路径需补齐（见下）。

## 描述符堆二进制布局（每条目 24 字节 = 3 × uint64）

> **权威来源已更正**：MSC 4.0 自带 runtime 头文件
> `/usr/local/include/metal_irconverter_runtime/metal_irconverter_runtime.h`
> 中的 `IRDescriptorTableEntry` 与 `IRDescriptorTableSetBuffer/SetTexture/SetSampler`。
> 此前基于 DXMT airconv `dxmt_context.cpp` 的布局表**字段映射错误**（airconv 与
> MSC 4.0 是两套不同的绑定模型，不可混用），已废弃。

### IRDescriptorTableEntry 通用结构（24B = 3×uint64）

```c
typedef struct IRDescriptorTableEntry {
    uint64_t gpuVA;          // +0
    uint64_t textureViewID;  // +8
    uint64_t metadata;       // +16
} IRDescriptorTableEntry;
```

### 各资源类型的字段语义（由 `IRDescriptorTableSet*` 定义）

| 资源类型 | +0 gpuVA | +8 textureViewID | +16 metadata |
|---------|----------|------------------|--------------|
| Buffer | `buffer.gpuAddress` | 0（typed buffer 才填 texture view 的 gpuResourceID） | `(size & 0xffffffff) << 0 \| (texViewOffset & 0xff) << 32 \| typedBuffer << 63` |
| Texture | **0** | **`texture.gpuResourceID`** | `(minLODClamp 的 float 位模式) \| ((uint64)metadata << 32)` |
| Sampler | **`sampler.gpuResourceID`** | 0 | `lodBias` 的 float 位模式 |

> **关键更正**：texture 的 `gpuResourceID` 在 **+8 字段(textureViewID)**，不是 +0。
> sampler 的 `gpuResourceID` 才在 +0。此前文档把 texture 的 gpuResourceID 放 +0 是错的，
> 导致采样返回 0（shader 在 +8 找不到有效 resourceID）。

### buffer index 分配（来自 `kIR*BindPoint` 常量）

```c
kIRDescriptorHeapBindPoint              = 0;  // res_desc_heap_ab（texture 描述符堆）
kIRSamplerHeapBindPoint                 = 1;  // smp_desc_heap_ab（sampler 描述符堆）
kIRArgumentBufferBindPoint              = 2;  // top_level_global_ab（GRS）
kIRArgumentBufferHullDomainBindPoint    = 3;
kIRArgumentBufferDrawArgumentsBindPoint = 4;
kIRArgumentBufferUniformsBindPoint      = 5;
kIRVertexBufferBindPoint                = 6;
```

### 实测绑定方式（已验证：`Phase10TextureSamplerDescriptorTests`）

对于**无 root constants 的简单 shader**（纯 Texture2D + SamplerState）：
- Metal reflection 显示 `top_level_global_ab` 在 buffer(2) `active=1`，
  `res_desc_heap_ab`(buffer0) / `smp_desc_heap_ab`(buffer1) `active=0`。
- **描述符条目直接平铺在 buffer(2) 的 top_level_global_ab 里**，
  按 MSC 反射 `TopLevelArgumentBuffer[i].EltOffset` 排列（texture@0, sampler@24）。
- 用 `setFragmentBytes(argBuf, 2)` 内联传入即可（48 字节 ≤ setBytes 上限）。
- texture 必须 `UseResource(texture, Read, Fragment)` 声明驻留；sampler 无需。

对于**有 root constants 或复杂 root signature 的 shader**，top_level_global_ab 可能
包含 root constants + 指向 res_desc_heap/smp_desc_heap 的指针，需配合
`IRRootSignatureCreateFromDescriptor` 生成的布局解析（待后续验证）。

### 关键前提（来自 `winemetal_unix.c` 与 MSC runtime 头文件）

- `MTLSamplerDescriptor.supportArgumentBuffers = YES`，否则 `[sampler gpuResourceID]` 返回 0
  （`bridge.m` 的 `MTLDevice_newSamplerState` 已硬编码 YES）
- texture 需具备 `MTLTextureUsageShaderRead` 才能取到非零 `gpuResourceID`
  （`bridge.m` 已暴露 `MTLTexture_gpuResourceID` / `MTLSamplerState_gpuResourceID`）

## 下一步（已更新）

1. ✅ bridge：`supportArgumentBuffers = YES`；`MTLTexture_gpuResourceID` / `MTLSamplerState_gpuResourceID`
2. ✅ C#：`IRDescriptorTableEntry` / `TextureDescriptor` / `SamplerDescriptor`（24B）+
   `MetalTexture.GpuResourceID` / `MetalSamplerState.GpuResourceID` +
   `ToTextureDescriptor` / `ToSamplerDescriptor` 便利扩展
3. ✅ 验证测试：`Phase10TextureSamplerDescriptorTests` —— 渲染全红 Texture2D，
   描述符堆路径采样，断言 RT 全红（redPixels=64/64）
4. ⬜ 实现 `ArgumentBufferEncoder`（按反射 `EltOffset` 序列化混合描述符表，
   自动处理 buffer/texture/sampler 的字段差异）
5. ⬜ 验证 root signature 场景（root constants + descriptor table 指针）的 top_level 布局
