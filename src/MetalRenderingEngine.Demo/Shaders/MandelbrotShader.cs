using MetalRenderingEngine.Shader;

namespace MetalRenderingEngine.Demo.Shaders;

/// <summary>
/// Mandelbrot 集合计算着色器（源生成器版）。
/// 与 <c>src/MetalRenderingEngine.Shaders/Compute/Mandelbrot.slang</c>（手写版）布局严格一致：
/// 单 buffer 方案 —— Output[0] = float4(cx, cy, scale, maxIter)，Output[1..] = 像素颜色。
/// 二维 dispatch：每个线程处理 (id.x, id.y) 对应的一个像素。
/// </summary>
[Shader]
[ThreadGroupSize(16, 16, 1)]
#pragma warning disable CS0649
partial struct MandelbrotShader : IComputeShader
{
    public ReadWriteBuffer<float4> Output;

    public void Execute(ThreadId id)
    {
        var width = 1024;
        var height = 768;
        var x = (int)id.DispatchThreadID.X;
        var y = (int)id.DispatchThreadID.Y;

        // +1 跳过 Output[0] 参数槽
        var index = y * width + x + 1;
        if (index - 1 >= width * height)
            return;

        var cx = Output[0].X;
        var cy = Output[0].Y;
        var scale = Output[0].Z;
        var maxIter = (int)Output[0].W;

        var c = new float2(
            (x - width / 2.0f) * scale + cx,
            (y - height / 2.0f) * scale + cy
        );

        var z = new float2(0.0f, 0.0f);
        var i = 0;

        while (i < maxIter)
        {
            var z2 = new float2(
                z.X * z.X - z.Y * z.Y,
                2.0f * z.X * z.Y
            );
            z = new float2(z2.X + c.X, z2.Y + c.Y);

            if (z.X * z.X + z.Y * z.Y > 4.0f)
                break;

            i++;
        }

        if (i >= maxIter)
        {
            Output[index] = new float4(0.0f, 0.0f, 0.0f, 1.0f);
        }
        else
        {
            var t = (float)i / maxIter;
            Output[index] = new float4(
                t * 0.5f,
                t * 0.8f,
                t * 1.0f,
                1.0f
            );
        }
    }
}
