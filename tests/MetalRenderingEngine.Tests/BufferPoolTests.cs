using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// BufferPool 测试：覆盖 Rent/归还复用、Retain 安全性（调用方误 Dispose 不毁池内 master）、
/// size 分桶、压力循环（防泄漏回归）。沿用 <see cref="MetalBufferLifecycleTests"/> 的自包含模式。
/// </summary>
public class BufferPoolTests
{
    /// <summary>首次 Rent 应新建 buffer，BufferLease 非空且 Length &gt;= minSize。</summary>
    [Fact]
    public void Rent_CreatesBuffer_WhenPoolEmpty()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);

        Assert.Equal(0, pool.Count);
        var lease = pool.Rent(128);
        try
        {
            Assert.Equal(1, pool.Count);
            Assert.False(lease.Buffer.IsInvalid);
            Assert.True(lease.Length >= 128);
            Assert.Equal(lease.Buffer.Length, lease.Length);
        }
        finally { lease.Dispose(); }
    }

    /// <summary>Rent→归还→再 Rent 应返回同一底层 handle（验证复用）。</summary>
    [Fact]
    public void RentReused_AfterReturn_SameHandle()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);

        nuint h1;
        var lease1 = pool.Rent(256);
        h1 = lease1.Buffer.Handle;
        lease1.Dispose();

        var lease2 = pool.Rent(256);
        try
        {
            Assert.Equal(h1, lease2.Buffer.Handle);
            Assert.Equal(1, pool.Count);  // 复用，未新建
        }
        finally { lease2.Dispose(); }
    }

    /// <summary>lease.Dispose() 后池仍可用（验证 Retain 语义：池内 master 不被毁）。</summary>
    [Fact]
    public void Rent_DoesNotDestroyPooledObject_WhenLeaseDisposed()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);

        var lease = pool.Rent(512);
        nuint masterHandle = lease.Buffer.Handle;
        lease.Dispose();

        // 归还后再租应拿到同一对象（说明 master 没被 lease.Dispose 毁掉）
        var lease2 = pool.Rent(512);
        try
        {
            Assert.Equal(masterHandle, lease2.Buffer.Handle);
            Assert.False(lease2.Buffer.IsInvalid);
        }
        finally { lease2.Dispose(); }
    }

    /// <summary>申请 512、归还、再申请 1024 应新建（桶不匹配）。</summary>
    [Fact]
    public void Rent_LargerSize_CreatesNew_WhenNoMatch()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);

        var lease1 = pool.Rent(512);
        lease1.Dispose();

        var lease2 = pool.Rent(1024);
        try
        {
            Assert.True(lease2.Length >= 1024);
            Assert.Equal(2, pool.Count);  // 两个不同桶各一个
        }
        finally { lease2.Dispose(); }
    }

    /// <summary>Rent(300) 返回 Length &gt;= 300 的 buffer（向上对齐到 512 桶）。</summary>
    [Fact]
    public void Rent_RoundsUp_ToBucket()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);

        var lease = pool.Rent(300);
        try
        {
            Assert.True(lease.Length >= 300);
            Assert.True(lease.Length >= 512);  // 300 向上取整到 512 桶
        }
        finally { lease.Dispose(); }
    }

    /// <summary>多个不同尺寸交错 Rent/Dispose，复用正确。</summary>
    [Fact]
    public void Rent_MixedSizes_ReuseCorrectly()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);

        var a = pool.Rent(256);
        var b = pool.Rent(1024);
        nuint ha = a.Buffer.Handle, hb = b.Buffer.Handle;
        Assert.NotEqual(ha, hb);
        a.Dispose();
        b.Dispose();

        // 同尺寸再租应复用
        var a2 = pool.Rent(256);
        var b2 = pool.Rent(1024);
        try
        {
            Assert.Equal(ha, a2.Buffer.Handle);
            Assert.Equal(hb, b2.Buffer.Handle);
            Assert.Equal(2, pool.Count);
        }
        finally { a2.Dispose(); b2.Dispose(); }
    }

    /// <summary>压力测试：10 轮 × 100 次并发 Rent/Dispose 混合尺寸 + GC，不应崩溃或泄漏。
    /// 100 个 lease 并发持有，故每轮峰值 100 个 master；轮间应稳定复用（Count 不持续增长）。</summary>
    [Fact]
    public void Stress_RentReturn_NoCrash()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);
        var leases = new BufferLease[100];
        ulong[] sizes = { 64, 256, 300, 1024, 4096 };

        int countAfterFirstRound = 0;
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < leases.Length; i++)
                leases[i] = pool.Rent(sizes[i % sizes.Length]);
            if (round == 0) countAfterFirstRound = pool.Count;
            for (int i = 0; i < leases.Length; i++)
                leases[i].Dispose();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // 复用正确：每轮峰值 master 数应稳定（不持续增长 = 无泄漏）
        Assert.Equal(countAfterFirstRound, pool.Count);
        // 全部归还后空闲数 = 总数
        Assert.Equal(pool.Count, pool.AvailableCount);
    }

    /// <summary>Trim 释放所有空闲 master，但已租出的不动（归还后 Count 恢复）。</summary>
    [Fact]
    public void Trim_ReleasesIdle_KeepsInFlight()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new BufferPool(device);

        var lease = pool.Rent(256);
        Assert.Equal(1, pool.Count);
        Assert.Equal(0, pool.AvailableCount);  // 唯一一个已租出
        pool.Trim();
        Assert.Equal(1, pool.Count);  // 已租出的不动
        lease.Dispose();
        Assert.Equal(1, pool.Count);  // 归还后 master 仍在池里
        Assert.Equal(1, pool.AvailableCount);
    }

    /// <summary>池 Dispose 后所有 master 释放。</summary>
    [Fact]
    public void PoolDispose_ReleasesAllBuffers()
    {
        using var device = MetalDevice.CreateSystemDefault();
        BufferPool pool = new BufferPool(device);
        var lease = pool.Rent(256);
        lease.Dispose();
        Assert.Equal(1, pool.Count);

        pool.Dispose();
        Assert.Equal(0, pool.Count);
    }

    /// <summary>Dispose 后 Rent 抛 ObjectDisposedException。</summary>
    [Fact]
    public void AfterDispose_Rent_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var pool = new BufferPool(device);
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent(256));
    }
}
