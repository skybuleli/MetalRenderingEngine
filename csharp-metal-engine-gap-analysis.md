# C# → Metal 游戏引擎功能覆盖分析

> 对照一般游戏引擎的功能清单，分析本框架的覆盖度、主要阻碍点、需要完善的模块。

---

## 1. 总览矩阵

| 功能域 | 覆盖状态 | 阻碍类型 | 优先级 |
|--------|---------|---------|--------|
| 顶点/片元着色器 | 🟡 蓝图已设计 | Bridge 需实现 | P1 |
| 计算着色器 | 🟡 蓝图已设计 | 最小链路可跑 | P1 |
| 多 Render Target (MRT) | 🔴 未覆盖 | Bridge 需支持 | P1 |
| Render-to-Texture | 🔴 未覆盖 | Bridge + 架构 | P1 |
| 常量缓冲区 (UBO) | 🟡 蓝图提及 | 需完整布局方案 | P1 |
| 纹理采样 | 🟡 蓝图提及 | Bridge 需支持 | P1 |
| 深度/模板测试 | 🔴 未覆盖 | Bridge 需支持 | P1 |
| 混合 (Blending) | 🔴 未覆盖 | Bridge 需支持 | P1 |
| 输入布局 (Vertex Descriptor) | 🔴 未覆盖 | 需 Metal 顶点描述符 | P1 |
| Sampler 状态 | 🟡 蓝图提及 | Bridge 需支持 | P1 |
| 实例化绘制 | 🔴 未覆盖 | 纯 Metal API 调用 | P2 |
| 间接绘制 (GPU Driven) | 🔴 未覆盖 | 需 ICB 或间接 Buffer | P2 |
| 多 Pass 渲染管线 | 🔴 未覆盖 | 架构设计 | P2 |
| Mipmap 生成 | 🔴 未覆盖 | 需 Blit Encoder | P2 |
| MSAA / 抗锯齿 | 🔴 未覆盖 | Bridge 需支持 | P2 |
| 后处理管线 | 🔴 未覆盖 | 依赖 MRT + Compute | P2 |
| Shadow Map | 🔴 未覆盖 | 依赖 RTT + 深度 | P2 |
| 延迟渲染 (Deferred) | 🔴 未覆盖 | 依赖 MRT + 多 Pass | P3 |
| 材质系统 | 🔴 未覆盖 | 纯 C# 工程 | P3 |
| 场景图 / ECS | 🔴 未覆盖 | 纯 C# 工程 | P3 |
| 视锥剔除 / LOD | 🔴 未覆盖 | 纯 C# + Compute | P3 |
| 骨骼动画 (GPU Skinning) | 🔴 未覆盖 | Compute Shader | P3 |
| Geometry Shader 替代 | 🔴 需绕行 | Metal 无 GS | P3 |
| Tessellation | 🔴 需绕行 | Metal 曲面细分与 D3D 不同 | P3 |
| Mesh Shader (Metal 4) | 🔴 未覆盖 | 需要新 API 绑定 | P4 |
| Ray Tracing | 🔴 未覆盖 | M1 性能有限 | P4 |
| 粒子系统 (GPU Particles) | 🔴 未覆盖 | Compute + Indirect Draw | P3 |
| 地形渲染 | 🔴 未覆盖 | 依赖纹理 + Compute | P3 |
| 天空盒 / 环境贴图 | 🔴 未覆盖 | 依赖 Cubemap | P3 |
| UI 渲染 (ImGUI 风格) | 🔴 未覆盖 | 纯渲染管线 | P4 |
| 字体渲染 (SDF) | 🔴 未覆盖 | 依赖纹理 + 着色器 | P4 |
| 资源异步加载 | 🔴 未覆盖 | 纯 C# 工程 | P4 |
| 帧图 (Frame Graph) | 🔴 未覆盖 | 架构设计 | P4 |
| GPU 性能分析 / Debug | 🔴 未覆盖 | 需 Metal Capture | P5 |
| 音频 | 🔴 未覆盖 | 独立系统，不阻塞 | P5 |
| 物理 | 🔴 未覆盖 | 独立系统，不阻塞 | P5 |
| 网络 | 🔴 未覆盖 | 独立系统，不阻塞 | P5 |

---

## 2. 关键阻碍点分析

### 2.1 🚨 最大阻碍：Metal 无 Geometry Shader

**问题：** Metal 没有 Geometry Shader 阶段。许多引擎功能依赖 GS（如 Shadow Cube Map 一次性渲染 6 面、粒子四边形扩展、体素化等）。

**影响的功能：**
- Shadow Cube Map（传统做法是 GS 输出到 6 个 layer）
- 粒子系统（GS 扩展点 → 四边形）
- 线框渲染
- 体素化
- 某些后处理效果

**解决方案（按难度排序）：**

| 方案 | 难度 | 说明 |
|------|------|------|
| **Mesh Shader 替代** | 低（Metal 4+） | Metal 4 支持 Mesh Shader，天然替代 GS。但需要 macOS 15+/M1+ |
| **Compute Shader 模拟** | 中 | kk 的方案：用 Compute Shader 实现 GS 阶段，写入中间 buffer，然后间接绘制 |
| **CPU 端预处理** | 高 | 对简单场景可行，复杂场景性能灾难 |
| **Instance 多 Draw Call** | 低 | Shadow Cube Map 可用 6 次 instanced draw 替代 1 次 GS draw |

**建议：** 采用组合策略——对 Cube Map 用 Instance Draw，对复杂 GS 场景用 Compute 模拟（参考 kk 的方案）。

### 2.2 🚨 第二大阻碍：Metal 的资源绑定模型

**问题：** Metal 使用 Argument Buffer + slot-based binding，与 HLSL 的 `register(b0)` / `register(t0)` 模型有显著差异。MSC (metal-shaderconverter) 会做自动映射，但映射结果需要运行时正确配合。

**影响的功能：**
- 材质系统（大量纹理 + 常量的高效绑定）
- Bindless 资源（Argument Buffer Tier 2）
- 着色器变体管理

**关键差距：**

```
HLSL/D3D12 模型：
  CBV(b0) | SRV(t0,t1,t2) | UAV(u0) | Sampler(s0,s1)

Metal 模型：
  [[buffer(0)]] [[buffer(1)]] ... [[texture(0)]] ... [[sampler(0)]]
```

MSC 会将 HLSL 的 register 映射到 Metal slot，但映射规则需要对每种着色器类型验证。**我们必须在 bridge 层清楚知道每个着色器的绑定布局。**

**解决方案：**
- 方案 A（短期）：手动管理绑定布局——每种着色器类型附带一个 JSON 元数据描述其绑定
- 方案 B（中期）：在 Slang 编译时输出反射信息（Slang 支持 `-dump-reflection`）
- 方案 C（长期）：在 C# 源生成器中直接生成绑定代码

### 2.3 🚨 第三大阻碍：缺失的 Blit/复制/Dispatch 操作

**问题：** 我们的 bridge 目前只设计了 Compute Encoder 和 Render Encoder。缺少 Blit Encoder 和直接的内存操作。

**影响的功能：**
- Mipmap 生成（需要 `generateMipmapsForTexture:`）
- 纹理复制/区域更新
- Buffer 间拷贝
- GPU 计数器解析

**需要添加到 bridge.h 的函数：**

```objc
// Blit Encoder
void* metal_blit_command_encoder(void* cmd_buf);
void  metal_blit_encoder_copy_buffer(void* enc, void* src, size_t src_off, void* dst, size_t dst_off, size_t size);
void  metal_blit_encoder_copy_texture(void* enc, void* src_tex, void* dst_tex, ...);
void  metal_blit_encoder_generate_mipmaps(void* enc, void* texture);
void  metal_blit_encoder_fill_buffer(void* enc, void* buffer, size_t offset, size_t size, uint8_t value);
void  metal_blit_encoder_end_encoding(void* enc);
```

### 2.4 🟡 中等阻碍：着色器反射与绑定自动化

**问题：** 当前蓝图中，C# 端需要手动知道每个着色器接受了哪些 buffer/texture。这在着色器数量增多后不可维护。

**Slang 反射方案：**

```bash
# Slang 可以输出反射 JSON
slangc shader.slang -target dxil -dump-reflection -o shader.dxil

# 反射输出包括：
# - 参数列表（名称、类型、binding slot、大小）
# - 线程组大小
# - 入口点信息
# - 纹理/采样器绑定
```

**需要构建的模块：**
- `ShaderReflection.cs`：解析 Slang 反射 JSON → C# 类型化绑定信息
- `ShaderBindingLayout.cs`：根据反射信息计算 Metal buffer/texture/sampler 的 slot 分配
- `MaterialBinder.cs`：自动设置 encoder 的 buffer/texture/sampler

### 2.5 🟡 中等阻碍：Metal 纹理压缩格式

**问题：** D3D 常用 BC1-BC7，Metal 在 Apple Silicon 上**可选**支持 BC，但强制支持 ASTC。

| 格式 | Metal (Apple Silicon) | 说明 |
|------|----------------------|------|
| BC1-BC3 (DXT1-5) | ✅ 支持（M1+） | 传统 D3D 格式 |
| BC4-BC7 | ✅ 支持（M1+） | 高精度压缩 |
| ASTC 4×4 → 12×12 | ✅ 强制支持 | Apple 首选，质量更好 |
| ETC2 / EAC | ❌ 不支持 | OpenGL ES 格式 |

**需要构建的模块：**
- 纹理加载器（PNG、JPEG、HDR 等）
- 运行时压缩器（如果需要在构建时转换格式）
- 纹理格式映射表（引擎格式 → Metal MTLPixelFormat）

### 2.6 🟡 中等阻碍：渲染管线状态管理

**问题：** Metal 的 PSO (Pipeline State Object) 创建是昂贵的，必须在初始化时预创建。运行时切换 PSO 有开销。

**需要构建的模块：**

- `PipelineCache`：PSO 缓存（keyed by 着色器组合 + 状态组合）
- `PipelineKey`：描述一个完整 PSO 的哈希键
- 延迟 PSO 创建（异步编译）
- Metal Binary Archive 支持（`MTLBinaryArchive` 可跨运行持久化 PSO）

此外，Metal 缺少传统的独立 RasterizerState / DepthStencilState / BlendState 对象（虽然可以用 MTLDepthStencilState，但光栅化状态在 PSO 创建时锁定）。这需要我们在 C# 层自己设计状态组合策略。

---

## 3. 需要完善/新增的模块

### 3.1 bridge.m 扩展（必须）

| 新增 API | 行数估计 | 用途 |
|----------|---------|------|
| Blit Encoder 全套 | ~40 行 | Mipmap、拷贝、填充 |
| 渲染管线描述符完整版 | ~30 行 | 混合、深度、模板、光栅化 |
| 顶点描述符 | ~30 行 | 输入布局 |
| 间接绘制 | ~20 行 | GPU Driven |
| Argument Buffer | ~30 行 | 高效资源绑定 |
| Binary Archive | ~20 行 | PSO 缓存持久化 |
| 纹理区域更新 | ~20 行 | 动态纹理 |
| **合计新增** | **~190 行** | |

### 3.2 C# 引擎层新模块

| 模块 | 说明 | 优先级 |
|------|------|--------|
| `ShaderReflection` | 解析 Slang 反射 JSON | P1 |
| `ShaderBindingLayout` | 计算 Metal slot 布局 | P1 |
| `BlitCommandEncoder` | Blit 操作封装 | P1 |
| `RenderPassBuilder` | 构建 MTLRenderPassDescriptor | P1 |
| `PipelineCache` | PSO 缓存 + Binary Archive | P2 |
| `RenderGraph` (Frame Graph) | 自动管理多 Pass 依赖和资源 barrier | P3 |
| `MaterialSystem` | 材质参数绑定 | P3 |
| `MeshLoader` (glTF) | 模型加载 | P3 |
| `TextureLoader` | 纹理加载 + 格式转换 | P3 |
| `GeometryShaderEmulator` | GS → Compute/Mesh 适配层 | P3 |
| `GPUCulling` | 基于 Compute 的视锥/遮挡剔除 | P3 |
| `PostProcessStack` | Bloom、Tonemapping 等 | P3 |
| `AnimationSystem` (GPU) | 骨骼动画 Compute | P3 |
| `ImGuiRenderer` | 调试 UI 渲染 | P4 |

### 3.3 着色器库（示例着色器）

| 着色器 | 用途 |
|--------|------|
| FullscreenQuad | 后处理基础 |
| PBR Standard | 标准 PBR 材质 |
| ShadowMap | 阴影贴图生成 |
| Skybox | 天空盒 |
| GPU Skinning | 骨骼动画 |
| Bloom | 后处理 |
| SDF Text | 文字渲染 |

---

## 4. 风险清单

| 风险 | 严重度 | 缓解措施 |
|------|--------|---------|
| MSC 转换质量不确定（Wave→SIMD、资源绑定） | 🔴 高 | Phase 1 即验证；参考 kk 的子群限制清单；备选 Path B（DXMT airconv） |
| MTL4Compiler 并发崩溃 | 🟡 中 | kk 已验证：全局单例 + autoreleasepool |
| Metal 无 GS → 功能缺失 | 🟡 中 | Mesh Shader 替代（Metal 4）或 Compute 模拟 |
| Slang 反射信息不完整 | 🟡 中 | 验证反射输出；备选：手动绑定描述文件 |
| M1 8GB 显存不足（大场景） | 🟢 低 | 初期场景规模可控；UMA 下可溢出到系统内存 |
| bridge.m 内存泄漏 | 🟡 中 | Phase 1 即建立泄漏测试；SafeHandle 保证确定性释放 |

---

## 5. 修订后的实施路线图

### Phase 1：Compute 最小链路（不变）
→ 验证 bridge.m + P/Invoke + Slang→metallib 端到端可用

### Phase 2：基础渲染（扩展）
在原有基础上增加：
- [ ] 深度/模板状态
- [ ] 混合状态
- [ ] 顶点描述符（输入布局）
- [ ] MRT（多 Render Target）
- [ ] Render-to-Texture

### Phase 3：Blit + 纹理系统（新增）
- [ ] Blit Encoder（mipmap、拷贝、填充）
- [ ] 纹理加载器
- [ ] 纹理格式映射
- [ ] Sampler 状态管理

### Phase 4：着色器工具链完善（位移）
- [ ] Slang 反射解析器
- [ ] 自动绑定布局生成
- [ ] Pipeline Cache + Binary Archive
- [ ] MSBuild 集成

### Phase 5：引擎基础功能（新增）
- [ ] RenderPass 构建器
- [ ] 多 Pass 渲染
- [ ] Shadow Map
- [ ] 后处理基础（Bloom、Tonemapping）
- [ ] 材质系统初版

### Phase 6：进阶功能
- [ ] GS 模拟（Mesh Shader / Compute）
- [ ] GPU Culling
- [ ] 骨骼动画
- [ ] Frame Graph
- [ ] C# 源生成器
