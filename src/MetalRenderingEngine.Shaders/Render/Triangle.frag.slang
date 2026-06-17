// Triangle.frag.slang — 输出插值后的顶点色

struct PSInput {
    float4 position : SV_Position;
    float3 color    : COLOR;
};

[shader("fragment")]
float4 main(PSInput input) : SV_Target
{
    return float4(input.color, 1.0);
}
