// MandelbrotDisplay.frag.slang — 从 Mandelbrot compute 输出 buffer 采样像素
// 与 Mandelbrot.slang 的输出布局对齐：
//   Output[0]   = float4(cx, cy, scale, maxIter)   ← 参数槽
//   Output[1..] = float4 像素，按行优先 [y*W + x + 1] 索引

static const int WIDTH  = 1024;
static const int HEIGHT = 768;

struct PSInput {
    float4 position : SV_Position;
    float2 uv       : TEXCOORD;
};

[shader("fragment")]
float4 main(PSInput input, StructuredBuffer<float4> pixels) : SV_Target
{
    // uv.y=0 在底部，但 Mandelbrot 输出行 0 在顶部 → 需翻转 y。
    int x = int(input.uv.x * float(WIDTH));
    int y = int((1.0 - input.uv.y) * float(HEIGHT));
    x = clamp(x, 0, WIDTH - 1);
    y = clamp(y, 0, HEIGHT - 1);

    // +1 跳过 Output[0] 参数槽
    int index = y * WIDTH + x + 1;
    return pixels[index];
}
