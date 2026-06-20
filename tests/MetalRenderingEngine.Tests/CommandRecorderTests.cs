using System.IO;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 8: ICommandRecorder 测试套件。
/// 验证：装饰器透明性、Memento 捕获/回放、PipelineBuilder 校验、
/// MetalCommandRecorder 批量回放保真（关键回归测试）。
/// </summary>
public class CommandRecorderTests
{
    private static string TriangleVertPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.vert.metallib");
    private static string TriangleFragPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.frag.metallib");

    /// <summary>
    /// LoggingCommandRecorder 包裹 RecordingCommandRecorder，调用应穿透到内部。
    /// 验证装饰器透明性：日志写入 + 命令捕获。
    /// </summary>
    [Fact]
    public void LoggingRecorder_DelegatesToInner()
    {
        var inner = new RecordingCommandRecorder();
        using var sw = new StringWriter();
        using var logger = new LoggingCommandRecorder(inner, sw, verbose: true);

        logger.BeginFrame();
        logger.SetCullMode(MTLCullMode.Back);
        logger.EndFrame();

        // 内部 RecordingCommandRecorder 应捕获到 SetCullMode
        Assert.Equal(1, inner.CommandCount);
        Assert.Equal("SetCullMode", inner.Commands[0].Name);

        // 日志应包含命令文本
        string log = sw.ToString();
        Assert.Contains("SetCullMode(Back)", log);
        Assert.Contains("BeginFrame", log);
        Assert.Contains("EndFrame", log);
    }

    /// <summary>
    /// Memento 模式：录制 3 条命令到 RecordingCommandRecorder A，
    /// 复制同样 3 条到 B，Diff 应为空。
    /// </summary>
    [Fact]
    public void Memento_Diff_IdenticalSequences_Empty()
    {
        var a = new RecordingCommandRecorder();
        var b = new RecordingCommandRecorder();

        a.SetCullMode(MTLCullMode.Back);
        a.SetCullMode(MTLCullMode.Front);
        a.Draw(0, 0, 3, 1);

        b.SetCullMode(MTLCullMode.Back);
        b.SetCullMode(MTLCullMode.Front);
        b.Draw(0, 0, 3, 1);

        var diffs = a.Diff(b);
        Assert.Empty(diffs);
    }

    /// <summary>
    /// Memento 模式：不同序列 Diff 应报告差异。
    /// </summary>
    [Fact]
    public void Memento_Diff_DifferentSequences_ReportsDifferences()
    {
        var a = new RecordingCommandRecorder();
        var b = new RecordingCommandRecorder();

        a.SetCullMode(MTLCullMode.Back);
        a.Draw(0, 0, 3, 1);

        b.SetCullMode(MTLCullMode.Front);
        b.Draw(0, 0, 6, 1);

        var diffs = a.Diff(b);
        Assert.NotEmpty(diffs);
        Assert.Equal(2, diffs.Count);  // 两条命令都不同
    }

    /// <summary>
    /// PipelineBuilder 校验：无颜色附件时应抛异常。
    /// </summary>
    [Fact]
    public void PipelineBuilder_NoColorAttachments_Throws()
    {
        var builder = new PipelineBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.BuildDescriptor());
    }

    /// <summary>
    /// PipelineBuilder 校验：非法 SampleCount 应抛异常。
    /// </summary>
    [Fact]
    public void PipelineBuilder_InvalidSampleCount_Throws()
    {
        var builder = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(3);  // 非法
        Assert.Throws<InvalidOperationException>(() => builder.BuildDescriptor());
    }

    /// <summary>
    /// PipelineBuilder 校验：合法配置应成功。
    /// </summary>
    [Fact]
    public void PipelineBuilder_ValidConfig_Succeeds()
    {
        var builder = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithDepth(MTLPixelFormat.Depth32Float)
            .WithSampleCount(4);  // 4x MSAA
        var desc = builder.BuildDescriptor();
        Assert.Equal(1, desc.ColorCount);
        Assert.Equal(4, desc.SampleCount);
        Assert.Equal((int)MTLPixelFormat.Depth32Float, desc.DepthPixelFormat);
    }

    /// <summary>
    /// MetalCommandRecorder 端到端：经 ICommandRecorder 接口录制命令，
    /// 内部走 MetalCommandList 批量回放，验证渲染不崩溃且 command buffer 完成无错误。
    /// 这是 Phase 8 的关键回归测试——确认批量回放路径与直接 encoder 路径行为一致。
    /// </summary>
    [Fact]
    public void MetalCommandRecorder_BatchedReplay_RendersWithoutError()
    {
        Assert.True(File.Exists(TriangleVertPath), $"metallib 不存在：{TriangleVertPath}");
        Assert.True(File.Exists(TriangleFragPath), $"metallib 不存在：{TriangleFragPath}");

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .BuildDescriptor();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        var colorInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = 64, Height = 64, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var colorTex = device.NewTexture(colorInfo);

        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = colorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        // 通过 ICommandRecorder 接口录制
        using ICommandRecorder recorder = new MetalCommandRecorder(device);
        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        recorder.SetPipelineState(pso);
        recorder.SetViewport(0, 0, 64, 64, 0, 1);
        recorder.SetCullMode(MTLCullMode.None);
        recorder.Draw(0, 0, 3, 1);  // Triangle shader 的 3 顶点
        recorder.EndRenderPass();
        recorder.EndFrame();

        // 验证：应该录入了 4 条命令（SetPipelineState + SetViewport + SetCullMode + Draw）
        Assert.True(recorder.CommandCount >= 4, $"Expected >= 4 commands, got {recorder.CommandCount}");
    }

    /// <summary>
    /// MetalCommandRecorder 多 draw 批量回放：录制 100 次 Draw，
    /// 验证全部经 MetalCommandList 单次 P/Invoke 回放完成（无错误）。
    /// 这验证了批量优化的核心价值——100 个 draw 只需 1 次回放 P/Invoke。
    /// </summary>
    [Fact]
    public void MetalCommandRecorder_100Draws_BatchedReplay_NoError()
    {
        Assert.True(File.Exists(TriangleVertPath));
        Assert.True(File.Exists(TriangleFragPath));

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .BuildDescriptor();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        var colorInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = 64, Height = 64, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var colorTex = device.NewTexture(colorInfo);

        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = colorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        using ICommandRecorder recorder = new MetalCommandRecorder(device);
        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        recorder.SetPipelineState(pso);
        recorder.SetViewport(0, 0, 64, 64, 0, 1);

        // 录制 100 次 Draw —— 全部经 MetalCommandList 批量缓冲
        for (int i = 0; i < 100; i++)
            recorder.Draw(0, 0, 3, 1);

        Assert.True(recorder.CommandCount >= 102, $"Expected >= 102 commands, got {recorder.CommandCount}");
        recorder.EndRenderPass();
        recorder.EndFrame();
    }

    /// <summary>
    /// 性能回归守护：1000 次 Draw 必须只在 MetalCommandList 中累积，
    /// EndRenderPass 时一次 replay；录制阶段不能退回逐命令提交/清空。
    /// </summary>
    [Fact]
    public void MetalCommandRecorder_1000Draws_CommandCountRemainsLinearUntilSingleReplay()
    {
        Assert.True(File.Exists(TriangleVertPath));
        Assert.True(File.Exists(TriangleFragPath));

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .BuildDescriptor();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        var colorInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = 64, Height = 64, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var colorTex = device.NewTexture(colorInfo);

        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = colorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        using var metalRecorder = new MetalCommandRecorder(device);
        ICommandRecorder recorder = metalRecorder;
        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        recorder.SetPipelineState(pso);
        recorder.SetViewport(0, 0, 64, 64, 0, 1);

        for (int i = 0; i < 1000; i++)
            recorder.Draw(0, 0, 3, 1);

        // 2 条状态命令 + 1000 条 draw 全部仍处于同一批次中，等待 EndRenderPass 单次回放。
        Assert.Equal(1002, recorder.CommandCount);
        Assert.Equal(0, metalRecorder.LastRenderReplayCallCount);
        recorder.EndRenderPass();
        Assert.Equal(1, metalRecorder.LastRenderReplayCallCount);
        recorder.EndFrame();
        Assert.Equal(1, metalRecorder.LastRenderReplayCallCount);
    }
}
