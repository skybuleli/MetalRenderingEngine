using System.Numerics;
using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// 当前 3D 离屏渲染最小集成验证。
/// 目标：锁定 instancing + argument buffer + depth-enabled render-to-texture 路径仍可工作。
/// </summary>
public class ThreeDSceneIntegrationTests
{
    private const int Width = 256;
    private const int Height = 256;
    private const int InstanceCount = 100;
    private const ulong ArgumentTableBufferIndex = 2;

    private static string ThreeDVertPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "ThreeDScene.vert.metallib");

    private static string ThreeDFragPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "ThreeDScene.frag.metallib");

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VertArgBuffer
    {
        public UavDescriptor Srv0;
        public UavDescriptor Srv1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrameCB
    {
        public Matrix4x4 ViewProj;
        public Vector4 LightDir;
        public Vector4 CamPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceData
    {
        public Matrix4x4 Model;
    }

    private static float ReadHalf(byte[] bytes, int byteOffset)
    {
        ushort bits = BitConverter.ToUInt16(bytes, byteOffset);
        return (float)BitConverter.UInt16BitsToHalf(bits);
    }

    [Fact]
    public void InstancedThreeDScene_RendersVisiblePixels()
    {
        Assert.True(File.Exists(ThreeDVertPath), $"metallib 不存在：{ThreeDVertPath}");
        Assert.True(File.Exists(ThreeDFragPath), $"metallib 不存在：{ThreeDFragPath}");

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(ThreeDVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(ThreeDFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new RenderPipelineDescBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithColorAttachment(1, MTLPixelFormat.RGBA16Float)
            .WithDepth(MTLPixelFormat.Depth32Float)
            .WithSampleCount(1)
            .Build();
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        var colorInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = Width,
            Height = Height,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var colorTex = device.NewTexture(colorInfo);
        var gbufferInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.RGBA16Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = Width,
            Height = Height,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var gbufferTex = device.NewTexture(gbufferInfo);
        var depthInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.Depth32Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = Width,
            Height = Height,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModePrivate,
        };
        using var depthTex = device.NewTexture(depthInfo);
        var dsDesc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };
        using var depthState = device.NewDepthStencilState(dsDesc);

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
            instances[i].Model = Matrix4x4.CreateTranslation(x, y, z);
        }

        int perFrameSize = Marshal.SizeOf<PerFrameCB>();
        using var perFrameBuffer = device.NewBuffer((ulong)perFrameSize, MTLResourceOptions.StorageModeShared);
        var camera = new Camera(MathF.PI / 4f, (float)Width / Height, 0.1f, 100f);
        camera.LookFrom(new Vector3(0, 5, 15), Vector3.Zero, Vector3.UnitY);
        perFrameBuffer.AsSpan<PerFrameCB>()[0] = new PerFrameCB
        {
            ViewProj = camera.ViewProj,
            LightDir = new Vector4(Vector3.Normalize(new Vector3(0.5f, 1f, 0.3f)), 0),
            CamPos = new Vector4(0, 5, 15, 0),
        };

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
        Assert.Equal(10, recorder.CommandCount);
        recorder.EndFrame();

        byte[] pixels = new byte[Width * Height * 4];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(colorTex.Handle, p, (ulong)pixels.Length, 0);
                Assert.Equal((ulong)pixels.Length, written);
            }
        }

        int nonBlack = 0;
        int alphaMismatch = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0)
                nonBlack++;
            if (pixels[i + 3] != 255)
                alphaMismatch++;
        }

        float coverage = (float)nonBlack / (Width * Height);
        Assert.True(coverage >= 0.03f, $"3D 场景覆盖率过低：{coverage:P2}");
        Assert.Equal(0, alphaMismatch);

        byte[] gbufferBytes = new byte[Width * Height * 8];
        unsafe
        {
            fixed (byte* p = gbufferBytes)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(gbufferTex.Handle, p, (ulong)gbufferBytes.Length, 0);
                Assert.Equal((ulong)gbufferBytes.Length, written);
            }
        }

        int mrt1NonZero = 0;
        float minDepth = 1f;
        float maxDepth = 0f;
        float roughness = -1f;
        for (int pixel = 0; pixel < Width * Height; pixel++)
        {
            int offset = pixel * 8;
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
        Assert.True(mrt1Coverage >= 0.03f, $"MRT1 覆盖率过低：{mrt1Coverage:P2}");
        Assert.InRange(minDepth, 0f, 1f);
        Assert.InRange(maxDepth, 0f, 1f);
        Assert.InRange(roughness, 0f, 1f);
    }
}
