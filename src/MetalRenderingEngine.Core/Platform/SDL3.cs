using System.Runtime.InteropServices;

namespace MetalRenderingEngine.Platform;

/// <summary>
/// SDL3 最小 P/Invoke 绑定（仅 Phase 2 所需函数）。
/// 目标：零 NuGet 依赖，手写仅覆盖 window + Metal + event 基础。
/// 签名严格对应 SDL 3.4.0 头文件。
/// </summary>
public static partial class SDL3
{
    public const string LibraryName = "libSDL3";

    // ============================================================
    //  初始化 / 退出
    // ============================================================

    /// <summary>SDL_Init 返回 bool（C _Bool，1字节）；flags 为 SDL_InitFlags（Uint32）。</summary>
    [LibraryImport(LibraryName, EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool Init(uint flags);

    [LibraryImport(LibraryName, EntryPoint = "SDL_Quit")]
    public static partial void Quit();

    // ============================================================
    //  窗口
    // ============================================================

    /// <summary>SDL3 的 CreateWindow 不再接受 x/y 位置参数；flags 为 SDL_WindowFlags（Uint64）。</summary>
    [LibraryImport(LibraryName, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateWindow(string title, int w, int h, ulong flags);

    [LibraryImport(LibraryName, EntryPoint = "SDL_DestroyWindow")]
    public static partial void DestroyWindow(nint window);

    // ============================================================
    //  事件
    // ============================================================

    /// <summary>SDL3 的 PollEvent 返回 bool（非 int）。</summary>
    [LibraryImport(LibraryName, EntryPoint = "SDL_PollEvent")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool PollEvent(out SDL_Event e);

    // ============================================================
    //  Metal 视图
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "SDL_Metal_CreateView")]
    public static partial nint Metal_CreateView(nint window);

    [LibraryImport(LibraryName, EntryPoint = "SDL_Metal_GetLayer")]
    public static partial nint Metal_GetLayer(nint view);

    [LibraryImport(LibraryName, EntryPoint = "SDL_Metal_DestroyView")]
    public static partial void Metal_DestroyView(nint view);

    // ============================================================
    //  错误
    // ============================================================

    /// <summary>返回 const char*（UTF-8），由 SDL 内部管理，调用方无需释放。</summary>
    [LibraryImport(LibraryName, EntryPoint = "SDL_GetError")]
    public static partial nint GetError();

    /// <summary>将 SDL_GetError 返回的 C 字符串转为 C# string。</summary>
    public static string GetErrorString()
    {
        nint ptr = GetError();
        return ptr == nint.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";
    }

    // ============================================================
    //  常量
    // ============================================================

    /// <summary>SDL_INIT_VIDEO（Uint32）= 0x00000020u，隐含 SDL_INIT_EVENTS。</summary>
    public const uint SDL_INIT_VIDEO = 0x00000020u;

    /// <summary>SDL_WINDOW_METAL（Uint64）= 0x20000000。</summary>
    public const ulong SDL_WINDOW_METAL = 0x0000000020000000UL;

    /// <summary>SDL_WINDOW_HIGH_PIXEL_DENSITY（Uint64）= 0x2000。</summary>
    public const ulong SDL_WINDOW_HIGH_PIXEL_DENSITY = 0x0000000000002000UL;

    /// <summary>SDL_WINDOW_RESIZABLE = 0x20。</summary>
    public const ulong SDL_WINDOW_RESIZABLE = 0x0000000000000020UL;

    /// <summary>SDL_EVENT_QUIT = 0x100（用户请求关闭窗口）。</summary>
    public const uint SDL_EVENT_QUIT = 0x100;

    // ============================================================
    //  SDL_Event（128 字节 union，必须与 C 端大小一致）
    // ============================================================

    /// <summary>
    /// SDL3 的 SDL_Event 是 128 字节的 union。
    /// 前 4 字节为 event type（Uint32），其余为各事件类型的 payload。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct SDL_Event
    {
        /// <summary>事件类型（Uint32），位于 union 起始位置。</summary>
        [FieldOffset(0)]
        public uint Type;
    }
}
