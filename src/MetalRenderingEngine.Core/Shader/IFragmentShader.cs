namespace MetalRenderingEngine.Shader;

/// <summary>
/// 片元着色器接口。
/// </summary>
/// <typeparam name="TIn">片元输入结构体（通常与对应顶点着色器的 TOut 相同，字段为插值后的值）。</typeparam>
/// <typeparam name="TOut">片元输出类型（通常为 float4 表示颜色，或自定义 struct 表示 MRT 多渲染目标）。</typeparam>
public interface IFragmentShader<TIn, TOut>
    where TIn : unmanaged
    where TOut : unmanaged
{
    /// <summary>
    /// 片元着色器入口。对每个片元（像素）调用一次。
    /// </summary>
    /// <param name="input">插值后的顶点数据（从顶点着色器输出经光栅化插值而来）。</param>
    /// <returns>片元输出颜色（float4 → SV_Target，或 MRT struct → SV_Target0/1/2...）。</returns>
    TOut Execute(TIn input);
}
