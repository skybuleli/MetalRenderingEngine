using System.Runtime.InteropServices;
using MetalRenderingEngine.Binding;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Reflection;
using SysMatrix = System.Numerics.Matrix4x4;
using SysVector3 = System.Numerics.Vector3;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 10 可视化 Demo：贴图旋转立方体。
/// 把 Phase 10A-10D 的能力串到一个肉眼可见的窗口场景：
///   10A: 真 Texture2D.Sample + sampler 描述符堆路径（非 StructuredBuffer 模拟）
///   10B: ArgumentBufferEncoder 自动编码（由 ResourceTable 内部调用）
///   10C: ResourceTable 按 slot 绑定 + Apply 自动绑到 buffer(2) + UseResource
///   10D: ReflectionLoader 从预编译 reflect.json 加载反射（不经运行时编译）
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- textured-cube
/// </summary>
internal static class TexturedCubeDemo
{
    private const int W = 800, H = 600;

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrame
    {
        public SysMatrix ViewProj;
        public SysMatrix Model;   // 旋转矩阵（CPU 算好）
    }

    public static int Run()
    {
        using var device = MetalDevice.CreateSystemDefault();
        Console.WriteLine($"[TexturedCube] Device: {device.Name}");

        // ── 1. 着色器：预编译 metallib（MetalShaderLoader）─
        // Phase 10D：反射由 ShaderBindingLayout 构造时从 reflect.json 加载（见下）。
        using var vertFn = MetalShaderLoader.GetFunction(device, "TexturedCube.vert", "main");
        using var fragFn = MetalShaderLoader.GetFunction(device, "TexturedCube.frag", "main");

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

        // ── 4. 棋盘格纹理（真 Texture2D，非 StructuredBuffer 模拟）─
        // Phase 10A：texture 走描述符堆路径（IRDescriptorTableEntry）。
        const int TexSize = 256;
        using var cubeTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, TexSize, TexSize,
            MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));
        byte[] texPixels = new byte[TexSize * TexSize * 4];
        FillCheckerboard(texPixels, TexSize, 32);  // 32px 格子
        cubeTex.ReplaceRegion(0, 0, TexSize, TexSize, 0, 0, texPixels, (ulong)(TexSize * 4));

        // ── 5. sampler（需 supportArgumentBuffers=YES，bridge.m 已硬编码）─
        using var sampler = device.NewSamplerState(new WMTSamplerInfo
        {
            MinFilter = (int)MTLSamplerMinMagFilter.Nearest,
            MagFilter = (int)MTLSamplerMinMagFilter.Nearest,
            MipFilter = (int)MTLSamplerMipFilter.NotMipmapped,
            SAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1,
            CompareFunction = -1,
            LodMinClamp = 0f,
            LodMaxClamp = float.MaxValue,
        });
        // 守护：supportArgumentBuffers 必须生效，否则描述符堆路径不可用
        if (sampler.GpuResourceID == 0)
            throw new InvalidOperationException("sampler.GpuResourceID == 0，supportArgumentBuffers 未生效。");
        if (cubeTex.GpuResourceID == 0)
            throw new InvalidOperationException("cubeTex.GpuResourceID == 0。");

        // ── 6. uniform buffer（PerFrame: viewProj + model）─
        using var perFrameBuf = device.NewBuffer((ulong)Marshal.SizeOf<PerFrame>(), MTLResourceOptions.StorageModeShared);

        // ── 7. 窗口 + layer ─
        using var window = SDL3Window.Create("Textured Cube — Phase 10 (ReflectionLoader + ResourceTable)", W, H);
        var layer = new MetalLayer(window.LayerHandle);
        layer.SetDevice(device);
        layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
        layer.SetDrawableSize(W, H);

        // ── 8. ShaderBindingLayout（Phase 10E：半自动绑定，强类型属性封装 slot）─
        // 对比 Phase 10C 的 ResourceTable（手写 slot 数字），这里用语义属性名绑定：
        // 调用方无需记 vert/frag 的 slot，且 shader 改 register 只改 BindingLayout 子类。
        var layout = new TexturedCubeBindingLayout
        {
            PerFrame = perFrameBuf,      // vert SRV slot=0
            ColorTex = cubeTex,          // frag SRV slot=0
            LinearSampler = sampler,     // frag Sampler slot=0
        };

        // ── 9. 渲染循环 ─
        var camera = new Camera(MathF.PI / 4f, (float)W / H, 0.1f, 100f);
        using ICommandRecorder recorder = new MetalCommandRecorder(device);
        long start = Environment.TickCount64;
        int frame = 0;

        while (true)
        {
            if (window.PollShouldClose()) break;
            var drawable = layer.NextDrawable();
            if (drawable == null) { Thread.Sleep(8); continue; }
            using (drawable)
            {
                float t = (Environment.TickCount64 - start) / 1000f;
                // 相机固定，立方体自转
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

                // Phase 10E：ShaderBindingLayout.Apply 一次性应用 vert+frag 绑定
                // （内部调 ResourceTable.Apply → ArgumentBufferEncoder.Encode → SetVertexBytes/
                // SetFragmentBytes(buffer(2)) + UseResource，反射由构造时 ReflectionLoader 加载）
                layout.Apply(recorder);

                recorder.Draw(0, 0, 36, 1);
                recorder.EndRenderPass();

                ((MetalCommandRecorder)recorder).PresentDrawable(drawable);
                ((MetalCommandRecorder)recorder).Submit();
            }
            if (++frame % 60 == 0)
            {
                Console.WriteLine($"  frame {frame} ({recorder.CommandCount} cmds)");
                Console.Out.Flush();
            }
            Thread.Sleep(16);
        }
        Console.WriteLine($"✅ {frame} frames, textured cube with Phase 10A-10D binding pipeline");
        return 0;
    }

    /// <summary>填充 RGBA8 棋盘格纹理（格子大小 tileSize 像素）。</summary>
    private static void FillCheckerboard(byte[] pixels, int size, int tileSize)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bool dark = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                if (dark)
                {
                    pixels[i] = 200; pixels[i + 1] = 60; pixels[i + 2] = 60; pixels[i + 3] = 255;  // 暖红
                }
                else
                {
                    pixels[i] = 240; pixels[i + 1] = 240; pixels[i + 2] = 240; pixels[i + 3] = 255;  // 白
                }
            }
        }
    }
}
