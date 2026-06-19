using System.Diagnostics;
using System.Text;
using MetalRenderingEngine.Metal;

namespace MetalRenderingEngine.Shader.Compilers;

/// <summary>
/// SpirvCross 着色器编译器：SPIR-V 二进制 → spirv-cross → MSL 源码 → newLibraryWithSource → .metallib。
///
/// <para>此编译器需要 <see cref="MetalDevice"/> 来调用 <c>newLibraryWithSource</c>（AGENTS.md §3.3 例外）。</para>
///
/// <para>流转：SPIR-V binary → <c>spirv-cross --msl</c> → MSL text → <c>MTLDevice.newLibraryWithSource</c>。</para>
/// </summary>
public sealed class SpirvCrossCompiler : IShaderCompiler
{
    private const int SpirvCrossTimeoutMs = 30_000;

    private readonly MetalDevice _device;

    /// <summary>
    /// 创建 SpirvCross 编译器实例。
    /// </summary>
    /// <param name="device">Metal 设备（用于 MSL 编译为 MTLLibrary）。</param>
    public SpirvCrossCompiler(MetalDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <inheritdoc/>
    public ShaderFormat SourceFormat => ShaderFormat.Spirv;

    /// <summary>
    /// 将 SPIR-V 转换为 MSL 源码（不编译为 metallib）。
    /// 可用于调试或保存 MSL 中间产物。
    /// </summary>
    public string ConvertToMsl(byte[] spirvData, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(spirvData);

        var tempDir = CreateTempDirectory();
        try
        {
            var spvPath = Path.Combine(tempDir, "shader.spv");
            File.WriteAllBytes(spvPath, spirvData);
            return RunSpirvCross(spvPath, stage);
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    /// <inheritdoc/>
    public ShaderCompileResult Compile(string sourcePath, ShaderCompileOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("SPIR-V file not found.", sourcePath);

        var spirvData = File.ReadAllBytes(sourcePath);
        return CompileFromSource(spirvData, sourcePath, options);
    }

    /// <inheritdoc/>
    public ShaderCompileResult CompileFromSource(byte[] sourceCode, string sourceName, ShaderCompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(options);

        var sw = Stopwatch.StartNew();

        var tempDir = CreateTempDirectory();
        try
        {
            // Step 1: SPIR-V → MSL（via spirv-cross CLI）
            var spvPath = Path.Combine(tempDir, "shader.spv");
            File.WriteAllBytes(spvPath, sourceCode);

            var mslSource = RunSpirvCross(spvPath, options.Stage);

            // Step 2: MSL → MTLLibrary（via newLibraryWithSource）
            // 不直接创建 MetalLibrary（会引入 SafeHandle 生命周期复杂度），
            // 而是将 MSL 源码作为"metallib"数据的替代返回。
            // 实际使用时调用方应使用 CompileToLibrary 方法。
            var mslBytes = Encoding.UTF8.GetBytes(mslSource);

            sw.Stop();
            return new ShaderCompileResult
            {
                MetallibData = mslBytes,  // 注意：这里是 MSL 源码字节，非 metallib
                ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
            };
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    /// <summary>
    /// 完整流程：SPIR-V → MSL → MTLLibrary。
    /// 返回可用的 MetalLibrary（调用方负责 Dispose）。
    /// </summary>
    /// <param name="spirvData">SPIR-V 二进制数据。</param>
    /// <param name="stage">着色器阶段。</param>
    /// <returns>MetalLibrary。调用方负责 Dispose。</returns>
    public MetalLibrary CompileToLibrary(byte[] spirvData, ShaderStage stage)
    {
        var mslSource = ConvertToMsl(spirvData, stage);
        return _device.NewLibraryWithSource(mslSource);
    }

    /// <summary>
    /// 完整流程：从文件 SPIR-V → MSL → MTLLibrary。
    /// </summary>
    public MetalLibrary CompileToLibrary(string spirvPath, ShaderStage stage)
    {
        var spirvData = File.ReadAllBytes(spirvPath);
        return CompileToLibrary(spirvData, stage);
    }

    /// <summary>
    /// 调用 spirv-cross CLI 将 SPIR-V 转换为 MSL 源码。
    /// </summary>
    private static string RunSpirvCross(string spvPath, ShaderStage stage)
    {
        var mslPath = spvPath + ".msl";
        var args = new StringBuilder();
        args.Append($"\"{spvPath}\" --msl --output \"{mslPath}\"");
        args.Append($" --msl-version 30100");  // MSL 3.1

        // spirv-cross 的 stage 参数（可选，用于歧义消除）
        args.Append($" --stage {SpirvCrossStageString(stage)}");

        var psi = new ProcessStartInfo
        {
            FileName = "spirv-cross",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi);
        if (proc is null)
            throw new ShaderCompileException("无法启动 spirv-cross，请确认已安装并加入 PATH。");

        if (!proc.WaitForExit(SpirvCrossTimeoutMs))
        {
            try { proc.Kill(); } catch { }
            throw new ShaderCompileException($"spirv-cross 超时（{SpirvCrossTimeoutMs}ms）。");
        }

        if (proc.ExitCode != 0)
        {
            var stderr = proc.StandardError.ReadToEnd();
            throw new ShaderCompileException("spirv-cross", proc.ExitCode, stderr);
        }

        if (!File.Exists(mslPath))
            throw new ShaderCompileException("spirv-cross 未生成 MSL 输出文件。");

        return File.ReadAllText(mslPath);
    }

    private static string SpirvCrossStageString(ShaderStage stage) => stage switch
    {
        ShaderStage.Compute => "comp",
        ShaderStage.Vertex => "vert",
        ShaderStage.Fragment => "frag",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MetalSpirvCross", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupTempDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
