using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLComputeCommandEncoder 封装；用来设置 PSO、绑定资源并派发 thread group。
/// 一次 encoder pass 必须以 <see cref="EndEncoding"/> 收尾。
/// </summary>
public sealed class MetalComputeCommandEncoder : MetalObject
{
    internal MetalComputeCommandEncoder(nuint handle) { SetNativeHandle(handle); }

    /// <summary>切换 compute pipeline。</summary>
    public void SetComputePipelineState(MetalComputePipelineState pso)
    {
        ArgumentNullException.ThrowIfNull(pso);
        MetalBridge.MTLComputeCommandEncoder_setComputePipelineState(Handle, pso.Handle);
    }

    /// <summary>把 buffer 绑到 buffer(index)。</summary>
    public void SetBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        MetalBridge.MTLComputeCommandEncoder_setBuffer(Handle, buffer.Handle, offset, index);
    }

    /// <summary>把任意小常量数据原地传到 buffer(index)；适合 ≤ 4KB 的 uniform。</summary>
    /// <summary>把纹理绑到 texture(index)。</summary>
    public void SetTexture(MetalTexture texture, ulong index)
    {
        ArgumentNullException.ThrowIfNull(texture);
        MetalBridge.MTLComputeCommandEncoder_setTexture(Handle, texture.Handle, index);
    }

    public unsafe void SetBytes<T>(in T value, ulong index) where T : unmanaged
    {
        fixed (T* p = &value)
        {
            MetalBridge.MTLComputeCommandEncoder_setBytes(Handle, p, (ulong)sizeof(T), index);
        }
    }

    /// <summary>派发 thread group：threadgroupsPerGrid × threadsPerThreadgroup。</summary>
    public void DispatchThreadgroups(WMTSize threadgroupsPerGrid, WMTSize threadsPerThreadgroup)
        => MetalBridge.MTLComputeCommandEncoder_dispatchThreadgroups(Handle, threadgroupsPerGrid, threadsPerThreadgroup);

    /// <summary>
    /// 在 encoder 范围内驻留资源；用于通过 GPU 地址间接访问的场景
    /// （例如 MSC 输出的 metallib 通过 argument buffer 引用真实 buffer）。
    /// </summary>
    public void UseResource(MetalObject resource, MTLResourceUsage usage)
    {
        ArgumentNullException.ThrowIfNull(resource);
        MetalBridge.MTLComputeCommandEncoder_useResource(Handle, resource.Handle, (uint)usage);
    }

    /// <summary>结束此 encoder pass；之后 encoder 句柄不再可用（释放即可）。</summary>
    public void EndEncoding() => MetalBridge.MTLComputeCommandEncoder_endEncoding(Handle);
}
