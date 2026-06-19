using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// Metal 资源扩展方法：MSC 4.0 argument-buffer 描述符生成。
/// </summary>
public static class MetalBufferExtensions
{
    /// <summary>
    /// 生成本 buffer 的 <see cref="UavDescriptor"/>（GPU 地址 + 长度 + 步长），
    /// 便于直接 <c>recorder.SetVertexBytes(buf.ToUavDescriptor(stride), index)</c>。
    /// </summary>
    /// <param name="buffer">目标 buffer。</param>
    /// <param name="stride">元素步长（字节）。0 或不传时使用 buffer 全长。</param>
    public static UavDescriptor ToUavDescriptor(this MetalBuffer buffer, ulong stride = 0)
        => new() { GpuAddress = buffer.GpuAddress, Length = buffer.Length, Stride = stride == 0 ? buffer.Length : stride };

    /// <summary>
    /// 生成 texture 的描述符堆条目（24 字节）。
    /// 对齐 MSC 官方 <c>IRDescriptorTableSetTexture</c>：
    /// +0(gpuVA)=0，+8(textureViewID)=texture.gpuResourceID，
    /// +16(metadata)=(minLODClamp 的 float 位模式) | ((uint64)metadata &lt;&lt; 32)。
    /// 普通 2D 非数组 SRV：minLODClamp=0、metadata=0。
    /// </summary>
    /// <param name="texture">目标 texture（需具备 ShaderRead usage）。</param>
    /// <param name="minLodClamp">最小 LOD clamp，默认 0。</param>
    public static TextureDescriptor ToTextureDescriptor(this MetalTexture texture, float minLodClamp = 0f)
    {
        // minLODClamp 的 float 位模式（与 C 端 *(uint32_t*)&minLODClamp 等价）
        uint minLodBits;
        unsafe { minLodBits = *(uint*)&minLodClamp; }
        return new TextureDescriptor
        {
            GpuVA = 0,
            TextureViewID = texture.GpuResourceID,
            Metadata = minLodBits,  // metadata=0 时高 32 位为 0
        };
    }

    /// <summary>
    /// 生成 sampler 的描述符堆条目（24 字节）。
    /// 对齐 MSC 官方 <c>IRDescriptorTableSetSampler</c>：
    /// +0(gpuVA)=sampler.gpuResourceID，+8(textureViewID)=0，+16(metadata)=lodBias 的 float 位模式。
    /// sampler 需创建时 supportArgumentBuffers=YES（bridge.m 已硬编码）。
    /// </summary>
    /// <param name="sampler">目标 sampler。</param>
    /// <param name="lodBias">LOD bias，默认 0。</param>
    public static SamplerDescriptor ToSamplerDescriptor(this MetalSamplerState sampler, float lodBias = 0f)
    {
        uint lodBiasBits;
        unsafe { lodBiasBits = *(uint*)&lodBias; }
        return new SamplerDescriptor
        {
            GpuVA = sampler.GpuResourceID,
            TextureViewID = 0,
            Metadata = lodBiasBits,
        };
    }
}
