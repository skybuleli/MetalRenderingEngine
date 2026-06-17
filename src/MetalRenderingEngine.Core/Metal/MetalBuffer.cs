using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLBuffer 封装。Phase 1 仅暴露：CPU 端可寻址内存（<see cref="Contents"/>）
/// 与 Managed 模式下的脏区域上报（<see cref="DidModifyRange"/>）。
/// </summary>
public sealed class MetalBuffer : MetalObject
{
    /// <summary>构造时记录的字节数（与 native 端保持一致，避免每次重复跨越 P/Invoke）。</summary>
    public ulong Length { get; }

    internal MetalBuffer(nuint handle, ulong length)
    {
        SetNativeHandle(handle);
        Length = length;
    }

    /// <summary>
    /// 返回 CPU 可访问的内存裸指针；StorageModePrivate 时为 0（IntPtr.Zero）。
    /// 使用前请确保 buffer 用 StorageModeShared 或 StorageModeManaged 创建。
    /// </summary>
    public nint Contents => MetalBridge.MTLBuffer_contents(Handle);

    /// <summary>
    /// 64 位 GPU 虚拟地址（Apple Silicon 上恒为非零）。
    /// MSC 输出的 metallib 通过 top-level argument buffer 间接访问资源时使用，
    /// 详见 docs/slang-reflection-binding-design.md。
    /// </summary>
    public ulong GpuAddress => MetalBridge.MTLBuffer_gpuAddress(Handle);

    /// <summary>
    /// 以 <typeparamref name="T"/> 视角访问 buffer 全部内容（StorageModeShared/Managed 才有效）。
    /// </summary>
    public unsafe Span<T> AsSpan<T>() where T : unmanaged
    {
        nint p = Contents;
        if (p == IntPtr.Zero) throw new InvalidOperationException("Buffer has no CPU-accessible contents (StorageModePrivate?).");
        int count = checked((int)(Length / (ulong)sizeof(T)));
        return new Span<T>((void*)p, count);
    }

    /// <summary>Managed 模式下显式声明 CPU 端写入区间；其它模式 no-op。</summary>
    public void DidModifyRange(ulong offset, ulong length)
        => MetalBridge.MTLBuffer_didModifyRange(Handle, offset, length);
}
