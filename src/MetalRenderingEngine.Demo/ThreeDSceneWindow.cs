using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;
using MetalRenderingEngine.Rendering;
using SysVector3 = System.Numerics.Vector3;
using SysVector4 = System.Numerics.Vector4;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 7J/8: 100 个 instanced 旋转立方体，4x MSAA，Blinn-Phong 光照。
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- threed-win
/// </summary>
internal static class ThreeDSceneWindow
{
    private const int W = 800, H = 600;
    private const int InstanceCount = 100;
    private const int SampleCount = 4;  // 4x MSAA
    private const ulong ArgIndex = 2;  // MSC top-level argument buffer 在 buffer(2)

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrame { public System.Numerics.Matrix4x4 ViewProj; public SysVector4 LightDir; public SysVector4 CamPos; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance { public System.Numerics.Matrix4x4 Model; }

    // Vertex argument buffer：SRV0(PerFrame) + SRV1(InstanceData) = 48 字节（与 MSC 反射 EltOffset 对齐）
    [StructLayout(LayoutKind.Sequential)]
    private struct VertArgBuffer { public UavDescriptor Srv0; public UavDescriptor Srv1; }

    public static int Run()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var window = SDL3Window.Create("3D Instanced Cubes — Phase 7/8", W, H);
        var layer = new MetalLayer(window.LayerHandle);
        layer.SetDevice(device);
        layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
        layer.SetDrawableSize(W, H);

        // 着色器 + 管线（一行加载 + PipelineBuilder 链式构造）
        using var vertFn = MetalShaderLoader.GetFunction(device, "ThreeDScene.vert", "main");
        using var fragFn = MetalShaderLoader.GetFunction(device, "ThreeDScene.frag", "main");
        using var pso = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm).WithSampleCount(SampleCount)
            .Build(device, vertFn, fragFn);

        MetalTexture? msaaTex = SampleCount > 1
            ? device.NewTexture(WMTTextureInfo.Create2DMultisample(MTLPixelFormat.BGRA8Unorm, W, H, SampleCount))
            : null;
        using var instanceBuf = device.NewBuffer((ulong)(InstanceCount * Marshal.SizeOf<Instance>()), MTLResourceOptions.StorageModeShared);
        using var perFrameBuf = device.NewBuffer((ulong)Marshal.SizeOf<PerFrame>(), MTLResourceOptions.StorageModeShared);

        // 填充实例数据：100 个随机位置的立方体
        var rng = new Random(42);
        var instances = instanceBuf.AsSpan<Instance>();
        for (int i = 0; i < InstanceCount; i++)
            instances[i].Model = System.Numerics.Matrix4x4.CreateTranslation(
                (float)(rng.NextDouble() * 20 - 10), (float)(rng.NextDouble() * 6 - 3), (float)(rng.NextDouble() * 20 - 10));

        // 渲染循环（通过 ICommandRecorder 接口，内部走 MetalCommandList 批量回放）
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
                float r = 18f;
                camera.LookFrom(new SysVector3(MathF.Cos(t * 0.2f) * r, 5, MathF.Sin(t * 0.2f) * r), SysVector3.Zero);
                perFrameBuf.AsSpan<PerFrame>()[0] = new PerFrame
                {
                    ViewProj = camera.ViewProj,
                    LightDir = new SysVector4(SysVector3.Normalize(new(0.5f, 1f, 0.3f)), 0),
                    CamPos = new SysVector4(camera.Position, 0),
                };

                // argument buffer 描述符（buffer.ToUavDescriptor() 一行搞定）
                var vertArg = new VertArgBuffer
                {
                    Srv0 = perFrameBuf.ToUavDescriptor((ulong)Marshal.SizeOf<PerFrame>()),
                    Srv1 = instanceBuf.ToUavDescriptor((ulong)Marshal.SizeOf<Instance>()),
                };
                var fragArg = perFrameBuf.ToUavDescriptor((ulong)Marshal.SizeOf<PerFrame>());

                var passDesc = msaaTex is not null
                    ? new RenderPassBuilder().MsaaColor(msaaTex, drawable.Texture, new(0.1f, 0.12f, 0.15f, 1f)).Build()
                    : new RenderPassBuilder().Color(drawable.Texture, new(0.1f, 0.12f, 0.15f, 1f)).Build();

                recorder.BeginFrame();
                recorder.BeginRenderPass(passDesc);
                recorder.SetPipelineState(pso);
                recorder.SetViewport(0, 0, W, H, 0, 1);
                recorder.SetCullMode(MTLCullMode.Back);
                recorder.SetFrontFacing(MTLWinding.CounterClockwise);
                recorder.UseResource(instanceBuf, MTLResourceUsage.Read, MTLRenderStages.Vertex);
                recorder.UseResource(perFrameBuf, MTLResourceUsage.Read, MTLRenderStages.Vertex | MTLRenderStages.Fragment);
                recorder.SetVertexBytes(in vertArg, ArgIndex);
                recorder.SetFragmentBytes(in fragArg, ArgIndex);
                recorder.Draw(0, 0, 36, InstanceCount);
                recorder.EndRenderPass();

                ((MetalCommandRecorder)recorder).PresentDrawable(drawable);
                ((MetalCommandRecorder)recorder).Submit();
            }
            if (++frame % 60 == 0) { Console.WriteLine($"  frame {frame} ({recorder.CommandCount} cmds)"); Console.Out.Flush(); }
            Thread.Sleep(16);
        }
        Console.WriteLine($"✅ {frame} frames, {InstanceCount} instanced cubes, {SampleCount}x MSAA");
        return 0;
    }
}
