// InstancedTriangle.vert.slang — 多三角形顶点着色器（按 vid 定位）
// 1000 个三角形 × 3 顶点 = 3000 顶点，vertex shader 纯按 vid 定位。
// 用于 Phase 6 batched encoder 对比：逐次 draw（1000 次）vs 批量 draw（1 次回放）。
// 三角形按 32×32 网格排列，每个不同颜色。

struct VSOutput {
    float4 position : SV_Position;
    float4 color     : TEXCOORD;
};

[shader("vertex")]
VSOutput main(uint vid : SV_VertexID)
{
    VSOutput out;

    // 单个三角形的 3 个顶点（小三角形）
    float2 tri[3] = {
        float2( 0.00,  0.03),
        float2(-0.025, -0.02),
        float2( 0.025, -0.02),
    };

    // vid / 3 = 三角形 id，vid % 3 = 三角形内顶点
    uint triId = vid / 3;
    uint localVid = vid % 3;

    // 32 列 × 32 行网格（覆盖 1024 个三角形）
    float col = float(triId % 32);
    float row = float(triId / 32);
    float2 offset = float2(
        (col / 31.0) * 1.9 - 0.95,
        (row / 31.0) * 1.9 - 0.95
    );

    float2 pos = tri[localVid] + offset;
    out.position = float4(pos, 0.0, 1.0);

    // 按三角形 id 生成彩虹色
    float t = float(triId) / 999.0;
    out.color = float4(
        0.5 + 0.5 * sin(t * 6.28),
        0.5 + 0.5 * sin(t * 6.28 + 2.09),
        0.5 + 0.5 * sin(t * 6.28 + 4.18),
        1.0
    );
    return out;
}
