using System.Text;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLDevice 封装。<see cref="CreateSystemDefault"/> 获取系统默认 GPU。
/// </summary>
public sealed class MetalDevice : MetalObject
{
    private MetalDevice(nuint handle) { SetNativeHandle(handle); }

    /// <summary>对应 ObjC MTLCreateSystemDefaultDevice()。</summary>
    public static MetalDevice CreateSystemDefault()
    {
        nuint h = MetalBridge.MTLDevice_createSystemDefault();
        if (h == 0) throw new MetalException("No default Metal device available on this system.");
        return new MetalDevice(h);
    }

    /// <summary>GPU 设备名称（如 "Apple M1"）。</summary>
    public string Name
    {
        get
        {
            unsafe
            {
                ulong need = MetalBridge.MTLDevice_name(Handle, null, 0);
                if (need <= 1) return string.Empty;
                Span<byte> buf = need <= 256 ? stackalloc byte[(int)need] : new byte[need];
                fixed (byte* p = buf) { MetalBridge.MTLDevice_name(Handle, p, need); }
                int len = buf.Length;
                while (len > 0 && buf[len - 1] == 0) len--;
                return Encoding.UTF8.GetString(buf[..len]);
            }
        }
    }

    /// <summary>Apple Silicon 上为 true（CPU/GPU 共享同一物理内存）。</summary>
    public bool HasUnifiedMemory => MetalBridge.MTLDevice_hasUnifiedMemory(Handle) != 0;

    /// <summary>对当前进程推荐的最大常驻 GPU 内存（字节）。</summary>
    public ulong RecommendedMaxWorkingSetSize => MetalBridge.MTLDevice_recommendedMaxWorkingSetSize(Handle);

    /// <summary>从 .metallib 字节流构造 MTLLibrary。</summary>
    public MetalLibrary NewLibrary(ReadOnlySpan<byte> metallibData)
    {
        if (metallibData.IsEmpty) throw new ArgumentException("metallib data is empty.", nameof(metallibData));
        unsafe
        {
            nuint err = 0;
            fixed (byte* p = metallibData)
            {
                nuint lib = MetalBridge.MTLDevice_newLibrary(Handle, p, (ulong)metallibData.Length, &err);
                if (lib == 0)
                {
                    throw MetalException.FromError("MTLDevice newLibraryWithData",
                        new MetalError(err));
                }
                return new MetalLibrary(lib);
            }
        }
    }

    /// <summary>构造 compute pipeline state。</summary>
    public MetalComputePipelineState NewComputePipelineState(MetalFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        unsafe
        {
            nuint err = 0;
            nuint pso = MetalBridge.MTLDevice_newComputePipelineState(Handle, function.Handle, &err);
            if (pso == 0)
            {
                throw MetalException.FromError("MTLDevice newComputePipelineStateWithFunction",
                    new MetalError(err));
            }
            return new MetalComputePipelineState(pso);
        }
    }

    /// <summary>构造 MTLBuffer；length 必须 &gt; 0。</summary>
    public MetalBuffer NewBuffer(ulong length, MTLResourceOptions options)
    {
        if (length == 0) throw new ArgumentOutOfRangeException(nameof(length), "length must be > 0");
        unsafe
        {
            var info = new WMTBufferInfo { Length = length, Options = (uint)options, Reserved = 0 };
            nuint h = MetalBridge.MTLDevice_newBuffer(Handle, &info);
            if (h == 0) throw new MetalException($"MTLDevice newBufferWithLength({length}) returned nil.");
            return new MetalBuffer(h, length);
        }
    }

    /// <summary>构造命令队列。</summary>
    public MetalCommandQueue NewCommandQueue()
    {
        nuint h = MetalBridge.MTLDevice_newCommandQueue(Handle);
        if (h == 0) throw new MetalException("MTLDevice newCommandQueue returned nil.");
        return new MetalCommandQueue(h);
    }

    public MetalRenderPipelineState NewRenderPipelineState(MetalFunction vertex, MetalFunction fragment, in WMTRenderPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(vertex);
        ArgumentNullException.ThrowIfNull(fragment);
        unsafe
        {
            nuint err = 0;
            WMTRenderPipelineDesc local = desc;
            nuint pso = MetalBridge.MTLDevice_newRenderPipelineState(Handle, vertex.Handle, fragment.Handle, &local, &err);
            if (pso == 0)
            {
                throw MetalException.FromError("MTLDevice newRenderPipelineStateWithDescriptor",
                    new MetalError(err));
            }
            return new MetalRenderPipelineState(pso);
        }
    }
}
