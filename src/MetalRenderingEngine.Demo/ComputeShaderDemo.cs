using System.Diagnostics;
using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Demo.Shaders;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// 验证源生成器端到端流程：从 C# compute shader 生成 Slang →
/// 手动（MSBuild Task 就绪前）编译 → 加载 → 调度 → 校验结果。
/// </summary>
internal static class ComputeShaderDemo
{
    private const int ElementCount = 1024;
    private const int ThreadsPerGroup = 64;

    /// <summary>MSC 4.0 argument table 放在 buffer(2)。</summary>
    private const ulong ArgumentTableBufferIndex = 2;

    public static int Run()
    {
        Console.WriteLine("=== Compute Shader (Source Generated) Demo ===");

        // 1) 获取源生成器输出的 Slang 源码
        var slangSource = MultiplyShaderSlangSource.Source;
        var fileName = MultiplyShaderSlangSource.FileName;
        Console.WriteLine($"Generated Slang: {fileName}");
        Console.WriteLine($"--- BEGIN SLANG ---\n{slangSource}\n--- END SLANG ---");

        // 2) 将 Slang 源码写入临时文件
        var slangDir = Path.Combine(Path.GetTempPath(), "MetalShaderGen");
        Directory.CreateDirectory(slangDir);
        var slangPath = Path.Combine(slangDir, fileName);
        File.WriteAllText(slangPath, slangSource);

        // 3) 编译 Slang → metalib
        var metallibPath = Path.Combine(slangDir, Path.GetFileNameWithoutExtension(fileName) + ".metallib");
        Console.WriteLine($"Compiling: {slangPath} → {metallibPath}");

        if (!CompileSlangToMetallib(slangPath, metallibPath))
        {
            Console.Error.WriteLine("❌ Slang compilation failed.");
            return 1;
        }

        // 4) 加载 metallib 并调度
        byte[] metallib = File.ReadAllBytes(metallibPath);
        using var device = MetalDevice.CreateSystemDefault();
        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("main");
        using var pso = device.NewComputePipelineState(function);

        using var buffer = device.NewBuffer(
            (ulong)(ElementCount * sizeof(float)),
            MTLResourceOptions.StorageModeShared);

        Span<float> input = buffer.AsSpan<float>();
        for (int i = 0; i < ElementCount; i++) input[i] = i + 1.0f;

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var encoder = cmdbuf.ComputeCommandEncoder())
        {
            encoder.SetComputePipelineState(pso);
            encoder.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);

            var uavDesc = new UavDescriptor
            {
                GpuAddress = buffer.GpuAddress,
                Length = buffer.Length,
                Stride = sizeof(float),
            };
            encoder.SetBytes(uavDesc, ArgumentTableBufferIndex);

            var groups = new WMTSize((ulong)(ElementCount / ThreadsPerGroup), 1, 1);
            var threads = new WMTSize(ThreadsPerGroup, 1, 1);
            encoder.DispatchThreadgroups(groups, threads);
            encoder.EndEncoding();
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error())
        {
            if (err is not null)
            {
                Console.Error.WriteLine($"❌ GPU error: {err.Description}");
                return 1;
            }
        }

        // 5) 校验结果
        Span<float> output = buffer.AsSpan<float>();
        int wrong = 0;
        for (int i = 0; i < ElementCount; i++)
        {
            float expected = (i + 1.0f) * 2.0f;
            if (Math.Abs(output[i] - expected) > 1e-5f)
            {
                if (wrong < 5)
                    Console.Error.WriteLine($"  index {i}: expected {expected}, got {output[i]}");
                wrong++;
            }
        }

        if (wrong != 0)
        {
            Console.Error.WriteLine($"❌ {wrong}/{ElementCount} elements mismatch.");
            return 1;
        }

        Console.WriteLine($"✅ All {ElementCount} elements doubled correctly. Status: {cmdbuf.Status}");

        // 清理临时文件
        try { File.Delete(slangPath); File.Delete(metallibPath); } catch { }

        return 0;
    }

    /// <summary>调用 slangc → metal-shaderconverter 编译 Slang 为 metallib。</summary>
    private static bool CompileSlangToMetallib(string slangPath, string metallibPath)
    {
        var tempDir = Path.GetDirectoryName(metallibPath)!;
        var dxilPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(slangPath) + ".dxil");

        try
        {
            // Step 1: slangc → DXIL
            var slangc = new ProcessStartInfo
            {
                FileName = "slangc",
                Arguments = $"-o \"{dxilPath}\" -target dxil \"{slangPath}\" -Xdxc -rootsig-define=MULTIPLY_RS=RootFlags\\(0\\),UAV\\(u0\\)",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc1 = Process.Start(slangc);
            if (proc1 is null)
            {
                Console.Error.WriteLine("slangc not found in PATH.");
                return false;
            }
            proc1.WaitForExit(30_000);
            if (proc1.ExitCode != 0)
            {
                Console.Error.WriteLine($"slangc failed (exit={proc1.ExitCode}): {proc1.StandardError.ReadToEnd()}");
                return false;
            }

            if (!File.Exists(dxilPath))
            {
                Console.Error.WriteLine($"DXIL not produced: {dxilPath}");
                return false;
            }

            // Step 2: metal-shaderconverter → metallib
            var converter = new ProcessStartInfo
            {
                FileName = "metal-shaderconverter",
                Arguments = $"\"{dxilPath}\" -o \"{metallibPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc2 = Process.Start(converter);
            if (proc2 is null)
            {
                Console.Error.WriteLine("metal-shaderconverter not found.");
                return false;
            }
            proc2.WaitForExit(60_000);
            if (proc2.ExitCode != 0)
            {
                Console.Error.WriteLine($"metal-shaderconverter failed (exit={proc2.ExitCode}): {proc2.StandardError.ReadToEnd()}");
                return false;
            }

            return File.Exists(metallibPath);
        }
        finally
        {
            // 清理 DXIL 中间文件
            try { if (File.Exists(dxilPath)) File.Delete(dxilPath); } catch { }
        }
    }
}
