using System.Runtime.InteropServices;
using System.Text;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 10A 决策门验证：texture/sampler 描述符堆路径端到端可用性。
///
/// 路径：手写 MSC 描述符堆条目（TextureDescriptor + SamplerDescriptor，各 24 字节）
///   → 按 reflection 的 TopLevelArgumentBuffer 顺序拼成 struct
///   → setFragmentBytes(buffer(2))（MSC 4.0 top_level_global_ab 固定位置）
///   → UseResource(texture, Read, Fragment)（texture 必须声明驻留）
///   → 渲染全屏三角形，fragment 固定采样源 texture
///   → getBytes 读回 render target，断言采样颜色正确。
///
/// 前置条件（本测试不覆盖，由其它测试守护）：
/// - MTLArgumentEncoder 对 MSC 产物是死路（Phase10ArgEncoderIndexDiagTests 守护）
/// - 混合资源顶层布局 24B 步长（Phase10ArgumentBufferLayoutTests 守护）
///
/// 详见 docs/argument-buffer-layout.md。
/// </summary>
public class Phase10TextureSamplerDescriptorTests
{
    private const int SrcTexW = 4;
    private const int SrcTexH = 4;
    private const int RtW = 8;
    private const int RtH = 8;

    /// <summary>
    /// fragment shader：固定采样源 texture 的 UV(0.25, 0.25)。
    /// 4×4 纹理全涂红，故无论 Nearest 采样到哪个 texel 都返回红色。
    /// </summary>
    private const string ShaderSource = """
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
    // 覆盖整个 NDC 的全屏三角形
    static const float2 positions[3] = {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };

    static const float2 uvs[3] = {
        float2(0.0, 0.0),
        float2(0.0, 2.0),
        float2(2.0, 0.0)
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

    /// <summary>
    /// 描述符堆结构体：按 reflection TopLevelArgumentBuffer 的 EltOffset 顺序排列。
    /// 纯 Texture2D + SamplerState fragment shader 反射结果：
    ///   index 0 = SRV (texture) @ EltOffset 0
    ///   index 1 = Sampler         @ EltOffset 24
    /// （由本测试的反射守护段断言固化）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TexSampArgBuffer
    {
        public TextureDescriptor Tex;   // EltOffset 0
        public SamplerDescriptor Samp;  // EltOffset 24
    }

    [Fact]
    public void TextureSamplerDescriptorHeap_RendersSampledColor()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();

        // 1. 编译 vertex + fragment（fragment 生成反射）
        var vertResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(ShaderSource), "Phase10Desc.vert.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Vertex, EntryPoint = "main" });
        var fragResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(ShaderSource), "Phase10Desc.frag.slang",
            new ShaderCompileOptions
            {
                Stage = ShaderStage.Fragment,
                EntryPoint = "frag_main",
                GenerateReflection = true,
            });

        Assert.NotNull(vertResult.MetallibData);
        Assert.NotNull(fragResult.MetallibData);
        Assert.NotNull(fragResult.ReflectionJson);

        // 2. 反射守护：固化 texture@0 / sampler@24 顺序与 24B 步长
        var reflect = MscReflectionParser.Parse(fragResult.ReflectionJson!);
        Assert.Equal(2, reflect.ResourceCount);
        Assert.Equal(2, reflect.TopLevelArgumentBuffer.Count);
        Assert.Equal(new[] { 0 }, reflect.ShaderResourceViewIndices);
        Assert.Equal(new[] { 1 }, reflect.SamplerIndices);

        var texEntry = reflect.TopLevelArgumentBuffer[0];
        var sampEntry = reflect.TopLevelArgumentBuffer[1];
        Assert.Equal("SRV", texEntry.Type);
        Assert.Equal(0, texEntry.EltOffset);
        Assert.Equal(24, texEntry.Size);
        Assert.Equal("Sampler", sampEntry.Type);
        Assert.Equal(24, sampEntry.EltOffset);
        Assert.Equal(24, sampEntry.Size);

        // 3. 创建函数 + PSO
        using var vertFn = device.NewLibrary(vertResult.MetallibData!).NewFunction("main");
        using var fragFn = device.NewLibrary(fragResult.MetallibData!).NewFunction("frag_main");
        using var pso = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .Build(device, vertFn, fragFn);

        // 4. 源 texture：4×4 RGBA8Unorm，全部 texel 涂红。
        //    全涂红消除 UV→texel 映射歧义（4×4 纹理 UV(0.25,0.25) Nearest 采样到 texel(1,1)，
        //    若只涂 texel(0,0) 会采到黑色）。RGBA8Unorm 内存布局 (R,G,B,A)。
        using var srcTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, SrcTexW, SrcTexH,
            MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));

        byte[] srcPixels = new byte[SrcTexW * SrcTexH * 4];
        for (int i = 0; i < SrcTexW * SrcTexH; i++)
        {
            srcPixels[i * 4 + 0] = 255;   // R
            srcPixels[i * 4 + 1] = 0;     // G
            srcPixels[i * 4 + 2] = 0;     // B
            srcPixels[i * 4 + 3] = 255;   // A
        }
        srcTex.ReplaceRegion(0, 0, SrcTexW, SrcTexH, 0, 0, srcPixels, (ulong)(SrcTexW * 4));

        // 5. sampler：Nearest + ClampToEdge，无 lod bias
        var samplerInfo = new WMTSamplerInfo
        {
            MinFilter = (int)MTLSamplerMinMagFilter.Nearest,
            MagFilter = (int)MTLSamplerMinMagFilter.Nearest,
            MipFilter = (int)MTLSamplerMipFilter.NotMipmapped,
            SAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1,
            CompareFunction = -1,  // -1 = disabled（bridge.m 仅在 >= 0 时设置）
            LodMinClamp = 0f,
            LodMaxClamp = float.MaxValue,
        };
        using var sampler = device.NewSamplerState(samplerInfo);

        // R1 守护：supportArgumentBuffers 必须生效，否则描述符堆路径不可用
        Assert.NotEqual(0UL, sampler.GpuResourceID);
        // texture 的 gpuResourceID 不需要 supportArgumentBuffers，但需 ShaderRead（已设）
        Assert.NotEqual(0UL, srcTex.GpuResourceID);

        // 6. 离屏 render target：BGRA8Unorm
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
            ClearColor = new WMTClearColor(0, 0, 0, 1),  // 黑色背景
        });

        // 7. 编码：描述符堆路径
        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var enc = cmdbuf.RenderCommandEncoder(passDesc))
        {
            enc.SetRenderPipelineState(pso);
            enc.SetViewport(0, 0, RtW, RtH, 0, 1);

            // MSC 4.0 绑定模型（来自 metal_irconverter_runtime.h 反射探测）：
            //   buffer(2) = top_level_global_ab（active=1，shader 直接读）
            //   buffer(0) = res_desc_heap_ab（active=0）
            //   buffer(1) = smp_desc_heap_ab（active=0）
            // active=0 表明 res/smp desc heap 非直接读——对于无 root constants 的简单 shader，
            // 描述符条目直接平铺在 top_level_global_ab(buffer2)。
            // 反射 EltOffset: texture@0, sampler@24，与 IRDescriptorTableEntry(24B) 步长一致。
            var argBuf = new TexSampArgBuffer
            {
                Tex = srcTex.ToTextureDescriptor(),     // +0: gpuVA=0, +8: gpuResourceID, +16: 0
                Samp = sampler.ToSamplerDescriptor(),   // +0: gpuResourceID, +8: 0, +16: 0
            };
            enc.SetFragmentBytes(in argBuf, 2);

            // texture 必须声明驻留（sampler 无需 UseResource，MSC/DXMT 均不调）
            enc.UseResource(srcTex, MTLResourceUsage.Read, MTLRenderStages.Fragment);

            enc.DrawTriangles(0, 3);
            enc.EndEncoding();
        }

        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        // R3 守护：Metal validation 不应报错
        using (var err = cmdbuf.Error()) Assert.Null(err);
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);

        // 8. 读回 render target 像素
        int totalBytes = RtW * RtH * 4;
        byte[] pixels = new byte[totalBytes];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(rtTex.Handle, p, (ulong)totalBytes, 0);
                Assert.Equal((ulong)totalBytes, written);
            }
        }

        // 9. 断言：全屏三角形覆盖整个 RT，fragment 固定采样全红源 texture，
        //    所以整个 RT 应被涂成红色。BGRA8 内存布局 (B,G,R,A)。
        //    红色 = (B=0, G=0, R=255, A=255)。
        int redPixels = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            if (r > 200 && g < 50 && b < 50 && a > 200) redPixels++;
        }

        // R2 守护：描述符堆路径必须真正采样到颜色（核心 PoC 断言）
        Assert.True(redPixels > 0,
            $"描述符堆路径应采样到红色像素，实际 redPixels={redPixels}。" +
            $"采样像素全黑说明描述符堆布局/UseResource/buffer(2) 索引有误。");
        // 全屏三角形 + 全红源 texture → 整个 RT 应为红色
        Assert.True(redPixels > RtW * RtH / 2,
            $"全屏三角形采样全红 texture 应涂红大部分 RT，实际 redPixels={redPixels}/{RtW * RtH}。");
    }
}
