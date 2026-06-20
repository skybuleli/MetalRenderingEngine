using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Rendering.RenderGraph;
using SysMatrix = System.Numerics.Matrix4x4;
using SysVector3 = System.Numerics.Vector3;
using SysVector4 = System.Numerics.Vector4;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 11: Render Graph Demo — Shadow Map → GBuffer → Final 三 pass。
/// 演示声明式 pass 定义 + 拓扑排序自动排序 + 自动 UseResource。
///
/// 故意以 Final → ShadowMap → GBuffer 的乱序声明，验证排序正确。
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- rendergraph
/// <para><b>绑定路径说明</b>：本 Demo 使用 ThreeDScene shader 的旧 StructuredBuffer 绑定路径
    /// （手工 UavDescriptor argument buffer）。新的 Texture2D+SamplerState 描述符堆路径由
    /// <see cref="TexturedCubeDemo"/> / <see cref="MultiTextureCubeDemo"/> 演示。
    /// RenderGraph 与 ResourceTable 架构上兼容——在 <c>.Record()</c> 回调内调用
    /// <c>bindingLayout.Apply(recorder)</c> 即可，参见 TexturedCubeDemo 的用法。</para>
    
/// </summary>
internal static class RenderGraphDemo
{
    private const int Width = 256;
    private const int Height = 256;
    private const int InstanceCount = 20;
    private const ulong ArgumentTableBufferIndex = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct VertArgBuffer
    {
        public UavDescriptor Srv0; // PerFrame
        public UavDescriptor Srv1; // InstanceData
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrameCB
    {
        public SysMatrix ViewProj;
        public SysVector4 LightDir;
        public SysVector4 CamPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceData
    {
        public SysMatrix Model;
    }

    public static int Run()
    {
        using var device = MetalDevice.CreateSystemDefault();
        Console.WriteLine($"[RenderGraph] Device: {device.Name}");

        // 1) 加载着色器（复用 ThreeDScene 编译产物）
        using var vertLib = device.NewLibrary(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "shaders", "ThreeDScene.vert.metallib")));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "shaders", "ThreeDScene.frag.metallib")));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        // 2) PSO：BGRA8 + RGBA16Float + Depth32Float（支持所有三个 pass）
        var pipeDesc = new RenderPipelineDescBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithColorAttachment(1, MTLPixelFormat.RGBA16Float)
            .WithDepth(MTLPixelFormat.Depth32Float)
            .WithSampleCount(1)
            .Build();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        // 3) 深度状态
        var dsDesc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };
        using var depthState = device.NewDepthStencilState(dsDesc);

        // 4) 纹理资源
        var texInfo2D = (MTLPixelFormat fmt, MTLTextureUsage usage) => new WMTTextureInfo
        {
            PixelFormat = (int)fmt,
            TextureType = (int)MTLTextureType.Type2D,
            Width = Width, Height = Height, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)usage,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };

        // Shadow depth target
        using var shadowDepthTex = device.NewTexture(texInfo2D(
            MTLPixelFormat.Depth32Float,
            MTLTextureUsage.RenderTarget));

        // GBuffer: color + normal/depth
        using var gbufferColorTex = device.NewTexture(texInfo2D(
            MTLPixelFormat.BGRA8Unorm,
            MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead));
        using var gbufferNormalTex = device.NewTexture(texInfo2D(
            MTLPixelFormat.RGBA16Float,
            MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead));
        using var gbufferDepthTex = device.NewTexture(texInfo2D(
            MTLPixelFormat.Depth32Float,
            MTLTextureUsage.RenderTarget));

        // Final output
        using var finalColorTex = device.NewTexture(texInfo2D(
            MTLPixelFormat.BGRA8Unorm,
            MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead));

        // 5) 实例数据
        int instanceStructSize = Marshal.SizeOf<InstanceData>();
        using var instanceBuffer = device.NewBuffer(
            (ulong)(InstanceCount * instanceStructSize),
            MTLResourceOptions.StorageModeShared);
        Span<InstanceData> instances = instanceBuffer.AsSpan<InstanceData>();
        var rng = new Random(42);
        for (int i = 0; i < InstanceCount; i++)
        {
            float x = (float)(rng.NextDouble() * 10 - 5);
            float y = (float)(rng.NextDouble() * 6 - 3);
            float z = (float)(rng.NextDouble() * 10 - 5);
            instances[i].Model = SysMatrix.CreateTranslation(x, y, z);
        }

        // 6) PerFrame 常量
        int perFrameSize = Marshal.SizeOf<PerFrameCB>();
        var camera = new Camera(MathF.PI / 4f, (float)Width / Height, 0.1f, 100f);
        camera.LookFrom(new SysVector3(0, 5, 15), new SysVector3(0, 0, 0), new SysVector3(0, 1, 0));
        using var perFrameBuffer = device.NewBuffer((ulong)perFrameSize, MTLResourceOptions.StorageModeShared);
        perFrameBuffer.AsSpan<PerFrameCB>()[0] = new PerFrameCB
        {
            ViewProj = camera.ViewProj,
            LightDir = new SysVector4(SysVector3.Normalize(new SysVector3(0.5f, 1f, 0.3f)), 0),
            CamPos = new SysVector4(0, 5, 15, 0),
        };

        // 7) Render pass 描述符
        var shadowPassDesc = new RenderPassBuilder()
            .Depth(shadowDepthTex, 1f)
            .Build();

        var gbufferPassDesc = new WMTRenderPassDesc();
        gbufferPassDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = gbufferColorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });
        gbufferPassDesc.SetColorAt(1, new WMTRenderPassAttachment
        {
            Texture = gbufferNormalTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 0),
        });
        gbufferPassDesc.Depth = new WMTRenderPassAttachment
        {
            Texture = gbufferDepthTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearDepth = 1.0f,
        };

        var finalPassDesc = new RenderPassBuilder()
            .Color(finalColorTex, new WMTClearColor(0.1f, 0.1f, 0.15f, 1))
            .Build();

        // 8) Argument buffer
        var vertArgBuf = new VertArgBuffer
        {
            Srv0 = new UavDescriptor
            {
                GpuAddress = perFrameBuffer.GpuAddress,
                Length = perFrameBuffer.Length,
                Stride = (ulong)perFrameSize,
            },
            Srv1 = new UavDescriptor
            {
                GpuAddress = instanceBuffer.GpuAddress,
                Length = instanceBuffer.Length,
                Stride = (ulong)instanceStructSize,
            },
        };
        var fragArgBuf = new UavDescriptor
        {
            GpuAddress = perFrameBuffer.GpuAddress,
            Length = perFrameBuffer.Length,
            Stride = (ulong)perFrameSize,
        };

        // 9) 构建 Render Graph — 故意乱序声明以验证拓扑排序
        var graph = new RenderGraph();

        void RecordDraw(ICommandRecorder rec)
        {
            rec.SetPipelineState(pso);
            rec.SetDepthStencilState(depthState);
            rec.SetViewport(0, 0, Width, Height, 0, 1);
            rec.SetCullMode(MTLCullMode.Back);
            rec.SetFrontFacing(MTLWinding.CounterClockwise);
            rec.SetVertexBytes(in vertArgBuf, ArgumentTableBufferIndex);
            rec.SetFragmentBytes(in fragArgBuf, ArgumentTableBufferIndex);
            rec.Draw(0, 0, 36, InstanceCount);
        }

        // 声明 1/3: Final（依赖 GBuffer 输出，但声明在最前）
        graph.AddPass("Final")
            .Reads(gbufferColorTex, MTLRenderStages.Fragment, "gbuffer color")
            .Reads(gbufferNormalTex, MTLRenderStages.Fragment, "gbuffer normal")
            .Writes(finalColorTex, MTLRenderStages.Fragment)
            .WithRenderPassDesc(finalPassDesc)
            .Record(RecordDraw);

        // 声明 2/3: ShadowMap
        graph.AddPass("ShadowMap")
            .Writes(shadowDepthTex, MTLRenderStages.Vertex, "shadow depth")
            .Reads(perFrameBuffer, MTLRenderStages.Vertex, "per-frame")
            .WithRenderPassDesc(shadowPassDesc)
            .Record(RecordDraw);

        // 声明 3/3: GBuffer
        graph.AddPass("GBuffer")
            .Writes(gbufferColorTex, MTLRenderStages.Fragment, "gbuffer color")
            .Writes(gbufferNormalTex, MTLRenderStages.Fragment, "gbuffer normal")
            .Writes(gbufferDepthTex, MTLRenderStages.Fragment, "gbuffer depth")
            .Reads(perFrameBuffer, MTLRenderStages.Vertex, "per-frame")
            .WithRenderPassDesc(gbufferPassDesc)
            .Record(RecordDraw);

        // 10) 编译并验证排序
        var sorted = graph.Compile();
        Console.WriteLine($"[RenderGraph] 声明顺序: Final → ShadowMap → GBuffer");
        Console.WriteLine($"[RenderGraph] 排序结果: {string.Join(" → ", sorted.Select(p => p.Name))}");

        // Final 依赖 gbufferColorTex 和 gbufferNormalTex（由 GBuffer 写入）
        // 因此 GBuffer 必须在 Final 之前
        int gbufferIdx = sorted.ToList().FindIndex(p => p.Name == "GBuffer");
        int finalIdx = sorted.ToList().FindIndex(p => p.Name == "Final");
        if (gbufferIdx >= finalIdx)
        {
            Console.Error.WriteLine($"❌ 排序错误：GBuffer({gbufferIdx}) 应在 Final({finalIdx}) 之前");
            return 1;
        }
        Console.WriteLine("[RenderGraph] ✅ 拓扑排序正确：GBuffer 在 Final 之前");

        // 11) 执行 Render Graph
        using ICommandRecorder recorder = new MetalCommandRecorder(device);
        graph.Execute(recorder);
        Console.WriteLine($"[RenderGraph] 执行完成，共 {recorder.CommandCount} 条命令");

        // 12) 像素验证：final color target 应有非黑像素
        int bytesPerPixel = 4;
        int totalBytes = Width * Height * bytesPerPixel;
        byte[] pixels = new byte[totalBytes];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(finalColorTex.Handle, p, (ulong)totalBytes, 0);
                if (written != (ulong)totalBytes)
                {
                    Console.Error.WriteLine($"❌ Final color readback incomplete: {written}/{totalBytes}");
                    return 1;
                }
            }
        }

        // 统计非 clear-color 像素（clear = 0.1/0.1/0.15 ≈ 26/26/38 in BGRA8）
        int nonTrivialPixels = 0;
        for (int i = 0; i < totalBytes; i += bytesPerPixel)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            // clear color ≈ (26, 26, 38)，渲染后的像素应明显不同
            if (r > 50 || g > 50 || b > 60) nonTrivialPixels++;
        }
        float coverage = (float)nonTrivialPixels / (Width * Height);
        Console.WriteLine($"[RenderGraph] Final color 非 trivial 像素: {nonTrivialPixels}/{Width * Height} ({coverage:P1})");

        if (coverage < 0.01f)
        {
            Console.Error.WriteLine($"❌ Final color 几乎全空 ({coverage:P1} < 1%)");
            return 1;
        }

        Console.WriteLine("[RenderGraph] ✅ Render Graph 三 pass Demo 完成：排序正确 + UseResource 自动插入 + 像素非空。");
        return 0;
    }
}
