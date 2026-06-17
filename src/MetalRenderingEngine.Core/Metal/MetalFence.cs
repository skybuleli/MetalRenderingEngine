using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLFence 封装。用于 GPU 命令同步（如 triple-buffer 帧间保护）。
/// </summary>
public sealed class MetalFence : MetalObject
{
    internal MetalFence(nuint handle) { SetNativeHandle(handle); }
}
