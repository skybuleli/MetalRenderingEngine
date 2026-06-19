namespace MetalRenderingEngine.Shader;

/// <summary>
/// 着色器源/目标格式。
/// </summary>
public enum ShaderFormat
{
    /// <summary>Slang 源码（HLSL 超集）。</summary>
    Slang,
    /// <summary>DXIL 二进制（DirectX Intermediate Language）。</summary>
    Dxil,
    /// <summary>SPIR-V 二进制（Vulkan 中间表示）。</summary>
    Spirv,
    /// <summary>MSL 源码（Metal Shading Language，仅 SpirvCross 路径使用）。</summary>
    Msl,
    /// <summary>预编译 .metallib 二进制。</summary>
    MetalLib,
}

/// <summary>
/// 着色器阶段。
/// </summary>
public enum ShaderStage
{
    Compute,
    Vertex,
    Fragment,
}

/// <summary>
/// 着色器编译选项。
/// </summary>
public sealed class ShaderCompileOptions
{
    /// <summary>入口函数名（默认 "main"）。</summary>
    public string EntryPoint { get; init; } = "main";

    /// <summary>着色器阶段。</summary>
    public ShaderStage Stage { get; init; } = ShaderStage.Compute;

    /// <summary>Slang shader model profile（默认 "sm_6_0"）。</summary>
    public string Profile { get; init; } = "sm_6_0";

    /// <summary>是否生成 MSC 反射 JSON（<c>--output-reflection-file</c>）。</summary>
    public bool GenerateReflection { get; init; } = true;

    /// <summary>额外 slangc 参数（如 <c>-Xdxc -rootsig-define=...</c>）。</summary>
    public string[]? ExtraSlangcArgs { get; init; }

    /// <summary>额外 metal-shaderconverter 参数。</summary>
    public string[]? ExtraMscArgs { get; init; }
}

/// <summary>
/// 着色器编译结果。
/// </summary>
public sealed class ShaderCompileResult
{
    /// <summary>编译产出的 .metallib 字节（成功时非空）。</summary>
    public byte[]? MetallibData { get; init; }

    /// <summary>MSC 反射 JSON 字节（<see cref="ShaderCompileOptions.GenerateReflection"/> 为 true 时）。</summary>
    public byte[]? ReflectionJson { get; init; }

    /// <summary>编译耗时（毫秒）。</summary>
    public double ElapsedMilliseconds { get; init; }

    /// <summary>是否来自缓存命中。</summary>
    public bool CacheHit { get; init; }
}

/// <summary>
/// 着色器编译器统一接口。
/// </summary>
public interface IShaderCompiler
{
    /// <summary>编译器支持的源格式。</summary>
    ShaderFormat SourceFormat { get; }

    /// <summary>
    /// 编译着色器为 .metallib。
    /// </summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="options">编译选项。</param>
    /// <returns>编译结果，包含 metallib 字节和可选反射 JSON。</returns>
    /// <exception cref="ShaderCompileException">编译失败时抛出。</exception>
    ShaderCompileResult Compile(string sourcePath, ShaderCompileOptions options);

    /// <summary>
    /// 从内存中的源码编译（写入临时文件后调用外部工具链）。
    /// </summary>
    /// <param name="sourceCode">源码文本或二进制。</param>
    /// <param name="sourceName">源文件名（用于推断扩展名）。</param>
    /// <param name="options">编译选项。</param>
    ShaderCompileResult CompileFromSource(byte[] sourceCode, string sourceName, ShaderCompileOptions options);
}
