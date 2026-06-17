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

    /// <summary>查询当前鼠标位置和按钮状态。返回按钮掩码，x/y 为像素坐标。</summary>
    [LibraryImport(LibraryName, EntryPoint = "SDL_GetMouseState")]
    public static partial uint GetMouseState(out float x, out float y);

    /// <summary>查询窗口逻辑尺寸（points，不含 DPI 缩放）。</summary>
    [LibraryImport(LibraryName, EntryPoint = "SDL_GetWindowSize")]
    public static partial void GetWindowSize(nint window, out int w, out int h);

    /// <summary>查询窗口像素尺寸（pixels，含 DPI 缩放）。
    /// 用于 Metal drawable 尺寸匹配。</summary>
    [LibraryImport(LibraryName, EntryPoint = "SDL_GetWindowSizeInPixels")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool GetWindowSizeInPixels(nint window, out int w, out int h);

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

    /// <summary>SDL_EVENT_WINDOW_RESIZED = 0x206。</summary>
    public const uint SDL_EVENT_WINDOW_RESIZED = 0x206;
    /// <summary>SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED = 0x207。</summary>
    public const uint SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED = 0x207;

    /// <summary>SDL_EVENT_KEY_DOWN = 0x300。</summary>
    public const uint SDL_EVENT_KEY_DOWN = 0x300;
    /// <summary>SDL_EVENT_KEY_UP = 0x301。</summary>
    public const uint SDL_EVENT_KEY_UP = 0x301;
    /// <summary>SDL_EVENT_TEXT_INPUT = 0x302。</summary>
    public const uint SDL_EVENT_TEXT_INPUT = 0x302;

    /// <summary>SDL_EVENT_MOUSE_MOTION = 0x400。</summary>
    public const uint SDL_EVENT_MOUSE_MOTION = 0x400;
    /// <summary>SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401。</summary>
    public const uint SDL_EVENT_MOUSE_BUTTON_DOWN = 0x401;
    /// <summary>SDL_EVENT_MOUSE_BUTTON_UP = 0x402。</summary>
    public const uint SDL_EVENT_MOUSE_BUTTON_UP = 0x402;
    /// <summary>SDL_EVENT_MOUSE_WHEEL = 0x403。</summary>
    public const uint SDL_EVENT_MOUSE_WHEEL = 0x403;

    // Mouse button masks (SDL_BUTTON_MASK(X) = 1u << (X-1))
    public const uint SDL_BUTTON_LMASK = 1u << 0;   // Left
    public const uint SDL_BUTTON_MMASK = 1u << 1;   // Middle
    public const uint SDL_BUTTON_RMASK = 1u << 2;   // Right
    public const uint SDL_BUTTON_X1MASK = 1u << 3;  // X1
    public const uint SDL_BUTTON_X2MASK = 1u << 4;  // X2

    // ============================================================
    //  SDL_Event（128 字节 union，必须与 C 端大小一致）
    // ============================================================

    /// <summary>
    /// SDL3 的 SDL_Event 是 128 字节的 union。
    /// 前 4 字节为 event type（Uint32），其余为各事件类型的 payload。
    /// Phase 3.5 扩展：添加 Mouse / Key / Text 子结构。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct SDL_Event
    {
        [FieldOffset(0)]  public uint Type;
        [FieldOffset(0)]  public SDL_MouseMotionEvent Motion;
        [FieldOffset(0)]  public SDL_MouseButtonEvent Button;
        [FieldOffset(0)]  public SDL_MouseWheelEvent Wheel;
        [FieldOffset(0)]  public SDL_KeyboardEvent Key;
        [FieldOffset(0)]  public SDL_TextInputEvent Text;
    }

    // ── 鼠标移动 ──
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MouseMotionEvent
    {
        public uint Type;       // 0
        public uint Reserved;   // 4
        public ulong Timestamp; // 8
        public uint WindowID;   // 16
        public uint Which;      // 20
        public uint State;      // 24
        public float X;         // 28
        public float Y;         // 32
        public float Xrel;      // 36
        public float Yrel;      // 40
    }

    // ── 鼠标按键 ──
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MouseButtonEvent
    {
        public uint Type;       // 0
        public uint Reserved;   // 4
        public ulong Timestamp; // 8
        public uint WindowID;   // 16
        public uint Which;      // 20
        public byte Button;     // 24
        public byte State;      // 25
        public byte Clicks;     // 26
        public byte Padding;    // 27
        public float X;         // 28
        public float Y;         // 32
    }

    // ── 鼠标滚轮 ──
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_MouseWheelEvent
    {
        public uint Type;       // 0
        public uint Reserved;   // 4
        public ulong Timestamp; // 8
        public uint WindowID;   // 16
        public uint Which;      // 20
        public float X;         // 24
        public float Y;         // 28
        public uint Direction;  // 32
        public float MouseX;    // 36
        public float MouseY;    // 40
    }

    // ── 键盘 ──
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_KeyboardEvent
    {
        public uint Type;       // 0
        public uint Reserved;   // 4
        public ulong Timestamp; // 8
        public uint WindowID;   // 16
        public uint Which;      // 20
        public int Scancode;    // 24
        public int Key;         // 28
        public ushort Mod;      // 32
        public ushort Raw;      // 34
        public byte State;      // 36
        public byte Repeat;     // 37
        public byte Padding2;   // 38
        public byte Padding3;   // 39
    }

    // ── 文本输入 ──
    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_TextInputEvent
    {
        public uint Type;       // 0
        public uint Reserved;   // 4
        public ulong Timestamp; // 8
        public uint WindowID;   // 16
        // text[32] 在偏移 20 处，但我们只需要 unsafe 访问
        // 简化：通过 unsafe pointer 读取
    }

    // ── 按键 modifier ──
    public enum SDL_Keymod : ushort
    {
        None  = 0x0000,
        Shift = 0x0001,
        Ctrl  = 0x0040,
        Alt   = 0x0100,
    }

    // ── 事件类型枚举（内部使用）──
    public enum SDL_EventType : uint
    {
        Quit                   = SDL_EVENT_QUIT,
        WindowResized          = SDL_EVENT_WINDOW_RESIZED,
        WindowPixelSizeChanged = SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED,
        KeyDown                = SDL_EVENT_KEY_DOWN,
        KeyUp                  = SDL_EVENT_KEY_UP,
        TextInput              = SDL_EVENT_TEXT_INPUT,
        MouseWheel             = SDL_EVENT_MOUSE_WHEEL,
    }
}
