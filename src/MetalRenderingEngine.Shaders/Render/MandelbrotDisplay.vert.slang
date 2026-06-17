// MandelbrotDisplay.vert.slang — 全屏四边形顶点着色器
// 仅用 SV_VertexID 生成覆盖整个 NDC 的 2 个三角形（共 3 个顶点）。
// 不需要顶点 buffer；MVP 为单位阵（NDC 直接输出）。

struct VSOutput {
    float4 position : SV_Position;
    float2 uv       : TEXCOORD;
};

[shader("vertex")]
VSOutput main(uint vid : SV_VertexID)
{
    VSOutput out;

    // 大三角形覆盖技巧：3 个顶点即覆盖整个 [-1,1]² NDC。
    // vid 0 → (-1,-1)  左下
    // vid 1 → ( 3,-1)  远右下（裁剪掉一半，覆盖到 x=+1）
    // vid 2 → (-1, 3)  远左上（裁剪掉一半，覆盖到 y=+1）
    float2 pos[3] = {
        float2(-1.0, -1.0),
        float2( 3.0, -1.0),
        float2(-1.0,  3.0),
    };
    // 对应 uv：左下→(0,0)，覆盖到右上 (2,2)；屏外部分被裁剪。
    float2 uv[3] = {
        float2(0.0, 0.0),
        float2(2.0, 0.0),
        float2(0.0, 2.0),
    };

    out.position = float4(pos[vid], 0.0, 1.0);
    out.uv = uv[vid];
    return out;
}
