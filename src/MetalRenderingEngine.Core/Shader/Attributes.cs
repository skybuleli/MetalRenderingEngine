namespace MetalRenderingEngine.Shader;

/// <summary>
/// 标记一个 partial struct 为着色器定义。
/// 源生成器会扫描此属性标注的结构体并生成对应的 Slang 代码和绑定类。
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ShaderAttribute : Attribute { }

/// <summary>
/// 指定 compute shader 的线程组大小（对应 Slang 的 [numthreads(x, y, z)]）。
/// 仅对实现 <see cref="IComputeShader"/> 的结构体有效。
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ThreadGroupSizeAttribute : Attribute
{
    public int X { get; }
    public int Y { get; }
    public int Z { get; }

    public ThreadGroupSizeAttribute(int x, int y, int z)
    {
        X = x; Y = y; Z = z;
    }
}
