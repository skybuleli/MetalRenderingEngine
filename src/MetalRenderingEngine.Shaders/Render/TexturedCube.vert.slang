// TexturedCube.vert.slang — Phase 10 Demo: 贴图旋转立方体
// SV_VertexID 生成单位立方体（36 顶点），StructuredBuffer<PerFrame> 提供 viewProj。
// vertex 资源绑定走 Phase 10C ResourceTable（反射驱动），不经手写 UavDescriptor。

struct PerFrame {
    float4x4 viewProj;
    float4x4 model;       // 旋转矩阵（CPU 算好）
};

struct VSOut {
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
    float3 normal   : TEXCOORD1;
};

StructuredBuffer<PerFrame> perFrame : register(t0);

// 单位立方体 36 顶点（每面 2 三角形 × 6 面），与 ThreeDScene.vert.slang 一致
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

// 每面 UV 覆盖 [0,1]×[0,1]（两个三角形拼成矩形）
static const float2 cubeUvs[36] = {
    // +X
    float2(0,0), float2(0,1), float2(1,1),
    float2(0,0), float2(1,1), float2(1,0),
    // -X
    float2(0,0), float2(0,1), float2(1,1),
    float2(0,0), float2(1,1), float2(1,0),
    // +Y
    float2(0,0), float2(0,1), float2(1,1),
    float2(0,0), float2(1,1), float2(1,0),
    // -Y
    float2(0,0), float2(0,1), float2(1,1),
    float2(0,0), float2(1,1), float2(1,0),
    // +Z
    float2(0,0), float2(0,1), float2(1,1),
    float2(0,0), float2(1,1), float2(1,0),
    // -Z
    float2(0,0), float2(0,1), float2(1,1),
    float2(0,0), float2(1,1), float2(1,0),
};

[shader("vertex")]
VSOut main(uint vid : SV_VertexID)
{
    VSOut o;
    // 行主序矩阵：先 model 变换到世界空间，再 viewProj 变换到裁剪空间。
    // 顺序必须是 viewProj * (model * pos)，与 ThreeDScene.vert.slang 一致。
    float4 worldPos = mul(perFrame[0].model, float4(cubeVerts[vid], 1.0));
    o.position = mul(perFrame[0].viewProj, worldPos);
    o.uv = cubeUvs[vid];
    o.normal = mul((float3x3)perFrame[0].model, cubeNormals[vid]);
    return o;
}
