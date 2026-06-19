using System.Text;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 10A: 多资源 argument buffer 布局 PoC。
/// 先锁定 MSC 对混合资源（CBV/SRV/UAV/Texture/Sampler）的 reflect.json 排布，
/// 后续再据此实现 ArgumentBufferEncoder / ResourceTable。
/// </summary>
public class Phase10ArgumentBufferLayoutTests
{
    private const string MixedFragmentShaderSource = """
struct Params
{
    float4 tint;
    float lod;
    float3 pad;
};

struct PSInput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

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
    /// 实测结论：
    /// 1. 混合资源在 MSC 顶层 argument buffer 中统一是 24 字节条目；
    /// 2. 条目顺序不是源码声明顺序，fragment 下 texture SRV 会排在结构化 SRV 之前；
    /// 3. sampler / CBV 也进入同一个 top-level argument buffer。
    /// </summary>
    [Fact]
    public void MixedResources_FragmentReflection_RevealsStableTopLevelLayout()
    {
        var compiler = new SlangCompiler();
        var options = new ShaderCompileOptions
        {
            Stage = ShaderStage.Fragment,
            GenerateReflection = true,
        };

        var result = compiler.CompileFromSource(
            Encoding.UTF8.GetBytes(MixedFragmentShaderSource),
            "Phase10MixedResources.frag.slang",
            options);

        Assert.NotNull(result.ReflectionJson);

        var reflection = MscReflectionParser.Parse(result.ReflectionJson!);
        Assert.Equal("Fragment", reflection.ShaderType);
        Assert.Equal(7, reflection.ResourceCount);
        Assert.Equal(7, reflection.TopLevelArgumentBuffer.Count);

        Assert.Equal([5], reflection.ConstantBufferIndices);
        Assert.Equal([2, 3, 0, 1], reflection.ShaderResourceViewIndices);
        Assert.Equal([4], reflection.UnorderedAccessViewIndices);
        Assert.Equal([6], reflection.SamplerIndices);

        var entries = reflection.TopLevelArgumentBuffer;
        Assert.Collection(entries,
            e =>
            {
                Assert.Equal(MscResourceType.Srv, e.ResourceType);
                Assert.Equal(0, e.EltOffset);
                Assert.Equal(24, e.Size);
                Assert.Equal(2, e.Slot);
            },
            e =>
            {
                Assert.Equal(MscResourceType.Srv, e.ResourceType);
                Assert.Equal(24, e.EltOffset);
                Assert.Equal(24, e.Size);
                Assert.Equal(3, e.Slot);
            },
            e =>
            {
                Assert.Equal(MscResourceType.Srv, e.ResourceType);
                Assert.Equal(48, e.EltOffset);
                Assert.Equal(24, e.Size);
                Assert.Equal(0, e.Slot);
            },
            e =>
            {
                Assert.Equal(MscResourceType.Srv, e.ResourceType);
                Assert.Equal(72, e.EltOffset);
                Assert.Equal(24, e.Size);
                Assert.Equal(1, e.Slot);
            },
            e =>
            {
                Assert.Equal(MscResourceType.Uav, e.ResourceType);
                Assert.Equal(96, e.EltOffset);
                Assert.Equal(24, e.Size);
                Assert.Equal(0, e.Slot);
            },
            e =>
            {
                Assert.Equal(MscResourceType.Cbv, e.ResourceType);
                Assert.Equal(120, e.EltOffset);
                Assert.Equal(24, e.Size);
                Assert.Equal(0, e.Slot);
            },
            e =>
            {
                Assert.Equal(MscResourceType.Sampler, e.ResourceType);
                Assert.Equal(144, e.EltOffset);
                Assert.Equal(24, e.Size);
                Assert.Equal(0, e.Slot);
            });
    }
}
