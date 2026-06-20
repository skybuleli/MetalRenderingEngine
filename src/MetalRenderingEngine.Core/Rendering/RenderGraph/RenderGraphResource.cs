using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering.RenderGraph;

/// <summary>
/// Render Graph 中的资源访问声明。
/// 记录一个资源被某个 pass 如何访问，用于依赖分析和自动 UseResource 插入。
/// </summary>
public readonly record struct ResourceAccess
{
    /// <summary>资源句柄（MetalTexture / MetalBuffer 等任何 MetalObject 子类）。</summary>
    public MetalObject Resource { get; init; }

    /// <summary>访问类型：Read / Write / Sample。</summary>
    public MTLResourceUsage Usage { get; init; }

    /// <summary>访问发生在哪个渲染阶段（Vertex / Fragment / 两者）。</summary>
    public MTLRenderStages Stages { get; init; }

    /// <summary>用户可选的调试名（不影响功能）。</summary>
    public string? Name { get; init; }
}
