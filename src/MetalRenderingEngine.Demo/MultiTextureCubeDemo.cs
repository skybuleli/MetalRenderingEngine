using System.Runtime.InteropServices;
using MetalRenderingEngine.Binding;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;
using MetalRenderingEngine.Rendering;
using SysMatrix = System.Numerics.Matrix4x4;
using SysVector3 = System.Numerics.Vector3;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 10E Demo：双纹理混合旋转立方体。
/// 体现 ShaderBindingLayout 在多资源（4 个：PerFrame + AlbedoMap + DetailMap + Sampler）场景的封装价值。
/// 两张纹理（棋盘格 + 斜条纹）在 fragment 加权混合，带 Lambert 光照和深度测试。
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- multi-tex-cube
/// </summary>
internal static class MultiTextureCubeDemo
{
    private const int W = 800, H = 600;

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrame
    {
        public SysMatrix ViewProj;
        public SysMatrix Model;
    }

    public static int Run()
    {
        using var device = MetalDevice.CreateSystemDefault();
        Console.WriteLine($"[MultiTextureCube] Device: {device.Name}");

        // ── 1. 着色器（预编译 metallib，反射由 BindingLayout 构造时加载）─
        using var vertFn = MetalShaderLoader.GetFunction(device, "MultiTextureCube.vert", "main");
        using var fragFn = MetalShaderLoader.GetFunction(device, "MultiTextureCube.frag", "main");

        // ── 2. PSO + 深度状态 ─
        using var pso = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithDepth(MTLPixelFormat.Depth32Float)
            .WithSampleCount(1)
            .Build(device, vertFn, fragFn);
        using var depthState = device.NewDepthStencilState(new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        });

        // ── 3. 深度纹理 ─
        using var depthTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.Depth32Float, W, H,
            MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModePrivate));

        // ── 4. 两张纹理（真 Texture2D，走 Phase 10A 描述符堆路径）─
        const int TexSize = 256;
        using var albedoTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, TexSize, TexSize,
            MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));
        byte[] albedoPixels = new byte[TexSize * TexSize * 4];
        FillCheckerboard(albedoPixels, TexSize, 32);  // 棋盘格（暖红/白）
        albedoTex.ReplaceRegion(0, 0, TexSize, TexSize, 0, 0, albedoPixels, (ulong)(TexSize * 4));

        using var detailTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, TexSize, TexSize,
            MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));
        byte[] detailPixels = new byte[TexSize * TexSize * 4];
        FillDiagonalStripes(detailPixels, TexSize, 24);  // 斜条纹（蓝/暗蓝）
        detailTex.ReplaceRegion(0, 0, TexSize, TexSize, 0, 0, detailPixels, (ulong)(TexSize * 4));

        // ── 5. sampler ─
        using var sampler = device.NewSamplerState(new WMTSamplerInfo
        {
            MinFilter = (int)MTLSamplerMinMagFilter.Linear,
            MagFilter = (int)MTLSamplerMinMagFilter.Linear,
            MipFilter = (int)MTLSamplerMipFilter.NotMipmapped,
            SAddressMode = (int)MTLSamplerAddressMode.Repeat,
            TAddressMode = (int)MTLSamplerAddressMode.Repeat,
            RAddressMode = (int)MTLSamplerAddressMode.Repeat,
            MaxAnisotropy = 1,
            CompareFunction = -1,
            LodMinClamp = 0f,
            LodMaxClamp = float.MaxValue,
        });
        if (sampler.GpuResourceID == 0)
            throw new InvalidOperationException("sampler.GpuResourceID == 0，supportArgumentBuffers 未生效。");

        // ── 6. uniform buffer ─
        using var perFrameBuf = device.NewBuffer((ulong)Marshal.SizeOf<PerFrame>(), MTLResourceOptions.StorageModeShared);

        // ── 7. 窗口 + layer ─
        using var window = SDL3Window.Create("Multi-Texture Cube — Phase 10E (ShaderBindingLayout, 4 resources)", W, H);
        var layer = new MetalLayer(window.LayerHandle);
        layer.SetDevice(device);
        layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
        layer.SetDrawableSize(W, H);

        // ── 8. ShaderBindingLayout（Phase 10E：4 资源，强类型属性封装 slot）─
        // 对比 ResourceTable：调用方需手记 4 个 slot + 分 vert/frag 两表 + 手动加载反射 + 分两次 Apply。
        // 这里 4 个语义属性 + 一行构造 + 渲染循环一行 Apply。
        var layout = new MultiTextureCubeBindingLayout
        {
            PerFrame = perFrameBuf,
            AlbedoMap = albedoTex,
            DetailMap = detailTex,
            LinearSampler = sampler,
        };

        // ── 9. 渲染循环 ─
        var camera = new Camera(MathF.PI / 4f, (float)W / H, 0.1f, 100f);
        using ICommandRecorder recorder = new MetalCommandRecorder(device);
        using var sync = new FrameSync(device);
        long start = Environment.TickCount64;
        int frame = 0;

        layer.SetDisplaySyncEnabled(true);
        layer.SetMaximumDrawableCount(sync.InFlightFrames);

        while (true)
        {
            if (window.PollShouldClose()) break;
            sync.WaitFrame();
            var drawable = layer.NextDrawable();
            if (drawable == null) { Thread.Sleep(1); continue; }
            using (drawable)
            {
                float t = (Environment.TickCount64 - start) / 1000f;
                camera.LookFrom(new SysVector3(0, 0, -3), SysVector3.Zero);
                perFrameBuf.AsSpan<PerFrame>()[0] = new PerFrame
                {
                    ViewProj = camera.ViewProj,
                    Model = SysMatrix.CreateFromYawPitchRoll(t * 0.8f, t * 0.5f, 0f),
                };

                var passDesc = new RenderPassBuilder()
                    .ColorAt(0, drawable.Texture, new(0.1f, 0.12f, 0.15f, 1f))
                    .Depth(depthTex, clearDepth: 1f)
                    .Build();

                recorder.BeginFrame();
                recorder.BeginRenderPass(passDesc);
                recorder.SetPipelineState(pso);
                recorder.SetDepthStencilState(depthState);
                recorder.SetViewport(0, 0, W, H, 0, 1);
                recorder.SetCullMode(MTLCullMode.Back);
                recorder.SetFrontFacing(MTLWinding.CounterClockwise);

                // Phase 10E：一行 Apply 应用全部 4 资源（vert PerFrame + frag AlbedoMap/DetailMap/Sampler）
                layout.Apply(recorder);

                recorder.Draw(0, 0, 36, 1);
                recorder.EndRenderPass();

                sync.SignalFrame(((MetalCommandRecorder)recorder).CommandBuffer);
                ((MetalCommandRecorder)recorder).PresentDrawable(drawable);
                ((MetalCommandRecorder)recorder).Submit();
            }
            if (++frame % 60 == 0)
            {
                Console.WriteLine($"  frame {frame} ({recorder.CommandCount} cmds)");
                Console.Out.Flush();
            }
        }
        Console.WriteLine($"✅ {frame} frames, multi-texture cube (4 resources via ShaderBindingLayout)");
        return 0;
    }

    /// <summary>填充 RGBA8 棋盘格纹理（格子 tileSize 像素）。</summary>
    private static void FillCheckerboard(byte[] pixels, int size, int tileSize)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bool dark = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                if (dark) { pixels[i] = 200; pixels[i + 1] = 60; pixels[i + 2] = 60; pixels[i + 3] = 255; }
                else { pixels[i] = 240; pixels[i + 1] = 240; pixels[i + 2] = 240; pixels[i + 3] = 255; }
            }
    }

    /// <summary>填充 RGBA8 斜条纹纹理（条纹宽度 stripeWidth 像素）。</summary>
    private static void FillDiagonalStripes(byte[] pixels, int size, int stripeWidth)
    {
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bool stripe = ((x + y) / stripeWidth) % 2 == 0;
                if (stripe) { pixels[i] = 60; pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
                else { pixels[i] = 20; pixels[i + 1] = 40; pixels[i + 2] = 80; pixels[i + 3] = 255; }
            }
    }
}
