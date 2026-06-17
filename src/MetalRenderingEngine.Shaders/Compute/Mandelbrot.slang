// Mandelbrot compute shader.
// 单 buffer 方案：Output[0] = float4(cx, cy, scale, maxIter)，Output[1..] = 像素颜色。
RWStructuredBuffer<float4> Output : register(u0);

[numthreads(16, 16, 1)]
[shader("compute")]
void main(uint3 id : SV_DispatchThreadID)
{
    int width = 1024;
    int height = 768;
    int index = (int)(id.y * width + id.x) + 1; // +1 跳过参数槽
    if (index - 1 >= width * height) return;

    float cx = Output[0].x;
    float cy = Output[0].y;
    float scale = Output[0].z;
    int maxIter = (int)Output[0].w;

    float x = (float)id.x;
    float y = (float)id.y;
    float c_re = (x - width / 2.0) * scale + cx;
    float c_im = (y - height / 2.0) * scale + cy;
    float z_re = 0.0, z_im = 0.0;
    int iter = 0;

    for (int i = 0; i < maxIter; i++)
    {
        float z_re2 = z_re * z_re - z_im * z_im;
        float z_im2 = 2.0 * z_re * z_im;
        z_re = z_re2 + c_re;
        z_im = z_im2 + c_im;
        if (z_re * z_re + z_im * z_im > 4.0) break;
        iter++;
    }

    if (iter >= maxIter)
        Output[index] = float4(0.0, 0.0, 0.0, 1.0);
    else
    {
        float t = (float)iter / maxIter;
        Output[index] = float4(t * 0.5, t * 0.8, t * 1.0, 1.0);
    }
}
