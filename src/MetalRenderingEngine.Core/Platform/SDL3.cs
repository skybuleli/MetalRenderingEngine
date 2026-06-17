using System.Runtime.InteropServices;

namespace MetalRenderingEngine.Platform;

/// <summary>
/// SDL3 最小 P/Invoke 绑定（仅 Phase 2 所需函数）。
/// 目标：零 NuGet 依赖，手写仅覆盖 window + Metal + event 基础。
/// </summary>
public static partial class SDL3
{
    public const string LibraryName = "libSDL3";

    [LibraryImport(LibraryName, EntryPoint = "SDL_Init")]
    public static partial int Init(uint flags);

    [LibraryImport(LibraryName, EntryPoint = "SDL_Quit")]
    public static partial void Quit();

    [LibraryImport(LibraryName, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CreateWindow(string title, int w, int h, ulong flags);

    [LibraryImport(LibraryName, EntryPoint = "SDL_DestroyWindow")]
    public static partial void DestroyWindow(nint window);

    [LibraryImport(LibraryName, EntryPoint = "SDL_PollEvent")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial int PollEvent(out SDL_Event e);

    [LibraryImport(LibraryName, EntryPoint = "SDL_Metal_CreateView")]
    public static partial nint Metal_CreateView(nint window);

    [LibraryImport(LibraryName, EntryPoint = "SDL_Metal_GetLayer")]
    public static partial nint Metal_GetLayer(nint view);

    [LibraryImport(LibraryName, EntryPoint = "SDL_Metal_DestroyView")]
    public static partial void Metal_DestroyView(nint view);

    [LibraryImport(LibraryName, EntryPoint = "SDL_GetError")]
    public static partial nint GetError();

    public const uint SDL_INIT_VIDEO = 0x00000020;
    public const ulong SDL_WINDOW_METAL = 0x0000000020000000;
    public const ulong SDL_WINDOW_HIGH_PIXEL_DENSITY = 0x0000000000002000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SDL_Event
    {
        public uint Type;
        public uint _padding1;
        public ulong _padding2;
        public ulong _padding3;
        public int _padding4;
        public uint _padding5;
    }

    public const uint SDL_EVENT_QUIT = 0x100;
}
