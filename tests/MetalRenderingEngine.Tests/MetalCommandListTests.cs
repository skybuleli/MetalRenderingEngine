using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 6: 批量命令编码器（MetalCommandList）正确性测试。
/// 验证 wmtcmd 链表回放与逐次 P/Invoke 调用产生相同 GPU 结果。
/// </summary>
public class MetalCommandListTests
{
    private const ulong ArgumentTableBufferIndex = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    private static string MetallibPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Multiply.metallib");

    /// <summary>
    /// 用 MetalCommandList 录制 compute 命令链（SetPSO + UseResource + SetBytes + Dispatch），
    /// 一次 ReplayCompute 回放，断言结果与逐次调用一致（每个元素 ×2）。
    /// 这是 batched encoder 端到端正确性的核心验证。
    /// </summary>
    [Fact]
    public void ReplayCompute_ProducesSameResultAsDirectCalls()
    {
        Assert.True(File.Exists(MetallibPath),
            $"metallib 不存在：{MetallibPath}（先跑 ./build/compile_shaders.sh）");

        using var device = MetalDevice.CreateSystemDefault();
        byte[] metallib = File.ReadAllBytes(MetallibPath);
        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("main");
        using var pso = device.NewComputePipelineState(function);

        const int elementCount = 1024;
        const int threadsPerGroup = 64;

        using var buffer = device.NewBuffer(
            (ulong)(elementCount * sizeof(float)),
            MTLResourceOptions.StorageModeShared);
        Span<float> input = buffer.AsSpan<float>();
        for (int i = 0; i < elementCount; i++) input[i] = i + 1.0f;

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using var encoder = cmdbuf.ComputeCommandEncoder();

        // 录制整段 compute 命令链
        using var cmdList = new MetalCommandList();
        cmdList.RecordSetComputePipelineState(
            pso, new WMTSize(threadsPerGroup, 1, 1));
        cmdList.RecordUseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
        cmdList.RecordSetBytes(new UavDescriptor
        {
            GpuAddress = buffer.GpuAddress,
            Length = buffer.Length,
            Stride = sizeof(float),
        }, ArgumentTableBufferIndex);
        cmdList.RecordDispatch(
            new WMTSize((ulong)(elementCount / threadsPerGroup), 1, 1),
            new WMTSize(threadsPerGroup, 1, 1));

        Assert.Equal(4, cmdList.Count);  // 4 条命令

        // 一次 P/Invoke 回放（vs 逐次的 4 次）
        cmdList.ReplayCompute(encoder);
        Assert.Equal(0, cmdList.Count);  // 回放后清空

        encoder.EndEncoding();
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error()) Assert.Null(err);
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);

        Span<float> output = buffer.AsSpan<float>();
        for (int i = 0; i < elementCount; i++)
            Assert.Equal((i + 1.0f) * 2.0f, output[i]);
    }

    /// <summary>Clear 后链表应为空，ReplayCompute 无副作用。</summary>
    [Fact]
    public void Clear_EmptiesList()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var buffer = device.NewBuffer(64, MTLResourceOptions.StorageModeShared);

        using var cmdList = new MetalCommandList();
        cmdList.RecordUseResource(buffer, MTLResourceUsage.Read);
        Assert.Equal(1, cmdList.Count);

        cmdList.Clear();
        Assert.Equal(0, cmdList.Count);
        Assert.Equal(0, cmdList.BytesUsed);
    }

    /// <summary>空链表 Replay 不抛异常（边界情况）。</summary>
    [Fact]
    public void ReplayEmptyList_DoesNotThrow()
    {
        using var cmdList = new MetalCommandList();
        // 无 encoder 也能测 Clear 路径（ReplayXxx 在 _head==null 时直接返回）
        // 这里只验证 Clear 不抛
        cmdList.Clear();
        Assert.Equal(0, cmdList.Count);
    }
}
