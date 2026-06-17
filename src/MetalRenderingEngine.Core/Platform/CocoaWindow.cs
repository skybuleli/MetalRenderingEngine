using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Platform;

/// <summary>
/// Cocoa 窗口 + CAMetalLayer 的便捷封装。
/// 由于 SDL3 在当前环境初始化失败，临时使用 Cocoa 直接创建窗口。
/// </summary>
public sealed class CocoaWindow : IDisposable
{
    private nuint _windowHandle;
    private nuint _layerHandle;
    private bool _disposed;

    public nuint LayerHandle => _layerHandle;

    public static CocoaWindow Create(string title, float width, float height)
    {
        unsafe
        {
            nuint layerHandle = 0;
            nuint windowHandle = MetalBridge.Cocoa_CreateMetalWindow(title, width, height, &layerHandle);
            if (windowHandle == 0 || layerHandle == 0)
                throw new InvalidOperationException("Cocoa_CreateMetalWindow returned nil.");
            return new CocoaWindow(windowHandle, layerHandle);
        }
    }

    private CocoaWindow(nuint windowHandle, nuint layerHandle)
    {
        _windowHandle = windowHandle;
        _layerHandle = layerHandle;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Cocoa window 由 ARC 自动管理；无需显式释放
            _windowHandle = 0;
            _layerHandle = 0;
        }
    }

    /// <summary>
    /// 轮询一次 Cocoa 事件队列。返回 true 表示用户请求关闭（按 ESC 或关窗）。
    /// </summary>
    public bool PollShouldClose()
        => MetalBridge.Cocoa_PollEvents() != 0;
}
