// TexturedCube.frag.slang — Phase 10 Demo: 真 Texture2D.Sample + 方向光照
// texture/sampler 走 Phase 10A 描述符堆路径（IRDescriptorTableEntry），
// 由 Phase 10C ResourceTable + 10B ArgumentBufferEncoder 自动编码绑定。

struct VSOut {
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
    float3 normal   : TEXCOORD1;
};

Texture2D<float4> colorTex : register(t0);
SamplerState linearSampler : register(s0);

[shader("fragment")]
float4 main(VSOut input) : SV_Target0
{
    float4 texel = colorTex.Sample(linearSampler, input.uv);
    // 简单 Lambert 方向光照
    float3 n = normalize(input.normal);
    float3 lightDir = normalize(float3(0.5, 0.8, 0.3));
    float diff = max(dot(n, lightDir), 0.0);
    float ambient = 0.3;
    return texel * (ambient + diff * 0.7);
}
