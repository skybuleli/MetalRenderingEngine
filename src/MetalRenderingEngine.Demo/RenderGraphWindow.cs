using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;
using SysMatrix = System.Numerics.Matrix4x4;
using SysVector3 = System.Numerics.Vector3;
using SysVector4 = System.Numerics.Vector4;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Rendering.RenderGraph;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 11: Render Graph 窗口 Demo — Shadow Map → GBuffer → Final 三 pass + 4x MSAA + 旋转相机动画。
/// 演示 ExecutePasses（窗口模式）+ 每帧重建 graph（drawable 变化）+ 拓扑排序。
///
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- rendergraph-win
/// <para><b>绑定路径</b>：本 Demo 使用 StructuredBuffer + 手工 UavDescriptor argument buffer
    /// （Phase 7-9 遗留路径）。新开发请使用 ResourceTable / ShaderBindingLayout
    /// （Phase 10 描述符堆路径），参考 <see cref="TexturedCubeDemo"/>。</para>
    
/// </summary>
internal static class RenderGraphWindow
{
    private const int W = 800, H = 600;
    private const int InstanceCount = 100;
    private const int SampleCount = 4; // 4x MSAA
    private const ulong ArgIndex = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrame
    {
        public SysMatrix ViewProj;
        public SysVector4 LightDir;
        public SysVector4 CamPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public SysMatrix Model;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VertArgBuffer
    {
        public UavDescriptor Srv0; // PerFrame
        public UavDescriptor Srv1; // InstanceData
    }

    public static int Run()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var window = SDL3Window.Create("Render Graph — Phase 11 (3 passes)", W, H);
        var layer = new MetalLayer(window.LayerHandle);
        layer.SetDevice(device);
        layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
        layer.SetDrawableSize(W, H);

        // 着色器 + PSO
        using var vertFn = MetalShaderLoader.GetFunction(device, "ThreeDScene.vert", "main");
        using var fragFn = MetalShaderLoader.GetFunction(device, "ThreeDScene.frag", "main");
        using var pso = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithColorAttachment(1, MTLPixelFormat.RGBA16Float)
            .WithDepth(MTLPixelFormat.Depth32Float)
            .WithSampleCount(SampleCount)
            .Build(device, vertFn, fragFn);

        var dsDesc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };
        using var depthState = device.NewDepthStencilState(dsDesc);

        // MSAA 纹理（所有 pass 共享同一组 MSAA 纹理，每帧 clear）
        using var msaaColorTex = device.NewTexture(WMTTextureInfo.Create2DMultisample(
            MTLPixelFormat.BGRA8Unorm, W, H, SampleCount));
        using var msaaGbufferTex = device.NewTexture(WMTTextureInfo.Create2DMultisample(
            MTLPixelFormat.RGBA16Float, W, H, SampleCount));
        using var msaaDepthTex = device.NewTexture(WMTTextureInfo.Create2DMultisample(
            MTLPixelFormat.Depth32Float, W, H, SampleCount));

        // 解析后的离屏纹理（ShaderRead，供 Final pass 读取）
        using var shadowDepthTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.Depth32Float, W, H,
            MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModePrivate));

        using var gbufferColorTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.BGRA8Unorm, W, H,
            MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead,
            MTLResourceOptions.StorageModePrivate));

        using var gbufferNormalTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA16Float, W, H,
            MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead,
            MTLResourceOptions.StorageModePrivate));

        using var gbufferDepthTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.Depth32Float, W, H,
            MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModePrivate));

        // 实例数据 + PerFrame buffer
        using var instanceBuf = device.NewBuffer(
            (ulong)(InstanceCount * Marshal.SizeOf<Instance>()),
            MTLResourceOptions.StorageModeShared);
        using var perFrameBuf = device.NewBuffer(
            (ulong)Marshal.SizeOf<PerFrame>(),
            MTLResourceOptions.StorageModeShared);

        var rng = new Random(42);
        var instances = instanceBuf.AsSpan<Instance>();
        for (int i = 0; i < InstanceCount; i++)
            instances[i].Model = SysMatrix.CreateTranslation(
                (float)(rng.NextDouble() * 20 - 10),
                (float)(rng.NextDouble() * 6 - 3),
                (float)(rng.NextDouble() * 20 - 10));

        // Argument buffer
        var vertArg = new VertArgBuffer
        {
            Srv0 = perFrameBuf.ToUavDescriptor((ulong)Marshal.SizeOf<PerFrame>()),
            Srv1 = instanceBuf.ToUavDescriptor((ulong)Marshal.SizeOf<Instance>()),
        };
        var fragArg = perFrameBuf.ToUavDescriptor((ulong)Marshal.SizeOf<PerFrame>());

        // 离屏 pass 描述符（不依赖 drawable，可复用）
        // ShadowMap：MSAA 深度（不解析，每帧 clear）
        var shadowPassDesc = new WMTRenderPassDesc();
        shadowPassDesc.Depth = new WMTRenderPassAttachment
        {
            Texture = msaaDepthTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearDepth = 1.0f,
        };

        // GBuffer：MSAA 颜色 → 解析到 gbufferColorTex，MSAA GBuffer → 解析到 gbufferNormalTex，MSAA 深度
        var gbufferPassDesc = new WMTRenderPassDesc();
        gbufferPassDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = msaaColorTex.Handle,
            ResolveTexture = gbufferColorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.MultisampleResolve,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });
        gbufferPassDesc.SetColorAt(1, new WMTRenderPassAttachment
        {
            Texture = msaaGbufferTex.Handle,
            ResolveTexture = gbufferNormalTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.MultisampleResolve,
            ClearColor = new WMTClearColor(0, 0, 0, 0),
        });
        gbufferPassDesc.Depth = new WMTRenderPassAttachment
        {
            Texture = msaaDepthTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearDepth = 1.0f,
        };

        // 渲染循环
        var camera = new Camera(MathF.PI / 4f, (float)W / H, 0.1f, 100f);
        var recorder = new MetalCommandRecorder(device);
        using var sync = new FrameSync(device);
        long start = Environment.TickCount64;
        int frame = 0;

        // 启用 VSync + triple-buffer（与 FrameSync 配合实现精确帧同步）
        layer.SetDisplaySyncEnabled(true);
        layer.SetMaximumDrawableCount(sync.InFlightFrames);

        while (true)
        {
            if (window.PollShouldClose()) break;

            // WaitFrame：等待最旧帧的 GPU 完成（triple-buffer 帧限速）
            sync.WaitFrame();

            var drawable = layer.NextDrawable();
            if (drawable == null) { Thread.Sleep(1); continue; }
            using (drawable)
            {
                float t = (Environment.TickCount64 - start) / 1000f;
                float r = 18f;
                camera.LookFrom(
                    new SysVector3(MathF.Cos(t * 0.3f) * r, 5, MathF.Sin(t * 0.3f) * r),
                    SysVector3.Zero);
                perFrameBuf.AsSpan<PerFrame>()[0] = new PerFrame
                {
                    ViewProj = camera.ViewProj,
                    LightDir = new SysVector4(SysVector3.Normalize(new SysVector3(0.5f, 1f, 0.3f)), 0),
                    CamPos = new SysVector4(camera.Position, 0),
                };

                // Final pass 描述符（每帧更新 drawable）：MSAA 颜色 → 解析到 drawable
                var finalPassDesc = new WMTRenderPassDesc();
                finalPassDesc.SetColorAt(0, new WMTRenderPassAttachment
                {
                    Texture = msaaColorTex.Handle,
                    ResolveTexture = drawable.Texture.Handle,
                    LoadAction = (int)MTLLoadAction.Clear,
                    StoreAction = (int)MTLStoreAction.MultisampleResolve,
                    ClearColor = new WMTClearColor(0.08f, 0.08f, 0.12f, 1f),
                });

                // 每帧重建 Render Graph（drawable 纹理句柄每帧变化）
                var graph = new RenderGraph();

                // 故意乱序声明：Final → ShadowMap → GBuffer
                graph.AddPass("Final")
                    .Reads(gbufferColorTex, MTLRenderStages.Fragment, "gbuffer color")
                    .Reads(gbufferNormalTex, MTLRenderStages.Fragment, "gbuffer normal")
                    .Writes(drawable.Texture, MTLRenderStages.Fragment)
                    .WithRenderPassDesc(finalPassDesc)
                    .Record(RecordDraw);

                graph.AddPass("ShadowMap")
                    .Writes(shadowDepthTex, MTLRenderStages.Vertex, "shadow depth")
                    .Reads(perFrameBuf, MTLRenderStages.Vertex, "per-frame")
                    .WithRenderPassDesc(shadowPassDesc)
                    .Record(RecordDraw);

                graph.AddPass("GBuffer")
                    .Writes(gbufferColorTex, MTLRenderStages.Fragment, "gbuffer color")
                    .Writes(gbufferNormalTex, MTLRenderStages.Fragment, "gbuffer normal")
                    .Writes(gbufferDepthTex, MTLRenderStages.Fragment, "gbuffer depth")
                    .Reads(perFrameBuf, MTLRenderStages.Vertex, "per-frame")
                    .WithRenderPassDesc(gbufferPassDesc)
                    .Record(RecordDraw);

                // ExecutePasses：不管理帧边界，由外部 BeginFrame/PresentDrawable/Submit 控制
                recorder.BeginFrame();
                graph.ExecutePasses(recorder);

                sync.SignalFrame(recorder.CommandBuffer);
                recorder.PresentDrawable(drawable);
                recorder.Submit();
            }
            if (++frame % 60 == 0)
            {
                Console.WriteLine($"  frame {frame} ({recorder.CommandCount} cmds)");
                Console.Out.Flush();
            }
        }
        Console.WriteLine($"✅ {frame} frames, Render Graph 3-pass, {SampleCount}x MSAA, {InstanceCount} instanced cubes");
        return 0;

        void RecordDraw(ICommandRecorder rec)
        {
            rec.SetPipelineState(pso);
            rec.SetDepthStencilState(depthState);
            rec.SetViewport(0, 0, W, H, 0, 1);
            rec.SetCullMode(MTLCullMode.Back);
            rec.SetFrontFacing(MTLWinding.CounterClockwise);
            rec.UseResource(instanceBuf, MTLResourceUsage.Read, MTLRenderStages.Vertex);
            rec.UseResource(perFrameBuf, MTLResourceUsage.Read, MTLRenderStages.Vertex | MTLRenderStages.Fragment);
            rec.SetVertexBytes(in vertArg, ArgIndex);
            rec.SetFragmentBytes(in fragArg, ArgIndex);
            rec.Draw(0, 0, 36, InstanceCount);
        }
    }
}
