using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// SlangCompiler 单元测试：验证 Slang → DXIL → metallib 编译流程。
/// 需要系统安装 slangc + metal-shaderconverter。
/// </summary>
public class SlangCompilerTests
{
    /// <summary>
    /// 从源文件编译一个简单的 compute shader，验证 metallib 输出非空。
    /// </summary>
    [Fact]
    public void Compile_SlangFile_ProducesMetallib()
    {
        // Arrange: 写一个最简 compute shader 到临时文件
        var tempDir = Path.Combine(Path.GetTempPath(), "SlangTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var slangPath = Path.Combine(tempDir, "TestCompute.slang");
            File.WriteAllText(slangPath, @"
struct Output { uint value; };
RWStructuredBuffer<Output> outputBuffer;

[numthreads(1, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID) {
    outputBuffer[tid.x].value = tid.x * 2;
}
");

            var compiler = new SlangCompiler();
            var options = new ShaderCompileOptions
            {
                Stage = ShaderStage.Compute,
                EntryPoint = "main",
                GenerateReflection = true,
            };

            // Act
            var result = compiler.Compile(slangPath, options);

            // Assert
            Assert.NotNull(result.MetallibData);
            Assert.True(result.MetallibData.Length > 0, "metallib 不应为空");
            Assert.NotNull(result.ReflectionJson);
            Assert.True(result.ReflectionJson.Length > 0, "reflect.json 不应为空");
            Assert.True(result.ElapsedMilliseconds > 0);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// 从内存源码编译，验证 CompileFromSource 流程。
    /// </summary>
    [Fact]
    public void CompileFromSource_SlangCode_ProducesMetallib()
    {
        var source = System.Text.Encoding.UTF8.GetBytes(@"
struct Output { uint value; };
RWStructuredBuffer<Output> outputBuffer;

[numthreads(1, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID) {
    outputBuffer[tid.x].value = tid.x + 1;
}
");

        var compiler = new SlangCompiler();
        var options = new ShaderCompileOptions
        {
            Stage = ShaderStage.Compute,
            GenerateReflection = false,
        };

        var result = compiler.CompileFromSource(source, "TestShader.slang", options);

        Assert.NotNull(result.MetallibData);
        Assert.True(result.MetallibData.Length > 0);
        Assert.Null(result.ReflectionJson);  // 不生成反射
    }

    /// <summary>
    /// 编译不存在的文件应抛出 FileNotFoundException。
    /// </summary>
    [Fact]
    public void Compile_NonExistentFile_ThrowsFileNotFoundException()
    {
        var compiler = new SlangCompiler();
        var options = new ShaderCompileOptions { Stage = ShaderStage.Compute };

        Assert.Throws<FileNotFoundException>(() =>
            compiler.Compile("/tmp/nonexistent_shader.slang", options));
    }

    /// <summary>
    /// 语法错误的 Slang 应抛出 ShaderCompileException。
    /// </summary>
    [Fact]
    public void Compile_InvalidSlang_ThrowsShaderCompileException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SlangTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var slangPath = Path.Combine(tempDir, "BadShader.slang");
            File.WriteAllText(slangPath, "this is not valid slang code!!!");

            var compiler = new SlangCompiler();
            var options = new ShaderCompileOptions { Stage = ShaderStage.Compute };

            var ex = Assert.Throws<ShaderCompileException>(() =>
                compiler.Compile(slangPath, options));
            Assert.Equal("slangc", ex.ToolName);
            Assert.NotEqual(0, ex.ExitCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
