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
///
/// 已确证结论：MSC 产物虽有 reflect.json 条目，但所有合法 buffer index 上
/// <c>newArgumentEncoderWithBufferIndex()</c> 都返回 encodedLength==0。
/// 这不是 blocker，而是 MSC 4.0 的固有行为——其 top-level argument buffer
/// 是自定义描述符堆 struct（非 Metal 原生 argument buffer），故 MTLArgumentEncoder
/// 这条路对 MSC 产物是死路。正确做法是手写描述符（见
/// <c>Phase10ArgEncoderIndexDiagTests</c> 与 <c>docs/argument-buffer-layout.md</c>）。
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
    /// 固化已确证结论：MSC 产物的 reflect.json 虽含 texture/sampler 条目，
    /// 但 <c>newArgumentEncoderWithBufferIndex(0)</c> 返回的 encodedLength 仍为 0。
    /// MSC 4.0 的 top-level argument buffer 是自定义描述符堆 struct，
    /// 非 Metal 原生 argument buffer，故 MTLArgumentEncoder 路径不可用。
    /// texture/sampler 须改走手写描述符路径（见 docs/argument-buffer-layout.md）。
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
