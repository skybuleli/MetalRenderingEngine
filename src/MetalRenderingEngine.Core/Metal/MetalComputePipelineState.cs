using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLComputePipelineState 封装；查询 PSO 的线程组属性以推断派发参数。
/// </summary>
public sealed class MetalComputePipelineState : MetalObject
{
    internal MetalComputePipelineState(nuint handle) { SetNativeHandle(handle); }

    /// <summary>单个 threadgroup 可包含的最大线程数。</summary>
    public ulong MaxTotalThreadsPerThreadgroup
        => MetalBridge.MTLComputePipelineState_maxTotalThreadsPerThreadgroup(Handle);

    /// <summary>SIMD 宽度（M1 / Apple GPU 通常为 32）。</summary>
    public ulong ThreadExecutionWidth
        => MetalBridge.MTLComputePipelineState_threadExecutionWidth(Handle);
}
