using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Rendering.RenderGraph;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 11: Render Graph 拓扑排序单元测试。
/// 纯 C# 逻辑测试，不需要 GPU。
/// </summary>
public class RenderGraphTopologicalSortTests
{
    /// <summary>创建一个用于测试的 stub MetalObject（不实际分配 GPU 资源，不会调 NSObject_release）。</summary>
    private static MetalObject CreateStubResource()
    {
        return new StubMetalObject();
    }

    /// <summary>用于测试的 MetalObject stub（仅持有 fake handle，不释放 native）。</summary>
    private sealed class StubMetalObject : MetalObject
    {
        private static int s_counter;
        public StubMetalObject()
        {
            SetNativeHandle((nuint)Interlocked.Increment(ref s_counter));
        }
        protected override bool ReleaseHandle()
        {
            // 跳过 NSObject_release P/Invoke（fake handle 无对应 native 对象）
            SetHandle(IntPtr.Zero);
            return true;
        }
    }

    /// <summary>
    /// 线性链：A 写 R1，B 读 R1 写 R2，C 读 R2。
    /// 期望排序：A → B → C。
    /// </summary>
    [Fact]
    public void LinearChain_ABC()
    {
        var r1 = CreateStubResource();
        var r2 = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("A").Writes(r1, MTLRenderStages.Vertex).Record(_ => { });
        graph.AddPass("B").Reads(r1, MTLRenderStages.Vertex).Writes(r2, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("C").Reads(r2, MTLRenderStages.Fragment).Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal(3, sorted.Count);
        Assert.Equal("A", sorted[0].Name);
        Assert.Equal("B", sorted[1].Name);
        Assert.Equal("C", sorted[2].Name);
    }

    /// <summary>
    /// 无依赖：三个独立 pass，期望保持声明顺序。
    /// </summary>
    [Fact]
    public void NoDependency_PreservesDeclarationOrder()
    {
        var graph = new RenderGraph();
        graph.AddPass("First").Record(_ => { });
        graph.AddPass("Second").Record(_ => { });
        graph.AddPass("Third").Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal(3, sorted.Count);
        Assert.Equal("First", sorted[0].Name);
        Assert.Equal("Second", sorted[1].Name);
        Assert.Equal("Third", sorted[2].Name);
    }

    /// <summary>
    /// 循环依赖检测：A 写 R1 读 R2，B 写 R2 读 R1。
    /// 依赖边：B→A（B 写 R2，A 读 R2）+ A→B（A 写 R1，B 读 R1）= 环。
    /// 期望抛出 InvalidOperationException。
    /// </summary>
    [Fact]
    public void CycleDetection_ThrowsInvalidOperation()
    {
        var r1 = CreateStubResource();
        var r2 = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("A").Writes(r1, MTLRenderStages.Vertex).Reads(r2, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("B").Writes(r2, MTLRenderStages.Fragment).Reads(r1, MTLRenderStages.Vertex).Record(_ => { });

        Assert.Throws<InvalidOperationException>(() => graph.Compile());
    }

    /// <summary>
    /// 菱形依赖：A 写 R1/R2，B 读 R1 写 R3，C 读 R2 写 R4，D 读 R3/R4。
    /// 期望：A 在最前，D 在最后，B/C 在中间。
    /// </summary>
    [Fact]
    public void DiamondDependency()
    {
        var r1 = CreateStubResource();
        var r2 = CreateStubResource();
        var r3 = CreateStubResource();
        var r4 = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("A").Writes(r1, MTLRenderStages.Vertex).Writes(r2, MTLRenderStages.Vertex).Record(_ => { });
        graph.AddPass("B").Reads(r1, MTLRenderStages.Fragment).Writes(r3, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("C").Reads(r2, MTLRenderStages.Fragment).Writes(r4, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("D").Reads(r3, MTLRenderStages.Fragment).Reads(r4, MTLRenderStages.Fragment).Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal(4, sorted.Count);
        Assert.Equal("A", sorted[0].Name);
        Assert.Equal("D", sorted[3].Name);
        // B 和 C 的顺序不固定（都依赖 A，都被 D 依赖）
        var middleNames = new HashSet<string> { sorted[1].Name, sorted[2].Name };
        Assert.Contains("B", middleNames);
        Assert.Contains("C", middleNames);
    }

    /// <summary>
    /// WAW（写后写）：A 写 R1，B 也写 R1。
    /// 期望 A 在 B 前（按声明顺序）。
    /// </summary>
    [Fact]
    public void WAW_OrderPreserved()
    {
        var r1 = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("FirstWriter").Writes(r1, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("SecondWriter").Writes(r1, MTLRenderStages.Fragment).Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal(2, sorted.Count);
        Assert.Equal("FirstWriter", sorted[0].Name);
        Assert.Equal("SecondWriter", sorted[1].Name);
    }

    /// <summary>
    /// 单 pass 无依赖：直接通过。
    /// </summary>
    [Fact]
    public void SinglePass_NoDependency()
    {
        var graph = new RenderGraph();
        graph.AddPass("Only").Record(_ => { });

        var sorted = graph.Compile();
        Assert.Single(sorted);
        Assert.Equal("Only", sorted[0].Name);
    }

    /// <summary>
    /// 复杂 DAG 无环保证：多个 pass 交叉读写同一资源，算法始终生成有效 DAG。
    /// 验证排序结果满足所有 RAW/WAW 约束。
    /// </summary>
    [Fact]
    public void ComplexDAG_AllConstraintsSatisfied()
    {
        var r1 = CreateStubResource();
        var r2 = CreateStubResource();

        var graph = new RenderGraph();
        // 三个 pass 写同一资源 R1（WAW 链），两个 pass 读 R1
        graph.AddPass("W1").Writes(r1, MTLRenderStages.Vertex).Record(_ => { });
        graph.AddPass("R_a").Reads(r1, MTLRenderStages.Fragment).Writes(r2, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("W2").Writes(r1, MTLRenderStages.Vertex).Record(_ => { });
        graph.AddPass("R_b").Reads(r1, MTLRenderStages.Fragment).Reads(r2, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("W3").Writes(r1, MTLRenderStages.Vertex).Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal(5, sorted.Count);

        // 验证 WAW 链：W1 在 W2 前，W2 在 W3 前
        int IndexOf(string name) => sorted.ToList().FindIndex(p => p.Name == name);
        Assert.True(IndexOf("W1") < IndexOf("W2"));
        Assert.True(IndexOf("W2") < IndexOf("W3"));
        // 验证 RAW：R_a 在 W1 后，R_b 在 W2 后且 R_b 在 R_a 后
        Assert.True(IndexOf("R_a") > IndexOf("W1"));
        Assert.True(IndexOf("R_b") > IndexOf("W2"));
        Assert.True(IndexOf("R_b") > IndexOf("R_a"));
    }

    /// <summary>
    /// 多输入汇聚：B 和 C 都读 A 的输出 R1，D 读 B/C 的输出。
    /// 验证 A 在最前，D 在最后。
    /// </summary>
    [Fact]
    public void MultiInputConverge()
    {
        var r1 = CreateStubResource();
        var r2 = CreateStubResource();
        var r3 = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("A").Writes(r1, MTLRenderStages.Vertex).Record(_ => { });
        graph.AddPass("B").Reads(r1, MTLRenderStages.Fragment).Writes(r2, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("C").Reads(r1, MTLRenderStages.Fragment).Writes(r3, MTLRenderStages.Fragment).Record(_ => { });
        graph.AddPass("D").Reads(r2, MTLRenderStages.Fragment).Reads(r3, MTLRenderStages.Fragment).Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal(4, sorted.Count);
        Assert.Equal("A", sorted[0].Name);
        Assert.Equal("D", sorted[3].Name);
    }

    /// <summary>
    /// 声明顺序反向但无依赖：应保持声明顺序。
    /// 验证即使 "Lighting" 写在 "ShadowMap" 后面，无依赖时不重排。
    /// </summary>
    [Fact]
    public void ReverseDeclaration_NoDependency_KeepsOrder()
    {
        var graph = new RenderGraph();
        graph.AddPass("Lighting").Record(_ => { });
        graph.AddPass("ShadowMap").Record(_ => { });
        graph.AddPass("GBuffer").Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal("Lighting", sorted[0].Name);
        Assert.Equal("ShadowMap", sorted[1].Name);
        Assert.Equal("GBuffer", sorted[2].Name);
    }

    /// <summary>
    /// 性能守护：100 个 pass 链式依赖排序应在 10ms 内完成。
    /// </summary>
    [Fact]
    public void TopologicalSort_100Passes_Under10ms()
    {
        var resources = new MetalObject[100];
        for (int i = 0; i < 100; i++) resources[i] = CreateStubResource();

        var graph = new RenderGraph();
        for (int i = 0; i < 100; i++)
        {
            var builder = graph.AddPass($"Pass{i}");
            if (i > 0) builder.Reads(resources[i - 1], MTLRenderStages.Fragment);
            builder.Writes(resources[i], MTLRenderStages.Fragment).Record(_ => { });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sorted = graph.Compile();
        sw.Stop();

        Assert.Equal(100, sorted.Count);
        Assert.True(sw.ElapsedMilliseconds < 10, $"排序耗时 {sw.ElapsedMilliseconds}ms，应 < 10ms");
    }
}
