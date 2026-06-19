using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLSamplerState 封装。
/// </summary>
public sealed class MetalSamplerState : MetalObject
{
    internal MetalSamplerState(nuint handle) { SetNativeHandle(handle); }

    /// <summary>
    /// GPU 资源标识（MTLResourceID._impl，macOS 13+）。
    /// 用于 Phase 10 描述符堆绑定：把 sampler 写入 MSC 自定义描述符堆条目。
    /// sampler 必须在创建时设 supportArgumentBuffers=YES（bridge.m 已硬编码），
    /// 否则返回 0。
    /// </summary>
    public ulong GpuResourceID => MetalBridge.MTLSamplerState_gpuResourceID(Handle);
}
