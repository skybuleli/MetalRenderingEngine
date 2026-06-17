namespace MetalRenderingEngine.Shader;

/// <summary>
/// 计算着色器接口。实现此接口的 struct 必须标注 [Shader] 和 [ThreadGroupSize]。
/// </summary>
public interface IComputeShader
{
    /// <summary>
    /// 计算着色器入口。每个 GPU 线程调用一次此方法。
    /// </summary>
    /// <param name="id">线程标识符（对应 SV_DispatchThreadID / SV_GroupID / SV_GroupThreadID）。</param>
    void Execute(ThreadId id);
}
