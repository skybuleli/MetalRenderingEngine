using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLRenderPipelineState 封装。
/// </summary>
public sealed class MetalRenderPipelineState : MetalObject
{
    internal MetalRenderPipelineState(nuint handle) { SetNativeHandle(handle); }
}
