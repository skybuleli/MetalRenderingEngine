using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>MTLCommandQueue 封装。</summary>
public sealed class MetalCommandQueue : MetalObject
{
    internal MetalCommandQueue(nuint handle) { SetNativeHandle(handle); }

    /// <summary>从队列拿一个命令缓冲区（每帧/每次提交都新建一个）。</summary>
    public MetalCommandBuffer CommandBuffer()
    {
        nuint h = MetalBridge.MTLCommandQueue_commandBuffer(Handle);
        if (h == 0) throw new MetalException("MTLCommandQueue commandBuffer returned nil.");
        return new MetalCommandBuffer(h);
    }
}

/// <summary>MTLCommandBuffer 封装。提交后即可等待完成或查询状态/错误。</summary>
public sealed class MetalCommandBuffer : MetalObject
{
    internal MetalCommandBuffer(nuint handle) { SetNativeHandle(handle); }

    /// <summary>提交到 GPU 执行队列。</summary>
    public void Commit() => MetalBridge.MTLCommandBuffer_commit(Handle);

    /// <summary>阻塞直到 GPU 执行完毕。</summary>
    public void WaitUntilCompleted() => MetalBridge.MTLCommandBuffer_waitUntilCompleted(Handle);

    /// <summary>当前状态。</summary>
    public MTLCommandBufferStatus Status
        => (MTLCommandBufferStatus)MetalBridge.MTLCommandBuffer_status(Handle);

    /// <summary>
    /// 当前命令缓冲的错误对象；无错误返回 null。
    /// 返回的 <see cref="MetalError"/> 已 retain，调用方需 using/Dispose。
    /// </summary>
    public MetalError? Error()
    {
        nuint h = MetalBridge.MTLCommandBuffer_error(Handle);
        return h == 0 ? null : new MetalError(h);
    }

    /// <summary>创建 compute 编码器。</summary>
    public MetalComputeCommandEncoder ComputeCommandEncoder()
    {
        nuint h = MetalBridge.MTLCommandBuffer_computeCommandEncoder(Handle);
        if (h == 0) throw new MetalException("MTLCommandBuffer computeCommandEncoder returned nil.");
        return new MetalComputeCommandEncoder(h);
    }
}
