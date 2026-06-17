// Triangle.vert.slang — 硬编码全屏三角形
// 用 SV_VertexID 生成 3 个顶点，覆盖整个 clip space

struct VSOutput {
    float4 position : SV_Position;
    float3 color    : COLOR;
};

[shader("vertex")]
VSOutput main(uint vid : SV_VertexID)
{
    VSOutput out;
    // 居中三角形：清晰可见，不铺满全屏
    float2 pos[3] = {
        float2(-0.5, -0.5),
        float2( 0.5, -0.5),
        float2( 0.0,  0.5)
    };
    float3 col[3] = {
        float3(1.0, 0.0, 0.0),  // 红
        float3(0.0, 1.0, 0.0),  // 绿
        float3(0.0, 0.0, 1.0),  // 蓝
    };
    out.position = float4(pos[vid], 0.0, 1.0);
    out.color    = col[vid];
    return out;
}
