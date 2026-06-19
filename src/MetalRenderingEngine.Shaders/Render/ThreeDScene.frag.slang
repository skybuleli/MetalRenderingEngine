// ThreeDScene.frag.slang — Phase 7J/10A: Blinn-Phong 光照 + MRT 双输出
// MRT0: 最终 lit color
// MRT1: 轻量 G-buffer（normal.xy 编码 + depth01 + roughness）

struct PerFrame {
    float4x4 viewProj;
    float4   lightDir;
    float4   camPos;
};

struct PSInput {
    float4 position : SV_Position;
    float3 worldPos : TEXCOORD0;
    float3 normal   : TEXCOORD1;
};

struct PSOutput {
    float4 color   : SV_Target0;
    float4 gbuffer : SV_Target1;
};

[shader("fragment")]
PSOutput main(PSInput input, StructuredBuffer<PerFrame> perFrame)
{
    float3 N = normalize(input.normal);
    float3 L = normalize(perFrame[0].lightDir.xyz);
    float3 V = normalize(perFrame[0].camPos.xyz - input.worldPos);
    float3 H = normalize(L + V);

    // Blinn-Phong
    float diff = max(dot(N, L), 0.0);
    float spec = pow(max(dot(N, H), 0.0), 32.0);
    float ambient = 0.2;

    float3 baseColor = float3(0.8, 0.8, 0.85);
    float roughness = 0.35;
    float3 lit = baseColor * (ambient + diff) + float3(1.0, 1.0, 1.0) * spec * 0.5;
    float depth01 = saturate(length(perFrame[0].camPos.xyz - input.worldPos) / 50.0);
    float2 encodedNormalXY = N.xy * 0.5 + 0.5;

    PSOutput out;
    out.color = float4(lit, 1.0);
    out.gbuffer = float4(encodedNormalXY, depth01, roughness);
    return out;
}
