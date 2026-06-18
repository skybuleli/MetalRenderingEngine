using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// TexturePool 测试：覆盖 Rent/归还复用、描述符精确匹配、Retain 安全性、压力循环。
/// 沿用 <see cref="MetalBufferLifecycleTests"/> 的自包含模式。
/// </summary>
public class TexturePoolTests
{
    /// <summary>构造一个 RGBA8Unorm 64x64 的 shader-read texture 描述符。</summary>
    private static WMTTextureInfo MakeInfo(MTLPixelFormat format, int w, int h,
        MTLTextureUsage usage = MTLTextureUsage.ShaderRead)
        => new WMTTextureInfo
        {
            PixelFormat = (int)format,
            TextureType = (int)MTLTextureType.Type2D,
            Width = (ulong)w,
            Height = (ulong)h,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)usage,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };

    /// <summary>首次 Rent 应新建 texture。</summary>
    [Fact]
    public void Rent_CreatesTexture_WhenPoolEmpty()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new TexturePool(device);

        Assert.Equal(0, pool.Count);
        var info = MakeInfo(MTLPixelFormat.R8Unorm, 64, 64);
        var lease = pool.Rent(info);
        try
        {
            Assert.Equal(1, pool.Count);
            Assert.False(lease.Texture.IsInvalid);
            Assert.Equal(64u, lease.Texture.Width);
            Assert.Equal(64u, lease.Texture.Height);
        }
        finally { lease.Dispose(); }
    }

    /// <summary>同描述符归还后再 Rent 返回同 handle（验证复用）。</summary>
    [Fact]
    public void RentReused_AfterReturn_SameHandle()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new TexturePool(device);
        var info = MakeInfo(MTLPixelFormat.R8Unorm, 64, 64);

        nuint h1;
        var lease1 = pool.Rent(info);
        h1 = lease1.Texture.Handle;
        lease1.Dispose();

        var lease2 = pool.Rent(info);
        try
        {
            Assert.Equal(h1, lease2.Texture.Handle);
            Assert.Equal(1, pool.Count);  // 复用
        }
        finally { lease2.Dispose(); }
    }

    /// <summary>不同 pixel format / 尺寸不命中，应新建。</summary>
    [Fact]
    public void Rent_DifferentDescriptor_CreatesNew()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new TexturePool(device);

        var lease1 = pool.Rent(MakeInfo(MTLPixelFormat.R8Unorm, 64, 64));
        lease1.Dispose();

        var lease2 = pool.Rent(MakeInfo(MTLPixelFormat.RGBA8Unorm, 128, 128));
        try
        {
            Assert.True(lease2.Texture.Width == 128);
            Assert.Equal(2, pool.Count);  // 两个不同 key
        }
        finally { lease2.Dispose(); }
    }

    /// <summary>lease.Dispose() 后池仍可用（Retain 语义：master 不被毁）。</summary>
    [Fact]
    public void Rent_DoesNotDestroyPooledObject_WhenLeaseDisposed()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new TexturePool(device);
        var info = MakeInfo(MTLPixelFormat.R8Unorm, 64, 64);

        var lease = pool.Rent(info);
        nuint masterHandle = lease.Texture.Handle;
        lease.Dispose();

        var lease2 = pool.Rent(info);
        try
        {
            Assert.Equal(masterHandle, lease2.Texture.Handle);
            Assert.False(lease2.Texture.IsInvalid);
        }
        finally { lease2.Dispose(); }
    }

    /// <summary>仅 usage 不同也应视作不同 key（新建）。</summary>
    [Fact]
    public void Rent_DifferentUsage_CreatesNew()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new TexturePool(device);

        var lease1 = pool.Rent(MakeInfo(MTLPixelFormat.RGBA8Unorm, 64, 64, MTLTextureUsage.ShaderRead));
        lease1.Dispose();

        var lease2 = pool.Rent(MakeInfo(MTLPixelFormat.RGBA8Unorm, 64, 64, MTLTextureUsage.RenderTarget));
        try
        {
            Assert.Equal(2, pool.Count);
        }
        finally { lease2.Dispose(); }
    }

    /// <summary>压力测试：100 轮 × 10 次并发 Rent/Dispose 混合描述符，不崩溃或泄漏。
    /// 10 个 lease 并发持有，故每轮峰值 10 个 master；轮间应稳定复用（Count 不持续增长）。</summary>
    [Fact]
    public void Stress_TextureRentReturn_NoCrash()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var pool = new TexturePool(device);
        var infos = new[]
        {
            MakeInfo(MTLPixelFormat.R8Unorm, 64, 64),
            MakeInfo(MTLPixelFormat.R8Unorm, 128, 128),
            MakeInfo(MTLPixelFormat.RGBA8Unorm, 64, 64),
        };

        var leases = new TextureLease[10];
        int countAfterFirstRound = 0;
        for (int round = 0; round < 100; round++)
        {
            for (int i = 0; i < leases.Length; i++)
                leases[i] = pool.Rent(infos[i % infos.Length]);
            if (round == 0) countAfterFirstRound = pool.Count;
            for (int i = 0; i < leases.Length; i++)
                leases[i].Dispose();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(countAfterFirstRound, pool.Count);
        Assert.Equal(pool.Count, pool.AvailableCount);
    }

    /// <summary>Dispose 后 Rent 抛 ObjectDisposedException。</summary>
    [Fact]
    public void AfterDispose_Rent_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var pool = new TexturePool(device);
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Rent(MakeInfo(MTLPixelFormat.R8Unorm, 64, 64)));
    }
}
