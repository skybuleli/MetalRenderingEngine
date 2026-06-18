using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// Phase 6 资源池：单帧 transient scratch buffer 分配器。一帧内 bump 分配 sub-range，
/// 帧末整体重置；用 <see cref="GpuFence"/>（基于 <see cref="SharedEventPool"/>）保护
/// N 帧前的区域不被覆盖。
///
/// 用途：per-frame uniforms、staging upload、vertex/index data 等单帧短生命内存——
/// 避免每帧 <c>device.NewBuffer</c>。
///
/// 设计要点（参照 <see cref="MetalCommandList"/> 的 pinned bump 模式 + <see cref="GpuFence"/>）：
/// 1. 构造时一次性分配单个大 <see cref="MetalBuffer"/>（<see cref="MTLResourceOptions.StorageModeShared"/>，
///    UMA 上无需 <see cref="MetalBuffer.DidModifyRange"/>），按 <see cref="FrameCount"/> 均分为
///    N 个 frame slot。每帧只用当前 slot，跨帧互不重叠。
/// 2. <see cref="BeginFrame"/> 推进到下一 slot，若该 slot 上有未完成 fence 则阻塞
///    <see cref="GpuFence.Wait(ulong)"/>（精确唤醒，超时报错）。这是用户选择的"GpuFence + 单 ring buffer"
///    帧间复用策略——靠 SharedEvent 而非固定 N 帧延迟保证 GPU 已读完。
/// 3. <see cref="Allocate"/> 在当前 slot 内 bump 分配，返回 <see cref="TransientAllocation"/>
///    （持有共享大 buffer 的只读引用 + offset/length）。调用方用
///    <c>encoder.SetBuffer(alloc.Buffer, alloc.Offset, index)</c> 绑定（bridge 已支持 offset）。
/// 4. <see cref="EndFrame"/> 在 command buffer 末尾编码 <see cref="GpuFence.Signal"/>，记到当前 slot。
/// 5. 不线程安全；不运行时扩容（同 <see cref="MetalCommandList"/> 哲学），超容抛异常，调用方估容量。
/// </summary>
public sealed class TransientBufferAllocator : IDisposable
{
    private readonly MetalDevice _device;
    private readonly SharedEventPool _fencePool;
    private readonly MetalBuffer _buffer;       // 单个大 buffer（StorageModeShared）
    private readonly ulong _slotSize;           // 每个 frame slot 的字节数
    private readonly GpuFence?[] _fences;       // 每个 slot 上待完成的 fence（null=该 slot 空闲或已确认完成）
    private int _frameIndex;                    // 当前帧 slot 索引（0..FrameCount-1）
    private ulong _writeOffset;                 // 当前 slot 内已分配偏移（相对 slot 起点）
    private bool _disposed;

    /// <summary>构造时分配的总容量（字节）。</summary>
    public ulong Capacity { get; }

    /// <summary>frame slot 数（triple-buffer 默认 3）。</summary>
    public int FrameCount { get; }

    /// <summary>当前帧 slot 内已分配字节数（相对 slot 起点）。</summary>
    public ulong Used => _writeOffset;

    /// <summary>
    /// 创建分配器。构造时一次性分配 <paramref name="capacity"/> 字节的大 buffer，
    /// 按 <paramref name="frameCount"/> 均分 frame slot。
    /// </summary>
    /// <param name="device">Metal 设备。</param>
    /// <param name="fencePool">用于帧间 fence 的 <see cref="SharedEventPool"/>（必填）。</param>
    /// <param name="capacity">总容量字节（默认 16MB）。需能被 frameCount 整除语义才均匀，否则向下对齐。</param>
    /// <param name="frameCount">frame slot 数（默认 3，匹配 triple-buffer 约定）。</param>
    public TransientBufferAllocator(MetalDevice device, SharedEventPool fencePool,
                                     ulong capacity = 16 * 1024 * 1024, int frameCount = 3)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(fencePool);
        if (capacity < 1024) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity 至少 1KB");
        if (frameCount < 1 || frameCount > 8)
            throw new ArgumentOutOfRangeException(nameof(frameCount), "frameCount 应在 1..8");

        _device = device;
        _fencePool = fencePool;
        FrameCount = frameCount;
        // 每个 slot 容量向下对齐到 frameCount 的整数倍，保证 N 个 slot 等大
        _slotSize = (capacity / (ulong)frameCount / 256u) * 256u;  // 再 256 对齐（offset 对齐基准）
        if (_slotSize == 0) throw new ArgumentOutOfRangeException(nameof(capacity), "capacity 过小，每个 slot 容量为 0");
        Capacity = _slotSize * (ulong)frameCount;

        _buffer = device.NewBuffer(Capacity, MTLResourceOptions.StorageModeShared);
        _fences = new GpuFence?[frameCount];
        _frameIndex = frameCount - 1;  // 初始为最后一个，使首次 BeginFrame 推进到 0
        _writeOffset = 0;
    }

    /// <summary>底层共享大 buffer（只读引用；调用方不可 Dispose）。</summary>
    public MetalBuffer Buffer => _buffer;

    /// <summary>每个 frame slot 的容量（字节）。</summary>
    public ulong SlotSize => _slotSize;

    /// <summary>
    /// 开始新的一帧：推进到下一 slot，若该 slot 上有未完成 fence 则阻塞等待 GPU 完成，
    /// 然后重置写指针。每帧开头调一次。
    /// </summary>
    public void BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frameIndex = (_frameIndex + 1) % FrameCount;

        // 等待该 slot 上次使用时的 fence（若仍在飞行中）
        var fence = _fences[_frameIndex];
        if (fence is not null)
        {
            // 阻塞等 GPU signal（精确唤醒，超时报错避免死等）
            bool ok = fence.Wait(timeoutMs: 5000);
            // GpuFence.Wait 成功会自动 Dispose slot；失败也已被 Dispose，这里清理引用
            _fences[_frameIndex] = null;
            if (!ok)
                throw new InvalidOperationException("TransientBufferAllocator: 等待上一帧 fence 超时（GPU 可能未完成）");
        }
        _writeOffset = 0;
    }

    /// <summary>
    /// 在当前帧 slot 内 bump 分配 <paramref name="size"/> 字节。
    /// 返回的 <see cref="TransientAllocation"/> 持共享大 buffer 的只读引用 + offset。
    /// 不实现 IDisposable（底层 buffer 由本分配器持有）。
    /// </summary>
    /// <param name="size">需要分配的字节数（&gt; 0）。</param>
    /// <param name="alignment">对齐（默认 256，匹配 Metal buffer offset 对齐要求）。</param>
    public TransientAllocation Allocate(ulong size, int alignment = 256)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (size == 0) throw new ArgumentOutOfRangeException(nameof(size), "size 必须 > 0");
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment), "alignment 必须是 2 的幂");

        // 对齐 size 到 alignment（保证下次分配起点也对齐）
        ulong aligned = (size + (ulong)alignment - 1) & ~((ulong)alignment - 1);
        if (_writeOffset + aligned > _slotSize)
        {
            throw new InvalidOperationException(
                $"TransientBufferAllocator slot 已满（slot {_frameIndex}：{_writeOffset}/{_slotSize}，需 {aligned}）。" +
                "请构造时传入更大 capacity，或减少单帧分配量。");
        }

        ulong absOffset = (ulong)_frameIndex * _slotSize + _writeOffset;
        _writeOffset += aligned;
        return new TransientAllocation(_buffer, absOffset, size);
    }

    /// <summary>
    /// 结束当前帧：在 <paramref name="cmdbuf"/> 末尾编码 fence signal，记录到当前 slot。
    /// 在 <c>cmdbuf.Commit()</c> 之前调。下一帧 <see cref="BeginFrame"/> 推进到下一 slot 时
    /// 不会立即触碰本 slot（直到 N 帧后轮转回来才等本 fence）。
    /// </summary>
    public void EndFrame(MetalCommandBuffer cmdbuf)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cmdbuf);

        var fence = GpuFence.Create(_fencePool);
        fence.Signal(cmdbuf);  // GPU 执行到 cmdbuf 末尾时 signal
        _fences[_frameIndex] = fence;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 释放所有未完成 fence（注意：fence 可能仍被 GPU 引用，Dispose 只释放 SharedEventSlot 引用，
        // 不影响 GPU 已编码的 signal——SharedEvent 本身由 pool 持有）
        foreach (var f in _fences) f?.Dispose();
        _buffer.Dispose();
    }
}

/// <summary>
/// <see cref="TransientBufferAllocator.Allocate"/> 的返回值：共享大 buffer 内的一个 sub-range。
/// <see cref="Buffer"/> 是只读引用（不可 Dispose）；<see cref="Offset"/> 用于 encoder offset 绑定。
/// </summary>
public readonly struct TransientAllocation
{
    private readonly MetalBuffer _buffer;

    internal TransientAllocation(MetalBuffer buffer, ulong offset, ulong length)
    {
        _buffer = buffer;
        Offset = offset;
        Length = length;
    }

    /// <summary>底层共享大 buffer（只读引用，调用方不可 Dispose）。</summary>
    public MetalBuffer Buffer => _buffer;

    /// <summary>在大 buffer 内的绝对偏移（字节）。</summary>
    public ulong Offset { get; }

    /// <summary>本次分配的字节数（&gt;= 申请 size，已对齐到 alignment 用于推进写指针；本字段保留原始 size）。</summary>
    public ulong Length { get; }

    /// <summary>本 sub-range 的 GPU 虚拟地址（= <see cref="Buffer"/>.GpuAddress + <see cref="Offset"/>）。</summary>
    public ulong GpuAddress => _buffer.GpuAddress + Offset;

    /// <summary>
    /// 以 <typeparamref name="T"/> 视图访问本次分配的 sub-range。
    /// </summary>
    public unsafe Span<T> AsSpan<T>() where T : unmanaged
    {
        nint p = _buffer.Contents;
        if (p == IntPtr.Zero)
            throw new InvalidOperationException("Buffer has no CPU-accessible contents (StorageModePrivate?).");
        int count = checked((int)(Length / (ulong)sizeof(T)));
        return new Span<T>((void*)(p + (nint)Offset), count);
    }

    /// <summary>
    /// 生成本 sub-range 的 <see cref="UavDescriptor"/>（MSC argument-buffer 用的 GPU 地址 + 长度 + 步长），
    /// 便于直接 <c>encoder.SetBytes(alloc.ToUavDescriptor(stride), index)</c>。
    /// </summary>
    /// <param name="stride">单个元素步长（字节）；0 时填 <see cref="Length"/>。</param>
    public UavDescriptor ToUavDescriptor(ulong stride = 0)
        => new UavDescriptor { GpuAddress = GpuAddress, Length = Length, Stride = stride == 0 ? Length : stride };
}
