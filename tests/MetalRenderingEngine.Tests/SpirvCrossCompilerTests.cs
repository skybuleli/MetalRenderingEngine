using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// SpirvCrossCompiler 测试：SPIR-V → MSL 转换。
/// 需要系统安装 spirv-cross + slangc（生成 SPIR-V 输入）。
/// </summary>
public class SpirvCrossCompilerTests
{
    /// <summary>
    /// 验证 spirv-cross 是否已安装。
    /// </summary>
    private static bool IsSpirvCrossAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("spirv-cross", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            return proc?.WaitForExit(5000) == true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 使用 slangc 生成 SPIR-V 输入。
    /// </summary>
    private static byte[]? GenerateSpirv(string slangSource)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SpirvTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var slangPath = Path.Combine(tempDir, "test.slang");
            var spvPath = Path.Combine(tempDir, "test.spv");
            File.WriteAllText(slangPath, slangSource);

            var psi = new System.Diagnostics.ProcessStartInfo("slangc",
                $"\"{slangPath}\" -target spirv -entry main -stage compute -o \"{spvPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            proc.WaitForExit(30_000);
            if (proc.ExitCode != 0 || !File.Exists(spvPath)) return null;
            return File.ReadAllBytes(spvPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ConvertToMsl_SimpleCompute_ProducesMsl()
    {
        if (!IsSpirvCrossAvailable())
        {
            // 跳过：spirv-cross 未安装
            return;
        }

        var spirv = GenerateSpirv(@"
struct Output { uint value; };
RWStructuredBuffer<Output> outputBuffer;

[numthreads(64, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID) {
    outputBuffer[tid.x].value = tid.x * 2;
}
");
        Assert.NotNull(spirv);
        Assert.True(spirv!.Length > 0, "SPIR-V 数据不应为空");

        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SpirvCrossCompiler(device);

        var msl = compiler.ConvertToMsl(spirv, ShaderStage.Compute);

        Assert.False(string.IsNullOrWhiteSpace(msl), "MSL 输出不应为空");
        // MSL 应包含 Metal 关键字
        Assert.Contains("kernel", msl);
    }

    [Fact]
    public void CompileToLibrary_SimpleCompute_Succeeds()
    {
        if (!IsSpirvCrossAvailable())
            return;

        var spirv = GenerateSpirv(@"
struct Output { uint value; };
RWStructuredBuffer<Output> outputBuffer;

[numthreads(1, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID) {
    outputBuffer[tid.x].value = tid.x + 1;
}
");
        Assert.NotNull(spirv);

        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SpirvCrossCompiler(device);

        // 完整流程：SPIR-V → MSL → MTLLibrary
        using var library = compiler.CompileToLibrary(spirv!, ShaderStage.Compute);
        Assert.False(library.IsInvalid, "MTLLibrary 应成功创建");
    }

    [Fact]
    public void CompileFromSource_ReturnsMslBytes()
    {
        if (!IsSpirvCrossAvailable())
            return;

        var spirv = GenerateSpirv(@"
[numthreads(1, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID) {
}
");
        Assert.NotNull(spirv);

        using var device = MetalDevice.CreateSystemDefault();
        var compiler = new SpirvCrossCompiler(device);
        var options = new ShaderCompileOptions { Stage = ShaderStage.Compute };

        var result = compiler.CompileFromSource(spirv!, "test.spv", options);

        // CompileFromSource 返回的 MetallibData 实际是 MSL 源码字节
        Assert.NotNull(result.MetallibData);
        var msl = System.Text.Encoding.UTF8.GetString(result.MetallibData!);
        Assert.Contains("kernel", msl);
    }
}
