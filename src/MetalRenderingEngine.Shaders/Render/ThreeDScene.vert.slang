// ThreeDScene.vert.slang — Phase 7J: 100 instanced cubes with depth + MRT
// 使用 SV_VertexID 生成单位立方体（36 顶点 = 12 三角形），
// SV_InstanceID 索引 per-instance model matrix buffer。
// 注意：PerFrame 和 InstanceData 都用 StructuredBuffer（UAV 描述符），
// 复用已验证的 MSC 4.0 单 UAV 绑定路径（与 Multiply/Mandelbrot 一致）。

struct PerFrame {
    float4x4 viewProj;
    float4   lightDir;   // 方向光方向（已归一化，指向光源）
    float4   camPos;
};

struct InstanceData {
    float4x4 model;
};

struct VSOutput {
    float4 position : SV_Position;
    float3 worldPos : TEXCOORD0;
    float3 normal   : TEXCOORD1;
};

// 单位立方体 36 顶点（每面 2 三角形 × 6 面）
static const float3 cubeVerts[36] = {
    // +X
    float3( 0.5, -0.5, -0.5), float3( 0.5,  0.5, -0.5), float3( 0.5,  0.5,  0.5),
    float3( 0.5, -0.5, -0.5), float3( 0.5,  0.5,  0.5), float3( 0.5, -0.5,  0.5),
    // -X
    float3(-0.5, -0.5,  0.5), float3(-0.5,  0.5,  0.5), float3(-0.5,  0.5, -0.5),
    float3(-0.5, -0.5,  0.5), float3(-0.5,  0.5, -0.5), float3(-0.5, -0.5, -0.5),
    // +Y
    float3(-0.5,  0.5, -0.5), float3(-0.5,  0.5,  0.5), float3( 0.5,  0.5,  0.5),
    float3(-0.5,  0.5, -0.5), float3( 0.5,  0.5,  0.5), float3( 0.5,  0.5, -0.5),
    // -Y
    float3(-0.5, -0.5,  0.5), float3(-0.5, -0.5, -0.5), float3( 0.5, -0.5, -0.5),
    float3(-0.5, -0.5,  0.5), float3( 0.5, -0.5, -0.5), float3( 0.5, -0.5,  0.5),
    // +Z
    float3(-0.5, -0.5,  0.5), float3( 0.5, -0.5,  0.5), float3( 0.5,  0.5,  0.5),
    float3(-0.5, -0.5,  0.5), float3( 0.5,  0.5,  0.5), float3(-0.5,  0.5,  0.5),
    // -Z
    float3( 0.5, -0.5, -0.5), float3(-0.5, -0.5, -0.5), float3(-0.5,  0.5, -0.5),
    float3( 0.5, -0.5, -0.5), float3(-0.5,  0.5, -0.5), float3( 0.5,  0.5, -0.5),
};

static const float3 cubeNormals[36] = {
    float3(1,0,0), float3(1,0,0), float3(1,0,0), float3(1,0,0), float3(1,0,0), float3(1,0,0),
    float3(-1,0,0), float3(-1,0,0), float3(-1,0,0), float3(-1,0,0), float3(-1,0,0), float3(-1,0,0),
    float3(0,1,0), float3(0,1,0), float3(0,1,0), float3(0,1,0), float3(0,1,0), float3(0,1,0),
    float3(0,-1,0), float3(0,-1,0), float3(0,-1,0), float3(0,-1,0), float3(0,-1,0), float3(0,-1,0),
    float3(0,0,1), float3(0,0,1), float3(0,0,1), float3(0,0,1), float3(0,0,1), float3(0,0,1),
    float3(0,0,-1), float3(0,0,-1), float3(0,0,-1), float3(0,0,-1), float3(0,0,-1), float3(0,0,-1),
};

[shader("vertex")]
VSOutput main(uint vid : SV_VertexID, uint iid : SV_InstanceID,
              StructuredBuffer<PerFrame> perFrame,
              StructuredBuffer<InstanceData> instances)
{
    VSOutput out;
    float4x4 model = instances[iid].model;
    float4x4 vp = perFrame[0].viewProj;
    float3 localPos = cubeVerts[vid];
    float3 worldPos = mul(model, float4(localPos, 1.0)).xyz;
    out.position = mul(vp, float4(worldPos, 1.0));
    out.worldPos = worldPos;
    out.normal = mul((float3x3)model, cubeNormals[vid]);
    return out;
}
