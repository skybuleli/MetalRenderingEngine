using System.Collections.Concurrent;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// Phase 6: SharedEvent 池。预分配少量 <see cref="MetalSharedEvent"/>（Metal 建议同时活跃 ≤64），
/// 通过 signaledValue 单调递增区分不同同步点，避免为每个逻辑 fence 创建一个 event。
///
/// 设计要点：
/// 1. 预分配 N 个 event（默认 16，留余量给 Metal 64 上限）。
/// 2. 每个 event 内部维护单调 value 计数器；<see cref="Acquire"/> 轮转分配 event，
///    返回 <see cref="SharedEventSlot"/>（event + 分配的 value）。
/// 3. 同一 event 上可并发多个 in-flight signal value（Metal 支持），
///    只要总未完成 signal 不超过 listener 容量。
/// 4. <see cref="SharedEventSlot"/> 用完 Dispose 后，通过 <see cref="MetalSharedEvent.SignaledValue"/>
///    判断是否可回收（value 已被 GPU signal）。为避免阻塞，pool 不主动等回收，
///    而是依赖调用方在回调里调 <see cref="Release"/>。
/// </summary>
public sealed class SharedEventPool : IDisposable
{
    private readonly MetalDevice _device;
    private readonly MetalSharedEventListener _listener;
    private readonly MetalSharedEvent[] _events;
    private readonly ulong[] _nextValue;        // 每个 event 下一个待分配的 value
    private readonly object[] _locks;           // 每个 event 一把锁（value 分配原子性）
    private int _rrIndex;                       // 轮转索引
    private readonly object _rrLock = new();
    private bool _disposed;

    /// <summary>
    /// 创建池。
    /// </summary>
    /// <param name="device">Metal 设备。</param>
    /// <param name="eventCount">预分配 event 数量（默认 16，Metal 上限 ~64，留余量）。</param>
    /// <exception cref="MetalException">MTLSharedEvent 不可用（沙箱环境）。</exception>
    public SharedEventPool(MetalDevice device, int eventCount = 16)
    {
        if (eventCount < 1 || eventCount > 64)
            throw new ArgumentOutOfRangeException(nameof(eventCount), "eventCount 应在 1..64（Metal 活跃上限）");
        _device = device;
        _events = new MetalSharedEvent[eventCount];
        _nextValue = new ulong[eventCount];
        _locks = new object[eventCount];
        for (int i = 0; i < eventCount; i++)
        {
            _events[i] = device.NewSharedEvent();
            _nextValue[i] = 1;  // 0 是初始值，从 1 开始分配
            _locks[i] = new object();
        }
        _listener = MetalSharedEventListener.Create();
    }

    /// <summary>池中 event 数量。</summary>
    public int EventCount => _events.Length;

    /// <summary>listener（供 <see cref="SharedEventSlot.RegisterCompletionCallback"/> 用）。</summary>
    internal MetalSharedEventListener Listener => _listener;

    /// <summary>
    /// 轮转分配一个 slot。返回的 slot 持有 (event, value)，调用方在 command buffer 里
    /// <c>cmdbuf.EncodeSignalEvent(slot.Event, slot.Value)</c>，然后用完后 Dispose。
    /// </summary>
    public SharedEventSlot Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int idx;
        lock (_rrLock) { idx = _rrIndex++ % _events.Length; }

        ulong value;
        lock (_locks[idx]) { value = _nextValue[idx]++; }

        return new SharedEventSlot(this, _events[idx], value, idx);
    }

    /// <summary>内部：slot 完成后回调通知（由 GpuFence.Release 触发）。</summary>
    internal void OnSlotReleased(int eventIndex)
    {
        // 无操作：value 是单调递增的，event 可立即复用（下一个 Acquire 会分配新 value）。
        // 这里仅作为 hook 点，未来可加 in-flight 计数/容量限制。
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Dispose();
        foreach (var e in _events) e.Dispose();
    }
}

/// <summary>
/// 从 <see cref="SharedEventPool"/> 分配的一个同步点句柄。
/// 持有 (event, value) 对，调用方在 GPU 命令里 signal 此 value，
/// 然后用 <see cref="RegisterCompletionCallback"/>（异步）或 <see cref="Wait"/>（阻塞）感知完成。
/// </summary>
public sealed class SharedEventSlot : IDisposable
{
    private readonly SharedEventPool _pool;
    private readonly int _eventIndex;
    private bool _disposed;

    internal SharedEventSlot(SharedEventPool pool, MetalSharedEvent evt, ulong value, int eventIndex)
    {
        _pool = pool;
        Event = evt;
        Value = value;
        _eventIndex = eventIndex;
    }

    /// <summary>关联的 SharedEvent（用于 <c>EncodeSignalEvent</c>/<c>EncodeWaitForEvent</c>）。</summary>
    public MetalSharedEvent Event { get; }

    /// <summary>分配到的 signaledValue（GPU signal 此值表示完成）。</summary>
    public ulong Value { get; }

    /// <summary>
    /// 注册异步完成回调：GPU signal 到 <see cref="Value"/> 时在 listener 后台线程触发。
    /// 适合帧间同步（CPU 不阻塞，回调里推进流水线）。
    /// 回调触发后 slot 自动 Dispose。
    /// </summary>
    public void RegisterCompletionCallback(Action onComplete)
    {
        ArgumentNullException.ThrowIfNull(onComplete);
        Event.NotifyListener(_pool.Listener, Value, _ =>
        {
            try { onComplete(); } catch { }
            Dispose();
        });
    }

    /// <summary>
    /// 阻塞等待 GPU signal 到 <see cref="Value"/>。
    /// 适合语义必需的 CPU 数据依赖（游戏 CPU 必须读 GPU 结果）。
    /// 相比 <c>WaitUntilCompleted</c>，只等特定 value（更精确唤醒）。
    /// </summary>
    /// <param name="timeoutMs">超时毫秒，0=无限。</param>
    public bool Wait(ulong timeoutMs = 0)
    {
        bool ok = Event.WaitUntilSignaledValue(Value, timeoutMs);
        if (ok) Dispose();
        return ok;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.OnSlotReleased(_eventIndex);
    }
}
