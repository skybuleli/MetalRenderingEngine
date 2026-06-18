// InstancedTriangle.frag.slang — 实例化三角形片元着色器：直接输出顶点颜色

struct PSInput {
    float4 position : SV_Position;
    float4 color     : TEXCOORD;
};

[shader("fragment")]
float4 main(PSInput input) : SV_Target
{
    return input.color;
}
