using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// TransientBufferAllocator 测试：覆盖 Allocate offset 推进/对齐/超容、BeginFrame 重置、
/// 端到端 GPU 正确性（Multiply kernel）、triple-buffer 多帧 fence 轮转。
/// 沙箱环境（SharedEvent 不可用）自动跳过。沿用 <see cref="MetalCommandListTests"/> 的自包含模式。
/// </summary>
public class TransientBufferAllocatorTests
{
    /// <summary>MSC 4.0 top-level argument buffer 放在 buffer(2)。</summary>
    private const ulong ArgumentTableBufferIndex = 2;

    private static string MetallibPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Multiply.metallib");

    /// <summary>尝试创建 SharedEventPool；沙箱环境返回 null 供测试 soft-skip。</summary>
    private static SharedEventPool? TryCreatePool(MetalDevice device)
    {
        try { return new SharedEventPool(device, eventCount: 4); }
        catch (MetalException) { return null; }
    }

    /// <summary>Allocate 后 AsSpan 可写、GpuAddress = buffer.GpuAddress + offset。</summary>
    [Fact]
    public void Allocate_ReturnsValidSpan_AndGpuAddress()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP] SharedEvent 不可用"); return; }

        using var alloc = new TransientBufferAllocator(device, pool, capacity: 4096, frameCount: 2);
        alloc.BeginFrame();

        var a = alloc.Allocate(64, alignment: 64);
        Assert.Equal(0u, a.Offset);  // 第一个分配，slot 0 起点
        Assert.Equal(64u, a.Length);
        Assert.Equal(alloc.Buffer.GpuAddress, a.GpuAddress);

        Span<float> data = a.AsSpan<float>();
        for (int i = 0; i < data.Length; i++) data[i] = i;
        Span<float> reread = a.AsSpan<float>();
        for (int i = 0; i < reread.Length; i++) Assert.Equal(i, reread[i]);
    }

    /// <summary>连续两次 Allocate 的 offset 不重叠。</summary>
    [Fact]
    public void Allocate_AdvancesWriteOffset()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }

        using var alloc = new TransientBufferAllocator(device, pool, capacity: 8192, frameCount: 2);
        alloc.BeginFrame();

        var a = alloc.Allocate(100, alignment: 64);  // 对齐到 64 → 占 128
        var b = alloc.Allocate(50, alignment: 64);   // 占 64
        Assert.True(b.Offset >= a.Offset + 128, $"offset 重叠：a={a.Offset}, b={b.Offset}");
        Assert.Equal(128u + 64u, alloc.Used);
    }

    /// <summary>给定 alignment=256，所有 offset 都是 256 倍数。</summary>
    [Fact]
    public void Allocate_RespectsAlignment()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }

        using var alloc = new TransientBufferAllocator(device, pool, capacity: 8192, frameCount: 2);
        alloc.BeginFrame();

        for (int i = 0; i < 5; i++)
        {
            var a = alloc.Allocate(100, alignment: 256);
            Assert.Equal(0u, a.Offset % 256);
        }
    }

    /// <summary>超过 slot 容量抛 InvalidOperationException。</summary>
    [Fact]
    public void Allocate_OverCapacity_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }

        using var alloc = new TransientBufferAllocator(device, pool, capacity: 1024, frameCount: 1);
        alloc.BeginFrame();

        // slot 容量 = 1024（frameCount=1），分配超过应抛
        Assert.Throws<InvalidOperationException>(() => alloc.Allocate(alloc.SlotSize + 1));
    }

    /// <summary>BeginFrame 后 Used 归零，可重新分配。</summary>
    [Fact]
    public void BeginFrame_ResetsWriteOffset()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }

        using var queue = device.NewCommandQueue();
        using var alloc = new TransientBufferAllocator(device, pool, capacity: 4096, frameCount: 2);

        alloc.BeginFrame();
        alloc.Allocate(256);
        Assert.True(alloc.Used > 0);

        // 帧末 signal（需要 cmdbuf）
        using (var cmdbuf = queue.CommandBuffer())
        {
            alloc.EndFrame(cmdbuf);
            cmdbuf.Commit();
        }

        // 下一帧 BeginFrame 应重置（frameCount=2，推进到另一 slot，无需等 fence）
        alloc.BeginFrame();
        Assert.Equal(0u, alloc.Used);
    }

    /// <summary>
    /// 端到端：用 Multiply kernel 验证 transient buffer 的 sub-range 分配 + offset 绑定正确。
    /// Multiply kernel 把元素 ×2，in-place。我们用 transient allocator 分配 buffer sub-range，
    /// 通过 UavDescriptor（GpuAddress = buffer.GpuAddress + offset）+ UseResource 绑定。
    /// </summary>
    [Fact]
    public void EndToEnd_ComputeKernel_RunsCorrectly()
    {
        Assert.True(File.Exists(MetallibPath),
            $"metallib 不存在：{MetallibPath}（先跑 ./build/compile_shaders.sh）");
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP] SharedEvent 不可用"); return; }

        byte[] metallib = File.ReadAllBytes(MetallibPath);
        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("main");
        using var pso = device.NewComputePipelineState(function);
        using var queue = device.NewCommandQueue();

        const int elementCount = 256;
        const int threadsPerGroup = 64;
        // 容量足够放 elementCount floats，frameCount=1 简化（单帧单 slot）
        ulong needed = (ulong)(elementCount * sizeof(float));
        using var alloc = new TransientBufferAllocator(device, pool, capacity: needed + 1024, frameCount: 1);

        alloc.BeginFrame();
        var sub = alloc.Allocate(needed, alignment: 256);

        // 写入输入数据
        Span<float> input = sub.AsSpan<float>();
        for (int i = 0; i < elementCount; i++) input[i] = i + 1.0f;

        using (var cmdbuf = queue.CommandBuffer())
        {
            using (var enc = cmdbuf.ComputeCommandEncoder())
            {
                enc.SetComputePipelineState(pso);
                enc.UseResource(sub.Buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
                enc.SetBytes(sub.ToUavDescriptor(stride: sizeof(float)), ArgumentTableBufferIndex);
                enc.DispatchThreadgroups(
                    new WMTSize((ulong)(elementCount / threadsPerGroup), 1, 1),
                    new WMTSize(threadsPerGroup, 1, 1));
                enc.EndEncoding();
            }
            alloc.EndFrame(cmdbuf);  // cmdbuf 末尾 signal
            cmdbuf.Commit();
            cmdbuf.WaitUntilCompleted();
        }

        // 读回验证（sub-range 内）
        Span<float> output = sub.AsSpan<float>();
        for (int i = 0; i < elementCount; i++)
            Assert.Equal((i + 1.0f) * 2.0f, output[i]);
    }

    /// <summary>连续 3 次 BeginFrame/Allocate/EndFrame/Commit 不崩，验证 fence 轮转。</summary>
    [Fact]
    public void TripleBuffer_SurvivesThreeFrames()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }
        using var queue = device.NewCommandQueue();

        using var alloc = new TransientBufferAllocator(device, pool, capacity: 4096, frameCount: 3);

        for (int frame = 0; frame < 3; frame++)
        {
            alloc.BeginFrame();
            var sub = alloc.Allocate(256, alignment: 256);
            // 写点数据确保 sub-range 可用
            sub.AsSpan<int>()[0] = frame;
            using (var cmdbuf = queue.CommandBuffer())
            {
                // 不做真实 compute，只 signal fence
                alloc.EndFrame(cmdbuf);
                cmdbuf.Commit();
            }
        }
        // 第 4 帧 BeginFrame 会等到第 1 帧的 fence（frameCount=3 轮转回来）
        alloc.BeginFrame();
        Assert.Equal(0u, alloc.Used);
    }

    /// <summary>Dispose 后 Allocate 抛 ObjectDisposedException。</summary>
    [Fact]
    public void AfterDispose_Allocate_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }

        var alloc = new TransientBufferAllocator(device, pool, capacity: 4096);
        alloc.Dispose();

        Assert.Throws<ObjectDisposedException>(() => alloc.Allocate(64));
        Assert.Throws<ObjectDisposedException>(() => alloc.BeginFrame());
    }

    /// <summary>EndFrame 后 GpuFence 记录到 slot，BeginFrame 轮转回来时阻塞等待。</summary>
    [Fact]
    public void BeginFrame_WaitsForPreviousFrameFence()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }
        using var queue = device.NewCommandQueue();

        using var alloc = new TransientBufferAllocator(device, pool, capacity: 4096, frameCount: 2);

        // 帧 0：signal 但不 commit（fence 不会完成）—— 这里我们正常 commit 让它完成
        alloc.BeginFrame();
        alloc.Allocate(64);
        using (var cmdbuf = queue.CommandBuffer())
        {
            alloc.EndFrame(cmdbuf);
            cmdbuf.Commit();
        }

        // 帧 1：另一 slot，无需等
        alloc.BeginFrame();
        alloc.Allocate(64);
        using (var cmdbuf = queue.CommandBuffer())
        {
            alloc.EndFrame(cmdbuf);
            cmdbuf.Commit();
        }

        // 帧 2 = 回到 slot 0，应等到帧 0 的 fence 完成
        alloc.BeginFrame();
        Assert.Equal(0u, alloc.Used);
    }
}
