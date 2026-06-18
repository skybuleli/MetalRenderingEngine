using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// Phase 6 资源池：MTLBuffer 池。跨帧复用同样尺寸的 buffer，避免每帧
/// <c>device.NewBuffer</c>（ComputeDemo / Mandelbrot / FenceBenchmark 等都在反复 new
/// 同样大小的 buffer）。
///
/// 设计要点：
/// 1. 按"对齐到 2 的幂的 size 桶"分组缓存空闲 buffer（256 起步，幂步进）。
///    <see cref="Rent(ulong, MTLResourceOptions)"/> 把 <c>minSize</c> 向上取整到最近桶，
///    命中则复用，否则 <see cref="MetalDevice.NewBuffer"/> 新建。
/// 2. <b>Rent 时 Retain</b>（与 <see cref="MetalShaderLoader"/> 一致）：
///    池持有一份 <see cref="MetalObject"/> 主包装器（master，native +1，常驻栈中）；
///    每次 Rent 对 master 调 <see cref="MetalObject.Retain"/>（native +1），再构造一个
///    借用包装器（lease wrapper）持有这次 Retain 的 +1 交给调用方。归还时 lease wrapper
///    Dispose 释放 Retain 的 +1，master 仍有效复用。调用方误 Dispose 只毁借用包装器，
///    不影响池内 master。
/// 3. 不线程安全（与 <see cref="MetalCommandList"/> 一致）；多线程请每线程一个池。
/// 4. 长生命整块复用场景用本池；单帧 scratch（uniform/vertex upload）用
///    <see cref="TransientBufferAllocator"/>（offset 子分配）。
/// </summary>
public sealed class BufferPool : IDisposable
{
    private readonly MetalDevice _device;
    /// <summary>size 桶 → 空闲 master buffer 栈。桶 key 是已对齐的 2 的幂 size。</summary>
    private readonly Dictionary<long, Stack<MetalBuffer>> _buckets = new();
    private readonly List<long> _bucketKeys = new();  // 用于 Dispose 时遍历
    private int _totalCreated;     // 池内 master 总数（含已租出未归还的）
    private bool _disposed;

    /// <summary>最小桶 = 256 字节（Metal buffer offset 对齐基准）。</summary>
    private const long MinBucket = 256;

    /// <summary>
    /// 创建池。
    /// </summary>
    /// <param name="device">Metal 设备（池持有其引用，不接管所有权）。</param>
    public BufferPool(MetalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>池内 master buffer 总数（含空闲与已租出）。</summary>
    public int Count => _totalCreated;

    /// <summary>当前空闲可租数。</summary>
    public int AvailableCount => _buckets.Values.Sum(s => s.Count);

    /// <summary>
    /// 租借一个 <see cref="Length"/> &gt;= <paramref name="minSize"/> 的 buffer。
    /// 命中桶则复用 master，否则新建。返回的 <see cref="BufferLease"/> Dispose 即归还。
    /// </summary>
    /// <param name="minSize">需要的最小字节数（向上取整到最近 2 的幂桶）。</param>
    /// <param name="options">资源选项（默认 <see cref="MTLResourceOptions.StorageModeShared"/>）。</param>
    public BufferLease Rent(ulong minSize, MTLResourceOptions options = MTLResourceOptions.StorageModeShared)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (minSize == 0) throw new ArgumentOutOfRangeException(nameof(minSize), "minSize 必须 > 0");

        long bucket = AlignToBucket((long)minSize);
        if (!_buckets.TryGetValue(bucket, out var stack))
        {
            stack = new Stack<MetalBuffer>();
            _buckets[bucket] = stack;
            _bucketKeys.Add(bucket);
        }

        MetalBuffer master;
        if (stack.Count > 0)
        {
            master = stack.Pop();  // 复用：master 持 native +1
        }
        else
        {
            master = _device.NewBuffer((ulong)bucket, options);  // 新建：native +1
            _totalCreated++;
        }

        // Retain 给借用包装器（native +1 → +2）；master 持 +1，lease wrapper 持 +1
        master.Retain();
        var leaseWrapper = new MetalBuffer(master.Handle, master.Length);
        return new BufferLease(this, leaseWrapper, master, bucket);
    }

    /// <summary>释放所有空闲 master（已租出的不动）。回收内存压力时调用。</summary>
    public void Trim()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var key in _bucketKeys)
        {
            if (!_buckets.TryGetValue(key, out var stack)) continue;
            while (stack.Count > 0)
            {
                var b = stack.Pop();
                _totalCreated--;
                b.Dispose();  // 释放 master 的 +1
            }
        }
    }

    /// <summary>归还 master 到对应桶（由 <see cref="BufferLease.Dispose"/> 调用）。</summary>
    internal void Return(MetalBuffer master, long bucket)
    {
        if (_disposed)
        {
            // 池已 Dispose：若 master 仍有效，释放其 +1（lease 的 +1 由 leaseWrapper.Dispose 释放）
            if (!master.IsInvalid) master.Dispose();
            return;
        }
        if (master.IsInvalid)
        {
            // 调用方异常操作把 master 搞失效了（理论上不该发生，因为 master 不暴露给调用方），
            // 安全起见丢弃，计数减一。
            _totalCreated--;
            return;
        }
        if (!_buckets.TryGetValue(bucket, out var stack))
        {
            stack = new Stack<MetalBuffer>();
            _buckets[bucket] = stack;
            _bucketKeys.Add(bucket);
        }
        stack.Push(master);  // master 的 +1 由池继续持有，待下次 Rent 复用
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in _bucketKeys)
        {
            if (!_buckets.TryGetValue(key, out var stack)) continue;
            while (stack.Count > 0)
            {
                var b = stack.Pop();
                b.Dispose();  // 释放 master 的 +1
                _totalCreated--;
            }
        }
        _buckets.Clear();
        // 注：已租出未归还的 lease 仍持 lease wrapper 的 +1，lease Dispose 时 leaseWrapper.Dispose 释放；
        // master 已随 Return(_disposed 分支) 释放。两者最终都被回收，无 native 泄漏。
    }

    /// <summary>
    /// 把 size 向上取整到最近 2 的幂桶（最小 256）。
    /// 例：1..256→256, 257..512→512, 513..1024→1024。
    /// </summary>
    private static long AlignToBucket(long size)
    {
        if (size <= MinBucket) return MinBucket;
        long b = MinBucket;
        while (b < size) b <<= 1;
        return b;
    }
}

/// <summary>
/// 从 <see cref="BufferPool"/> 租借的 buffer 句柄。Dispose 即归还到池（不释放 native 对象）。
/// 暴露的 <see cref="Buffer"/> 是借用包装器；调用方误 Dispose 只释放借用的 +1，不影响池内 master。
/// </summary>
public sealed class BufferLease : IDisposable
{
    private readonly BufferPool _pool;
    private readonly MetalBuffer _master;
    private readonly long _bucket;
    private MetalBuffer? _leaseBuffer;
    private bool _disposed;

    internal BufferLease(BufferPool pool, MetalBuffer leaseBuffer, MetalBuffer master, long bucket)
    {
        _pool = pool;
        _leaseBuffer = leaseBuffer;
        _master = master;
        _bucket = bucket;
    }

    /// <summary>借用的 MTLBuffer 包装器（offset 恒 0）。Dispose 后访问抛 <see cref="ObjectDisposedException"/>。</summary>
    public MetalBuffer Buffer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _leaseBuffer!;
        }
    }

    /// <summary>BufferPool 整块租借，偏移恒 0。</summary>
    public ulong Offset => 0;

    /// <summary>实际 buffer 字节数（&gt;= 申请的 minSize，已对齐到桶）。</summary>
    public ulong Length => Buffer.Length;

    /// <summary>GPU 虚拟地址（= <see cref="MetalBuffer.GpuAddress"/>）。</summary>
    public ulong GpuAddress => Buffer.GpuAddress;

    /// <summary>以 <typeparamref name="T"/> 视图访问 buffer 全部内容。</summary>
    public unsafe Span<T> AsSpan<T>() where T : unmanaged => Buffer.AsSpan<T>();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 释放借用包装器的 +1（native -1，leaseBuffer 失效；master 仍有效）
        _leaseBuffer!.Dispose();
        _leaseBuffer = null;
        // master 归还到池（其 +1 由池继续持有）
        _pool.Return(_master, _bucket);
    }
}
