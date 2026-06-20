using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTL4ComputeCommandEncoder 封装。
/// 资源绑定走 argument table，不再使用 per-encoder setBuffer / setBytes / setTexture。
/// </summary>
public sealed class MetalComputeCommandEncoder : MetalObject
{
    private readonly MetalCommandBuffer _commandBuffer;

    internal MetalComputeCommandEncoder(nuint handle, MetalCommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        _commandBuffer = commandBuffer;
        SetNativeHandle(handle);
        MetalBridge.MTLComputeCommandEncoder_setArgumentTable(Handle, commandBuffer.ComputeArgumentTableHandle);
    }

    /// <summary>结束此 encoder pass；之后 encoder 句柄不再可用（释放即可）。</summary>
    public void EndEncoding()
        => MetalBridge.MTLComputeCommandEncoder_endEncoding(Handle);

    /// <summary>切换 compute pipeline。</summary>
    public void SetComputePipelineState(MetalComputePipelineState pso)
    {
        ArgumentNullException.ThrowIfNull(pso);
        MetalBridge.MTLComputeCommandEncoder_setComputePipelineState(Handle, pso.Handle);
    }

    /// <summary>把 buffer 绑到 argument table 的 buffer(index)。</summary>
    public void SetBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _commandBuffer.TrackResidency(buffer);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.ComputeArgumentTableHandle, buffer.GpuAddress + offset, index);
    }

    /// <summary>把任意小常量数据写入 scratch buffer，再以 GPU 地址绑定到 argument table。</summary>
    public unsafe void SetBytes<T>(in T value, ulong index) where T : unmanaged
    {
        T local = value;
        ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>((byte*)&local, sizeof(T));
        ulong address = _commandBuffer.AllocateScratch(bytes);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.ComputeArgumentTableHandle, address, index);
    }

    /// <summary>把任意字节数据写入 scratch buffer，再以 GPU 地址绑定到 argument table。</summary>
    public void SetBytes(ReadOnlySpan<byte> data, ulong index)
    {
        ulong address = _commandBuffer.AllocateScratch(data);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.ComputeArgumentTableHandle, address, index);
    }

    /// <summary>把纹理绑到 argument table 的 texture(index)。</summary>
    public void SetTexture(MetalTexture texture, ulong index)
    {
        ArgumentNullException.ThrowIfNull(texture);
        _commandBuffer.TrackResidency(texture);
        MetalBridge.MTLArgumentTable_setTexture(_commandBuffer.ComputeArgumentTableHandle, texture.GpuResourceID, index);
    }

    /// <summary>派发 thread group：threadgroupsPerGrid × threadsPerThreadgroup。</summary>
    public void DispatchThreadgroups(WMTSize threadgroupsPerGrid, WMTSize threadsPerThreadgroup)
        => MetalBridge.MTLComputeCommandEncoder_dispatchThreadgroups(Handle, threadgroupsPerGrid, threadsPerThreadgroup);

    /// <summary>
    /// 记录资源驻留，不再调用已移除的 useResource。
    /// </summary>
    public void UseResource(MetalObject resource, MTLResourceUsage usage)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _commandBuffer.TrackResidency(resource);
    }
}
