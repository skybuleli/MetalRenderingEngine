namespace MetalRenderingEngine.Platform;

/// <summary>
/// SDL3 窗口 + CAMetalLayer 封装。
/// 通过 SDL3 创建窗口，再用 SDL_Metal_CreateView / SDL_Metal_GetLayer 拿到 CAMetalLayer 交给 bridge 层。
/// </summary>
public sealed class SDL3Window : IDisposable
{
    private nint _window;
    private nint _metalView;
    private nuint _layerHandle;
    private bool _disposed;

    /// <summary>CAMetalLayer 句柄（bridge 层可直接使用）。</summary>
    public nuint LayerHandle => _layerHandle;

    /// <summary>创建 SDL3 窗口并初始化 Metal 视图。</summary>
    public static SDL3Window Create(string title, int width, int height)
    {
        // 1) 初始化 SDL3 视频子系统（隐含 events）
        if (!SDL3.Init(SDL3.SDL_INIT_VIDEO))
        {
            string err = SDL3.GetErrorString();
            throw new InvalidOperationException($"SDL_Init(SDL_INIT_VIDEO) failed: {err}");
        }

        // 2) 创建窗口
        ulong flags = SDL3.SDL_WINDOW_METAL | SDL3.SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL3.SDL_WINDOW_RESIZABLE;
        nint window = SDL3.CreateWindow(title, width, height, flags);
        if (window == nint.Zero)
        {
            string err = SDL3.GetErrorString();
            SDL3.Quit();
            throw new InvalidOperationException($"SDL_CreateWindow failed: {err}");
        }

        // 3) 创建 Metal 视图
        nint metalView = SDL3.Metal_CreateView(window);
        if (metalView == nint.Zero)
        {
            string err = SDL3.GetErrorString();
            SDL3.DestroyWindow(window);
            SDL3.Quit();
            throw new InvalidOperationException($"SDL_Metal_CreateView failed: {err}");
        }

        // 4) 获取 CAMetalLayer
        nint layerPtr = SDL3.Metal_GetLayer(metalView);
        if (layerPtr == nint.Zero)
        {
            SDL3.Metal_DestroyView(metalView);
            SDL3.DestroyWindow(window);
            SDL3.Quit();
            throw new InvalidOperationException("SDL_Metal_GetLayer returned null.");
        }

        return new SDL3Window(window, metalView, (nuint)layerPtr);
    }

    private SDL3Window(nint window, nint metalView, nuint layerHandle)
    {
        _window = window;
        _metalView = metalView;
        _layerHandle = layerHandle;
    }

    /// <summary>
    /// 轮询事件队列。返回 true 表示用户请求退出（关闭窗口 / Cmd+Q）。
    /// </summary>
    public bool PollShouldClose()
    {
        while (SDL3.PollEvent(out var e))
        {
            if (e.Type == SDL3.SDL_EVENT_QUIT)
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_metalView != nint.Zero)
            {
                SDL3.Metal_DestroyView(_metalView);
                _metalView = nint.Zero;
            }
            if (_window != nint.Zero)
            {
                SDL3.DestroyWindow(_window);
                _window = nint.Zero;
            }
            SDL3.Quit();
            _layerHandle = 0;
        }
    }
}
