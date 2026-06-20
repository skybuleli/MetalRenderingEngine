namespace MetalRenderingEngine.Metal;

/// <summary>
/// 帧同步器：基于 MTLSharedEvent 的 triple-buffer 帧限速。
/// CPU 可领先 GPU 最多 <see cref="InFlightFrames"/> 帧，避免 GPU 饱和或 CPU 空等。
///
/// <para>典型用法：</para>
/// <code>
/// var sync = new FrameSync(device);
/// while (running) {
///     sync.WaitFrame();                              // 等待 N 帧前的 GPU 完成
///     recorder.BeginFrame();
///     // ... 录制渲染命令 ...
///     sync.SignalFrame(recorder.CommandBuffer);       // 编码 GPU signal
///     recorder.PresentDrawable(drawable);
///     recorder.Submit();
/// }
/// </code>
///
/// <para>当 MTLSharedEvent 不可用时（沙箱环境），降级为 <see cref="MetalCommandBuffer.WaitUntilCompleted"/>，
/// 功能正确但丧失流水线能力（每帧阻塞到 GPU 完成）。</para>
/// </summary>
public sealed class FrameSync : IDisposable
{
    /// <summary>最大可同时在飞的帧数。</summary>
    public int InFlightFrames { get; }

    private readonly MetalSharedEvent?[] _events;
    private readonly ulong[] _values;
    private int _index;
    private readonly bool _useSharedEvent;
    private bool _disposed;

    /// <summary>
    /// 创建帧同步器。
    /// </summary>
    /// <param name="device">Metal 设备。</param>
    /// <param name="inFlightFrames">最大同时在飞帧数（推荐 3，对应 triple-buffer）。</param>
    public FrameSync(MetalDevice device, int inFlightFrames = 3)
    {
        if (inFlightFrames < 1 || inFlightFrames > 8)
            throw new ArgumentOutOfRangeException(nameof(inFlightFrames), "应在 1..8 范围");
        ArgumentNullException.ThrowIfNull(device);

        InFlightFrames = inFlightFrames;
        _events = new MetalSharedEvent[inFlightFrames];
        _values = new ulong[inFlightFrames];

        // 尝试创建 SharedEvent（沙箱环境可能不可用）
        bool ok = true;
        for (int i = 0; i < inFlightFrames; i++)
        {
            try { _events[i] = device.NewSharedEvent(); }
            catch { ok = false; break; }
        }
        _useSharedEvent = ok;
    }

    /// <summary>是否使用 MTLSharedEvent（false = 降级为阻塞等待）。</summary>
    public bool HasSharedEvent => _useSharedEvent;

    /// <summary>当前帧索引（0..InFlightFrames-1 循环）。</summary>
    public int CurrentFrameIndex => _index;

    /// <summary>
    /// 等待最旧帧的 GPU 完成（CPU 侧阻塞）。在帧首调用。
    /// </summary>
    public void WaitFrame()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(FrameSync));
        if (!_useSharedEvent) return; // 降级模式：由 Submit 后的 WaitUntilCompleted 处理

        var evt = _events[_index];
        if (evt == null) return;

        ulong waitValue = _values[_index];
        if (waitValue == 0) return; // 前 InFlightFrames 帧无需等待

        // 阻塞直到 GPU 完成该帧
        evt.WaitUntilSignaledValue(waitValue, timeoutMs: 5000);
    }

    /// <summary>
    /// 在当前命令缓冲区编码 GPU signal 事件，并推进帧索引。
    /// 必须在 <c>Submit()</c> 之前调用（signal 需要编码到活跃的 command buffer）。
    /// </summary>
    public void SignalFrame(MetalCommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        if (_disposed) throw new ObjectDisposedException(nameof(FrameSync));

        if (_useSharedEvent)
        {
            ulong value = ++_values[_index];
            commandBuffer.EncodeSignalEvent(_events[_index]!, value);
        }

        // 推进到下一帧（环形）
        _index = (_index + 1) % InFlightFrames;
    }

    /// <summary>
    /// 降级模式的帧末处理：阻塞等待 GPU 完成。
    /// 仅在 <see cref="HasSharedEvent"/> 为 false 时有效，否则无操作。
    /// </summary>
    public void WaitUntilCompletedFallback(MetalCommandBuffer commandBuffer)
    {
        if (_useSharedEvent) return;
        commandBuffer.WaitUntilCompleted();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var evt in _events)
            evt?.Dispose();
    }
}
