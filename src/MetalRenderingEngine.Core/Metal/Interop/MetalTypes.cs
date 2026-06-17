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
}

/// <summary>WMTRenderPipelineDesc 扩展方法（结构体方法不能 ref 返回 this 字段，CS8170）。</summary>
public static class WMTRenderPipelineDescExtensions
{
    /// <summary>通过索引访问颜色附件。</summary>
    public static ref WMTColorAttachment ColorAttachmentAt(this ref WMTRenderPipelineDesc desc, int i)
        => ref desc.Colors[i];
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
