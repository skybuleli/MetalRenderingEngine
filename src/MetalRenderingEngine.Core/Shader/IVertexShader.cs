namespace MetalRenderingEngine.Shader;

/// <summary>
/// 顶点着色器接口。
/// </summary>
/// <typeparam name="TIn">顶点输入结构体（必须 unmanaged，字段映射到顶点属性语义）。</typeparam>
/// <typeparam name="TOut">顶点输出结构体（必须 unmanaged，必须包含 Position 字段映射到 SV_Position）。</typeparam>
public interface IVertexShader<TIn, TOut>
    where TIn : unmanaged
    where TOut : unmanaged
{
    /// <summary>
    /// 顶点着色器入口。对每个顶点调用一次。
    /// </summary>
    /// <param name="input">顶点输入（从顶点缓冲区读取的属性）。</param>
    /// <returns>顶点输出（包含变换后位置和插值数据）。</returns>
    TOut Execute(TIn input);
}
