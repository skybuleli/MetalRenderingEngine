using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLLibrary 封装：一束已编译的 GPU 函数，按 entry name 取出 <see cref="MetalFunction"/>。
/// </summary>
public sealed class MetalLibrary : MetalObject
{
    internal MetalLibrary(nuint handle) { SetNativeHandle(handle); }

    /// <summary>对应 ObjC -[MTLLibrary newFunctionWithName:]。</summary>
    public MetalFunction NewFunction(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        nuint h = MetalBridge.MTLLibrary_newFunctionWithName(Handle, name);
        if (h == 0) throw new MetalException($"MTLLibrary newFunctionWithName(\"{name}\") not found.");
        return new MetalFunction(h);
    }
}

/// <summary>MTLFunction 封装；除句柄外无额外状态。</summary>
public sealed class MetalFunction : MetalObject
{
    internal MetalFunction(nuint handle) { SetNativeHandle(handle); }
}
