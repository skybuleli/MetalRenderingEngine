using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 7D/7E: 光栅化状态 + 深度/模板状态命令经 MetalCommandList 批量回放的正确性测试。
/// 验证新命令能录制进 ring buffer 并通过单次 P/Invoke 回放执行。
/// </summary>
public class RasterStateCommandListTests
{
    private const int TexWidth = 64;
    private const int TexHeight = 64;

    private static string TriangleVertPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.vert.metallib");
    private static string TriangleFragPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.frag.metallib");

    /// <summary>
    /// 录制 SetCullMode + SetFrontFacing + SetDepthStencilState + SetStencilReference
    /// 等命令到 MetalCommandList，单次 ReplayRender 回放，验证不崩溃且命令数正确。
    /// </summary>
    [Fact]
    public void RecordAndReplay_RasterAndDepthStencilCommands_NoCrash()
    {
        Assert.True(File.Exists(TriangleVertPath), $"metallib 不存在：{TriangleVertPath}");
        Assert.True(File.Exists(TriangleFragPath), $"metallib 不存在：{TriangleFragPath}");

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        // PSO 需要 depth 格式
        var pipeDesc = new WMTRenderPipelineDesc
        {
            ColorCount = 1,
            DepthPixelFormat = (int)MTLPixelFormat.Depth32Float,
            SampleCount = 1,
        };
        pipeDesc.ColorAttachmentAt(0).PixelFormat = (int)MTLPixelFormat.BGRA8Unorm;
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        // 深度状态：Less 比较 + 写入启用
        var dsDesc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };
        using var depthState = device.NewDepthStencilState(dsDesc);

        // offscreen color + depth texture
        var colorInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = TexWidth, Height = TexHeight, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        var depthInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.Depth32Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = TexWidth, Height = TexHeight, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModePrivate,
        };
        using var colorTex = device.NewTexture(colorInfo);
        using var depthTex = device.NewTexture(depthInfo);

        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = colorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });
        passDesc.Depth = new WMTRenderPassAttachment
        {
            Texture = depthTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearDepth = 1.0f,  // 远裁剪面
        };

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using var encoder = cmdbuf.RenderCommandEncoder(passDesc);

        // 录制全部状态命令到 MetalCommandList
        using var cmdList = new MetalCommandList();
        cmdList.RecordSetRenderPipelineState(pso);
        cmdList.RecordSetViewport(0, 0, TexWidth, TexHeight, 0, 1);
        cmdList.RecordSetCullMode(MTLCullMode.Back);
        cmdList.RecordSetFrontFacing(MTLWinding.CounterClockwise);
        cmdList.RecordSetDepthBias(0.0f, 0.0f, 0.0f);
        cmdList.RecordSetDepthClipMode(MTLDepthClipMode.Clip);
        cmdList.RecordSetTriangleFillMode(MTLTriangleFillMode.Fill);
        cmdList.RecordSetDepthStencilState(depthState);
        cmdList.RecordSetStencilReference(0, 0);
        cmdList.RecordDrawPrimitives(0, 0, 3);  // Triangle shader 的 3 顶点

        Assert.Equal(10, cmdList.Count);

        // 单次 P/Invoke 回放全部命令
        cmdList.ReplayRender(encoder);
        Assert.Equal(0, cmdList.Count);

        encoder.EndEncoding();
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error()) Assert.Null(err);
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);
    }

    /// <summary>
    /// 深度测试有效性：渲染一个近三角形（z=0）后，clear depth 为 1（远），
    /// 验证深度缓冲被正确写入（间接验证 SetDepthStencilState + depth clear 生效）。
    /// 此处只验证命令执行无错误；像素级深度验证在 ThreeDSceneDemo 中完成。
    /// </summary>
    [Fact]
    public void DepthTest_WithBatchedReplay_CompletesWithoutError()
    {
        Assert.True(File.Exists(TriangleVertPath), $"metallib 不存在：{TriangleVertPath}");
        Assert.True(File.Exists(TriangleFragPath), $"metallib 不存在：{TriangleFragPath}");

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new WMTRenderPipelineDesc
        {
            ColorCount = 1,
            DepthPixelFormat = (int)MTLPixelFormat.Depth32Float,
            SampleCount = 1,
        };
        pipeDesc.ColorAttachmentAt(0).PixelFormat = (int)MTLPixelFormat.BGRA8Unorm;
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        var dsDesc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };
        using var depthState = device.NewDepthStencilState(dsDesc);

        var colorInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = TexWidth, Height = TexHeight, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        var depthInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.Depth32Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = TexWidth, Height = TexHeight, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModePrivate,
        };
        using var colorTex = device.NewTexture(colorInfo);
        using var depthTex = device.NewTexture(depthInfo);

        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = colorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });
        passDesc.Depth = new WMTRenderPassAttachment
        {
            Texture = depthTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearDepth = 1.0f,
        };

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using var encoder = cmdbuf.RenderCommandEncoder(passDesc);

        using var cmdList = new MetalCommandList();
        cmdList.RecordSetRenderPipelineState(pso);
        cmdList.RecordSetViewport(0, 0, TexWidth, TexHeight, 0, 1);
        cmdList.RecordSetDepthStencilState(depthState);
        cmdList.RecordDrawPrimitives(0, 0, 3);
        cmdList.ReplayRender(encoder);

        encoder.EndEncoding();
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error()) Assert.Null(err);
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);
    }
}
