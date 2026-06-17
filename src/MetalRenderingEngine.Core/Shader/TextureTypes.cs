namespace MetalRenderingEngine.Shader;

// ============================================================
// GPU 纹理/采样器类型（空壳）
// 源生成器识别这些类型并生成对应的 Slang 资源绑定。
// ============================================================

/// <summary>
/// 2D 纹理（Slang Texture2D&lt;T&gt;）。
/// </summary>
/// <typeparam name="T">采样返回类型（通常为 float4）。</typeparam>
public readonly struct Texture2D<T> where T : unmanaged
{
    /// <summary>采样纹理（需配合 SamplerState 使用）。</summary>
    public T Sample(SamplerState sampler, float2 uv) => throw new InvalidOperationException(
        "Texture2D.Sample 仅可在 [Shader] 标注的方法体内使用，由源生成器翻译为 Slang 代码。");

    /// <summary>按像素坐标加载纹理（不经过采样器）。</summary>
    public T Load(int3 location) => throw new InvalidOperationException(
        "Texture2D.Load 仅可在 [Shader] 标注的方法体内使用。");
}

/// <summary>
/// 采样器状态（Slang SamplerState，register(sN)）。
/// </summary>
public readonly struct SamplerState
{
    // 空壳类型，仅供源生成器识别。
}
