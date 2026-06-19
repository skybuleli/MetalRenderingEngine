using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering;

/// <summary>
/// Phase 8D: 渲染管线构建器。
/// 链式构造 <see cref="PipelineDescriptor"/>，含参数校验，
/// 最终通过 <see cref="Build(MetalDevice, MetalFunction, MetalFunction)"/> 产出 <see cref="MetalRenderPipelineState"/>。
/// </summary>
public sealed class PipelineBuilder
{
    private readonly RenderPipelineDescBuilder _descBuilder = new();

    /// <summary>添加颜色附件。</summary>
    public PipelineBuilder WithColorAttachment(int index, MTLPixelFormat format)
    {
        _descBuilder.WithColorAttachment(index, format);
        return this;
    }

    /// <summary>添加带 blend 的颜色附件。</summary>
    public PipelineBuilder WithBlendedColorAttachment(int index, MTLPixelFormat format,
        MTLBlendFactor srcRgb = MTLBlendFactor.SourceAlpha,
        MTLBlendFactor dstRgb = MTLBlendFactor.OneMinusSourceAlpha)
    {
        _descBuilder.WithBlendedColorAttachment(index, format, srcRgb, dstRgb);
        return this;
    }

    /// <summary>设置深度附件格式。</summary>
    public PipelineBuilder WithDepth(MTLPixelFormat format)
    {
        _descBuilder.WithDepth(format);
        return this;
    }

    /// <summary>设置模板附件格式。</summary>
    public PipelineBuilder WithStencil(MTLPixelFormat format)
    {
        _descBuilder.WithStencil(format);
        return this;
    }

    /// <summary>设置采样数（MSAA）。</summary>
    public PipelineBuilder WithSampleCount(int sampleCount)
    {
        _descBuilder.WithSampleCount(sampleCount);
        return this;
    }

    /// <summary>设置顶点描述符。</summary>
    public PipelineBuilder WithVertexDescriptor(in WMTVertexDescriptor vertexDescriptor)
    {
        _descBuilder.WithVertexDescriptor(vertexDescriptor);
        return this;
    }

    /// <summary>构建不可变的描述符（含校验）。</summary>
    public WMTRenderPipelineDesc BuildDescriptor()
    {
        var desc = _descBuilder.Build();
        Validate(desc);
        return desc;
    }

    /// <summary>构建并创建 MetalRenderPipelineState。</summary>
    public MetalRenderPipelineState Build(MetalDevice device, MetalFunction vertexFunction, MetalFunction fragmentFunction)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(vertexFunction);
        ArgumentNullException.ThrowIfNull(fragmentFunction);

        var desc = BuildDescriptor();
        return device.NewRenderPipelineState(vertexFunction, fragmentFunction, desc);
    }

    private static void Validate(WMTRenderPipelineDesc desc)
    {
        if (desc.ColorCount < 1)
            throw new InvalidOperationException("管线至少需要一个颜色附件。");

        for (int i = 0; i < desc.ColorCount; i++)
        {
            if (desc.ColorAttachmentAt(i).PixelFormat == (int)MTLPixelFormat.Invalid)
                throw new InvalidOperationException($"颜色附件 {i} 的像素格式未设置。");
        }

        // MSAA 采样数必须是 1、2、4 或 8
        if (desc.SampleCount is not (1 or 2 or 4 or 8))
            throw new InvalidOperationException($"SampleCount 必须是 1/2/4/8，当前为 {desc.SampleCount}。");
    }
}
