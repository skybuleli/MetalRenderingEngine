namespace MetalRenderingEngine.Shader;

/// <summary>
/// 顶点位置+颜色结构体，用于简单着色器测试。
/// Position 映射到 SV_Position（顶点着色器输出）或 POSITION（顶点输入）。
/// Color 映射到 COLOR 或 TEXCOORD 语义。
/// </summary>
public struct VertexPosColor
{
    public float4 Position;
    public float4 Color;
}
