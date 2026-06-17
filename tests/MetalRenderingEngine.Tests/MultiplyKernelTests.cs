using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// 端到端 compute 调度集成测试：复刻 Demo 的 Multiply kernel 走通完整链路。
/// 验证范围：metallib 加载 / pipeline 构建 / argument buffer (buffer 索引 2) /
/// useResource / GPU 地址间接访问 / 命令缓冲提交与等待。
/// </summary>
public class MultiplyKernelTests
{
    /// <summary>MSC 4.0 把 top-level argument buffer 放在 buffer(2)；详见
    /// docs/slang-reflection-binding-design.md §3.5。</summary>
    private const ulong ArgumentTableBufferIndex = 2;

    /// <summary>Structured UAV 描述符布局（24 字节，对应 reflection 的 EltSize）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    private static string MetallibPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Multiply.metallib");

    [Fact]
    public void Multiply_DoublesAllElements()
    {
        Assert.True(File.Exists(MetallibPath),
            $"metallib 不存在：{MetallibPath}（先跑 ./build/compile_shaders.sh）");

        using var device = MetalDevice.CreateSystemDefault();
        byte[] metallib = File.ReadAllBytes(MetallibPath);

        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("main");
        using var pso = device.NewComputePipelineState(function);

        Assert.True(pso.MaxTotalThreadsPerThreadgroup >= 64);
        Assert.True(pso.ThreadExecutionWidth is 32 or 16); // M1 = 32

        const int elementCount = 1024;
        const int threadsPerGroup = 64;

        using var buffer = device.NewBuffer(
            (ulong)(elementCount * sizeof(float)),
            MTLResourceOptions.StorageModeShared);

        Span<float> input = buffer.AsSpan<float>();
        for (int i = 0; i < elementCount; i++) input[i] = i + 1.0f;

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var encoder = cmdbuf.ComputeCommandEncoder())
        {
            encoder.SetComputePipelineState(pso);
            encoder.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);

            var desc = new UavDescriptor
            {
                GpuAddress = buffer.GpuAddress,
                Length = buffer.Length,
                Stride = sizeof(float),
            };
            encoder.SetBytes(desc, ArgumentTableBufferIndex);

            encoder.DispatchThreadgroups(
                new WMTSize((ulong)(elementCount / threadsPerGroup), 1, 1),
                new WMTSize(threadsPerGroup, 1, 1));
            encoder.EndEncoding();
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error())
        {
            Assert.Null(err);
        }
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);

        Span<float> output = buffer.AsSpan<float>();
        for (int i = 0; i < elementCount; i++)
        {
            Assert.Equal((i + 1.0f) * 2.0f, output[i]);
        }
    }

    [Fact]
    public void NewLibrary_FromBogusBytes_ThrowsMetalException()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var bogus = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        Assert.Throws<MetalException>(() => device.NewLibrary(bogus));
    }

    [Fact]
    public void NewFunction_MissingEntry_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        byte[] metallib = File.ReadAllBytes(MetallibPath);
        using var library = device.NewLibrary(metallib);
        Assert.Throws<MetalException>(() => library.NewFunction("nonexistent_entry"));
    }
}
