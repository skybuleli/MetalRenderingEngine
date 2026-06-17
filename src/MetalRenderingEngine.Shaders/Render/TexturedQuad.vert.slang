// TexturedQuad.vert.slang — 四边形顶点着色器（MVP + UV）
// 6 个硬编码顶点构成 2 个三角形 = 1 个四边形

struct PerFrameCB {
    float4x4 mvp;
    float    time;
};

struct VSOutput {
    float4 position : SV_Position;
    float2 uv       : TEXCOORD;
};

[shader("vertex")]
VSOutput main(uint vid : SV_VertexID, ConstantBuffer<PerFrameCB> cb)
{
    VSOutput out;

    // 四边形：2 个三角形（6 个顶点）
    float3 pos[6] = {
        float3(-0.5, -0.5, 0.0),  // 左下
        float3( 0.5, -0.5, 0.0),  // 右下
        float3(-0.5,  0.5, 0.0),  // 左上
        float3(-0.5,  0.5, 0.0),  // 左上
        float3( 0.5, -0.5, 0.0),  // 右下
        float3( 0.5,  0.5, 0.0),  // 右上
    };

    float2 uvs[6] = {
        float2(0.0, 1.0),  // 左下
        float2(1.0, 1.0),  // 右下
        float2(0.0, 0.0),  // 左上
        float2(0.0, 0.0),  // 左上
        float2(1.0, 1.0),  // 右下
        float2(1.0, 0.0),  // 右上
    };

    out.position = mul(cb.mvp, float4(pos[vid], 1.0));
    out.uv = uvs[vid];
    return out;
}
