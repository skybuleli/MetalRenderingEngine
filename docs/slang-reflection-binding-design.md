# Slang 反射与 Metal 资源绑定详细设计

> 基于 Slang 反射 JSON 格式 + Metal 绑定模型  
> 版本: 1.0 | 日期: 2026-06-17

---

## 1. 问题定义

### 1.1 要解决的核心问题

当 C# 运行时加载一个 `.metallib` 并创建 `MTLComputePipelineState` 之后，如何知道：

1. 这个着色器期望哪些资源？（Buffer、Texture、Sampler）
2. 每个资源应该绑在 Metal 的哪个 slot？（`[[buffer(N)]]`、`[[texture(N)]]`、`[[sampler(N)]]`）
3. 常量缓冲区的内部布局是什么？（字段偏移、大小、stride）
4. 线程组大小是多少？

### 1.2 为什么不能靠约定

| 着色器类型 | 参数数量 | 绑定复杂度 |
|-----------|---------|-----------|
| 简单 compute | 2-3 | 低 |
| PBR 材质 | 10+ (albedo, normal, metallic, roughness, ao, envMap, brdfLUT, shadowMap × N, lights CB) | 高 |
| 延迟渲染 GBuffer | 5 MRT + depth + 3 输入纹理 + CB | 很高 |

手动维护绑定表在超过 5 个着色器后即不可持续。

---

## 2. Slang 反射 JSON 格式

### 2.1 获取反射数据

```bash
# 编译的同时输出反射 JSON
slangc shader.slang -target dxil -profile sm_6_0 \
  -entry main -stage compute \
  -reflection-json shader.reflection.json \
  -o shader.dxil
```

### 2.2 JSON 结构（基于 Slang 测试用例实际输出）

```jsonc
{
    "parameters": [
        // --- 常量缓冲区 ---
        {
            "name": "PerFrameCB",
            "binding": {"kind": "constantBuffer", "index": 0},
            "type": {
                "kind": "constantBuffer",
                "elementType": {
                    "kind": "struct",
                    "name": "PerFrameConstants",
                    "fields": [
                        {
                            "name": "viewProj",
                            "type": {
                                "kind": "matrix",
                                "rowCount": 4,
                                "colCount": 4,
                                "elementType": {"kind": "scalar", "scalarType": "float32"}
                            },
                            "binding": {"kind": "uniform", "offset": 0, "size": 64, "elementStride": 0}
                        },
                        {
                            "name": "cameraPos",
                            "type": {
                                "kind": "vector",
                                "elementCount": 3,
                                "elementType": {"kind": "scalar", "scalarType": "float32"}
                            },
                            "binding": {"kind": "uniform", "offset": 64, "size": 12, "elementStride": 0}
                        },
                        {
                            "name": "time",
                            "type": {"kind": "scalar", "scalarType": "float32"},
                            "binding": {"kind": "uniform", "offset": 76, "size": 4, "elementStride": 0}
                        }
                    ]
                }
            }
        },
        // --- 只读纹理 ---
        {
            "name": "albedoMap",
            "binding": {"kind": "shaderResource", "index": 0},
            "type": {"kind": "resource", "baseShape": "texture2D"}
        },
        // --- 读写缓冲区 ---
        {
            "name": "outputBuffer",
            "binding": {"kind": "unorderedAccess", "index": 0},
            "type": {
                "kind": "resource",
                "baseShape": "structuredBuffer",
                "elementType": {"kind": "vector", "elementCount": 4, "elementType": {"kind": "scalar", "scalarType": "float32"}}
            }
        },
        // --- 采样器 ---
        {
            "name": "linearSampler",
            "binding": {"kind": "samplerState", "index": 0},
            "type": {"kind": "samplerState"}
        }
    ],
    "entryPoints": [
        {
            "name": "main",
            "stage": "compute",
            "threadGroupSize": [8, 8, 1]
        }
    ]
}
```

### 2.3 Binding Kind 枚举

| JSON `kind` | 含义 | D3D 对应 |
|-------------|------|---------|
| `constantBuffer` | 常量缓冲区 | `register(bN)` / CBV |
| `shaderResource` | 只读纹理/Buffer | `register(tN)` / SRV |
| `unorderedAccess` | 读写纹理/Buffer | `register(uN)` / UAV |
| `samplerState` | 采样器 | `register(sN)` |
| `uniform` | 结构体字段（在 CB 内部） | CB 成员 |
| `descriptorTableSlot` | D3D12 描述符表（带 space） | `register(bN, spaceM)` |

### 2.4 Type Kind 枚举

| JSON `kind` | 说明 | 附加字段 |
|-------------|------|---------|
| `scalar` | 标量 | `scalarType`: float32/int32/uint32 |
| `vector` | 向量 | `elementCount` |
| `matrix` | 矩阵 | `rowCount`, `colCount` |
| `array` | 数组 | `elementCount`, `uniformStride` |
| `struct` | 结构体 | `fields[]`, `name` |
| `constantBuffer` | 常量缓冲区包装 | `elementType` (struct) |
| `resource` | GPU 资源 | `baseShape`: texture2D/texture3D/structuredBuffer/byteAddressBuffer |
| `samplerState` | 采样器 | — |

---

## 3. Metal 绑定模型与 MSC 映射规则

### 3.1 Metal 的槽位空间

Metal 有三个独立的槽位空间：

| 空间 | 槽位数 | 对应 HLSL |
|------|--------|----------|
| `[[buffer(N)]]` | 31 (0-30) | CBV, SRV (buffer), UAV (buffer) |
| `[[texture(N)]]` | 128 (0-127) | SRV (texture), UAV (texture) |
| `[[sampler(N)]]` | 32 (0-31) | Sampler |

### 3.2 MSC (metal-shaderconverter) 的映射规则

MSC 将 DXIL 中的 D3D register 映射到 Metal 槽位，规则如下：

```
Metal [[buffer(N)]] 分配顺序：
1. CBV      → buffer(0), buffer(1), ...   (按 register(bN) 的 N 排序)
2. SRV-UAV  → buffer(CBV_COUNT), ...      (按 register(tN/uN) 合并排序)

Metal [[texture(N)]] 分配：
  SRV texture  → texture(0), texture(1), ...
  UAV texture  → 继续 texture(…)

Metal [[sampler(N)]] 分配：
  sampler      → sampler(0), sampler(1), ...   (按 register(sN))
```

**关键问题：** MSC 的 SRV-UAV buffer 映射不是 1:1 对应 register 索引的。MSC 会收集所有 buffer-typed SRV 和 UAV，按某种顺序排列到 buffer 空间中。

**我们的策略：不依赖 MSC 的隐式映射。强制使用确定性映射。**

### 3.3 确定性绑定策略

**方案：基于反射 JSON 计算确定性的 Metal 槽位分配。**

由于 MSC 的映射规则是确定性的，我们可以自己实现相同的算法：

```
算法：reflection_json_to_metal_slots(parameters)

inputs: Slang 反射 JSON 的 parameters 数组
outputs: { buffers[], textures[], samplers[] }

Step 1 — 收集所有 CBV（constantBuffer）
  cbv_params = filter(params, kind == "constantBuffer")
  sort cbv_params by binding.index
  for i, p in enumerate(cbv_params):
      slots.buffers.append({metal_slot: i, param: p})

Step 2 — 收集所有 buffer-typed SRV/UAV
  buffer_params = filter(params, kind in ("shaderResource","unorderedAccess") 
                         AND type.baseShape in ("structuredBuffer","byteAddressBuffer"))
  sort buffer_params by (kind priority, binding.index)
  for i, p in enumerate(buffer_params, start=cbv_count):
      slots.buffers.append({metal_slot: i, param: p})

Step 3 — 收集所有 texture-typed SRV/UAV
  tex_params = filter(params, kind in ("shaderResource","unorderedAccess") 
                      AND type.baseShape starts "texture")
  sort tex_params by (kind priority, binding.index)
  for i, p in enumerate(tex_params):
      slots.textures.append({metal_slot: i, param: p})

Step 4 — 收集所有 sampler
  sampler_params = filter(params, kind == "samplerState")
  sort sampler_params by binding.index
  for i, p in enumerate(sampler_params):
      slots.samplers.append({metal_slot: i, param: p})
```

**但这是脆弱的**——任何 MSC 版本升级都可能改变映射规则。

### 3.4 最终采用方案：Slang 显式 Metal 标注 + 构建时验证

#### 3.4.1 着色器作者侧（约定）

```hlsl
// 着色器中显式指定 Metal binding
// 语法：[[buffer(N)]], [[texture(N)]], [[sampler(N)]]

// 顶点着色器示例
[[vk::binding(0, 0)]] [[buffer(0)]]
ConstantBuffer<PerFrameCB> frameData;

[[vk::binding(0, 1)]] [[buffer(1)]]
ConstantBuffer<PerDrawCB> drawData;

[[vk::binding(0, 2)]] [[texture(0)]]
Texture2D<float4> albedoMap;

[[vk::binding(0, 3)]] [[sampler(0)]]
SamplerState linearSampler;
```

> **注意：** `[[buffer(n)]]` 是 Slang 的 Metal-specific 属性。Slang PR #11073 已支持 DescriptorHandle 的 `[[buffer(n)]]` 标注。对于全局参数，Slang 会在生成 Metal 输出时使用这些显式绑定。

#### 3.4.2 构建时验证

构建脚本在 MSC 之后，解析生成的 `.metallib` 中的绑定信息，与期望值对比。如果不匹配，构建失败。

```bash
# 使用 metal-shaderconverter 的 --dump 模式验证
metal-shaderconverter shader.dxil --dump-metal -o /dev/null 2>&1 | grep "buffer("
```

#### 3.4.3 运行时（C# 侧）

C# 从反射 JSON 中提取：
- 参数名（如 `"albedoMap"`）
- 参数类型（如 texture2D）
- **Metal 槽位号**：从 Slang 显式标注或 MSC 确定性映射中获取

然后在 dispatch 时按名称/槽位设置资源：

```csharp
// 反射驱动的绑定
var binding = shaderReflection.GetBinding("albedoMap");
encoder.SetTexture(albedoTexture, binding.MetalTextureSlot);

var cbBinding = shaderReflection.GetBinding("frameData");
encoder.SetBuffer(frameDataBuffer, 0, cbBinding.MetalBufferSlot);
```

### 3.5 MSC 4.0 实测发现（Phase 1 验证记录）

> 添加日期：2026-06-17 · 验证环境：macOS 26.4.1 / Apple M1 / metal-irconverter 4.0.0  
> 验证程序：`src/MetalRenderingEngine.Demo/Program.cs`

实际跑通最简 compute kernel（`RWStructuredBuffer<float>` ×2）后，MSC 的真实行为与 §3.1–3.4 的描述存在**三处偏差**，本节记录之，用于指导 Phase 4/5 工具链设计。

#### 3.5.1 反射 JSON 的 `Slot` 不是 Metal buffer 索引

`metal-shaderconverter --output-reflection-file` 输出的 reflection JSON：

```json
{
  "TopLevelArgumentBuffer": [
    { "EltOffset": 0, "Size": 24, "Slot": 0, "Type": "UAV" }
  ],
  "UsedResources": [
    { "bindingIndex": 0, "isInGRS": true, "tableStartIndex": 4294967295 }
  ]
}
```

字段 `Slot: 0` **不**代表"绑到 Metal `[[buffer(0)]]`"——它是 D3D12 root parameter slot。MSC 实际期望的 Metal buffer 索引由 `MTL_SHADER_VALIDATION=1 MTL_DEBUG_LAYER=1` 跑出来的错误信息揭示：

```
validateComputeFunctionArguments:1038: failed assertion 
'Compute Function(main): missing Buffer binding at index 2 
 for struct.top_level_global_ab[0].'
```

**MSC 4.0 实际把 top-level argument buffer 放在 `[[buffer(2)]]`**，把 `[[buffer(0)]]` / `[[buffer(1)]]` 保留给 push constants / draw arguments（与 Apple 系统约定一致）。这意味着 §3.4 的"使用 Slang 显式 `[[buffer(n)]]`"方案在经过 MSC 后会失效——MSC 会重写所有 buffer 索引。

#### 3.5.2 资源是描述符，不是 buffer 句柄

§3.1 的"Metal `[[buffer(N)]]` 槽位 ↔ HLSL 资源"对应关系不准确。MSC 的真实模型是：

```
[[buffer(2)]]  →  指向 argument buffer 的指针（大小 = 所有资源描述符之和）
                     ↓
                  argument buffer 内布局（按 reflection 的 EltOffset/Size）：
                     offset 0:  UavDescriptor { gpuAddress; length; stride }   // 24 B for structured
                     offset N:  下一个资源描述符
                     ...
```

每个 UAV/SRV 描述符的字节布局：

| 资源类型 | 字节数 | 布局（对应 reflection.Size） |
|---------|--------|------|
| **Structured UAV/SRV**（`RWStructuredBuffer`、`StructuredBuffer`） | 24 | `{u64 gpuAddress; u64 length; u64 stride}` |
| **Raw UAV/SRV**（`RWByteAddressBuffer`、`ByteAddressBuffer`） | 8 | `{u64 gpuAddress}`（猜测，待 Phase 3 验证） |
| **CBV**（`ConstantBuffer<T>`） | 8 | `{u64 gpuAddress}`（猜测，待 Phase 3 验证） |
| **Texture/Sampler** | ？ | 通过 `MTLArgumentEncoder`-friendly 描述符（待 Phase 3 验证） |

#### 3.5.3 必须 `useResource:` 让 GPU 驻留底层 buffer

由于 MSC 走的是 GPU 地址间接访问，仅 `setBytes` 写描述符**不够**——CPU 端绑定调用并未告诉 Metal 驱动"这个底层 buffer 也要驻留"，GPU 会在解引用 gpuAddress 时触发 page fault（或返回 0）。

正确流程：

```csharp
encoder.UseResource(dataBuffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
var desc = new UavDescriptor {
    GpuAddress = dataBuffer.GpuAddress,
    Length = dataBuffer.Length,
    Stride = sizeof(float),
};
encoder.SetBytes(desc, index: 2);
encoder.DispatchThreadgroups(groups, threads);
```

> 这与 DXMT 在 `references/dxmt/src/dxmt/dxmt_context.cpp:105` 的做法一致：把每个资源 `gpuAddress() + offset` 写到 entries 数组，再 `makeResident<>` 注册驻留。

#### 3.5.4 对工具链的影响

| 阶段 | 影响 |
|------|------|
| Phase 1 现状 | `Demo/Program.cs` 已硬编码 buffer index = 2 + 24 字节描述符跑通 |
| Phase 4（MSBuild 工具链） | `MetalShaderLoader` 必须解析 reflection JSON，并**额外通过一次 dry-run dispatch + validation 错误抓取**确认 MSC 实际偏移量；不能直接信任 `Slot` 字段 |
| Phase 5（C# 源生成器） | 生成的 `ShaderBindingContext`（§6）必须按"argument buffer 布局表"绑定，不再是直接 `setBuffer(buffer, offset, slot)` |
| Phase 6 引擎抽象层 | `IBuffer` 必须暴露 `GpuAddress`；`ICommandList.UseResource` 必须在抽象接口中存在（不可作为 Metal 后端私有实现） |

#### 3.5.5 后续验证清单

为了让 §3.4 的"显式标注 + 构建时验证"方案真正可行，还需要在 Phase 3/4 验证：

- [ ] 多个 UAV/SRV 共存时的 EltOffset 对齐规则（reflection 是否报告对齐）
- [ ] CBV 是 8 字节 gpuAddress 还是直接内嵌（function constants 模式？）
- [ ] Texture / Sampler 在 argument buffer 中的布局（是否使用 `MTLResourceID`？）
- [ ] 多 entry point（vertex + fragment）是否共用一个 argument buffer 或各自独立
- [ ] MSC 不同版本（4.0 → 未来）下 buffer 索引偏移的稳定性

---

## 4. C# 反射数据模型

### 4.1 类型定义

```csharp
// ShaderReflectionData.cs — 解析后的着色器反射信息

/// <summary>完整着色器反射数据</summary>
public class ShaderReflectionData
{
    public string EntryPointName { get; init; }
    public ShaderStage Stage { get; init; }
    public uint[] ThreadGroupSize { get; init; } // [x, y, z]
    public IReadOnlyList<ShaderParameter> Parameters { get; init; }
}

public enum ShaderStage { Compute, Vertex, Fragment, Mesh, Object }

public enum ParameterKind
{
    ConstantBuffer,     // CBV → Metal [[buffer(N)]]
    StructuredBuffer,   // SRV buffer → Metal [[buffer(N)]]
    RWStructuredBuffer, // UAV buffer → Metal [[buffer(N)]]
    Texture,            // SRV texture → Metal [[texture(N)]]
    RWTexture,          // UAV texture → Metal [[texture(N)]]
    Sampler,            // Sampler → Metal [[sampler(N)]]
}

public enum ScalarType { Float32, Int32, Uint32, Float16, Bool }

/// <summary>单个着色器参数</summary>
public class ShaderParameter
{
    public string Name { get; init; }
    public ParameterKind Kind { get; init; }
    public ShaderType Type { get; init; }

    // D3D 绑定
    public int RegisterIndex { get; init; }
    public int RegisterSpace { get; init; }

    // Metal 绑定（由分析阶段计算得出）
    public int MetalBufferSlot { get; init; }  // 仅对 buffer 类有效
    public int MetalTextureSlot { get; init; } // 仅对 texture 类有效
    public int MetalSamplerSlot { get; init; } // 仅对 sampler 类有效

    // 常量缓冲区内字段布局（仅对 ConstantBuffer 类的字段有效）
    public int UniformOffset { get; init; }
    public int UniformSize { get; init; }
}

/// <summary>着色器类型（递归）</summary>
public abstract class ShaderType { }

public class ScalarShaderType : ShaderType
{
    public ScalarType ScalarType { get; init; }
}

public class VectorShaderType : ShaderType
{
    public ScalarType ElementType { get; init; }
    public int ElementCount { get; init; }
}

public class MatrixShaderType : ShaderType
{
    public ScalarType ElementType { get; init; }
    public int RowCount { get; init; }
    public int ColCount { get; init; }
}

public class ArrayShaderType : ShaderType
{
    public ShaderType ElementType { get; init; }
    public int ElementCount { get; init; }
    public int Stride { get; init; } // uniformStride
}

public class StructField
{
    public string Name { get; init; }
    public ShaderType Type { get; init; }
    public int Offset { get; init; }
    public int Size { get; init; }
}

public class StructShaderType : ShaderType
{
    public string Name { get; init; }
    public IReadOnlyList<StructField> Fields { get; init; }
}

public class ConstantBufferShaderType : ShaderType
{
    public StructShaderType ElementType { get; init; }
}

public class ResourceShaderType : ShaderType
{
    public ResourceShape Shape { get; init; }
    public ShaderType? ElementType { get; init; }
}

public enum ResourceShape
{
    Texture1D, Texture2D, Texture3D, TextureCube,
    StructuredBuffer, ByteAddressBuffer
}
```

### 4.2 JSON 解析器

```csharp
// SlangReflectionParser.cs

public static class SlangReflectionParser
{
    public static ShaderReflectionData Parse(string reflectionJson, 
        MetalSlotAssigner slotAssigner)
    {
        using var doc = JsonDocument.Parse(reflectionJson);
        var root = doc.RootElement;

        var parameters = new List<ShaderParameter>();
        
        foreach (var param in root.GetProperty("parameters").EnumerateArray())
        {
            var paramName = param.GetProperty("name").GetString()!;
            var binding = param.GetProperty("binding");
            var bindingKind = binding.GetProperty("kind").GetString()!;
            
            var (kind, registerIndex) = ParseBindingKind(bindingKind, binding);
            var shaderType = ParseTypeRecursive(param.GetProperty("type"));
            var metalSlots = slotAssigner.Assign(kind, registerIndex, shaderType);
            
            parameters.Add(new ShaderParameter
            {
                Name = paramName,
                Kind = kind,
                Type = shaderType,
                RegisterIndex = registerIndex,
                MetalBufferSlot = metalSlots.BufferSlot,
                MetalTextureSlot = metalSlots.TextureSlot,
                MetalSamplerSlot = metalSlots.SamplerSlot,
            });
        }

        var entryPoint = root.GetProperty("entryPoints")[0];

        return new ShaderReflectionData
        {
            EntryPointName = entryPoint.GetProperty("name").GetString()!,
            Stage = ParseStage(entryPoint.GetProperty("stage").GetString()!),
            ThreadGroupSize = ParseThreadGroupSize(entryPoint),
            Parameters = parameters,
        };
    }

    private static ShaderType ParseTypeRecursive(JsonElement typeElement)
    {
        var kind = typeElement.GetProperty("kind").GetString();
        return kind switch
        {
            "scalar" => new ScalarShaderType
            {
                ScalarType = ParseScalarType(typeElement.GetProperty("scalarType").GetString()!)
            },
            "vector" => new VectorShaderType
            {
                ElementType = ParseScalarType(typeElement.GetProperty("elementType")
                    .GetProperty("scalarType").GetString()!),
                ElementCount = typeElement.GetProperty("elementCount").GetInt32(),
            },
            "matrix" => new MatrixShaderType
            {
                ElementType = ParseScalarType(typeElement.GetProperty("elementType")
                    .GetProperty("scalarType").GetString()!),
                RowCount = typeElement.GetProperty("rowCount").GetInt32(),
                ColCount = typeElement.GetProperty("colCount").GetInt32(),
            },
            "struct" => new StructShaderType
            {
                Name = typeElement.TryGetProperty("name", out var n) ? n.GetString()! : "",
                Fields = typeElement.GetProperty("fields").EnumerateArray()
                    .Select(f => new StructField
                    {
                        Name = f.GetProperty("name").GetString()!,
                        Type = ParseTypeRecursive(f.GetProperty("type")),
                        Offset = f.GetProperty("binding").GetProperty("offset").GetInt32(),
                        Size = f.GetProperty("binding").GetProperty("size").GetInt32(),
                    }).ToList(),
            },
            "constantBuffer" => new ConstantBufferShaderType
            {
                ElementType = (StructShaderType)ParseTypeRecursive(
                    typeElement.GetProperty("elementType"))
            },
            "resource" => new ResourceShaderType
            {
                Shape = ParseResourceShape(typeElement.GetProperty("baseShape").GetString()!),
            },
            "array" => new ArrayShaderType
            {
                ElementType = ParseTypeRecursive(typeElement.GetProperty("elementType")),
                ElementCount = typeElement.GetProperty("elementCount").GetInt32(),
                Stride = typeElement.TryGetProperty("uniformStride", out var s) 
                    ? s.GetInt32() : 0,
            },
            _ => throw new NotSupportedException($"Unknown type kind: {kind}")
        };
    }

    // ... parse helpers ...
}
```

---

## 5. Metal 槽位分配器

### 5.1 确定性分配算法

```csharp
// MetalSlotAssigner.cs

/// <summary>将 D3D 寄存器绑定映射到 Metal 槽位</summary>
public class MetalSlotAssigner
{
    private int _nextBufferSlot = 0;
    private int _nextTextureSlot = 0;
    private int _nextSamplerSlot = 0;

    /// <summary>分配一个参数的 Metal 槽位</summary>
    public MetalSlotResult Assign(ParameterKind kind, int registerIndex, ShaderType type)
    {
        return kind switch
        {
            ParameterKind.ConstantBuffer => new MetalSlotResult
            {
                BufferSlot = _nextBufferSlot++,
                TextureSlot = -1,
                SamplerSlot = -1,
            },
            ParameterKind.StructuredBuffer or ParameterKind.RWStructuredBuffer => new MetalSlotResult
            {
                BufferSlot = _nextBufferSlot++,
                TextureSlot = -1,
                SamplerSlot = -1,
            },
            ParameterKind.Texture or ParameterKind.RWTexture => new MetalSlotResult
            {
                BufferSlot = -1,
                TextureSlot = _nextTextureSlot++,
                SamplerSlot = -1,
            },
            ParameterKind.Sampler => new MetalSlotResult
            {
                BufferSlot = -1,
                TextureSlot = -1,
                SamplerSlot = _nextSamplerSlot++,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}

public readonly record struct MetalSlotResult(int BufferSlot, int TextureSlot, int SamplerSlot);
```

### 5.2 构建时验证

```bash
#!/bin/bash
# build/verify_metal_bindings.sh

SHADER_SLANG="$1"
SHADER_DXIL="$2"

# 1. 获取 Slang 反射
slangc "$SHADER_SLANG" -target dxil -profile sm_6_0 \
  -reflection-json /tmp/reflect.json -o "$SHADER_DXIL"

# 2. 用 C# 工具解析反射 + 计算 Metal slots
dotnet run --project tools/MetalSlotVerifier -- \
  --reflection /tmp/reflect.json \
  --expected-bindings expected_bindings.json

# 3. MSC 转换后验证
metal-shaderconverter "$SHADER_DXIL" \
  --dump-metal -o /dev/null 2>&1 | \
  dotnet run --project tools/MetalSlotVerifier -- \
  --verify-msc-output
```

---

## 6. 运行时绑定

### 6.1 自动绑定器

```csharp
// ShaderBindingContext.cs — 运行时着色器参数绑定上下文

public class ShaderBindingContext
{
    private readonly ShaderReflectionData _reflection;
    private readonly Dictionary<string, (MetalBuffer Buffer, nuint Offset)> _buffers = new();
    private readonly Dictionary<string, MetalTexture> _textures = new();
    private readonly Dictionary<string, MetalSamplerState> _samplers = new();
    private readonly Dictionary<string, byte[]> _pushConstants = new();

    /// <summary>按参数名设置缓冲区</summary>
    public void SetBuffer(string name, MetalBuffer buffer, nuint offset = 0)
    {
        if (!_reflection.Parameters.Any(p => p.Name == name))
            throw new ArgumentException($"Shader has no parameter '{name}'");
        _buffers[name] = (buffer, offset);
    }

    /// <summary>按参数名设置纹理</summary>
    public void SetTexture(string name, MetalTexture texture) { ... }
    
    /// <summary>按参数名设置采样器</summary>
    public void SetSampler(string name, MetalSamplerState sampler) { ... }

    /// <summary>将所有绑定的资源应用到编码器</summary>
    public void ApplyTo(MetalComputeEncoder encoder)
    {
        foreach (var param in _reflection.Parameters)
        {
            switch (param.Kind)
            {
                case ParameterKind.ConstantBuffer:
                    if (_buffers.TryGetValue(param.Name, out var cb))
                        encoder.SetBuffer(cb.Buffer, cb.Offset, param.MetalBufferSlot);
                    else
                        throw new InvalidOperationException(
                            $"ConstantBuffer '{param.Name}' not bound");
                    break;

                case ParameterKind.StructuredBuffer:
                case ParameterKind.RWStructuredBuffer:
                    if (_buffers.TryGetValue(param.Name, out var sb))
                        encoder.SetBuffer(sb.Buffer, sb.Offset, param.MetalBufferSlot);
                    else
                        throw new InvalidOperationException(
                            $"Buffer '{param.Name}' not bound");
                    break;

                case ParameterKind.Texture:
                case ParameterKind.RWTexture:
                    if (_textures.TryGetValue(param.Name, out var tex))
                        encoder.SetTexture(tex, param.MetalTextureSlot);
                    break;

                case ParameterKind.Sampler:
                    if (_samplers.TryGetValue(param.Name, out var sam))
                        encoder.SetSampler(sam, param.MetalSamplerSlot);
                    break;
            }
        }
    }
}
```

### 6.2 使用示例

```csharp
// 加载着色器
var metallib = File.ReadAllBytes("shaders/pbr_standard.metallib");
var library = device.NewLibrary(metallib);
var function = library.NewFunction("pbr_fragment");
var pso = device.NewRenderPipeline(vertexFunc, function, pipelineDesc);

// 加载反射数据（构建时生成，嵌入资源）
var reflectionJson = File.ReadAllText("shaders/pbr_standard.reflection.json");
var reflection = SlangReflectionParser.Parse(reflectionJson, new MetalSlotAssigner());

// 运行时绑定
var bindings = new ShaderBindingContext(reflection);
bindings.SetBuffer("frameData", frameDataBuffer);
bindings.SetTexture("albedoMap", albedoTexture);
bindings.SetTexture("normalMap", normalTexture);
bindings.SetTexture("metallicRoughnessMap", metallicRoughnessTexture);
bindings.SetSampler("linearSampler", linearSampler);

// 编码 + 绘制
var encoder = cmdBuf.CreateRenderEncoder(renderPass);
encoder.SetPipeline(pso);
bindings.ApplyTo(encoder); // 自动设置所有绑定！
encoder.DrawPrimitives(...);
encoder.EndEncoding();
cmdBuf.Commit();
```

---

## 7. 构建管线集成

### 7.1 MSBuild Target

```xml
<!-- ShaderCompile.targets -->
<Target Name="CompileSlangShaders" BeforeTargets="BeforeBuild">
    <ItemGroup>
        <SlangShader Include="Shaders/**/*.slang" />
    </ItemGroup>

    <!-- Step 1: Slang → DXIL + Reflection JSON -->
    <Exec Command="slangc %(SlangShader.Identity) 
        -target dxil -profile sm_6_0 
        -entry main -stage Compute
        -reflection-json $(IntermediateOutputPath)%(Filename).reflect.json
        -o $(IntermediateOutputPath)%(Filename).dxil" />

    <!-- Step 2: DXIL → metallib -->
    <Exec Command="metal-shaderconverter 
        $(IntermediateOutputPath)%(Filename).dxil 
        -o $(OutDir)Shaders/%(Filename).metallib" />

    <!-- Step 3: 复制反射 JSON 到输出 -->
    <Copy SourceFiles="$(IntermediateOutputPath)%(Filename).reflect.json"
          DestinationFolder="$(OutDir)Shaders/" />

    <!-- Step 4: 可选——嵌入资源 -->
    <ItemGroup>
        <EmbeddedResource Include="$(OutDir)Shaders/*.metallib" />
        <EmbeddedResource Include="$(OutDir)Shaders/*.reflect.json" />
    </ItemGroup>
</Target>
```

### 7.2 着色器项目结构

```
MetalRenderingEngine.Shaders/
├── Compute/
│   ├── Mandelbrot.slang
│   ├── GPUCulling.slang
│   └── GenerateMipmaps.slang
├── Render/
│   ├── PBR/
│   │   ├── PBR.vert.slang
│   │   ├── PBR.frag.slang
│   │   └── PBR.bindings.json     # 可选的绑定描述文件
│   ├── ShadowMap/
│   │   ├── ShadowMap.vert.slang
│   │   └── ShadowMap.frag.slang
│   ├── PostProcess/
│   │   ├── Bloom.slang
│   │   ├── Tonemap.slang
│   │   └── FullscreenQuad.vert.slang
│   └── UI/
│       ├── ImGui.vert.slang
│       └── ImGui.frag.slang
└── Include/
    ├── CommonTypes.slang          # 共享类型定义
    ├── Lighting.slang             # 光照函数
    └── Bindings.slang             # 统一的绑定约定
```

### 7.3 统一绑定约定文件

```hlsl
// Include/Bindings.slang — 项目级绑定约定

// Buffer slots
#define SLOT_CB_PER_FRAME       [[buffer(0)]]
#define SLOT_CB_PER_DRAW        [[buffer(1)]]
#define SLOT_CB_PER_MATERIAL    [[buffer(2)]]
#define SLOT_SB_LIGHTS          [[buffer(3)]]
#define SLOT_UAV_OUTPUT         [[buffer(4)]]

// Texture slots
#define SLOT_TEX_ALBEDO         [[texture(0)]]
#define SLOT_TEX_NORMAL         [[texture(1)]]
#define SLOT_TEX_METALLIC_ROUGH [[texture(2)]]
#define SLOT_TEX_AO             [[texture(3)]]
#define SLOT_TEX_SHADOWMAP      [[texture(4)]]
#define SLOT_TEX_ENVMAP         [[texture(5)]]
#define SLOT_TEX_BRDF_LUT       [[texture(6)]]

// Sampler slots
#define SLOT_SAMPLER_LINEAR     [[sampler(0)]]
#define SLOT_SAMPLER_POINT      [[sampler(1)]]
#define SLOT_SAMPLER_SHADOW     [[sampler(2)]]
```

---

## 8. 扩展到未来 C# 源生成器

当 C# 源生成器就绪后，上述绑定流程可以完全自动化：

```csharp
// 未来的 C# 着色器代码
[Shader(BindingConvention = BindingConvention.PBRStandard)]
public readonly partial struct PBRPixelShader : IFragmentShader
{
    [ConstantBuffer(Slot = 0)]  // → buffer(0)
    public PerFrameCB FrameData;

    [ConstantBuffer(Slot = 1)]  // → buffer(1)
    public PerDrawCB DrawData;

    [Texture(Slot = 0)]         // → texture(0)
    public Texture2D<float4> AlbedoMap;

    [Texture(Slot = 1)]         // → texture(1)
    public Texture2D<float4> NormalMap;

    [Sampler(Slot = 0)]         // → sampler(0)
    public SamplerState LinearSampler;

    [StageInput]
    public float2 UV;
    [StageInput]
    public float3 WorldNormal;

    [StageOutput(Slot = 0)]
    public float4 OutColor;

    public void Execute()
    {
        // 着色器逻辑...
    }
}

// 源生成器自动生成：
// 1. Slang 着色器代码（含显式 [[buffer(n)]] 标注）
// 2. ShaderReflectionData（含 Metal slot 映射）
// 3. 类型安全的绑定类
//    public sealed class PBRPixelShaderBindings : ShaderBindings
//    {
//        public BufferBinding<PerFrameCB> FrameData { get; }
//        public TextureBinding AlbedoMap { get; }
//        ...
//    }
```

---

## 9. 依赖总结

| 组件 | 文件 | 职责 |
|------|------|------|
| `SlangReflectionParser` | ~300行 C# | 解析反射 JSON → 强类型数据模型 |
| `MetalSlotAssigner` | ~100行 C# | D3D register → Metal slot 映射 |
| `ShaderBindingContext` | ~200行 C# | 运行时自动绑定所有资源 |
| `Include/Bindings.slang` | ~30行 Slang | 统一绑定约定 |
| MSBuild `.targets` | ~40行 XML | 构建管线集成 |
