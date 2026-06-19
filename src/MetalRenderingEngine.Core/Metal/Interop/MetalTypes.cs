using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MetalRenderingEngine.Metal.Interop;

/// <summary>
/// Buffer 创建参数。字段顺序与 native/bridge.h 中 <c>WMTBufferInfo</c> 严格一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTBufferInfo
{
    /// <summary>字节数。</summary>
    public ulong Length;
    /// <summary><see cref="MTLResourceOptions"/> 位或后的 uint32。</summary>
    public uint Options;
    /// <summary>对齐填充，必须为 0。</summary>
    public uint Reserved;
}

/// <summary>
/// 三维尺寸；对应 MTLSize / native/bridge.h 中 <c>WMTSize</c>。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTSize
{
    public ulong Width;
    public ulong Height;
    public ulong Depth;

    public WMTSize(ulong w, ulong h, ulong d) { Width = w; Height = h; Depth = d; }
}

/// <summary>
/// 渲染管线颜色附件描述（C 端 <c>WMTColorAttachment</c>）。
/// Phase 3.5 新增 blend 字段：36 字节 = 9 int。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTColorAttachment
{
    public int PixelFormat;
    public int WriteMask;
    public int BlendingEnabled;
    public int SrcRgbBlendFactor;
    public int DstRgbBlendFactor;
    public int SrcAlphaBlendFactor;
    public int DstAlphaBlendFactor;
    public int RgbBlendOp;
    public int AlphaBlendOp;
}

/// <summary>
/// 8 个 WMTColorAttachment 的内联数组封装（.NET 10 InlineArray）。
/// 内存布局与 C 端 <c>WMTColorAttachment[8]</c> 一致（8 × 36 = 288 字节）。
/// </summary>
[InlineArray(8)]
public struct WMTColorAttachmentBuffer8
{
    private WMTColorAttachment _e0;
}

/// <summary>
/// 渲染管线整体描述（C 端 <c>WMTRenderPipelineDesc</c>）。
/// 使用 InlineArray 替代 fixed byte[]，提供安全的索引访问。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTRenderPipelineDesc
{
    /// <summary>8 个颜色附件的内联数组（0..ColorCount-1 有效）。</summary>
    public WMTColorAttachmentBuffer8 Colors;
    public int ColorCount;
    public int DepthPixelFormat;
    public int StencilPixelFormat;
    public int SampleCount;
    public WMTVertexDescriptor VertexDescriptor;  // Phase 7F
}

/// <summary>WMTRenderPipelineDesc 扩展方法（结构体方法不能 ref 返回 this 字段，CS8170）。</summary>
public static class WMTRenderPipelineDescExtensions
{
    /// <summary>通过索引访问颜色附件。</summary>
    public static ref WMTColorAttachment ColorAttachmentAt(this ref WMTRenderPipelineDesc desc, int i)
        => ref desc.Colors[i];
}

/// <summary>
/// Phase 7I: WMTRenderPipelineDesc 的 fluent builder。
/// MRT (≤8 color attachments) + depth/stencil + vertex descriptor 的链式构造。
/// </summary>
public sealed class RenderPipelineDescBuilder
{
    private WMTRenderPipelineDesc _desc = new()
    {
        ColorCount = 0,
        SampleCount = 1,
    };

    /// <summary>添加一个颜色附件（返回 this 以链式调用）。</summary>
    public RenderPipelineDescBuilder WithColorAttachment(int index, MTLPixelFormat format)
    {
        if (index is < 0 or >= 8) throw new ArgumentOutOfRangeException(nameof(index));
        ref var att = ref _desc.ColorAttachmentAt(index);
        att.PixelFormat = (int)format;
        att.WriteMask = 0xF;  // RGBA 全写
        if (index + 1 > _desc.ColorCount) _desc.ColorCount = index + 1;
        return this;
    }

    /// <summary>添加带 blend 的颜色附件。</summary>
    public RenderPipelineDescBuilder WithBlendedColorAttachment(int index, MTLPixelFormat format,
        MTLBlendFactor srcRgb = MTLBlendFactor.SourceAlpha,
        MTLBlendFactor dstRgb = MTLBlendFactor.OneMinusSourceAlpha)
    {
        WithColorAttachment(index, format);
        ref var att = ref _desc.ColorAttachmentAt(index);
        att.BlendingEnabled = 1;
        att.SrcRgbBlendFactor = (int)srcRgb;
        att.DstRgbBlendFactor = (int)dstRgb;
        att.SrcAlphaBlendFactor = (int)MTLBlendFactor.One;
        att.DstAlphaBlendFactor = (int)MTLBlendFactor.OneMinusSourceAlpha;
        att.RgbBlendOp = (int)MTLBlendOperation.Add;
        att.AlphaBlendOp = (int)MTLBlendOperation.Add;
        return this;
    }

    /// <summary>设置深度附件像素格式。</summary>
    public RenderPipelineDescBuilder WithDepth(MTLPixelFormat format)
    {
        _desc.DepthPixelFormat = (int)format;
        return this;
    }

    /// <summary>设置模板附件像素格式。</summary>
    public RenderPipelineDescBuilder WithStencil(MTLPixelFormat format)
    {
        _desc.StencilPixelFormat = (int)format;
        return this;
    }

    /// <summary>设置采样数（MSAA）。</summary>
    public RenderPipelineDescBuilder WithSampleCount(int sampleCount)
    {
        _desc.SampleCount = sampleCount;
        return this;
    }

    /// <summary>设置顶点描述符。</summary>
    public RenderPipelineDescBuilder WithVertexDescriptor(in WMTVertexDescriptor vertexDescriptor)
    {
        _desc.VertexDescriptor = vertexDescriptor;
        return this;
    }

    /// <summary>构建不可变的描述符副本。</summary>
    public WMTRenderPipelineDesc Build() => _desc;
}

/// <summary>
/// 清除颜色（C 端 <c>WMTClearColor</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTClearColor
{
    public float R, G, B, A;
    public WMTClearColor(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
}

/// <summary>
/// Render pass 单个附件描述（C 端 <c>WMTRenderPassAttachment</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTRenderPassAttachment
{
    public nuint Texture;
    public int LoadAction;
    public int StoreAction;
    public WMTClearColor ClearColor;
    public float ClearDepth;
    public int ClearStencil;
    /// <summary>Phase 7K: MSAA resolve target（StoreAction=MultisampleResolve 时使用）。</summary>
    public nuint ResolveTexture;
}

/// <summary>
/// 8 个 WMTRenderPassAttachment 的内联数组封装（.NET 10 InlineArray）。
/// 内存布局与 C 端一致（8 × 40 = 320 字节）。
/// </summary>
[InlineArray(8)]
public struct WMTRenderPassAttachmentBuffer8
{
    private WMTRenderPassAttachment _e0;
}

/// <summary>
/// Render pass 整体描述（C 端 <c>WMTRenderPassDesc</c>）。
/// 使用 InlineArray 替代 fixed byte[]，提供安全的访问器。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTRenderPassDesc
{
    /// <summary>8 个颜色附件描述（通过 ColorAt/SetColorAt 安全访问）。</summary>
    public WMTRenderPassAttachmentBuffer8 Colors;
    public WMTRenderPassAttachment Depth;
    public WMTRenderPassAttachment Stencil;
}

/// <summary>WMTRenderPassDesc 扩展方法（结构体方法不能 ref 返回 this 字段，CS8170）。</summary>
public static class WMTRenderPassDescExtensions
{
    /// <summary>安全获取颜色附件。</summary>
    public static ref WMTRenderPassAttachment ColorAt(this ref WMTRenderPassDesc desc, int i)
        => ref desc.Colors[i];

    /// <summary>安全设置颜色附件。</summary>
    public static void SetColorAt(this ref WMTRenderPassDesc desc, int i, in WMTRenderPassAttachment attachment)
        => desc.Colors[i] = attachment;
}

// ============================================================
// Phase 3 结构体
// ============================================================

/// <summary>
/// 纹理创建参数（C 端 <c>WMTTextureInfo</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTTextureInfo
{
    public int PixelFormat;
    public int TextureType;
    public ulong Width;
    public ulong Height;
    public ulong Depth;
    public int MipmapLevels;
    public int SampleCount;
    public int Usage;
    public int Options;

    /// <summary>创建 2D 纹理参数（便利工厂）。</summary>
    public static WMTTextureInfo Create2D(MTLPixelFormat format, int width, int height,
        MTLTextureUsage usage = MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead,
        MTLResourceOptions options = MTLResourceOptions.StorageModeShared)
        => new()
        {
            PixelFormat = (int)format,
            TextureType = (int)MTLTextureType.Type2D,
            Width = (ulong)width, Height = (ulong)height, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)usage,
            Options = (int)options,
        };

    /// <summary>创建 2D 多采样纹理参数（MSAA，便利工厂）。</summary>
    public static WMTTextureInfo Create2DMultisample(MTLPixelFormat format, int width, int height, int sampleCount,
        MTLResourceOptions options = MTLResourceOptions.StorageModePrivate)
        => new()
        {
            PixelFormat = (int)format,
            TextureType = (int)MTLTextureType.Type2DMultisample,
            Width = (ulong)width, Height = (ulong)height, Depth = 1,
            MipmapLevels = 1, SampleCount = sampleCount,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)options,
        };
}

/// <summary>
/// 采样器创建参数（C 端 <c>WMTSamplerInfo</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTSamplerInfo
{
    public int MinFilter;
    public int MagFilter;
    public int MipFilter;
    public int SAddressMode;
    public int TAddressMode;
    public int RAddressMode;
    public int MaxAnisotropy;
    public int CompareFunction;
    public float LodMinClamp;
    public float LodMaxClamp;
}

/// <summary>
/// 三维原点（C 端 <c>WMTOrigin</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTOrigin
{
    public ulong X;
    public ulong Y;
    public ulong Z;

    public WMTOrigin(ulong x, ulong y, ulong z) { X = x; Y = y; Z = z; }
}

// ============================================================
// Phase 7F: VertexDescriptor 结构体
// ============================================================

/// <summary>
/// 顶点属性描述（C 端 <c>WMTVertexAttributeDesc</c>）。
/// 4 + 8 + 4 + 4 = 20 字节，C 端自然对齐到 24？实际按 C 布局。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTVertexAttributeDesc
{
    public int Format;         // MTLVertexFormat
    public ulong Offset;
    public uint BufferIndex;
    public uint Pad;           // 对齐填充
}

/// <summary>
/// 顶点缓冲区布局描述（C 端 <c>WMTVertexBufferLayoutDesc</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTVertexBufferLayoutDesc
{
    public ulong Stride;
    public uint StepFunction;  // MTLVertexStepFunction
    public uint StepRate;
}

/// <summary>
/// 8 个 WMTVertexAttributeDesc 的内联数组。
/// </summary>
[InlineArray(8)]
public struct WMTVertexAttributeDescBuffer8
{
    private WMTVertexAttributeDesc _e0;
}

/// <summary>
/// 8 个 WMTVertexBufferLayoutDesc 的内联数组。
/// </summary>
[InlineArray(8)]
public struct WMTVertexBufferLayoutDescBuffer8
{
    private WMTVertexBufferLayoutDesc _e0;
}

/// <summary>
/// 顶点描述符（C 端 <c>WMTVertexDescriptor</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTVertexDescriptor
{
    public WMTVertexAttributeDescBuffer8 Attributes;
    public int AttributeCount;
    public WMTVertexBufferLayoutDescBuffer8 Layouts;
    public int LayoutCount;
}

// ============================================================
// Phase 7A: MTLDepthStencilState 结构体
// ============================================================

/// <summary>
/// 单面 stencil 描述（C 端 <c>WMTStencilDescriptor</c>，对应 MTLStencilDescriptor）。
/// 6 × uint32 = 24 字节。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTStencilDescriptor
{
    public int StencilFailureOperation;    // WMTStencilOperation
    public int DepthFailureOperation;      // WMTStencilOperation
    public int DepthStencilPassOperation;  // WMTStencilOperation
    public int StencilCompareFunction;     // WMTCompareFunction
    public uint ReadMask;
    public uint WriteMask;
}

/// <summary>
/// 深度/模板状态描述（C 端 <c>WMTDepthStencilDesc</c>，对应 MTLDepthStencilDescriptor）。
/// 内存布局：4 + 1 + 3(pad) + 24 + 24 = 56 字节，与 C 端一致。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTDepthStencilDesc
{
    public int DepthCompareFunction;    // MTLCompareFunction
    public byte DepthWriteEnabled;      // 0=禁用写入, 1=启用
    private byte _pad0, _pad1, _pad2;  // 对齐填充到 4 字节
    public WMTStencilDescriptor FrontFaceStencil;
    public WMTStencilDescriptor BackFaceStencil;
}

// ============================================================
// Phase 3.5: MSC Argument Buffer 描述符
// ============================================================

/// <summary>
/// MSC 4.0 的 argument buffer 中 UAV/SRV/CBV 描述符（24 字节）。
/// 字段顺序与 reflection JSON 的 TopLevelArgumentBuffer 描述对齐。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct UavDescriptor
{
    public ulong GpuAddress;
    public ulong Length;
    public ulong Stride;
}
