namespace MetalRenderingEngine.Shader;

// ============================================================
// GPU 资源类型（空壳）
// 源生成器识别这些类型并生成对应的 Slang 资源绑定。
// C# 端索引器仅供编译期类型检查，运行时由生成的 Binding 类管理。
// ============================================================

/// <summary>
/// 读写结构化缓冲区（Slang RWStructuredBuffer&lt;T&gt;，register(uN)）。
/// </summary>
/// <typeparam name="T">元素类型（必须是 unmanaged）。</typeparam>
public struct ReadWriteBuffer<T> where T : unmanaged
{
    /// <summary>按索引读写元素（仅着色器方法体内有效，由源生成器翻译）。</summary>
    public T this[int index]
    {
        get => throw new InvalidOperationException(
            "ReadWriteBuffer 索引器仅可在 [Shader] 标注的方法体内使用，由源生成器翻译为 Slang 代码。");
        set => throw new InvalidOperationException(
            "ReadWriteBuffer 索引器仅可在 [Shader] 标注的方法体内使用，由源生成器翻译为 Slang 代码。");
    }

    /// <summary>缓冲区元素数量（仅着色器方法体内有效）。</summary>
    public int Count => throw new InvalidOperationException(
        "ReadWriteBuffer.Count 仅可在 [Shader] 标注的方法体内使用。");
}

/// <summary>
/// 只读结构化缓冲区（Slang StructuredBuffer&lt;T&gt;，register(tN)）。
/// </summary>
/// <typeparam name="T">元素类型（必须是 unmanaged）。</typeparam>
public readonly struct ReadOnlyBuffer<T> where T : unmanaged
{
    /// <summary>按索引读取元素（仅着色器方法体内有效，由源生成器翻译）。</summary>
    public T this[int index] => throw new InvalidOperationException(
        "ReadOnlyBuffer 索引器仅可在 [Shader] 标注的方法体内使用，由源生成器翻译为 Slang 代码。");

    /// <summary>缓冲区元素数量（仅着色器方法体内有效）。</summary>
    public int Count => throw new InvalidOperationException(
        "ReadOnlyBuffer.Count 仅可在 [Shader] 标注的方法体内使用。");
}

/// <summary>
/// 常量缓冲区（Slang ConstantBuffer&lt;T&gt;，register(bN)）。
/// 字段 T 会被打包到 argument buffer 的 CBV 描述符中。
/// </summary>
/// <typeparam name="T">结构体类型（必须是 unmanaged）。</typeparam>
public readonly struct ConstantBuffer<T> where T : unmanaged
{
    /// <summary>获取常量缓冲区内容（仅着色器方法体内有效）。</summary>
    public T Value => throw new InvalidOperationException(
        "ConstantBuffer.Value 仅可在 [Shader] 标注的方法体内使用。");
}
