using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// 深度附件开启后的最小像素级回归。
/// 目标：锁定 color + depth render pass + depth state 不会把可见图元渲染成全黑。
/// </summary>
public class DepthEnabledTrianglePixelTests
{
    private const int TexWidth = 128;
    private const int TexHeight = 128;

    private static string TriangleVertPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.vert.metallib");

    private static string TriangleFragPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.frag.metallib");

    [Fact]
    public void Triangle_WithDepthAttachment_RendersVisiblePixels()
    {
        Assert.True(File.Exists(TriangleVertPath), $"metallib 不存在：{TriangleVertPath}");
        Assert.True(File.Exists(TriangleFragPath), $"metallib 不存在：{TriangleFragPath}");

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new RenderPipelineDescBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithDepth(MTLPixelFormat.Depth32Float)
            .WithSampleCount(1)
            .Build();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        var dsDesc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };
        using var depthState = device.NewDepthStencilState(dsDesc);

        using var colorTex = device.NewTexture(new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = TexWidth,
            Height = TexHeight,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        });
        using var depthTex = device.NewTexture(new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.Depth32Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = TexWidth,
            Height = TexHeight,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModePrivate,
        });

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
        using (var encoder = cmdbuf.RenderCommandEncoder(passDesc))
        {
            encoder.SetRenderPipelineState(pso);
            encoder.SetDepthStencilState(depthState);
            encoder.SetViewport(0, 0, TexWidth, TexHeight, 0, 1);
            encoder.DrawTriangles(0, 3);
            encoder.EndEncoding();
        }

        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error()) Assert.Null(err);
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);

        byte[] pixels = new byte[TexWidth * TexHeight * 4];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(colorTex.Handle, p, (ulong)pixels.Length, 0);
                Assert.Equal((ulong)pixels.Length, written);
            }
        }

        int nonBlack = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
                nonBlack++;
        }

        Assert.True(nonBlack > 0, "启用 depth attachment 后三角形不应全部变成黑色。");
    }
}
