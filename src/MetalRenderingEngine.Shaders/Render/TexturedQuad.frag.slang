// TexturedQuad.frag.slang — 纹理采样片段着色器
// 使用 StructuredBuffer<uint> 存储 RGBA8 打包像素，手动最近邻采样

static const int TEX_SIZE = 64;

struct PSInput {
    float4 position : SV_Position;
    float2 uv       : TEXCOORD;
};

[shader("fragment")]
float4 main(PSInput input, StructuredBuffer<uint> texPixels) : SV_Target
{
    // 最近邻采样
    int2 coord = clamp(int2(input.uv * float2(TEX_SIZE, TEX_SIZE)),
                       int2(0, 0), int2(TEX_SIZE - 1, TEX_SIZE - 1));
    int index = coord.y * TEX_SIZE + coord.x;
    uint packed = texPixels[index];

    // 解包 RGBA8 (R 在低字节)
    float r = float((packed >>  0) & 0xFF) / 255.0;
    float g = float((packed >>  8) & 0xFF) / 255.0;
    float b = float((packed >> 16) & 0xFF) / 255.0;
    float a = float((packed >> 24) & 0xFF) / 255.0;

    return float4(r, g, b, a);
}
