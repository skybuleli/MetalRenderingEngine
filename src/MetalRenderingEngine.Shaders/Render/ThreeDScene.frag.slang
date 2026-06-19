// ThreeDScene.frag.slang — Phase 7J: Blinn-Phong 光照（单 color attachment）
// PerFrame 用 StructuredBuffer（与 vertex 一致，复用已验证的 SRV 绑定路径）。
// MRT 输出推迟到 Phase 10A PoC 验证后（MSC 多 target 输出未验证）。

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

[shader("fragment")]
float4 main(PSInput input, StructuredBuffer<PerFrame> perFrame) : SV_Target
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
    float3 lit = baseColor * (ambient + diff) + float3(1.0, 1.0, 1.0) * spec * 0.5;

    return float4(lit, 1.0);
}
