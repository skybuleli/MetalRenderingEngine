using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Platform;

/// <summary>
/// 原生 macOS NSWindow + CAMetalLayer 封装（零 SDL3 依赖）。
/// 通过 bridge.m 的 Cocoa_CreateMetalWindow 创建窗口，
/// 直接暴露 NSView / CAMetalLayer 句柄供 ImGui 后端和引擎使用。
/// </summary>
public sealed class NativeWindow : IDisposable
{
    private nuint _windowHandle;   // NSWindow（retained，需释放）
    private nuint _layerHandle;    // CAMetalLayer（retained，需释放）
    private nuint _viewHandle;     // NSView（不持有，由 window 管理）
    private bool _disposed;

    /// <summary>CAMetalLayer 原生句柄（可直接传给 MetalLayer 封装类）。</summary>
    public nuint LayerHandle => _layerHandle;

    /// <summary>NSView 原生句柄（传给 ImGuiImplOSX.Init；不持有引用）。</summary>
    public nuint ViewHandle => _viewHandle;

    /// <summary>NSWindow 原生句柄。</summary>
    public nuint WindowHandle => _windowHandle;

    /// <summary>创建原生 NSWindow 并挂载 CAMetalLayer。</summary>
    public static NativeWindow Create(string title, int width, int height)
    {
        unsafe
        {
            nuint layerHandle = 0;
            nuint windowHandle = MetalBridge.Cocoa_CreateMetalWindow(
                title, (float)width, (float)height, &layerHandle);
            if (windowHandle == 0)
                throw new InvalidOperationException("Cocoa_CreateMetalWindow 返回 null，窗口创建失败。");
            if (layerHandle == 0)
                throw new InvalidOperationException("Cocoa_CreateMetalWindow 未返回 CAMetalLayer。");

            nuint viewHandle = MetalBridge.Cocoa_WindowContentView(windowHandle);
            if (viewHandle == 0)
                throw new InvalidOperationException("Cocoa_WindowContentView 返回 null。");

            return new NativeWindow(windowHandle, layerHandle, viewHandle);
        }
    }

    private NativeWindow(nuint windowHandle, nuint layerHandle, nuint viewHandle)
    {
        _windowHandle = windowHandle;
        _layerHandle = layerHandle;
        _viewHandle = viewHandle;
    }

    /// <summary>
    /// 轮询一次 Cocoa 事件队列。
    /// 返回 true 表示用户请求关闭（点击关闭按钮 / 按 ESC）。
    /// </summary>
    public bool PollShouldClose()
    {
        return MetalBridge.Cocoa_PollEvents() != 0;
    }

    /// <summary>
    /// 获取视图的当前 drawable 尺寸（像素，已考虑 HiDPI）。
    /// </summary>
    public (int Width, int Height) GetDrawableSize()
    {
        unsafe
        {
            float w = 0, h = 0;
            MetalBridge.Cocoa_ViewDrawableSize(_viewHandle, &w, &h);
            return ((int)w, (int)h);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // layer 和 window 都是 retained 句柄，需手动释放
            if (_layerHandle != 0)
            {
                MetalBridge.NSObject_release(_layerHandle);
                _layerHandle = 0;
            }
            if (_windowHandle != 0)
            {
                MetalBridge.NSObject_release(_windowHandle);
                _windowHandle = 0;
            }
            _viewHandle = 0; // view 由 window 持有，不需要单独释放
        }
    }
}
