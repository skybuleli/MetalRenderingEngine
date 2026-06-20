using System.Runtime.CompilerServices;
using MetalRenderingEngine.Metal;

namespace MetalRenderingEngine.Rendering.RenderGraph;

/// <summary>
/// 轻量版 Render Graph：声明式 pass 定义 + 依赖分析自动排序 + 自动 UseResource。
/// </summary>
/// <remarks>
/// <para>典型用法：</para>
/// <code>
/// var graph = new RenderGraph();
/// graph.AddPass("ShadowMap")
///      .Writes(shadowDepth, MTLRenderStages.Vertex)
///      .WithRenderPassDesc(shadowPassDesc)
///      .Record(rec => { /* shadow draw calls */ });
/// graph.AddPass("GBuffer")
///      .Writes(gbufferTex, MTLRenderStages.Fragment)
///      .WithRenderPassDesc(gbufferPassDesc)
///      .Record(rec => { /* gbuffer draw calls */ });
/// graph.AddPass("Lighting")
///      .Reads(gbufferTex, MTLRenderStages.Fragment)
///      .Reads(shadowDepth, MTLRenderStages.Fragment)
///      .Writes(finalColor, MTLRenderStages.Fragment)
///      .WithRenderPassDesc(lightingPassDesc)
///      .Record(rec => { /* fullscreen quad */ });
/// graph.Execute(recorder);
/// </code>
/// <para>设计原则：</para>
/// <list type="bullet">
/// <item>不创建/销毁任何 Metal 资源，只做调度</item>
/// <item>不修改 ICommandRecorder 接口，是 recorder 的上层消费者</item>
/// <item>UseResource 只自动插入 Inputs（render target attachment 通过 PassDesc 隐式声明 residency）</item>
/// </list>
/// </remarks>
public sealed class RenderGraph
{
    private readonly List<RenderGraphPassBuilder> _builders = new();
    private IReadOnlyList<RenderGraphPass>? _compiled;

    /// <summary>添加一个 pass 并返回其 builder。</summary>
    /// <param name="name">Pass 调试名（用于日志和拓扑排序可视化）。</param>
    public RenderGraphPassBuilder AddPass(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _compiled = null; // 新增 pass 后失效缓存
        var builder = new RenderGraphPassBuilder(name);
        _builders.Add(builder);
        return builder;
    }

    /// <summary>
    /// 编译并执行所有 pass。
    /// 自动 BeginFrame/EndFrame 管理帧边界。
    /// </summary>
    /// <remarks>
    /// 步骤：Build → TopologicalSort → BeginFrame → 逐 pass（BeginRenderPass + UseResource + Record + EndRenderPass）→ EndFrame。
    /// 适用于离屏渲染场景。窗口模式请使用 <see cref="ExecutePasses"/>。
    /// </remarks>
    public void Execute(ICommandRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var sorted = Compile();

        recorder.BeginFrame();
        foreach (var pass in sorted)
        {
            ExecutePass(recorder, pass);
        }
        recorder.EndFrame();
    }

    /// <summary>
    /// 仅执行 pass，不管理帧边界（不自动 BeginFrame/EndFrame）。
    /// 适用于窗口模式：用户自行管理 BeginFrame → ExecutePasses → PresentDrawable → EndFrame。
    /// </summary>
    public void ExecutePasses(ICommandRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        var sorted = Compile();
        foreach (var pass in sorted)
        {
            ExecutePass(recorder, pass);
        }
    }

    /// <summary>
    /// 仅编译不执行，返回拓扑排序后的 pass 列表（用于测试/可视化）。
    /// </summary>
    public IReadOnlyList<RenderGraphPass> Compile()
    {
        if (_compiled is not null)
            return _compiled;

        if (_builders.Count == 0)
            throw new InvalidOperationException("Render Graph 无 pass。请调用 AddPass() 添加至少一个 pass。");

        var passes = new List<RenderGraphPass>(_builders.Count);
        foreach (var b in _builders)
            passes.Add(b.Build());

        _compiled = TopologicalSort(passes);
        return _compiled;
    }

    // ────────────────── 内部实现 ──────────────────

    /// <summary>执行单个 pass：BeginRenderPass + 自动 UseResource + RecordAction + EndRenderPass。</summary>
    private static void ExecutePass(ICommandRecorder recorder, RenderGraphPass pass)
    {
        // 复制到局部变量以满足 in 引用的不可变约束
        var passDesc = pass.PassDesc;
        recorder.BeginRenderPass(in passDesc);

        // 自动插入 UseResource（所有声明的输入资源）
        foreach (var input in pass.Inputs)
        {
            recorder.UseResource(input.Resource, input.Usage, input.Stages);
        }

        // 用户录制回调
        pass.RecordAction!(recorder);

        recorder.EndRenderPass();
    }

    /// <summary>
    /// 基于 Kahn 算法的拓扑排序。
    /// </summary>
    /// <remarks>
    /// <para>两遍算法：</para>
    /// <list type="number">
    /// <item>预扫描：构建 lastProducer map（资源 → 最后写入该资源的 pass 索引）</item>
    /// <item>按声明序遍历：对每个 read 创建 lastProducer→consumer 边（RAW），对同资源 writer 按声明序链接（WAW）</item>
    /// </list>
    /// <para>传递性保证：只需链接最后一个 producer，更早的 writer 通过 WAW 链间接排序。</para>
    /// <para>支持声明顺序任意：consumer 可先于 producer 声明，算法仍生成正确排序。</para>
    /// <para>外部资源（不被任何 pass 写入）不产生依赖边。</para>
    /// </remarks>
    internal static List<RenderGraphPass> TopologicalSort(List<RenderGraphPass> passes)
    {
        int n = passes.Count;
        var lastWriter = new Dictionary<MetalObject, int>(ReferenceEqualityComparer.Instance);

        // 邻接表 + 入度
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new();
        var inDegree = new int[n];

        void AddEdge(int from, int to)
        {
            if (from != to && !adj[from].Contains(to))
            {
                adj[from].Add(to);
                inDegree[to]++;
            }
        }

        // 第一遍：前向扫描，处理 WAW + 构建 lastWriter map
        for (int i = 0; i < n; i++)
        {
            foreach (var output in passes[i].Outputs)
            {
                if (lastWriter.TryGetValue(output.Resource, out int prev))
                    AddEdge(prev, i);
                lastWriter[output.Resource] = i;
            }
        }

        // 第二遍：RAW 边（lastWriter 已是最终状态，每个 reader 只需链接最后一个 writer）
        for (int i = 0; i < n; i++)
        {
            foreach (var input in passes[i].Inputs)
            {
                if (lastWriter.TryGetValue(input.Resource, out int w))
                    AddEdge(w, i);
            }
        }

        // Kahn BFS（按声明序出队，保证无依赖 pass 保持声明顺序）
        var queue = new SortedSet<int>();
        for (int i = 0; i < n; i++)
            if (inDegree[i] == 0) queue.Add(i);

        var result = new List<RenderGraphPass>(n);
        while (queue.Count > 0)
        {
            int idx = queue.Min;
            queue.Remove(idx);
            result.Add(passes[idx]);
            foreach (int next in adj[idx])
            {
                if (--inDegree[next] == 0)
                    queue.Add(next);
            }
        }

        if (result.Count != n)
            throw new InvalidOperationException(
                "Render Graph 存在循环依赖，无法拓扑排序。请检查 pass 间的资源读写声明。");

        return result;
    }
}
