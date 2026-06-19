using System.Text;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 10 根因回归守护：MSC 4.0 产物在所有合法 buffer index 上的
/// <c>newArgumentEncoderWithBufferIndex</c> 都返回 encodedLength==0。
///
/// 这固化了一个确证结论：MSC 的 top-level argument buffer 不是 Metal 原生
/// argument buffer 结构（而是 MSC 自定义描述符堆 struct，经普通 [[buffer(2)]] 传入），
/// 因此 <c>MTLArgumentEncoder</c> 这条路对 MSC 产物是死路，无论用哪个 buffer index。
///
/// 详见 <c>docs/argument-buffer-layout.md</c> “根因确证”一节。
/// 若未来此测试开始失败（某 index 返回非零），说明 MSC 行为变化，需重新评估绑定策略。
/// </summary>
public class Phase10ArgEncoderIndexDiagTests
{
    private const string TextureSamplerShaderSource = """
struct VSOut
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

Texture2D<float4> colorTex : register(t0);
SamplerState nearestSampler : register(s0);

[shader("vertex")]
VSOut main(uint vid : SV_VertexID)
{
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

    [Fact]
    public void MscMetallib_AllValidBufferIndices_EncodedLengthIsZero()
    {
        var compiler = new SlangCompiler();

        var fragResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TextureSamplerShaderSource),
            "Phase10Diag.frag.slang",
            new ShaderCompileOptions
            {
                Stage = ShaderStage.Fragment,
                EntryPoint = "frag_main",
                GenerateReflection = true,
            });

        // 反射确认 texture+sampler 两个顶层条目存在
        var fragReflection = MscReflectionParser.Parse(fragResult.ReflectionJson!);
        Assert.Equal(2, fragReflection.ResourceCount);
        Assert.Equal(2, fragReflection.TopLevelArgumentBuffer.Count);

        using var device = MetalDevice.CreateSystemDefault();
        using var fragFn = device.NewLibrary(fragResult.MetallibData!).NewFunction("frag_main");

        // 遍历 0..2（MSC 4.0 top_level_global_ab 在 buffer(2)；buffer(0)/buffer(1)
        // 留给 push constants / draw args。index >= 3 会触发 ObjC assert 终止进程，不能测）。
        for (ulong i = 0; i <= 2; i++)
        {
            using var enc = fragFn.NewArgumentEncoder(i);
            Assert.Equal(0UL, enc.EncodedLength);
        }
    }
}
