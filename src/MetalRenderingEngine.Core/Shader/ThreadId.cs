namespace MetalRenderingEngine.Shader;

/// <summary>
/// GPU 线程标识符（对应 Slang 的 SV_DispatchThreadID / SV_GroupID / SV_GroupThreadID）。
/// 源生成器会将此翻译为对应的 uint3 语义参数。
/// </summary>
public readonly struct ThreadId
{
    /// <summary>全局线程 ID（SV_DispatchThreadID）。</summary>
    public uint3 DispatchThreadID { get; }

    /// <summary>线程组 ID（SV_GroupID）。</summary>
    public uint3 GroupID { get; }

    /// <summary>组内线程 ID（SV_GroupThreadID）。</summary>
    public uint3 GroupThreadID { get; }
}
