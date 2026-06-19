using System.Buffers.Binary;
using System.Text;
using MetalRenderingEngine.Binding;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 10B: ArgumentBufferEncoder 测试。
/// 验证 encoder 按反射 EltOffset 序列化 IRDescriptorTableEntry（24B/条目），
/// 字段语义对齐 MSC 官方 metal_irconverter_runtime.h 的 IRDescriptorTableSet*。
/// </summary>
public class ArgumentBufferEncoderTests
{
    private const int SrcTexW = 4;
    private const int SrcTexH = 4;
    private const int RtW = 8;
    private const int RtH = 8;

    /// <summary>纯 Texture2D + SamplerState fragment shader（与 Phase10TextureSamplerDescriptorTests 一致）。</summary>
    private const string TexSampShaderSource = """
struct VSOut
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
};

Texture2D<float4> colorTex : register(t0);
SamplerState nearestSampler : register(s0);

[shader("vertex")]
VSOut main(uint vid : SV_VertexID)
{
    static const float2 positions[3] = {
        float2(-1.0, -1.0), float2(-1.0, 3.0), float2(3.0, -1.0)
    };
    static const float2 uvs[3] = {
        float2(0.0, 0.0), float2(0.0, 2.0), float2(2.0, 0.0)
    };
    VSOut o;
    o.position = float4(positions[vid], 0.0, 1.0);
    o.uv = uvs[vid];
    return o;
}

[shader("fragment")]
float4 frag_main(VSOut input) : SV_Target0
{
    return colorTex.Sample(nearestSampler, float2(0.25, 0.25));
}
""";

    /// <summary>7 资源混合 fragment shader（与 Phase10ArgumentBufferLayoutTests 一致）。</summary>
    private const string MixedShaderSource = """
struct Params { float4 tint; float lod; float3 pad; };
struct PSInput { float4 position : SV_Position; float2 uv : TEXCOORD0; };

ConstantBuffer<Params> paramsCb : register(b0);
StructuredBuffer<float4> inputA : register(t0);
StructuredBuffer<uint> inputB : register(t1);
RWStructuredBuffer<float4> outputBuffer : register(u0);
Texture2D<float4> colorTex : register(t2);
Texture2D<float4> normalTex : register(t3);
SamplerState linearSampler : register(s0);

[shader("fragment")]
float4 main(PSInput input) : SV_Target0
{
    float4 texel = colorTex.Sample(linearSampler, input.uv);
    float4 normal = normalTex.Sample(linearSampler, input.uv);
    float4 data = inputA[0] + float4(inputB[0], 0.0, 0.0, 0.0);
    outputBuffer[0] = texel + normal + data;
    return outputBuffer[0] * paramsCb.tint;
}
""";

    /// <summary>
    /// 纯纹理+采样器：encoder 字节级断言 + 端到端渲染验证。
    /// </summary>
    [Fact]
    public void Encode_TextureSampler_ByteLayoutMatchesAndRenders()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();

        var vertResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TexSampShaderSource), "Enc.vert.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Vertex, EntryPoint = "main" });
        var fragResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TexSampShaderSource), "Enc.frag.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Fragment, EntryPoint = "frag_main", GenerateReflection = true });

        var reflect = MscReflectionParser.Parse(fragResult.ReflectionJson!);
        Assert.Equal(2, reflect.TopLevelArgumentBuffer.Count);

        // 源 texture：全红
        using var srcTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, SrcTexW, SrcTexH,
            MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));
        byte[] srcPixels = new byte[SrcTexW * SrcTexH * 4];
        for (int i = 0; i < SrcTexW * SrcTexH; i++)
        {
            srcPixels[i * 4] = 255; srcPixels[i * 4 + 3] = 255;
        }
        srcTex.ReplaceRegion(0, 0, SrcTexW, SrcTexH, 0, 0, srcPixels, (ulong)(SrcTexW * 4));

        // sampler
        using var sampler = device.NewSamplerState(new WMTSamplerInfo
        {
            MinFilter = (int)MTLSamplerMinMagFilter.Nearest,
            MagFilter = (int)MTLSamplerMinMagFilter.Nearest,
            MipFilter = (int)MTLSamplerMipFilter.NotMipmapped,
            SAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1, CompareFunction = -1,
            LodMinClamp = 0f, LodMaxClamp = float.MaxValue,
        });

        // encoder：按反射顺序传入（texture@0, sampler@24）
        byte[] encoded = ArgumentBufferEncoder.Encode(reflect,
            ResourceBinding.ForTexture(srcTex),
            ResourceBinding.ForSampler(sampler));

        // === 字节级断言 ===
        Assert.Equal(48, encoded.Length);

        // texture 条目 @ offset 0：{gpuVA=0, textureViewID=gpuResourceID, metadata=0}
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(0, 8)));
        Assert.Equal(srcTex.GpuResourceID, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(8, 8)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(16, 8)));

        // sampler 条目 @ offset 24：{gpuVA=gpuResourceID, textureViewID=0, metadata=0}
        Assert.Equal(sampler.GpuResourceID, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(24, 8)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(32, 8)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(40, 8)));

        // === 端到端渲染 ===
        using var vertFn = device.NewLibrary(vertResult.MetallibData!).NewFunction("main");
        using var fragFn = device.NewLibrary(fragResult.MetallibData!).NewFunction("frag_main");
        using var pso = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .Build(device, vertFn, fragFn);

        using var rtTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.BGRA8Unorm, RtW, RtH,
            MTLTextureUsage.RenderTarget | MTLTextureUsage.ShaderRead,
            MTLResourceOptions.StorageModeShared));

        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = rtTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var enc = cmdbuf.RenderCommandEncoder(passDesc))
        {
            enc.SetRenderPipelineState(pso);
            enc.SetViewport(0, 0, RtW, RtH, 0, 1);

            // encoder 产出的字节直接绑到 buffer(2)
            enc.SetFragmentBytes(encoded, 2);
            // texture 声明驻留
            foreach (var res in ArgumentBufferEncoder.GetResourcesRequiringResidency(
                         new[] { ResourceBinding.ForTexture(srcTex), ResourceBinding.ForSampler(sampler) }))
            {
                enc.UseResource(res, MTLResourceUsage.Read, MTLRenderStages.Fragment);
            }

            enc.DrawTriangles(0, 3);
            enc.EndEncoding();
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();
        using (var err = cmdbuf.Error()) Assert.Null(err);

        // 读回 RT，断言全红
        int totalBytes = RtW * RtH * 4;
        byte[] pixels = new byte[totalBytes];
        unsafe
        {
            fixed (byte* p = pixels)
                MetalBridge.MTLTexture_getBytes(rtTex.Handle, p, (ulong)totalBytes, 0);
        }
        int redPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 2] > 200 && pixels[i] < 50 && pixels[i + 1] < 50) redPixels++;

        Assert.True(redPixels > RtW * RtH / 2, $"encoder 端到端应采样到红色，redPixels={redPixels}");
    }

    /// <summary>
    /// 7 资源混合：encoder 字节级断言（验证 CBV/UAV/SRV buffer/texture/sampler 混合布局）。
    /// </summary>
    [Fact]
    public void Encode_MixedResources_ByteLayoutMatchesReflection()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();
        var result = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(MixedShaderSource), "EncMixed.frag.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Fragment, GenerateReflection = true });

        var reflect = MscReflectionParser.Parse(result.ReflectionJson!);
        Assert.Equal(7, reflect.TopLevelArgumentBuffer.Count);

        // 创建测试资源
        using var cbBuf = device.NewBuffer(256, MTLResourceOptions.StorageModeShared);
        using var srvBuf0 = device.NewBuffer(256, MTLResourceOptions.StorageModeShared);
        using var srvBuf1 = device.NewBuffer(256, MTLResourceOptions.StorageModeShared);
        using var uavBuf = device.NewBuffer(256, MTLResourceOptions.StorageModeShared);
        using var tex0 = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, 4, 4, MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));
        using var tex1 = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, 4, 4, MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));
        using var sampler = device.NewSamplerState(new WMTSamplerInfo
        {
            MinFilter = (int)MTLSamplerMinMagFilter.Linear, MagFilter = (int)MTLSamplerMinMagFilter.Linear,
            MipFilter = (int)MTLSamplerMipFilter.NotMipmapped,
            SAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1, CompareFunction = -1,
            LodMinClamp = 0f, LodMaxClamp = float.MaxValue,
        });

        // 反射顺序（Phase10ArgumentBufferLayoutTests 已固化）：
        // 0: SRV texture slot2 (colorTex)   @ EltOffset 0
        // 1: SRV texture slot3 (normalTex)  @ EltOffset 24
        // 2: SRV buffer  slot0 (inputA)     @ EltOffset 48
        // 3: SRV buffer  slot1 (inputB)     @ EltOffset 72
        // 4: UAV buffer  slot0 (outputBuf)  @ EltOffset 96
        // 5: CBV buffer  slot0 (paramsCb)   @ EltOffset 120
        // 6: Sampler     slot0              @ EltOffset 144
        byte[] encoded = ArgumentBufferEncoder.Encode(reflect,
            ResourceBinding.ForTexture(tex0),
            ResourceBinding.ForTexture(tex1),
            ResourceBinding.ForBuffer(srvBuf0, MscResourceType.Srv),
            ResourceBinding.ForBuffer(srvBuf1, MscResourceType.Srv),
            ResourceBinding.ForBuffer(uavBuf, MscResourceType.Uav),
            ResourceBinding.ForBuffer(cbBuf, MscResourceType.Cbv),
            ResourceBinding.ForSampler(sampler));

        Assert.Equal(168, encoded.Length);

        // 逐条目断言（IRDescriptorTableEntry: +0 gpuVA, +8 textureViewID, +16 metadata）
        // texture@0: gpuVA=0, textureViewID=tex0.GpuResourceID
        Assert.Equal(0UL, ReadU64(encoded, 0));
        Assert.Equal(tex0.GpuResourceID, ReadU64(encoded, 8));
        // texture@24: gpuVA=0, textureViewID=tex1.GpuResourceID
        Assert.Equal(0UL, ReadU64(encoded, 24));
        Assert.Equal(tex1.GpuResourceID, ReadU64(encoded, 32));
        // SRV buffer@48: gpuVA=srvBuf0.GpuAddress, textureViewID=0
        Assert.Equal(srvBuf0.GpuAddress, ReadU64(encoded, 48));
        Assert.Equal(0UL, ReadU64(encoded, 56));
        // SRV buffer@72: gpuVA=srvBuf1.GpuAddress
        Assert.Equal(srvBuf1.GpuAddress, ReadU64(encoded, 72));
        // UAV buffer@96: gpuVA=uavBuf.GpuAddress
        Assert.Equal(uavBuf.GpuAddress, ReadU64(encoded, 96));
        // CBV buffer@120: gpuVA=cbBuf.GpuAddress
        Assert.Equal(cbBuf.GpuAddress, ReadU64(encoded, 120));
        // Sampler@144: gpuVA=sampler.GpuResourceID
        Assert.Equal(sampler.GpuResourceID, ReadU64(encoded, 144));
        Assert.Equal(0UL, ReadU64(encoded, 152));

        // GetResourcesRequiringResidency：6 个（2 texture + 4 buffer，sampler 不含）
        var residency = ArgumentBufferEncoder.GetResourcesRequiringResidency(new[]
        {
            ResourceBinding.ForTexture(tex0), ResourceBinding.ForTexture(tex1),
            ResourceBinding.ForBuffer(srvBuf0, MscResourceType.Srv),
            ResourceBinding.ForBuffer(srvBuf1, MscResourceType.Srv),
            ResourceBinding.ForBuffer(uavBuf, MscResourceType.Uav),
            ResourceBinding.ForBuffer(cbBuf, MscResourceType.Cbv),
            ResourceBinding.ForSampler(sampler),
        });
        Assert.Equal(6, residency.Count);
    }

    /// <summary>边界校验：绑定数与反射条目数不一致抛异常；空反射返回空数组。</summary>
    [Fact]
    public void Encode_BindingCountMismatch_Throws()
    {
        var reflection = new MscReflection
        {
            TopLevelArgumentBuffer = new() { new MscArgumentBufferEntry { EltOffset = 0, Size = 24 } }
        };
        Assert.Throws<ArgumentException>(() => ArgumentBufferEncoder.Encode(reflection));
    }

    [Fact]
    public void Encode_EmptyReflection_ReturnsEmpty()
    {
        var reflection = new MscReflection { TopLevelArgumentBuffer = new() };
        byte[] encoded = ArgumentBufferEncoder.Encode(reflection);
        Assert.Empty(encoded);
    }

    private static ulong ReadU64(byte[] buf, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(offset, 8));
}
