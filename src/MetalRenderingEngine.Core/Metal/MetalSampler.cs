using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLSamplerState 封装。
/// </summary>
public sealed class MetalSamplerState : MetalObject
{
    internal MetalSamplerState(nuint handle) { SetNativeHandle(handle); }
}
