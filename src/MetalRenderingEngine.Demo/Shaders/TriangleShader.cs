using MetalRenderingEngine.Shader;

namespace MetalRenderingEngine.Demo.Shaders;

/// <summary>
/// Triangle 顶点着色器：pass-through，直接输出输入位置和颜色。
/// 注意：TIn(VertexPosColor) 与 TOut(VertexPosColorOut) 必须是不同类型 ——
/// 源生成器对 In/Out 同型会加 _Input/_Output 后缀，但不会改写方法体内的类型引用，
/// 导致生成的 Slang 引用未定义类型。使用不同类型名可从根上规避。
/// </summary>
[Shader]
#pragma warning disable CS0649
partial struct TriangleVertexShader : IVertexShader<VertexPosColor, VertexPosColorOut>
{
    public VertexPosColorOut Execute(VertexPosColor input)
    {
        var output = new VertexPosColorOut();
        output.Position = input.Position;
        output.Color = input.Color;
        return output;
    }
}

/// <summary>
/// Triangle 片元着色器：直接输出红色。
/// </summary>
[Shader]
#pragma warning disable CS0649
partial struct TriangleFragmentShader : IFragmentShader<VertexPosColorOut, float4>
{
    public float4 Execute(VertexPosColorOut input)
    {
        return new float4(1.0f, 0.0f, 0.0f, 1.0f);
    }
}
