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

## 新发现：当前 blocker

测试位置：
- `tests/MetalRenderingEngine.Tests/Phase10TextureSamplerRuntimeTests.cs`

实测结果：
- `reflect.json` 中清楚包含 `Texture2D + SamplerState` 两个条目
- 但从 MSC 产物得到的 `MTLFunction` 调 `newArgumentEncoderWithBufferIndex(...)` 后，
  当前拿到的 `encodedLength == 0`

这意味着当前 blocker 不是“C# 侧还没写 helper”，而是：
- 现有 `MTLArgumentEncoder` bridge 还不足以从 MSC metallib 取回可用布局
- 在这个问题搞清前，`ArgumentBufferEncoder.cs` 只能停留在 buffer-like 资源，不能贸然宣称支持 texture/sampler

## 下一步

最小下一步：
1. 再补一条运行时验证，确认 texture / sampler 混合资源不仅能反射，还能实际取样。
2. 在此基础上再实现 `ArgumentBufferEncoder`，不要反过来。
