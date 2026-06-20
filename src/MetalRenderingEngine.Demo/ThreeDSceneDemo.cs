using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using SysMatrix = System.Numerics.Matrix4x4;
using SysVector3 = System.Numerics.Vector3;
using SysVector4 = System.Numerics.Vector4;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 7J: 3D 场景 Demo — 100 个 instanced 旋转立方体。
/// 当前验证范围：深度测试、背面剔除、GPU 实例化、离屏渲染读回、Blinn-Phong 光照、轻量 G-buffer。
/// 命令录制走 ICommandRecorder/MetalCommandList，验证批量回放路径不改变像素结果。
///
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- threed
/// <para><b>绑定路径</b>：本 Demo 使用 StructuredBuffer + 手工 UavDescriptor argument buffer
/// （Phase 7-9 遗留路径）。新开发请使用 ResourceTable / ShaderBindingLayout
/// （Phase 10 描述符堆路径），参考 <see cref="TexturedCubeDemo"/>。</para>
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

    private static float ReadHalf(byte[] bytes, int byteOffset)
    {
        ushort bits = BitConverter.ToUInt16(bytes, byteOffset);
        return (float)BitConverter.UInt16BitsToHalf(bits);
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

        // 2) PSO：当前验证双 color + depth attachment 的离屏 3D 渲染
        var pipeDesc = new RenderPipelineDescBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithColorAttachment(1, MTLPixelFormat.RGBA16Float)
            .WithDepth(MTLPixelFormat.Depth32Float)
            .WithSampleCount(1)
            .Build();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);
        Console.WriteLine("[ThreeDScene] PSO created (BGRA8 + RGBA16Float G-buffer + Depth32Float offscreen 3D path)");

        // 3) 深度状态：Less 比较 + 深度写入
        var dsDesc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };
        using var depthState = device.NewDepthStencilState(dsDesc);

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
        var gbufferInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.RGBA16Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = Width, Height = Height, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var gbufferTex = device.NewTexture(gbufferInfo);
        var depthInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.Depth32Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = Width, Height = Height, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModePrivate,
        };
        using var depthTex = device.NewTexture(depthInfo);

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
        var camera = new Camera(MathF.PI / 4f, (float)Width / Height, 0.1f, 100f);
        camera.LookFrom(new SysVector3(0, 5, 15), new SysVector3(0, 0, 0), new SysVector3(0, 1, 0));
        var perFrame = new PerFrameCB
        {
            ViewProj = camera.ViewProj,
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
        passDesc.SetColorAt(1, new WMTRenderPassAttachment
        {
            Texture = gbufferTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 0),
        });
        passDesc.Depth = new WMTRenderPassAttachment
        {
            Texture = depthTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearDepth = 1.0f,
        };

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

        // 8) 命令编码：经 MetalCommandList 批量回放
        using ICommandRecorder recorder = new MetalCommandRecorder(device);
        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        recorder.UseResource(instanceBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex);
        recorder.UseResource(perFrameBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex | MTLRenderStages.Fragment);
        recorder.SetVertexBytes(in vertArgBuf, ArgumentTableBufferIndex);
        recorder.SetFragmentBytes(in fragArgBuf, ArgumentTableBufferIndex);
        recorder.SetPipelineState(pso);
        recorder.SetDepthStencilState(depthState);
        recorder.SetViewport(0, 0, Width, Height, 0, 1);
        recorder.SetCullMode(MTLCullMode.Back);
        recorder.SetFrontFacing(MTLWinding.CounterClockwise);
        recorder.Draw(0, 0, 36, InstanceCount);
        Console.WriteLine($"[ThreeDScene] 1 draw call for {InstanceCount} instanced cubes");
        Console.WriteLine($"[ThreeDScene] Recorded commands: {recorder.CommandCount}");
        recorder.EndFrame();
        Console.WriteLine("[ThreeDScene] ✅ Rendered 100 instanced cubes via MetalCommandRecorder.");

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
        int alphaMismatch = 0;
        for (int i = 0; i < totalBytes; i += bytesPerPixel)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) nonBlack++;
            if (pixels[i + 3] != 255) alphaMismatch++;
        }
        float coverage = (float)nonBlack / (Width * Height);
        Console.WriteLine($"[ThreeDScene] Non-black pixels: {nonBlack}/{Width * Height} ({coverage:P1})");
        Console.WriteLine($"[ThreeDScene] MRT0 alpha mismatches: {alphaMismatch}/{Width * Height}");

        if (coverage < 0.05f)
        {
            Console.Error.WriteLine($"❌ Too few visible pixels ({coverage:P1} < 5%). Scene may be empty.");
            return 1;
        }
        if (alphaMismatch != 0)
        {
            Console.Error.WriteLine($"❌ MRT0 alpha mismatch: {alphaMismatch} pixel(s) are not 1.0.");
            return 1;
        }

        // 10) 验证 MRT1 像素：G-buffer 的 normal.xy / depth / roughness 应合理
        int mrt1BytesPerPixel = 8; // RGBA16Float
        byte[] gbufferBytes = new byte[Width * Height * mrt1BytesPerPixel];
        unsafe
        {
            fixed (byte* p = gbufferBytes)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(gbufferTex.Handle, p, (ulong)gbufferBytes.Length, 0);
                Console.WriteLine($"[ThreeDScene] MRT1 getBytes returned: {written}/{gbufferBytes.Length}");
                if (written != (ulong)gbufferBytes.Length)
                {
                    Console.Error.WriteLine($"❌ MRT1 readback incomplete: {written}/{gbufferBytes.Length}");
                    return 1;
                }
            }
        }

        int mrt1NonZero = 0;
        float minDepth = 1f;
        float maxDepth = 0f;
        float roughness = -1f;
        for (int pixel = 0; pixel < Width * Height; pixel++)
        {
            int offset = pixel * mrt1BytesPerPixel;
            float normalX = ReadHalf(gbufferBytes, offset);
            float normalY = ReadHalf(gbufferBytes, offset + 2);
            float depth01 = ReadHalf(gbufferBytes, offset + 4);
            float pixelRoughness = ReadHalf(gbufferBytes, offset + 6);
            if (normalX != 0 || normalY != 0 || depth01 != 0 || pixelRoughness != 0)
            {
                mrt1NonZero++;
                minDepth = MathF.Min(minDepth, depth01);
                maxDepth = MathF.Max(maxDepth, depth01);
                roughness = pixelRoughness;
            }
        }
        float mrt1Coverage = (float)mrt1NonZero / (Width * Height);
        Console.WriteLine($"[ThreeDScene] MRT1 non-zero pixels: {mrt1NonZero}/{Width * Height} ({mrt1Coverage:P1})");
        Console.WriteLine($"[ThreeDScene] MRT1 depth range: [{minDepth:F3}, {maxDepth:F3}], roughness={roughness:F3}");

        if (mrt1Coverage < 0.05f)
        {
            Console.Error.WriteLine($"❌ MRT1 coverage too low ({mrt1Coverage:P1} < 5%).");
            return 1;
        }
        if (minDepth < 0f || maxDepth > 1f)
        {
            Console.Error.WriteLine($"❌ MRT1 depth out of range: [{minDepth:F3}, {maxDepth:F3}]");
            return 1;
        }
        if (roughness < 0f || roughness > 1f)
        {
            Console.Error.WriteLine($"❌ MRT1 roughness out of range: {roughness:F3}");
            return 1;
        }

        Console.WriteLine("[ThreeDScene] ✅ 3D scene rendered: MRT0 color + MRT1 G-buffer + depth test + culling.");
        return 0;
    }
}
