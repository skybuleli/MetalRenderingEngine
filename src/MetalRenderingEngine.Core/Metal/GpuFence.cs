using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// Phase 6: GPU fence 统一抽象。屏蔽 <see cref="MetalFence"/>（GPU-only）与
/// <see cref="SharedEventPool"/>/<see cref="SharedEventSlot"/>（CPU-GPU 跨设备）差异，
/// 根据 <see cref="FenceStrategy"/> 自动选择同步方式。
///
/// 两种策略：
/// <list type="bullet">
/// <item><term><see cref="FenceStrategy.AsyncCallback"/></term>
///   <description>帧间同步：GPU signal 后异步回调唤醒 CPU，主线程不阻塞。
///   适合 triple-buffer 帧间保护（CPU 可流水线准备下一帧）。</description></item>
/// <item><term><see cref="FenceStrategy.BlockingWait"/></term>
///   <description>数据依赖同步：CPU 阻塞等 GPU signal 到位。
///   适合游戏 CPU 必须读 GPU 结果的语义必需同步点（如 occlusion query 回读、
///   transform feedback 回读）。比 <c>WaitUntilCompleted</c> 更精确——只等特定 value。</description></item>
/// </list>
/// </summary>
public enum FenceStrategy
{
    /// <summary>帧间：异步回调，主线程不阻塞（推荐默认）。</summary>
    AsyncCallback,
    /// <summary>数据依赖：阻塞等待 GPU signal（语义必需的 CPU 读 GPU 结果）。</summary>
    BlockingWait,
}

/// <summary>
/// GPU fence 句柄。创建后调 <see cref="Signal"/> 在 GPU 命令里编码 signal，
/// 然后用 <see cref="WaitAsync"/>（帧间）或 <see cref="Wait"/>（数据依赖）感知完成。
/// 用完 Dispose 归还资源。
/// </summary>
public sealed class GpuFence : IDisposable
{
    private readonly SharedEventPool _pool;
    private readonly SharedEventSlot _slot;
    private bool _signaled;  // 是否已调 Signal
    private bool _disposed;

    private GpuFence(SharedEventPool pool, SharedEventSlot slot)
    {
        _pool = pool;
        _slot = slot;
    }

    /// <summary>从池分配一个 fence。</summary>
    public static GpuFence Create(SharedEventPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return new GpuFence(pool, pool.Acquire());
    }

    /// <summary>关联的 SharedEvent（用于 <c>EncodeSignalEvent</c>）。</summary>
    public MetalSharedEvent Event => _slot.Event;

    /// <summary>分配到的 signaledValue。</summary>
    public ulong Value => _slot.Value;

    /// <summary>在 GPU 命令缓冲里编码 signal（GPU 执行到此处时标记本 fence 完成）。</summary>
    public void Signal(MetalCommandBuffer cmdbuf)
    {
        ArgumentNullException.ThrowIfNull(cmdbuf);
        if (_signaled) throw new InvalidOperationException("GpuFence 已 signal，不能重复。");
        cmdbuf.EncodeSignalEvent(_slot.Event, _slot.Value);
        _signaled = true;
    }

    /// <summary>在 GPU 命令缓冲里编码 wait（GPU 执行到此处时阻塞等本 fence 完成）。</summary>
    public void GpuWait(MetalCommandBuffer cmdbuf)
    {
        ArgumentNullException.ThrowIfNull(cmdbuf);
        cmdbuf.EncodeWaitForEvent(_slot.Event, _slot.Value);
    }

    /// <summary>
    /// 帧间策略：异步回调。GPU signal 到位时在 listener 后台线程触发 <paramref name="onComplete"/>，
    /// 主线程不阻塞。适合 triple-buffer 帧间保护。
    /// 回调触发后 fence 自动 Dispose。
    /// </summary>
    public void WaitAsync(Action onComplete)
    {
        if (!_signaled) throw new InvalidOperationException("必须先 Signal 再 WaitAsync。");
        _slot.RegisterCompletionCallback(onComplete);
    }

    /// <summary>
    /// 数据依赖策略：阻塞等待 GPU signal。适合游戏 CPU 必须读 GPU 结果的语义必需同步点。
    /// 相比 <c>MetalCommandBuffer.WaitUntilCompleted</c>，只等特定 value（更精确唤醒）。
    /// 完成后 fence 自动 Dispose。
    /// </summary>
    /// <param name="timeoutMs">超时毫秒，0=无限。</param>
    /// <returns>true=完成；false=超时。</returns>
    public bool Wait(ulong timeoutMs = 0)
    {
        if (!_signaled) throw new InvalidOperationException("必须先 Signal 再 Wait。");
        return _slot.Wait(timeoutMs);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slot.Dispose();
    }
}
