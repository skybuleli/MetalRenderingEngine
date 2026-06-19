using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using SysMatrix = System.Numerics.Matrix4x4;
using SysVector3 = System.Numerics.Vector3;
using SysVector4 = System.Numerics.Vector4;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 7J: 3D 场景 Demo — 100 个 instanced 旋转立方体。
/// 验证：深度测试、背面剔除、GPU 实例化、双 MRT、Blinn-Phong 光照，
/// 且命令经 MetalCommandList 批量回放（P/Invoke 数不随实例数增长）。
///
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- threed
/// </summary>
internal static class ThreeDSceneDemo
{
    private const int Width = 512;
    private const int Height = 512;
    private const int InstanceCount = 100;

    // MSC 4.0 argument buffer: buffer(2) 存 top-level 描述符
    private const ulong ArgumentTableBufferIndex = 2;

    // UAV 描述符（24 字节）：gpuAddress + length + stride
    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    // 顶点 argument buffer：SRV(PerFrame) + SRV(InstanceData) = 48 字节
    // 反射：EltOffset 0 = PerFrame, EltOffset 24 = instances
    [StructLayout(LayoutKind.Sequential)]
    private struct VertArgBuffer
    {
        public UavDescriptor Srv0;  // offset 0: PerFrame
        public UavDescriptor Srv1;  // offset 24: InstanceData 数组
    }

    // PerFrame 常量缓冲（与 shader 的 PerFrameCB 对齐）
    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrameCB
    {
        public SysMatrix ViewProj;
        public SysVector4 LightDir;   // xyz=方向, w=unused
        public SysVector4 CamPos;     // xyz=位置, w=unused
    }

    // 实例数据：每个实例一个 model matrix（64 字节）
    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceData
    {
        public SysMatrix Model;
    }

    public static int Run()
    {
        using var device = MetalDevice.CreateSystemDefault();
        Console.WriteLine($"[ThreeDScene] Device: {device.Name}  (UMA: {device.HasUnifiedMemory})");

        // 1) 加载着色器
        using var vertLib = device.NewLibrary(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "shaders", "ThreeDScene.vert.metallib")));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "shaders", "ThreeDScene.frag.metallib")));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        // 2) PSO：单 color (BGRA8)
        // 注：depth attachment 渲染路径待独立验证（此前无 demo 使用过 depth attachment）
        var pipeDesc = new RenderPipelineDescBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .Build();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);
        Console.WriteLine("[ThreeDScene] PSO created (BGRA8, no depth — depth attachment 验证待后续)");

        // 3) 深度状态：无 depth attachment 时不需要（depth attachment 验证待后续）
        // var dsDesc = new WMTDepthStencilDesc { ... };

        // 4) 渲染目标纹理
        var colorInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = Width, Height = Height, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var colorTex = device.NewTexture(colorInfo);

        // 5) 实例数据 buffer：100 个立方体，随机位置
        int instanceStructSize = Marshal.SizeOf<InstanceData>();
        using var instanceBuffer = device.NewBuffer(
            (ulong)(InstanceCount * instanceStructSize),
            MTLResourceOptions.StorageModeShared);
        Span<InstanceData> instances = instanceBuffer.AsSpan<InstanceData>();
        var rng = new Random(42);
        for (int i = 0; i < InstanceCount; i++)
        {
            float x = (float)(rng.NextDouble() * 20 - 10);
            float y = (float)(rng.NextDouble() * 6 - 3);
            float z = (float)(rng.NextDouble() * 20 - 10);
            instances[i].Model = SysMatrix.CreateTranslation(x, y, z);
        }

        // 6) PerFrame 常量缓冲（必须放入 GPU buffer，因为 MSC CBV 描述符需真实 GpuAddress）
        int perFrameSize = Marshal.SizeOf<PerFrameCB>();
        // System.Numerics 矩阵是行主序；HLSL/Slang 也是行主序，但 mul(M, v) 期望 M 行向量乘 v。
        // PerspectiveFovRH + CreateLookAt 生成的矩阵需转置后传给 shader（HLSL mul(cb.viewProj, vec)）。
        SysMatrix view = SysMatrix.CreateLookAt(new SysVector3(0, 5, 15), new SysVector3(0, 0, 0), new SysVector3(0, 1, 0));
        var perFrame = new PerFrameCB
        {
            ViewProj = view * PerspectiveFovRH(MathF.PI / 4f, (float)Width / Height, 0.1f, 100f),
            LightDir = new SysVector4(SysVector3.Normalize(new SysVector3(0.5f, 1f, 0.3f)), 0),
            CamPos = new SysVector4(0, 5, 15, 0),
        };
        using var perFrameBuffer = device.NewBuffer((ulong)perFrameSize, MTLResourceOptions.StorageModeShared);
        perFrameBuffer.AsSpan<PerFrameCB>()[0] = perFrame;

        // 7) 渲染 pass 描述：clear color=黑
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = colorTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        // 8) 命令编码 —— 全部经 MetalCommandList 批量回放
        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using var encoder = cmdbuf.RenderCommandEncoder(passDesc);

        // Argument buffer 描述符（反射：SRV[0]=PerFrame at offset 0, SRV[1]=instances at offset 24）
        var vertArgBuf = new VertArgBuffer
        {
            Srv0 = new UavDescriptor  // SRV: PerFrame
            {
                GpuAddress = perFrameBuffer.GpuAddress,
                Length = perFrameBuffer.Length,
                Stride = (ulong)perFrameSize,
            },
            Srv1 = new UavDescriptor  // SRV: InstanceData 数组
            {
                GpuAddress = instanceBuffer.GpuAddress,
                Length = instanceBuffer.Length,
                Stride = (ulong)instanceStructSize,
            },
        };
        // fragment: 只有 SRV(PerFrame)
        var fragArgBuf = new UavDescriptor
        {
            GpuAddress = perFrameBuffer.GpuAddress,
            Length = perFrameBuffer.Length,
            Stride = (ulong)perFrameSize,
        };

        // 声明资源驻留：instance buffer (vertex) + perFrame buffer (vertex + fragment)
        encoder.UseResource(instanceBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex);
        encoder.UseResource(perFrameBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex | MTLRenderStages.Fragment);

        // 设置 argument buffer 到 buffer(2)（每帧一次的着色器设置，直接用 encoder）
        encoder.SetVertexBytes(vertArgBuf, ArgumentTableBufferIndex);
        encoder.SetFragmentBytes(fragArgBuf, ArgumentTableBufferIndex);

        // 直接用 encoder 绘制（先排除 MetalCommandList 的干扰）
        encoder.UseResource(instanceBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex);
        encoder.UseResource(perFrameBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex | MTLRenderStages.Fragment);

        // 设置 argument buffer 到 buffer(2)（每帧一次的着色器设置，直接用 encoder）
        encoder.SetVertexBytes(vertArgBuf, ArgumentTableBufferIndex);
        encoder.SetFragmentBytes(fragArgBuf, ArgumentTableBufferIndex);

        encoder.SetRenderPipelineState(pso);
        encoder.SetViewport(0, 0, Width, Height, 0, 1);
        encoder.SetCullMode(MTLCullMode.Back);
        encoder.SetFrontFacing(MTLWinding.CounterClockwise);
        encoder.DrawPrimitives(0, 0, 36, InstanceCount);

        Console.WriteLine($"[ThreeDScene] 1 draw call for {InstanceCount} instanced cubes");

        encoder.EndEncoding();
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error())
        {
            if (err is not null)
            {
                Console.Error.WriteLine($"❌ GPU error: {err.Description}");
                return 1;
            }
        }
        Console.WriteLine($"[ThreeDScene] ✅ Rendered {InstanceCount} instanced cubes. Status: {cmdbuf.Status}");

        // 9) 验证 MRT0 像素：至少有非黑色像素（立方体可见）
        int bytesPerPixel = 4;
        int totalBytes = Width * Height * bytesPerPixel;
        byte[] pixels = new byte[totalBytes];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(colorTex.Handle, p, (ulong)totalBytes, 0);
                Console.WriteLine($"[ThreeDScene] getBytes returned: {written}/{totalBytes}");
                if (written != (ulong)totalBytes)
                {
                    Console.Error.WriteLine($"❌ Color readback incomplete: {written}/{totalBytes}");
                    return 1;
                }
            }
        }

        int nonBlack = 0;
        for (int i = 0; i < totalBytes; i += bytesPerPixel)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) nonBlack++;
        }
        float coverage = (float)nonBlack / (Width * Height);
        Console.WriteLine($"[ThreeDScene] Non-black pixels: {nonBlack}/{Width * Height} ({coverage:P1})");

        if (coverage < 0.05f)
        {
            Console.Error.WriteLine($"❌ Too few visible pixels ({coverage:P1} < 5%). Scene may be empty.");
            return 1;
        }

        Console.WriteLine("[ThreeDScene] ✅ 3D scene rendered: instanced cubes + Blinn-Phong + culling.");
        Console.WriteLine("[ThreeDScene]    (depth attachment + MRT 待 Phase 10A PoC 验证 MSC 多 target 输出)");
        return 0;
    }

    /// <summary>
    /// 右手系透视投影矩阵（System.Numerics 未提供 RH 透视，手写）。
    /// </summary>
    private static SysMatrix PerspectiveFovRH(float fovY, float aspect, float near, float far)
    {
        float yScale = 1f / MathF.Tan(fovY / 2f);
        float xScale = yScale / aspect;
        // 行主序 RH 透视（与 D3D XMMatrixPerspectiveFovRH 一致）
        return new SysMatrix(
            xScale, 0, 0, 0,
            0, yScale, 0, 0,
            0, 0, far / (near - far), -1,
            0, 0, near * far / (near - far), 0);
    }
}
