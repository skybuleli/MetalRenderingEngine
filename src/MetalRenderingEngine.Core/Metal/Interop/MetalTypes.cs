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
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTColorAttachment
{
    public int PixelFormat;
    public int WriteMask;
    public int BlendingEnabled;
}

/// <summary>
/// 渲染管线整体描述（C 端 <c>WMTRenderPipelineDesc</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTRenderPipelineDesc
{
    public unsafe fixed byte ColorsRaw[8 * 12]; // 8 x WMTColorAttachment
    public int ColorCount;
    public int DepthPixelFormat;
    public int StencilPixelFormat;
    public int SampleCount;
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
/// Render pass 整体描述（C 端 <c>WMTRenderPassDesc</c>）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct WMTRenderPassDesc
{
    public unsafe fixed byte ColorsRaw[8 * 40]; // 8 x WMTRenderPassAttachment
    public unsafe fixed byte DepthRaw[40];
    public unsafe fixed byte StencilRaw[40];
}
