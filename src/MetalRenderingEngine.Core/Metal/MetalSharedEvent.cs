using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLSharedEvent 封装。支持 CPU-GPU 跨设备同步：
/// <list type="bullet">
/// <item><see cref="SignaledValue"/>：CPU 读取当前 GPU 已 signal 的值（非阻塞）</item>
/// <item><see cref="WaitUntilSignaledValue"/>：CPU 阻塞等待</item>
/// <item><see cref="NotifyListener"/>：异步回调，GPU signal 到指定值时在 listener 线程触发</item>
/// <item>GPU 侧：<see cref="MetalCommandBuffer.EncodeSignalEvent"/> / <see cref="MetalCommandBuffer.EncodeWaitForEvent"/></item>
/// </list>
/// 与 <see cref="MetalFence"/>（纯 GPU、无值、CPU 无法等待）互补。
/// </summary>
public sealed class MetalSharedEvent : MetalObject
{
    // 持有回调委托，防止 GC 回收（bridge 侧只存裸函数指针，不持有托管对象引用）
    private readonly List<MetalBridge.SharedEventCallback> _liveCallbacks = new();

    internal MetalSharedEvent(nuint handle) { SetNativeHandle(handle); }

    /// <summary>当前已 signal 的值（CPU 侧读取，单调递增）。</summary>
    public ulong SignaledValue => MetalBridge.MTLSharedEvent_signaledValue(Handle);

    /// <summary>
    /// CPU 阻塞等待 event 达到 <paramref name="value"/>。
    /// </summary>
    /// <param name="value">目标值</param>
    /// <param name="timeoutMs">超时毫秒，0 表示无限等待</param>
    /// <returns>true=达到；false=超时</returns>
    public bool WaitUntilSignaledValue(ulong value, ulong timeoutMs = 0)
        => MetalBridge.MTLSharedEvent_waitUntilSignaledValue(Handle, value, timeoutMs) != 0;

    /// <summary>
    /// 注册异步通知：当 GPU signal 到 <paramref name="value"/> 时，在 <paramref name="listener"/>
    /// 的后台线程触发 <paramref name="callback"/>。回调内可设置 ManualResetEventSlim 等唤醒主线程。
    /// 注意：回调在 listener 后台线程触发，C# 端需线程安全；Metal 对同一 value 只通知一次。
    /// </summary>
    public void NotifyListener(MetalSharedEventListener listener, ulong value, Action<ulong> callback)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(callback);

        // 用 GCHandle 钉住 Action，bridge 回调时透传回 C#
        var gch = GCHandle.Alloc(callback);
        var cb = new MetalBridge.SharedEventCallback(NotifyCallbackShim);
        _liveCallbacks.Add(cb);  // 持有委托防 GC

        MetalBridge.MTLSharedEvent_notifyListener(
            Handle, listener.Handle, value, cb, (nuint)GCHandle.ToIntPtr(gch));
    }

    /// <summary>原生回调 shim：解 GCHandle 取出 Action 并调用，完成后 Free。</summary>
    private static void NotifyCallbackShim(nuint userData, ulong value)
    {
        var gch = GCHandle.FromIntPtr((IntPtr)userData);
        if (gch.Target is Action<ulong> cb)
        {
            try { cb(value); } catch { /* 吞异常，避免崩 listener 线程 */ }
        }
        gch.Free();
    }
}
