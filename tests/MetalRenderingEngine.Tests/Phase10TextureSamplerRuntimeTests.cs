using System.Runtime.InteropServices;
using System.Text;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 10A: texture + sampler 运行时 PoC。
/// 当前先锁定一个真实 blocker：MSC 产物虽然有 reflect.json 条目，
/// 但 newArgumentEncoderWithBufferIndex() 暂时拿不到可用 encodedLength。
/// </summary>
public class Phase10TextureSamplerRuntimeTests
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
    // 固定采样左上 texel，避免因插值和过滤把断言搞复杂。
    return colorTex.Sample(nearestSampler, float2(0.25, 0.25));
}
""";

    /// <summary>
    /// 当前 MSC 产物的 reflect.json 明确存在 texture/sampler 两个顶层条目，
    /// 但从 metallib 取回的 MTLArgumentEncoder encodedLength 仍为 0。
    /// 这说明 Phase 10 下一步不是“直接写 encoder helper”，
    /// 而是先搞清楚 MSC metallib 与 newArgumentEncoderWithBufferIndex 的真实兼容边界。
    /// </summary>
    [Fact]
    public void TextureAndSampler_MscMetallib_ArgumentEncoderMetadataIsStillMissing()
    {
        var compiler = new SlangCompiler();

        var vertResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TextureSamplerShaderSource),
            "Phase10TextureSampler.vert.slang",
            new ShaderCompileOptions
            {
                Stage = ShaderStage.Vertex,
                GenerateReflection = true,
            });

        var fragResult = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(TextureSamplerShaderSource),
            "Phase10TextureSampler.frag.slang",
            new ShaderCompileOptions
            {
                Stage = ShaderStage.Fragment,
                EntryPoint = "frag_main",
                GenerateReflection = true,
            });

        var fragReflection = MscReflectionParser.Parse(fragResult.ReflectionJson!);
        Assert.Equal(2, fragReflection.ResourceCount);
        Assert.Equal([1], fragReflection.SamplerIndices);
        Assert.Equal([0], fragReflection.ShaderResourceViewIndices);
        Assert.Equal(2, fragReflection.TopLevelArgumentBuffer.Count);

        using var device = MetalDevice.CreateSystemDefault();
        using var vertFn = device.NewLibrary(vertResult.MetallibData!).NewFunction("main");
        using var fragFn = device.NewLibrary(fragResult.MetallibData!).NewFunction("frag_main");
        using var argEncoder = fragFn.NewArgumentEncoder(0);

        Assert.Equal(0UL, argEncoder.EncodedLength);
    }
}
