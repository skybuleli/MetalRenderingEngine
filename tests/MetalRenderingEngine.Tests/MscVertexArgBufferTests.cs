using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// MSC argument buffer 在 vertex shader 中的可用性验证。
/// 根因：MSC argument buffer 需要 UseResource 声明资源驻留，否则 GPU 无法解析内嵌的 GPU 地址。
/// </summary>
public class MscVertexArgBufferTests
{
    private const int TexW = 256, TexH = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public Vector4 Color;
        public float Life, MaxLife, Size, Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrameCB
    {
        public Vector2 Viewport;
        public float Time, DeltaTime;
        public Vector2 Gravity, Wind;
        public float EmitterX, EmitterY;
        public int ActiveCount;
        public float Pad1, Pad2, Pad3;
    }

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

    private const string RenderShaderSource = @"
struct Particle {
    float2 position; float2 velocity; float4 color;
    float life, maxLife, size, pad;
};
struct PerFrame {
    float2 viewport; float time, deltaTime;
    float2 gravity, wind;
    float emitterX, emitterY;
    int activeCount;
    float pad1, pad2, pad3;
};
struct VSOut {
    float4 position : SV_Position;
    float4 color    : TEXCOORD0;
    float2 uv       : TEXCOORD1;
};
static const float2 offsets[6] = {
    float2(-1,-1), float2(-1,1), float2(1,-1),
    float2(1,-1),  float2(-1,1), float2(1,1)
};
[shader(""vertex"")]
VSOut main(uint vid : SV_VertexID, uint iid : SV_InstanceID,
           StructuredBuffer<Particle> particles,
           StructuredBuffer<PerFrame> pf)
{
    Particle p = particles[iid];
    float2 off = offsets[vid];
    float2 vp = pf[0].viewport;
    float2 ndc = (p.position / vp) * 2.0 - 1.0;
    ndc.y = -ndc.y;
    float sz = p.size;
    float2 pxSize = sz * 2.0 / vp;
    VSOut o;
    o.position = float4(ndc + off * pxSize, 0, 1);
    o.color = p.color;
    o.uv = off;
    return o;
}
[shader(""fragment"")]
float4 frag_main(VSOut input) : SV_Target0
{
    float dist = length(input.uv);
    if (dist > 1.0) discard;
    float alpha = input.color.a * (1.0 - dist * dist);
    return float4(input.color.rgb * alpha, alpha);
}
";

    /// <summary>
    /// 验证 MSC argument buffer 在 vertex shader 中能正确渲染（需 UseResource）。
    /// 渲染 100 个已知位置的粒子到 offscreen texture，验证非背景像素数 > 0。
    /// </summary>
    [Fact]
    public void MscVertexArgBuffer_WithUseResource_RendersVisiblePixels()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();

        // 编译 vertex + fragment
        var vertOpts = new ShaderCompileOptions { Stage = ShaderStage.Vertex, GenerateReflection = true };
        var fragOpts = new ShaderCompileOptions { Stage = ShaderStage.Fragment, EntryPoint = "frag_main" };
        var vertResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(RenderShaderSource), "TestRender.slang", vertOpts);
        var fragResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(RenderShaderSource), "TestRender.slang", fragOpts);

        Assert.NotNull(vertResult.MetallibData);
        Assert.NotNull(fragResult.MetallibData);

        // 反射验证
        var reflect = Shader.Reflection.MscReflectionParser.Parse(vertResult.ReflectionJson!);
        Assert.Equal(2, reflect.ResourceCount);

        // 创建 PSO
        using var vertFn = device.NewLibrary(vertResult.MetallibData).NewFunction("main");
        using var fragFn = device.NewLibrary(fragResult.MetallibData).NewFunction("frag_main");
        var pipeDesc = new WMTRenderPipelineDesc { ColorCount = 1, SampleCount = 1 };
        pipeDesc.ColorAttachmentAt(0) = new WMTColorAttachment
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            WriteMask = 0xF,
            BlendingEnabled = 1,
            SrcRgbBlendFactor = (int)MTLBlendFactor.One,
            DstRgbBlendFactor = (int)MTLBlendFactor.One,
            SrcAlphaBlendFactor = (int)MTLBlendFactor.One,
            DstAlphaBlendFactor = (int)MTLBlendFactor.One,
            RgbBlendOp = (int)MTLBlendOperation.Add,
            AlphaBlendOp = (int)MTLBlendOperation.Add,
        };
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        // 创建 offscreen texture
        var texInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = 0, Width = TexW, Height = TexH, Depth = 1,
            MipmapLevels = 1, SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var texture = device.NewTexture(texInfo);

        // Buffers
        int count = 100;
        int particleSize = Marshal.SizeOf<Particle>();
        int perFrameSize = Marshal.SizeOf<PerFrameCB>();
        using var particleBuf = device.NewBuffer((ulong)(count * particleSize), MTLResourceOptions.StorageModeShared);
        using var perFrameBuf = device.NewBuffer((ulong)perFrameSize, MTLResourceOptions.StorageModeShared);

        // 初始化粒子在中心
        Span<Particle> parts = particleBuf.AsSpan<Particle>();
        for (int i = 0; i < count; i++)
        {
            parts[i] = new Particle
            {
                Position = new Vector2(TexW / 2f, TexH / 2f),
                Color = new Vector4(1f, 0.5f, 0.2f, 1f),
                Life = 2f, MaxLife = 3f, Size = 20f,
            };
        }
        perFrameBuf.AsSpan<PerFrameCB>()[0] = new PerFrameCB
        {
            Viewport = new Vector2(TexW, TexH),
            ActiveCount = count,
        };

        // Render
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = texture.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0.02f, 0.02f, 0.06f, 1f),
        });

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var enc = cmdbuf.RenderCommandEncoder(passDesc))
        {
            enc.SetRenderPipelineState(pso);
            enc.SetViewport(0, 0, TexW, TexH, 0, 1);

            // 关键：UseResource 声明资源驻留
            enc.UseResource(particleBuf, MTLResourceUsage.Read, MTLRenderStages.Vertex);
            enc.UseResource(perFrameBuf, MTLResourceUsage.Read, MTLRenderStages.Vertex);

            // argument buffer
            var argBuf = new VertArgBuffer
            {
                Srv0 = new UavDescriptor { GpuAddress = particleBuf.GpuAddress, Length = particleBuf.Length, Stride = (ulong)particleSize },
                Srv1 = new UavDescriptor { GpuAddress = perFrameBuf.GpuAddress, Length = perFrameBuf.Length, Stride = (ulong)perFrameSize },
            };
            enc.SetVertexBytes(in argBuf, 2);

            enc.DrawPrimitives(0, 0, 6, (ulong)count);
            enc.EndEncoding();
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error()) Assert.Null(err);

        // 读回像素并统计
        int totalBytes = TexW * TexH * 4;
        byte[] pixels = new byte[totalBytes];
        unsafe
        {
            fixed (byte* p = pixels)
                MetalBridge.MTLTexture_getBytes(texture.Handle, p, (ulong)totalBytes, 0);
        }

        int nonBgPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            // 背景色约 (5, 5, 15)，粒子偏暖色 (255, 127, 51)
            if (r > 30 || g > 30) nonBgPixels++;
        }

        // 100 个 20px 粒子在 256×256 纹理中心，应有大量非背景像素
        Assert.True(nonBgPixels > 500, $"MSC vertex arg buffer 应渲染可见像素，实际 {nonBgPixels}");
    }
}
