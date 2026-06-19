namespace MetalRenderingEngine.Shader;

/// <summary>
/// 着色器编译失败时抛出的异常。
/// </summary>
public sealed class ShaderCompileException : Exception
{
    /// <summary>编译工具名称（如 "slangc"、"metal-shaderconverter"、"spirv-cross"）。</summary>
    public string ToolName { get; }

    /// <summary>工具进程的退出码。</summary>
    public int ExitCode { get; }

    /// <summary>工具 stderr 输出。</summary>
    public string StandardError { get; }

    public ShaderCompileException(string toolName, int exitCode, string standardError)
        : base($"Shader compilation failed: {toolName} exited with code {exitCode}.\n{standardError}")
    {
        ToolName = toolName;
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public ShaderCompileException(string message)
        : base(message)
    {
        ToolName = string.Empty;
        StandardError = message;
    }

    public ShaderCompileException(string message, Exception innerException)
        : base(message, innerException)
    {
        ToolName = string.Empty;
        StandardError = message;
    }
}
