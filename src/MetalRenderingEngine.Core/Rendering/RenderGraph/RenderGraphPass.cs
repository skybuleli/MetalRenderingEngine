using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering.RenderGraph;

/// <summary>
/// Render Graph 中单个 pass 的声明式定义。
/// 用户通过 <see cref="RenderGraphPassBuilder"/> 流式构造，声明输入/输出资源和录制逻辑。
/// </summary>
/// <remarks>
/// <para><b>输入资源</b>（<see cref="Inputs"/>）：pass 读取/采样的资源，框架自动插入 UseResource。</para>
/// <para><b>输出资源</b>（<see cref="Outputs"/>）：pass 写入的资源，用于依赖分析确定 pass 间顺序。
/// render target attachment 通过 <see cref="PassDesc"/> 的纹理句柄隐式声明 residency，
/// 无需额外的 UseResource。</para>
/// </remarks>
public sealed class RenderGraphPass
{
    /// <summary>Pass 调试名（用于日志和依赖图可视化）。</summary>
    public string Name { get; }

    /// <summary>Render pass 描述符（颜色/深度附件等）。</summary>
    public WMTRenderPassDesc PassDesc { get; internal set; }

    /// <summary>输入资源列表（只读/采样）。</summary>
    public IReadOnlyList<ResourceAccess> Inputs => _inputs;

    /// <summary>输出资源列表（写入/读写）。</summary>
    public IReadOnlyList<ResourceAccess> Outputs => _outputs;

    internal readonly List<ResourceAccess> _inputs = new();
    internal readonly List<ResourceAccess> _outputs = new();

    /// <summary>
    /// pass 的录制回调。接收 ICommandRecorder，用户在其中录入
    /// SetPipelineState / SetVertexBytes / Draw 等命令。
    /// 框架已在此之前调用 BeginRenderPass，在此之后调用 EndRenderPass。
    /// </summary>
    public Action<ICommandRecorder>? RecordAction { get; internal set; }

    internal RenderGraphPass(string name) => Name = name;

    /// <summary>返回 pass 名称。</summary>
    public override string ToString() => Name;
}
