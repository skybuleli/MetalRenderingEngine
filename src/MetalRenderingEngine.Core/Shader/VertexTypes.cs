namespace MetalRenderingEngine.Shader;

/// <summary>
/// 顶点输入：位置+颜色（POSITION/COLOR 语义）。
/// </summary>
public struct VertexPosColor
{
    public float4 Position;
    public float4 Color;
}

/// <summary>
/// 顶点输出：变换后位置+插值颜色（SV_Position/TEXCOORD 语义）。
/// 故意使用与 <see cref="VertexPosColor"/> 不同的类型名 —— 源生成器对 In/Out 同型
/// 会加 _Input/_Output 后缀，但不会改写方法体内的类型引用，导致生成的 Slang
/// 引用未定义类型。使用不同类型名可从根上规避此问题。
/// </summary>
public struct VertexPosColorOut
{
    public float4 Position;
    public float4 Color;
}
