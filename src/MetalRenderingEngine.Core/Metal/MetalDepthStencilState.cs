using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLDepthStencilState 封装。
/// 对应 native/bridge.h 的 MTLDevice_newDepthStencilState。
/// </summary>
public sealed class MetalDepthStencilState : MetalObject
{
    internal MetalDepthStencilState(nuint handle) { SetNativeHandle(handle); }
}
