// MultiTextureCube.frag.slang — Phase 10E Demo: 双纹理混合 + Lambert 光照
// 两张 Texture2D（AlbedoMap 基色 + DetailMap 细节）混合采样，体现多资源描述符堆绑定。
// texture/sampler 走 Phase 10A 描述符堆路径，由 Phase 10E ShaderBindingLayout 封装 slot。

struct VSOut {
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
    float3 normal   : TEXCOORD1;
};

Texture2D<float4> albedoMap : register(t0);
Texture2D<float4> detailMap : register(t1);
SamplerState linearSampler : register(s0);

[shader("fragment")]
float4 main(VSOut input) : SV_Target0
{
    float4 albedo = albedoMap.Sample(linearSampler, input.uv);
    float4 detail = detailMap.Sample(linearSampler, input.uv);
    // 混合：基色 70% + 细节 30%
    float4 color = albedo * 0.7 + detail * 0.3;
    // Lambert 方向光照
    float3 n = normalize(input.normal);
    float3 lightDir = normalize(float3(0.5, 0.8, 0.3));
    float diff = max(dot(n, lightDir), 0.0);
    float ambient = 0.3;
    return color * (ambient + diff * 0.7);
}
