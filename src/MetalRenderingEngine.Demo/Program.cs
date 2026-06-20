using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using System.Runtime.InteropServices;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 1 验证：把 1024 元素 buffer 经 Metal compute shader ×2 后断言结果。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "compute";

        try
        {
            if (mode == "compute") return ComputeDemo.Run();
            if (mode == "compute-gen") return ComputeShaderDemo.Run();
            if (mode == "mandelbrot") return MandelbrotDemo.Run();
            if (mode == "triangle") return TriangleApp.Run();
            if (mode == "textured") return TexturedApp.Run();
            if (mode == "imgui") return ImGuiApp.Run();
            if (mode == "instanced") return InstancedTrianglesDemo.Run();
            if (mode == "fence-bench") return FenceBenchmarkDemo.Run();
            if (mode == "threed") return ThreeDSceneDemo.Run();
            if (mode == "threed-win") return ThreeDSceneWindow.Run();
            if (mode == "particles") return GpuParticleDemo.Run();
            if (mode == "textured-cube") return TexturedCubeDemo.Run();
            if (mode == "multi-tex-cube") return MultiTextureCubeDemo.Run();
            Console.Error.WriteLine("Usage: dotnet run -- [compute|compute-gen|mandelbrot|triangle|textured|imgui|instanced|fence-bench|threed|threed-win|particles|textured-cube|multi-tex-cube]");
            return 1;
        }
        catch (MetalException ex)
        {
            Console.Error.WriteLine($"❌ Metal error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Unexpected error: {ex}");
            return 3;
        }
    }
}

internal static class ComputeDemo
{
    private const int ElementCount = 1024;
    private const int ThreadsPerGroup = 64; // 与 Multiply.slang 的 [numthreads(64,1,1)] 对齐

    public static int Run()
    {
        // 1) 设备
        using var device = MetalDevice.CreateSystemDefault();
        Console.WriteLine($"Device: {device.Name}  (UMA: {device.HasUnifiedMemory})");

        // 2) 加载 metallib
        using var function = MetalShaderLoader.GetFunction(device, "Multiply", "main");
        using var pso = device.NewComputePipelineState(function);
        Console.WriteLine($"PSO: maxTotalThreadsPerThreadgroup={pso.MaxTotalThreadsPerThreadgroup}, " +
                          $"threadExecutionWidth={pso.ThreadExecutionWidth}");

        // 3) 输入 buffer：填 1.0..ElementCount
        using var buffer = device.NewBuffer(
            (ulong)(ElementCount * sizeof(float)),
            MTLResourceOptions.StorageModeShared);

        Span<float> input = buffer.AsSpan<float>();
        for (int i = 0; i < ElementCount; i++) input[i] = i + 1.0f;

        // 4) 命令编码 + 提交
        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var encoder = cmdbuf.ComputeCommandEncoder())
        {
            encoder.SetComputePipelineState(pso);

            // MSC 输出的 metallib 通过 top-level argument buffer 间接访问资源
            // （详见 docs/slang-reflection-binding-design.md §3.2 与 memory/msc-binding-model.md）。
            // Metal validation 显示 MSC 把 top_level_global_ab 放在 buffer(2)
            // —— 这与 DXMT airconv 的 SM50_BINDING_INDEX_ARGUMENT_TABLE=1 不同；
            // MSC 4.0 实际使用 buffer(2) 留出 buffer(0)/buffer(1) 给 push constants/draw args。
            // reflection 显示 24 字节描述符 (gpuAddress, length, stride)。
            const ulong ArgumentTableBufferIndex = 2;
            encoder.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);

            var uavDesc = new UavDescriptor
            {
                GpuAddress = buffer.GpuAddress,
                Length     = buffer.Length,
                Stride     = sizeof(float),
            };
            encoder.SetBytes(uavDesc, index: ArgumentTableBufferIndex);

            // 1024 个元素 / 每组 64 线程 = 16 个 thread group
            var groups = new WMTSize((ulong)(ElementCount / ThreadsPerGroup), 1, 1);
            var threads = new WMTSize(ThreadsPerGroup, 1, 1);
            encoder.DispatchThreadgroups(groups, threads);
            encoder.EndEncoding();
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        // 5) 错误检查
        using (var err = cmdbuf.Error())
        {
            if (err is not null)
            {
                Console.Error.WriteLine($"❌ GPU error: {err.Description}");
                return 1;
            }
        }

        // 6) 读回并校验
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
        return 0;
    }
}
