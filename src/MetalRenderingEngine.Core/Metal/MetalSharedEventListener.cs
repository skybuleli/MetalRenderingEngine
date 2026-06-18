using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLSharedEventListener 封装。内部启动后台 CFRunLoop 线程承载
/// <see cref="MetalSharedEvent.NotifyListener"/> 的异步回调。
/// 释放时停止后台 runloop 并 join 线程（<see cref="MetalObject.ReleaseHandle"/>）。
/// 参照 DXMT winemetal_unix.c:2471-2498。
/// </summary>
public sealed class MetalSharedEventListener : MetalObject
{
    internal MetalSharedEventListener(nuint handle) { SetNativeHandle(handle); }

    /// <summary>创建 listener（启动后台 runloop 线程）。</summary>
    public static MetalSharedEventListener Create()
    {
        nuint h = MetalBridge.MTLSharedEventListener_create();
        if (h == 0) throw new MetalException("MTLSharedEventListener_create returned null.");
        return new MetalSharedEventListener(h);
    }

    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            MetalBridge.MTLSharedEventListener_release((nuint)(nint)handle);
            SetHandle(IntPtr.Zero);
        }
        return true;
    }
}
