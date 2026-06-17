using MetalRenderingEngine.Shader;

namespace MetalRenderingEngine.Demo.Shaders;

/// <summary>
/// Mandelbrot 集合计算着色器。
/// 每个线程计算一个像素的迭代次数并映射为颜色。
/// </summary>
[Shader]
[ThreadGroupSize(16, 16, 1)]
#pragma warning disable CS0649
partial struct MandelbrotShader : IComputeShader
{
    public ReadWriteBuffer<float4> Output;
    public ReadOnlyBuffer<float4> Constants;

    public void Execute(ThreadId id)
    {
        var index = (int)id.DispatchThreadID.X;
        var cx = Constants[0].X;
        var cy = Constants[0].Y;
        var scale = Constants[0].Z;
        var maxIter = (int)Constants[0].W;

        var width = 1024;
        var x = index % width;
        var y = index / width;

        var c = new float2(
            (x - width / 2.0f) * scale + cx,
            (y - width / 2.0f) * scale + cy
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
