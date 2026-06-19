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
/// Phase 10C: ResourceTable 测试。
/// 验证按 (Type, Slot) 绑定资源、Apply 自动编码 + 绑定 + 声明驻留，
/// 走 ICommandRecorder（MetalCommandRecorder）批量回放路径。
/// </summary>
public class ResourceTableTests
{
    private const int SrcTexW = 4;
    private const int SrcTexH = 4;
    private const int RtW = 8;
    private const int RtH = 8;

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
    /// 纯纹理+采样器端到端：ResourceTable.Apply 走 MetalCommandRecorder 批量回放路径，
    /// 渲染全红 texture 采样结果，断言 RT 全红。
    /// </summary>
    [Fact]
    public void Apply_TextureSampler_ViaCommandRecorder_RendersRed()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();

        var vertResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TexSampShaderSource), "RT.vert.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Vertex, EntryPoint = "main" });
        var fragResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TexSampShaderSource), "RT.frag.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Fragment, EntryPoint = "frag_main", GenerateReflection = true });

        var fragReflection = MscReflectionParser.Parse(fragResult.ReflectionJson!);
        // 反射：texture SRV slot=0 @ offset 0, sampler slot=0 @ offset 24
        Assert.Equal(2, fragReflection.TopLevelArgumentBuffer.Count);

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

        // PSO
        using var vertFn = device.NewLibrary(vertResult.MetallibData!).NewFunction("main");
        using var fragFn = device.NewLibrary(fragResult.MetallibData!).NewFunction("frag_main");
        using var pso = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .Build(device, vertFn, fragFn);

        // 离屏 RT
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

        // ResourceTable：按 (Type, Slot) 绑定
        var table = new ResourceTable();
        table.BindTexture(slot: 0, srcTex);       // SRV texture slot=0
        table.BindSampler(slot: 0, sampler);       // Sampler slot=0

        // 走 MetalCommandRecorder（批量回放路径）
        using var recorder = new MetalCommandRecorder(device);
        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        recorder.SetPipelineState(pso);
        recorder.SetViewport(0, 0, RtW, RtH, 0, 1);
        table.Apply(recorder, fragReflection, ShaderStage.Fragment);
        recorder.Draw(0, 0, 3);  // MTLPrimitiveType.Triangle = 0
        recorder.EndRenderPass();
        recorder.EndFrame();

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

        Assert.True(redPixels > RtW * RtH / 2,
            $"ResourceTable.Apply 走批量回放应采样到红色，redPixels={redPixels}");
    }

    /// <summary>
    /// 7 资源混合：ResourceTable 按 (Type, Slot) 绑定，Apply 录制正确命令数。
    /// </summary>
    [Fact]
    public void Apply_MixedResources_RecordsExpectedCommandCount()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();
        var result = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(MixedShaderSource), "RTMixed.frag.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Fragment, GenerateReflection = true });

        var reflection = MscReflectionParser.Parse(result.ReflectionJson!);
        Assert.Equal(7, reflection.TopLevelArgumentBuffer.Count);

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
        // 0: SRV texture slot2 (colorTex)   1: SRV texture slot3 (normalTex)
        // 2: SRV buffer  slot0 (inputA)     3: SRV buffer  slot1 (inputB)
        // 4: UAV buffer  slot0 (outputBuf)  5: CBV buffer  slot0 (paramsCb)
        // 6: Sampler     slot0
        var table = new ResourceTable();
        table.BindTexture(slot: 2, tex0);
        table.BindTexture(slot: 3, tex1);
        table.BindBuffer(slot: 0, srvBuf0, MscResourceType.Srv);
        table.BindBuffer(slot: 1, srvBuf1, MscResourceType.Srv);
        table.BindBuffer(slot: 0, uavBuf, MscResourceType.Uav);
        table.BindBuffer(slot: 0, cbBuf, MscResourceType.Cbv);
        table.BindSampler(slot: 0, sampler);

        // 用 LoggingCommandRecorder 包 MetalCommandRecorder 观察命令数
        using var inner = new MetalCommandRecorder(device);
        using var rtTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.BGRA8Unorm, RtW, RtH,
            MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModeShared));
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = rtTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        inner.BeginFrame();
        inner.BeginRenderPass(passDesc);
        table.Apply(inner, reflection, ShaderStage.Fragment);
        // Apply 应录制：1 个 SetFragmentBytes + 6 个 UseResource（2 texture + 4 buffer，sampler 不含）
        Assert.Equal(1 + 6, inner.CommandCount);
        inner.EndRenderPass();
        inner.EndFrame();
    }

    /// <summary>缺资源：反射有 2 条目但只绑定 1 个 → Apply 抛 InvalidOperationException。</summary>
    [Fact]
    public void Apply_MissingBinding_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();
        var result = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TexSampShaderSource), "RTMiss.frag.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Fragment, EntryPoint = "frag_main", GenerateReflection = true });
        var reflection = MscReflectionParser.Parse(result.ReflectionJson!);

        using var srcTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, 4, 4, MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));

        var table = new ResourceTable();
        table.BindTexture(slot: 0, srcTex);  // 只绑 texture，缺 sampler

        using var recorder = new MetalCommandRecorder(device);
        using var rtTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.BGRA8Unorm, RtW, RtH, MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModeShared));
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = rtTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        Assert.Throws<InvalidOperationException>(() =>
            table.Apply(recorder, reflection, ShaderStage.Fragment));
        recorder.EndRenderPass();
        recorder.EndFrame();
    }

    /// <summary>Span 版 SetFragmentBytes 透传验证：用 LoggingCommandRecorder 包装，确认不报错。</summary>
    [Fact]
    public void SpanOverload_PassesThroughLoggingRecorder()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var inner = new MetalCommandRecorder(device);
        using var logger = new LoggingCommandRecorder(inner, Console.Out);

        using var rtTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.BGRA8Unorm, RtW, RtH, MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModeShared));
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = rtTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        byte[] data = new byte[48];  // 2 个 24B 描述符
        logger.BeginFrame();
        logger.BeginRenderPass(passDesc);
        // Span 重载应正常透传，不抛 NotImplementedException 等
        logger.SetFragmentBytes(data, 2);
        logger.SetVertexBytes(data, 2);
        logger.EndRenderPass();
        logger.EndFrame();
        // 若执行到这里无异常，透传验证通过
    }
}
