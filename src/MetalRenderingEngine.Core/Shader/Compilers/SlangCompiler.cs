using System.Diagnostics;
using System.Text;

namespace MetalRenderingEngine.Shader.Compilers;

/// <summary>
/// Slang 着色器编译器：Slang 源码 → slangc → DXIL → metal-shaderconverter → .metallib。
///
/// <para>提炼自 <c>ComputeShaderDemo.CompileSlangToMetallib</c> 的子进程编译逻辑，
/// 增加反射 JSON 输出与错误处理。</para>
/// </summary>
public sealed class SlangCompiler : IShaderCompiler
{
    /// <summary>slangc 超时（毫秒）。</summary>
    private const int SlangcTimeoutMs = 30_000;

    /// <summary>metal-shaderconverter 超时（毫秒）。</summary>
    private const int MscTimeoutMs = 60_000;

    /// <inheritdoc/>
    public ShaderFormat SourceFormat => ShaderFormat.Slang;

    /// <inheritdoc/>
    public ShaderCompileResult Compile(string sourcePath, ShaderCompileOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Slang source file not found.", sourcePath);

        var sw = Stopwatch.StartNew();
        var tempDir = CreateTempDirectory();
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(sourcePath);
            var dxilPath = Path.Combine(tempDir, baseName + ".dxil");
            var metallibPath = Path.Combine(tempDir, baseName + ".metallib");
            var reflectPath = options.GenerateReflection
                ? Path.Combine(tempDir, baseName + ".reflect.json")
                : null;

            // Step 1: slangc → DXIL
            RunSlangc(sourcePath, dxilPath, options);

            // Step 2: metal-shaderconverter → metallib（+ 可选 reflect.json）
            RunMetalShaderConverter(dxilPath, metallibPath, reflectPath, options);

            // 读取产出
            var metallibData = File.ReadAllBytes(metallibPath);
            byte[]? reflectionData = null;
            if (reflectPath != null && File.Exists(reflectPath))
                reflectionData = File.ReadAllBytes(reflectPath);

            sw.Stop();
            return new ShaderCompileResult
            {
                MetallibData = metallibData,
                ReflectionJson = reflectionData,
                ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
            };
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    /// <inheritdoc/>
    public ShaderCompileResult CompileFromSource(byte[] sourceCode, string sourceName, ShaderCompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);

        var tempDir = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(tempDir, sourceName);
            File.WriteAllBytes(sourcePath, sourceCode);
            return Compile(sourcePath, options);
        }
        finally
        {
            // Compile 已经清理了 tempDir 的子文件，但目录本身可能还在
            // 这里再尝试清理目录
            // 注意：Compile 内部也 try-finally 清理了，所以这里可能已经删了
            // 安全起见包一层
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// slangc: Slang → DXIL。
    /// </summary>
    private static void RunSlangc(string sourcePath, string dxilPath, ShaderCompileOptions options)
    {
        var args = new StringBuilder();
        args.Append($"-o \"{dxilPath}\" -target dxil \"{sourcePath}\"");
        args.Append($" -entry {options.EntryPoint}");
        args.Append($" -stage {StageToString(options.Stage)}");
        args.Append($" -profile {options.Profile}");

        if (options.ExtraSlangcArgs is { Length: > 0 })
        {
            foreach (var extra in options.ExtraSlangcArgs)
                args.Append(' ').Append(extra);
        }

        RunProcess("slangc", args.ToString(), SlangcTimeoutMs);
    }

    /// <summary>
    /// metal-shaderconverter: DXIL → metallib（+ 可选 reflection）。
    /// </summary>
    private static void RunMetalShaderConverter(string dxilPath, string metallibPath, string? reflectPath,
        ShaderCompileOptions options)
    {
        if (!File.Exists(dxilPath))
            throw new ShaderCompileException($"DXIL 文件未生成: {dxilPath}");

        var args = new StringBuilder();
        args.Append($"\"{dxilPath}\" -o \"{metallibPath}\"");

        if (reflectPath != null)
            args.Append($" --output-reflection-file \"{reflectPath}\"");

        if (options.ExtraMscArgs is { Length: > 0 })
        {
            foreach (var extra in options.ExtraMscArgs)
                args.Append(' ').Append(extra);
        }

        RunProcess("metal-shaderconverter", args.ToString(), MscTimeoutMs);
    }

    /// <summary>
    /// 启动外部编译进程并等待完成。
    /// </summary>
    private static void RunProcess(string fileName, string arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            throw new ShaderCompileException($"无法启动 {fileName}，请确认已安装并加入 PATH。");

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(); } catch { }
            throw new ShaderCompileException($"{fileName} 超时（{timeoutMs}ms）。");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new ShaderCompileException(fileName, proc.ExitCode, stderr);
        }
    }

    private static string StageToString(ShaderStage stage) => stage switch
    {
        ShaderStage.Compute => "compute",
        ShaderStage.Vertex => "vertex",
        ShaderStage.Fragment => "fragment",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MetalShaderGen", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
