using MetalRenderingEngine.Shader;

namespace MetalRenderingEngine.Demo.Shaders;

/// <summary>
/// Triangle 顶点着色器：pass-through，直接输出输入位置和颜色。
/// </summary>
[Shader]
#pragma warning disable CS0649
partial struct TriangleVertexShader : IVertexShader<VertexPosColor, VertexPosColor>
{
    public VertexPosColor Execute(VertexPosColor input)
    {
        return input;
    }
}

/// <summary>
/// Triangle 片元着色器：直接输出红色。
/// </summary>
[Shader]
#pragma warning disable CS0649
partial struct TriangleFragmentShader : IFragmentShader<VertexPosColor, float4>
{
    public float4 Execute(VertexPosColor input)
    {
        return new float4(1.0f, 0.0f, 0.0f, 1.0f);
    }
}
