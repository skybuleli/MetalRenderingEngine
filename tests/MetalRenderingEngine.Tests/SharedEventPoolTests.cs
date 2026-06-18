using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// SharedEventPool + GpuFence 测试。
/// 验证 pool 的 event 复用、signaledValue 单调递增，以及 GpuFence 的两种策略
/// （AsyncCallback 帧间 / BlockingWait 数据依赖）。
/// 沙箱环境（SharedEvent 不可用）自动跳过。
/// </summary>
public class SharedEventPoolTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }
    private const ulong ArgumentTableBufferIndex = 2;
    private static string MetallibPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Multiply.metallib");

    private static SharedEventPool? TryCreatePool(MetalDevice device)
    {
        try { return new SharedEventPool(device, eventCount: 4); }
        catch (MetalException) { return null; }
    }

    /// <summary>池创建后应有指定数量的 event。</summary>
    [Fact]
    public void Create_HasRequestedEventCount()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP] SharedEvent 不可用"); return; }
        Assert.Equal(4, pool.EventCount);
    }

    /// <summary>连续 Acquire 应轮转分配不同 event，value 单调递增。</summary>
    [Fact]
    public void Acquire_RotatesEvents_IncrementsValue()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }

        var slots = new SharedEventSlot[8];
        for (int i = 0; i < 8; i++) slots[i] = pool.Acquire();

        // 8 个 slot 在 4 个 event 上轮转：slot[0] 和 slot[4] 应同 event 但不同 value
        Assert.Equal(slots[0].Event.Handle, slots[4].Event.Handle);
        Assert.NotEqual(slots[0].Value, slots[4].Value);
        Assert.True(slots[4].Value > slots[0].Value);

        foreach (var s in slots) s.Dispose();
    }

    /// <summary>GpuFence AsyncCallback 策略：signal 后异步回调触发。</summary>
    [Fact]
    public void GpuFence_AsyncCallback_FiresAfterSignal()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }
        using var queue = device.NewCommandQueue();

        var fence = GpuFence.Create(pool);
        var fired = new ManualResetEventSlim(false);

        using (var cmdbuf = queue.CommandBuffer())
        {
            fence.Signal(cmdbuf);
            fence.WaitAsync(() => fired.Set());
            cmdbuf.Commit();
        }
        Assert.True(fired.Wait(5000), "异步回调未触发");
    }

    /// <summary>GpuFence BlockingWait 策略：signal 后阻塞等待返回 true。</summary>
    [Fact]
    public void GpuFence_BlockingWait_ReturnsAfterSignal()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }
        using var queue = device.NewCommandQueue();

        var fence = GpuFence.Create(pool);
        using (var cmdbuf = queue.CommandBuffer())
        {
            fence.Signal(cmdbuf);
            cmdbuf.Commit();
        }
        Assert.True(fence.Wait(5000));
    }

    /// <summary>GpuFence 跨 command buffer GpuWait：cb1 signal，cb2 wait 后才执行 compute。</summary>
    [Fact]
    public void GpuFence_GpuWait_BlocksUntilSignal()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = TryCreatePool(device);
        if (pool is null) { Console.WriteLine("[SKIP]"); return; }
        Assert.True(File.Exists(MetallibPath));
        using var queue = device.NewCommandQueue();

        byte[] metallib = File.ReadAllBytes(MetallibPath);
        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("main");
        using var pso = device.NewComputePipelineState(function);
        using var buffer = device.NewBuffer(64, MTLResourceOptions.StorageModeShared);
        buffer.AsSpan<float>()[0] = 1.0f;

        var fence = GpuFence.Create(pool);

        // cb1：signal
        using (var cb1 = queue.CommandBuffer())
        {
            fence.Signal(cb1);
            cb1.Commit();
        }

        // cb2：wait 后 dispatch Multiply
        MetalError? cb2Error;
        using (var cb2 = queue.CommandBuffer())
        {
            fence.GpuWait(cb2);
            using (var enc = cb2.ComputeCommandEncoder())
            {
                enc.SetComputePipelineState(pso);
                enc.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
                enc.SetBytes(new UavDescriptor
                {
                    GpuAddress = buffer.GpuAddress,
                    Length = buffer.Length,
                    Stride = sizeof(float),
                }, ArgumentTableBufferIndex);
                enc.DispatchThreadgroups(new WMTSize(1, 1, 1), new WMTSize(64, 1, 1));
                enc.EndEncoding();
            }
            cb2.Commit();
            cb2.WaitUntilCompleted();
            cb2Error = cb2.Error();
        }

        using (cb2Error) { }
        Assert.Equal(2.0f, buffer.AsSpan<float>()[0]);
    }
}
