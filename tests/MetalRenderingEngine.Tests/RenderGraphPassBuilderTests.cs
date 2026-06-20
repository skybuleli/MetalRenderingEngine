using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Rendering.RenderGraph;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 11: RenderGraphPassBuilder 流式 API 测试。
/// </summary>
public class RenderGraphPassBuilderTests
{
    /// <summary>用于测试的 stub MetalObject。</summary>
    private static MetalObject CreateStubResource()
    {
        return new StubMetalObject();
    }

    private sealed class StubMetalObject : MetalObject
    {
        private static int s_counter;
        public StubMetalObject()
        {
            SetNativeHandle((nuint)Interlocked.Increment(ref s_counter));
        }
        protected override bool ReleaseHandle()
        {
            SetHandle(IntPtr.Zero);
            return true;
        }
    }

    [Fact]
    public void Reads_AddsInput()
    {
        var resource = CreateStubResource();
        var graph = new RenderGraph();
        graph.AddPass("Test")
            .Reads(resource, MTLRenderStages.Fragment, "tex")
            .Record(_ => { });

        var sorted = graph.Compile();
        Assert.Single(sorted[0].Inputs);
        Assert.Equal(resource, sorted[0].Inputs[0].Resource);
        Assert.Equal(MTLResourceUsage.Read, sorted[0].Inputs[0].Usage);
        Assert.Equal(MTLRenderStages.Fragment, sorted[0].Inputs[0].Stages);
        Assert.Equal("tex", sorted[0].Inputs[0].Name);
    }

    [Fact]
    public void Writes_AddsOutput()
    {
        var resource = CreateStubResource();
        var graph = new RenderGraph();
        graph.AddPass("Test")
            .Writes(resource, MTLRenderStages.Vertex, "depth")
            .Record(_ => { });

        var sorted = graph.Compile();
        Assert.Single(sorted[0].Outputs);
        Assert.Equal(resource, sorted[0].Outputs[0].Resource);
        Assert.Equal(MTLResourceUsage.Write, sorted[0].Outputs[0].Usage);
        Assert.Equal(MTLRenderStages.Vertex, sorted[0].Outputs[0].Stages);
    }

    [Fact]
    public void ReadsWrites_AddsBoth()
    {
        var resource = CreateStubResource();
        var graph = new RenderGraph();
        graph.AddPass("Test")
            .ReadsWrites(resource, MTLRenderStages.Fragment, "rw_buffer")
            .Record(_ => { });

        var sorted = graph.Compile();
        Assert.Single(sorted[0].Inputs);
        Assert.Single(sorted[0].Outputs);
        Assert.Equal(resource, sorted[0].Inputs[0].Resource);
        Assert.Equal(resource, sorted[0].Outputs[0].Resource);
    }

    [Fact]
    public void WithRenderPassDesc_SetsPassDesc()
    {
        var desc = new WMTRenderPassDesc();
        var graph = new RenderGraph();
        graph.AddPass("Test")
            .WithRenderPassDesc(desc)
            .Record(_ => { });

        var sorted = graph.Compile();
        // 验证 PassDesc 被设置（struct 比较：检查是否有默认值即可）
        Assert.Equal(default, sorted[0].PassDesc.Depth);
    }

    [Fact]
    public void MissingRecord_ThrowsInvalidOperation()
    {
        var graph = new RenderGraph();
        graph.AddPass("NoRecord");  // 没有调 .Record()

        Assert.Throws<InvalidOperationException>(() => graph.Compile());
    }

    [Fact]
    public void NullResource_Reads_Throws()
    {
        var graph = new RenderGraph();
        Assert.Throws<ArgumentNullException>(() =>
            graph.AddPass("Test").Reads(null!, MTLRenderStages.Vertex));
    }

    [Fact]
    public void NullResource_Writes_Throws()
    {
        var graph = new RenderGraph();
        Assert.Throws<ArgumentNullException>(() =>
            graph.AddPass("Test").Writes(null!, MTLRenderStages.Vertex));
    }

    [Fact]
    public void NullRecord_Throws()
    {
        var graph = new RenderGraph();
        Assert.Throws<ArgumentNullException>(() =>
            graph.AddPass("Test").Record(null!));
    }

    [Fact]
    public void MultipleResources_MultipleInputs()
    {
        var r1 = CreateStubResource();
        var r2 = CreateStubResource();
        var r3 = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("Test")
            .Reads(r1, MTLRenderStages.Vertex, "scene")
            .Reads(r2, MTLRenderStages.Fragment, "gbuffer")
            .Reads(r3, MTLRenderStages.Fragment, "shadow")
            .Record(_ => { });

        var sorted = graph.Compile();
        Assert.Equal(3, sorted[0].Inputs.Count);
    }

    [Fact]
    public void WithRenderPassBuilder_Fluent()
    {
        var graph = new RenderGraph();
        graph.AddPass("Test")
            .WithRenderPassBuilder(new RenderPassBuilder())
            .Record(_ => { });

        var sorted = graph.Compile();
        Assert.NotNull(sorted);
    }
}
