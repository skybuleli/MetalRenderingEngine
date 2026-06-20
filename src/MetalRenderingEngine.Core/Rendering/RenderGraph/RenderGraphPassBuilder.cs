using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering.RenderGraph;

/// <summary>
/// 流式构造 <see cref="RenderGraphPass"/>。
/// 用法：
/// <code>
/// graph.AddPass("ShadowMap")
///      .Writes(shadowDepthTex, MTLRenderStages.Vertex)
///      .Reads(sceneBuffer, MTLRenderStages.Vertex)
///      .WithRenderPassDesc(desc)
///      .Record(rec => { /* shadow draw calls */ });
/// </code>
/// </summary>
public sealed class RenderGraphPassBuilder
{
    private readonly RenderGraphPass _pass;

    internal RenderGraphPassBuilder(string name) => _pass = new RenderGraphPass(name);

    /// <summary>声明一个输入资源（pass 读取此资源）。</summary>
    /// <param name="resource">目标资源（MetalTexture / MetalBuffer 等）。</param>
    /// <param name="stages">访问发生在哪个渲染阶段。</param>
    /// <param name="name">可选的调试名。</param>
    public RenderGraphPassBuilder Reads(
        MetalObject resource,
        MTLRenderStages stages,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _pass._inputs.Add(new ResourceAccess
        {
            Resource = resource,
            Usage = MTLResourceUsage.Read,
            Stages = stages,
            Name = name,
        });
        return this;
    }

    /// <summary>声明一个输出资源（pass 写入此资源）。</summary>
    /// <param name="resource">目标资源。</param>
    /// <param name="stages">写入发生在哪个渲染阶段。</param>
    /// <param name="name">可选的调试名。</param>
    public RenderGraphPassBuilder Writes(
        MetalObject resource,
        MTLRenderStages stages,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _pass._outputs.Add(new ResourceAccess
        {
            Resource = resource,
            Usage = MTLResourceUsage.Write,
            Stages = stages,
            Name = name,
        });
        return this;
    }

    /// <summary>声明一个读写资源（同时出现在 Inputs 和 Outputs 中）。</summary>
    /// <param name="resource">目标资源。</param>
    /// <param name="stages">访问发生在哪个渲染阶段。</param>
    /// <param name="name">可选的调试名。</param>
    public RenderGraphPassBuilder ReadsWrites(
        MetalObject resource,
        MTLRenderStages stages,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _pass._inputs.Add(new ResourceAccess
        {
            Resource = resource,
            Usage = MTLResourceUsage.Read,
            Stages = stages,
            Name = name,
        });
        _pass._outputs.Add(new ResourceAccess
        {
            Resource = resource,
            Usage = MTLResourceUsage.Write,
            Stages = stages,
            Name = name,
        });
        return this;
    }

    /// <summary>设置 render pass 描述符（颜色/深度附件）。</summary>
    public RenderGraphPassBuilder WithRenderPassDesc(WMTRenderPassDesc desc)
    {
        _pass.PassDesc = desc;
        return this;
    }

    /// <summary>使用 <see cref="RenderPassBuilder"/> 流式构造描述符。</summary>
    public RenderGraphPassBuilder WithRenderPassBuilder(RenderPassBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _pass.PassDesc = builder.Build();
        return this;
    }

    /// <summary>设置录制回调（pass 体的命令录入逻辑）。</summary>
    /// <remarks>
    /// 框架在调用回调前已执行 BeginRenderPass，回调结束后自动执行 EndRenderPass。
    /// 回调内只需录入 SetPipelineState / SetVertexBytes / Draw 等命令。
    /// </remarks>
    public RenderGraphPassBuilder Record(Action<ICommandRecorder> action)
    {
        _pass.RecordAction = action ?? throw new ArgumentNullException(nameof(action));
        return this;
    }

    /// <summary>构建不可变的 pass 对象。缺少 Record 回调时抛异常。</summary>
    internal RenderGraphPass Build()
    {
        if (_pass.RecordAction is null)
            throw new InvalidOperationException($"Pass '{_pass.Name}' 缺少 Record() 回调。");
        return _pass;
    }
}
