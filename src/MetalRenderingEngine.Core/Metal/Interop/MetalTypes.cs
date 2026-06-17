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
