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

    /// <summary>为指定 buffer index 创建 MTLArgumentEncoder。</summary>
    public MetalArgumentEncoder NewArgumentEncoder(ulong bufferIndex)
    {
        nuint h = MetalBridge.MTLFunction_newArgumentEncoder(Handle, bufferIndex);
        if (h == 0) throw new MetalException($"MTLFunction newArgumentEncoderWithBufferIndex({bufferIndex}) returned nil.");
        return new MetalArgumentEncoder(h);
    }
}

/// <summary>
/// MTLArgumentEncoder 封装。
/// 当前只暴露 Phase 10A 需要的最小能力：把 texture + sampler 编到 argument buffer。
/// </summary>
public sealed class MetalArgumentEncoder : MetalObject
{
    internal MetalArgumentEncoder(nuint handle) { SetNativeHandle(handle); }

    /// <summary>编码所需 argument buffer 总字节数。</summary>
    public ulong EncodedLength => MetalBridge.MTLArgumentEncoder_encodedLength(Handle);

    /// <summary>把 texture + sampler 编码到 argument buffer 指定偏移。</summary>
    public void EncodeTextureSampler(MetalBuffer argumentBuffer, ulong offset, MetalTexture texture, MetalSamplerState sampler)
    {
        ArgumentNullException.ThrowIfNull(argumentBuffer);
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(sampler);
        MetalBridge.MTLArgumentEncoder_encodeTextureSampler(Handle, argumentBuffer.Handle, offset, texture.Handle, sampler.Handle);
    }
}
