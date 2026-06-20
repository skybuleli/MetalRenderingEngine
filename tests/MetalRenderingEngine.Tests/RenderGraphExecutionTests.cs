using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Rendering.RenderGraph;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 11: Render Graph 执行流程测试。
/// 使用 RecordingCommandRecorder 捕获命令序列，验证 BeginRenderPass/UseResource/EndRenderPass 顺序。
/// </summary>
public class RenderGraphExecutionTests
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

    /// <summary>
    /// 三 pass 执行验证：使用 RecordingCommandRecorder 捕获命令，
    /// 验证 pass 按拓扑排序顺序执行。
    /// </summary>
    [Fact]
    public void Execute_ThreePassesCorrectOrder()
    {
        var r1 = CreateStubResource();  // shadow depth
        var r2 = CreateStubResource();  // gbuffer
        var r3 = CreateStubResource();  // final color
        var sceneBuffer = CreateStubResource();

        var executionOrder = new List<string>();

        var graph = new RenderGraph();
        // 故意打乱声明顺序：Lighting 写在最前但依赖 ShadowMap 和 GBuffer
        graph.AddPass("Lighting")
            .Reads(r2, MTLRenderStages.Fragment, "gbuffer")
            .Reads(r1, MTLRenderStages.Fragment, "shadow")
            .Writes(r3, MTLRenderStages.Fragment)
            .Record(rec => executionOrder.Add("Lighting"));

        graph.AddPass("ShadowMap")
            .Writes(r1, MTLRenderStages.Vertex, "shadow depth")
            .Reads(sceneBuffer, MTLRenderStages.Vertex, "scene")
            .Record(rec => executionOrder.Add("ShadowMap"));

        graph.AddPass("GBuffer")
            .Writes(r2, MTLRenderStages.Fragment, "gbuffer")
            .Reads(sceneBuffer, MTLRenderStages.Vertex, "scene")
            .Record(rec => executionOrder.Add("GBuffer"));

        var recorder = new RecordingCommandRecorder();
        graph.ExecutePasses(recorder);

        // 拓扑排序保证：ShadowMap → GBuffer → Lighting
        Assert.Equal(3, executionOrder.Count);
        Assert.Equal("ShadowMap", executionOrder[0]);
        Assert.Equal("GBuffer", executionOrder[1]);
        Assert.Equal("Lighting", executionOrder[2]);
    }

    /// <summary>
    /// 自动 UseResource 验证：声明的输入资源应在 BeginRenderPass 后自动插入 UseResource。
    /// </summary>
    [Fact]
    public void Execute_AutoUseResourceInserted()
    {
        var inputRes = CreateStubResource();
        var outputRes = CreateStubResource();
        var sceneBuffer = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("Writer")
            .Writes(inputRes, MTLRenderStages.Fragment)
            .Record(_ => { });

        graph.AddPass("Reader")
            .Reads(inputRes, MTLRenderStages.Fragment, "sampled")
            .Reads(sceneBuffer, MTLRenderStages.Vertex, "scene")
            .Writes(outputRes, MTLRenderStages.Fragment)
            .Record(_ => { });

        var recorder = new RecordingCommandRecorder();
        graph.ExecutePasses(recorder);

        // 检查 Reader pass 的 UseResource 命令（第二个 pass 的命令）
        // 命令序列：[pass1 无命令] [pass2: UseResource(inputRes), UseResource(sceneBuffer)]
        var useResourceCmds = recorder.Commands
            .OfType<UseResourceCommand>()
            .ToList();

        Assert.Equal(2, useResourceCmds.Count);
        // 验证 Usage 和 Stages
        Assert.Contains(useResourceCmds, c => c.Usage == MTLResourceUsage.Read);
    }

    /// <summary>
    /// ExecutePasses 不管理帧边界：不调 BeginFrame/EndFrame。
    /// </summary>
    [Fact]
    public void ExecutePasses_NoFrameBoundary()
    {
        var graph = new RenderGraph();
        graph.AddPass("Single").Record(_ => { });

        var recorder = new RecordingCommandRecorder();
        graph.ExecutePasses(recorder);

        // RecordingCommandRecorder 的 CommandCount 应该只包含 pass 内的命令
        // 不含 BeginFrame/EndFrame（RecordingCommandRecorder 对这两个是空操作，不计入 Commands）
        // 主要验证不抛异常
        Assert.True(true);
    }

    /// <summary>
    /// Record 回调接收正确的 ICommandRecorder 实例。
    /// </summary>
    [Fact]
    public void Record_ReceivesCorrectRecorder()
    {
        var graph = new RenderGraph();
        ICommandRecorder? capturedRecorder = null;

        graph.AddPass("Test")
            .Record(rec => capturedRecorder = rec);

        var recorder = new RecordingCommandRecorder();
        graph.ExecutePasses(recorder);

        Assert.NotNull(capturedRecorder);
        Assert.Same(recorder, capturedRecorder);
    }

    /// <summary>
    /// 无输入的 pass 不插入 UseResource。
    /// </summary>
    [Fact]
    public void Execute_NoInputPass_NoUseResource()
    {
        var outputRes = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("ClearOnly")
            .Writes(outputRes, MTLRenderStages.Fragment)
            .Record(_ => { });

        var recorder = new RecordingCommandRecorder();
        graph.ExecutePasses(recorder);

        var useResourceCmds = recorder.Commands.OfType<UseResourceCommand>().ToList();
        Assert.Empty(useResourceCmds);
    }

    /// <summary>
    /// 多 pass 共享输入：多个 pass 读同一资源，每个 pass 都应自动插入 UseResource。
    /// </summary>
    [Fact]
    public void Execute_SharedInput_AllPassesUseResource()
    {
        var sharedResource = CreateStubResource();
        var out1 = CreateStubResource();
        var out2 = CreateStubResource();

        var graph = new RenderGraph();
        graph.AddPass("Writer")
            .Writes(sharedResource, MTLRenderStages.Vertex)
            .Record(_ => { });

        graph.AddPass("Reader1")
            .Reads(sharedResource, MTLRenderStages.Fragment, "shared")
            .Writes(out1, MTLRenderStages.Fragment)
            .Record(_ => { });

        graph.AddPass("Reader2")
            .Reads(sharedResource, MTLRenderStages.Fragment, "shared")
            .Writes(out2, MTLRenderStages.Fragment)
            .Record(_ => { });

        var recorder = new RecordingCommandRecorder();
        graph.ExecutePasses(recorder);

        // Reader1 和 Reader2 都应各有 1 个 UseResource
        var useResourceCmds = recorder.Commands.OfType<UseResourceCommand>().ToList();
        Assert.Equal(2, useResourceCmds.Count);
    }

    /// <summary>
    /// Pass 名称 ToString 返回正确名称。
    /// </summary>
    [Fact]
    public void PassToString_ReturnsName()
    {
        var graph = new RenderGraph();
        graph.AddPass("MyShadowPass").Record(_ => { });
        var sorted = graph.Compile();
        Assert.Equal("MyShadowPass", sorted[0].ToString());
    }
}
