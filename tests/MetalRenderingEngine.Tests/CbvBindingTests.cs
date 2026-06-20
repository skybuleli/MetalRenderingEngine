using System.Runtime.InteropServices;
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
/// CBV（ConstantBuffer&lt;T&gt;）绑定端到端验证。
///
/// 验证 <c>ConstantBuffer&lt;T&gt; : register(bN)</c> 经 MSC 反射为 CBV 类型后，
/// 通过 ResourceTable.BindBuffer(slot, buf, Cbv) + Apply 绑定到 buffer(2)，
/// fragment shader 能正确读取 CBV 数据。
///
/// 现有 ArgumentBufferEncoder 已有 CBV 分支（{gpuAddress, 0, 0}，对齐
/// IRDescriptorTableSetBuffer），但此前从未端到端渲染验证。本测试补此缺口。
/// </summary>
public class CbvBindingTests
{
    private const int RtW = 8;
    private const int RtH = 8;

    /// <summary>CBV 数据布局：与 shader 的 Tint struct 对齐（float4 color = 16 字节）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TintCb { public System.Numerics.Vector4 Color; }

    /// <summary>
    /// shader：fragment 用 ConstantBuffer&lt;Tint&gt; 输出固定颜色。
    /// vert 全屏三角形，frag 直接返回 CBV 里的 color。
    /// </summary>
    private const string ShaderSource = """
struct Tint
{
    float4 color;
};

struct VSOut
{
    float4 position : SV_Position;
};

ConstantBuffer<Tint> tintCb : register(b0);

[shader("vertex")]
VSOut main(uint vid : SV_VertexID)
{
    static const float2 positions[3] = {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };
    VSOut o;
    o.position = float4(positions[vid], 0.0, 1.0);
    return o;
}

[shader("fragment")]
float4 frag_main(VSOut input) : SV_Target0
{
    return tintCb.color;
}
""";

    /// <summary>
    /// CBV 端到端：CBV buffer 装已知颜色 → ResourceTable.BindBuffer(Cbv) →
    /// Apply → 渲染全屏三角形 → 断言 RT 像素 = CBV 颜色。
    /// </summary>
    [Fact]
    public void CbvBinding_FragmentReadsConstantBufferColor()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SlangCompiler();

        var vertResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(ShaderSource), "CbvTest.vert.slang",
            new ShaderCompileOptions { Stage = ShaderStage.Vertex, EntryPoint = "main" });
        var fragResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(ShaderSource), "CbvTest.frag.slang",
            new ShaderCompileOptions
            {
                Stage = ShaderStage.Fragment,
                EntryPoint = "frag_main",
                GenerateReflection = true,
            });

        Assert.NotNull(fragResult.MetallibData);
        Assert.NotNull(fragResult.ReflectionJson);

        // 1. 反射守护：frag 1 个 CBV slot=0 @ offset 0
        var reflection = MscReflectionParser.Parse(fragResult.ReflectionJson!);
        Assert.Equal(1, reflection.ResourceCount);
        Assert.Single(reflection.TopLevelArgumentBuffer);
        Assert.Equal(new[] { 0 }, reflection.ConstantBufferIndices);
        var cbvEntry = reflection.TopLevelArgumentBuffer[0];
        Assert.Equal(MscResourceType.Cbv, cbvEntry.ResourceType);
        Assert.Equal(0, cbvEntry.Slot);
        Assert.Equal(0, cbvEntry.EltOffset);
        Assert.Equal(24, cbvEntry.Size);

        // 2. PSO
        using var vertFn = device.NewLibrary(vertResult.MetallibData!).NewFunction("main");
        using var fragFn = device.NewLibrary(fragResult.MetallibData!).NewFunction("frag_main");
        using var pso = new PipelineBuilder()
            .WithColorAttachment(0, MTLPixelFormat.BGRA8Unorm)
            .WithSampleCount(1)
            .Build(device, vertFn, fragFn);

        // 3. CBV buffer：写入已知颜色（绿色，归一化 RGBA）
        //    shader 的 float4 color 对应 C# 的 Vector4（16 字节）
        // 选一个非平凡颜色：R=0.2, G=0.8, B=0.3, A=1.0
        var tint = new TintCb { Color = new(0.2f, 0.8f, 0.3f, 1.0f) };
        using var cbBuf = device.NewBuffer((ulong)Marshal.SizeOf<TintCb>(), MTLResourceOptions.StorageModeShared);
        cbBuf.AsSpan<TintCb>()[0] = tint;

        // 4. 离屏 RT
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

        // 5. ResourceTable：按 (Cbv, slot=0) 绑定 CBV buffer
        var fragTable = new ResourceTable();
        fragTable.BindBuffer(slot: 0, cbBuf, MscResourceType.Cbv);

        // 6. 渲染：走 MetalCommandRecorder 批量回放路径
        using var recorder = new MetalCommandRecorder(device);
        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        recorder.SetPipelineState(pso);
        recorder.SetViewport(0, 0, RtW, RtH, 0, 1);
        // ResourceTable.Apply：CBV → ArgumentBufferEncoder 编码 {gpuAddress, 0, 0} →
        // SetFragmentBytes(buffer(2)) + UseResource(cbBuf, Read, Vertex|Fragment)
        fragTable.Apply(recorder, reflection, ShaderStage.Fragment);
        recorder.Draw(0, 0, 3);
        recorder.EndRenderPass();
        recorder.EndFrame();

        // 7. 读回 RT 像素
        int totalBytes = RtW * RtH * 4;
        byte[] pixels = new byte[totalBytes];
        unsafe
        {
            fixed (byte* p = pixels)
                MetalBridge.MTLTexture_getBytes(rtTex.Handle, p, (ulong)totalBytes, 0);
        }

        // 8. 断言：全屏三角形覆盖整个 RT，RT 应被涂成 CBV 里的颜色。
        //    BGRA8 内存布局 (B,G,R,A)。
        //    期望：R=0.2→51, G=0.8→204, B=0.3→77, A=1.0→255
        byte expR = (byte)Math.Round(tint.Color.X * 255);
        byte expG = (byte)Math.Round(tint.Color.Y * 255);
        byte expB = (byte)Math.Round(tint.Color.Z * 255);

        int matched = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
            // 允许 ±2 的误差（float→uint8 量化 + 插值）
            if (Math.Abs(r - expR) <= 2 && Math.Abs(g - expG) <= 2 &&
                Math.Abs(b - expB) <= 2 && a > 250)
                matched++;
        }

        Assert.True(matched > RtW * RtH / 2,
            $"CBV 颜色应传到 fragment 并涂满 RT。期望 BGRA=({expB},{expG},{expR},255)，" +
            $"匹配像素 {matched}/{RtW * RtH}。" +
            $"首像素 BGRA=({pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]})。");
    }
}
