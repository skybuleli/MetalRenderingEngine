using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>MTL4CommandQueue 封装。</summary>
public sealed class MetalCommandQueue : MetalObject
{
    private readonly MetalDevice _device;

    internal MetalCommandQueue(nuint handle, MetalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        SetNativeHandle(handle);
    }

    internal MetalDevice Device => _device;

    /// <summary>从设备拿一个 Metal 4 命令缓冲区并自动 begin。</summary>
    public MetalCommandBuffer CommandBuffer()
    {
        nuint h = MetalBridge.MTLDevice_newCommandBuffer(_device.Handle);
        if (h == 0) throw new MetalException("MTLDevice newCommandBuffer returned nil.");
        return new MetalCommandBuffer(h, this, _device);
    }

    internal void WaitForDrawable(MetalDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        MetalBridge.MTLCommandQueue_waitForDrawable(Handle, drawable.Handle);
    }

    internal void SignalDrawable(MetalDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        MetalBridge.MTLCommandQueue_signalDrawable(Handle, drawable.Handle);
    }

    internal void CommitOne(MetalCommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        unsafe
        {
            nuint error = 0;
            MetalBridge.MTLCommandQueue_commitOne(Handle, commandBuffer.Handle, &error);
            commandBuffer.SetCompletionError(error);
        }
    }
}

/// <summary>MTL4CommandBuffer 封装。自动管理 allocator、argument table 与 residency set。</summary>
public sealed class MetalCommandBuffer : MetalObject
{
    private const ulong ScratchCapacity = 1024 * 1024;

    private readonly MetalCommandQueue _queue;
    private readonly MetalDevice _device;
    private readonly nuint _allocator;
    private readonly nuint _computeArgumentTable;
    private readonly nuint _vertexArgumentTable;
    private readonly nuint _fragmentArgumentTable;
    private readonly nuint _residencySet;
    private readonly MetalBuffer _scratchBuffer;

    private ulong _scratchOffset;
    private MetalDrawable? _pendingDrawable;
    private nuint _completionErrorHandle;
    private MTLCommandBufferStatus _status = MTLCommandBufferStatus.NotEnqueued;

    internal MetalCommandBuffer(nuint handle, MetalCommandQueue queue, MetalDevice device)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(device);

        _queue = queue;
        _device = device;
        SetNativeHandle(handle);

        _allocator = _device.NewCommandAllocatorHandle();
        if (_allocator == 0) throw new MetalException("MTLDevice newCommandAllocator returned nil.");

        _computeArgumentTable = _device.NewArgumentTableHandle();
        _vertexArgumentTable = _device.NewArgumentTableHandle();
        _fragmentArgumentTable = _device.NewArgumentTableHandle();
        _residencySet = _device.NewResidencySetHandle();

        if (_computeArgumentTable == 0 || _vertexArgumentTable == 0 || _fragmentArgumentTable == 0 || _residencySet == 0)
            throw new MetalException("Metal 4 tables or residency set returned nil.");

        _scratchBuffer = _device.NewBuffer(ScratchCapacity, MTLResourceOptions.StorageModeShared);

        MetalBridge.MTLCommandBuffer_beginCommandBufferWithAllocator(Handle, _allocator);
        MetalBridge.MTLCommandBuffer_useResidencySet(Handle, _residencySet);
        MetalBridge.MTLResidencySet_addAllocation(_residencySet, _scratchBuffer.Handle);
        MetalBridge.MTLResidencySet_commit(_residencySet);
    }

    internal void SetCompletionError(nuint errorHandle)
    {
        _completionErrorHandle = errorHandle;
        _status = errorHandle == 0 ? MTLCommandBufferStatus.Completed : MTLCommandBufferStatus.Error;
    }

    internal ulong AllocateScratch(ReadOnlySpan<byte> data)
    {
        const ulong alignment = 16;
        ulong offset = (_scratchOffset + alignment - 1) & ~(alignment - 1);
        ulong end = offset + (ulong)data.Length;
        if (end > _scratchBuffer.Length)
            throw new InvalidOperationException("MetalCommandBuffer scratch buffer overflow.");

        unsafe
        {
            nint ptr = _scratchBuffer.Contents;
            if (ptr == IntPtr.Zero)
                throw new InvalidOperationException("Scratch buffer has no CPU-accessible contents.");
            fixed (byte* src = data)
            {
                Buffer.MemoryCopy(src, (void*)((nint)ptr + (nint)offset), data.Length, data.Length);
            }
        }

        _scratchOffset = end;
        return _scratchBuffer.GpuAddress + offset;
    }

    internal void TrackResidency(MetalObject resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        MetalBridge.MTLResidencySet_addAllocation(_residencySet, resource.Handle);
    }

    internal nuint ComputeArgumentTableHandle => _computeArgumentTable;
    internal nuint VertexArgumentTableHandle => _vertexArgumentTable;
    internal nuint FragmentArgumentTableHandle => _fragmentArgumentTable;

    /// <summary>提交到 GPU 并等待完成。Metal 4 通过 commit feedback 完成回调。</summary>
    public void Commit()
    {
        if (_status is MTLCommandBufferStatus.Committed or MTLCommandBufferStatus.Completed)
            return;

        MetalBridge.MTLCommandBuffer_endCommandBuffer(Handle);
        _status = MTLCommandBufferStatus.Committed;
        MetalBridge.MTLResidencySet_commit(_residencySet);
        _queue.CommitOne(this);
        _pendingDrawable?.InvokePresent(_queue);
        _pendingDrawable = null;
    }

    /// <summary>阻塞直到 GPU 执行完毕。当前实现里 Commit 已同步等待，因此这里是空操作。</summary>
    public void WaitUntilCompleted() { }

    internal void PreparePresentDrawable(MetalDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        _queue.WaitForDrawable(drawable);
        _pendingDrawable = drawable;
    }

    internal void InvokePendingPresent()
    {
        if (_pendingDrawable is null) return;
        _queue.SignalDrawable(_pendingDrawable);
        _pendingDrawable.Present();
    }

    /// <summary>GPU 执行到此处时给 shared event 赋 value（队列级 signal）。</summary>
    public void EncodeSignalEvent(MetalSharedEvent evt, ulong value)
    {
        ArgumentNullException.ThrowIfNull(evt);
        MetalBridge.MTLCommandQueue_signalEvent(_queue.Handle, evt.Handle, value);
    }

    /// <summary>GPU 执行到此处时阻塞直到 event.SignaledValue &gt;= value。</summary>
    public void EncodeWaitForEvent(MetalSharedEvent evt, ulong value)
    {
        ArgumentNullException.ThrowIfNull(evt);
        MetalBridge.MTLCommandQueue_waitForEvent(_queue.Handle, evt.Handle, value);
    }

    /// <summary>当前状态。</summary>
    public MTLCommandBufferStatus Status => _status;

    /// <summary>当前命令缓冲的错误对象；无错误返回 null。</summary>
    public MetalError? Error()
    {
        if (_completionErrorHandle == 0) return null;
        nuint h = _completionErrorHandle;
        _completionErrorHandle = 0;
        return new MetalError(h);
    }

    /// <summary>创建 compute 编码器。</summary>
    public MetalComputeCommandEncoder ComputeCommandEncoder()
    {
        nuint h = MetalBridge.MTLCommandBuffer_computeCommandEncoder(Handle);
        if (h == 0) throw new MetalException("MTLCommandBuffer computeCommandEncoder returned nil.");
        return new MetalComputeCommandEncoder(h, this);
    }

    /// <summary>创建 render 编码器。</summary>
    public MetalRenderEncoder RenderCommandEncoder(in WMTRenderPassDesc desc)
    {
        unsafe
        {
            WMTRenderPassDesc local = desc;
            nuint h = MetalBridge.MTLCommandBuffer_renderCommandEncoder(Handle, &local);
            if (h == 0) throw new MetalException("MTLCommandBuffer renderCommandEncoder returned nil.");
            return new MetalRenderEncoder(h, this);
        }
    }

    /// <summary>准备一个 drawable，提交后会在队列上 signal 并 present。</summary>
    public void PresentDrawable(MetalDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        PreparePresentDrawable(drawable);
    }

    protected override bool ReleaseHandle()
    {
        if (_completionErrorHandle != 0) MetalBridge.NSObject_release(_completionErrorHandle);
        _scratchBuffer.Dispose();
        if (_residencySet != 0) MetalBridge.NSObject_release(_residencySet);
        if (_fragmentArgumentTable != 0) MetalBridge.NSObject_release(_fragmentArgumentTable);
        if (_vertexArgumentTable != 0) MetalBridge.NSObject_release(_vertexArgumentTable);
        if (_computeArgumentTable != 0) MetalBridge.NSObject_release(_computeArgumentTable);
        if (_allocator != 0) MetalBridge.NSObject_release(_allocator);
        return base.ReleaseHandle();
    }
}

internal static class MetalDrawablePresentationExtensions
{
    internal static void InvokePresent(this MetalDrawable drawable, MetalCommandQueue queue)
    {
        queue.SignalDrawable(drawable);
        drawable.Present();
    }

    internal static void Present(this MetalDrawable drawable)
        => MetalBridge.MTLDrawable_present(drawable.Handle);
}
