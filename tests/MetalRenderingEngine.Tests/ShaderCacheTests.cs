using MetalRenderingEngine.Shader;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// ShaderCache 两级缓存单元测试。
/// </summary>
public class ShaderCacheTests : IDisposable
{
    private readonly string _testCacheDir;
    private readonly ShaderCache _cache;

    public ShaderCacheTests()
    {
        _testCacheDir = Path.Combine(Path.GetTempPath(),
            "ShaderCacheTest_" + Guid.NewGuid().ToString("N")[..8]);
        _cache = new ShaderCache(_testCacheDir);
    }

    public void Dispose()
    {
        _cache.ClearAll();
        try { if (Directory.Exists(_testCacheDir)) Directory.Delete(_testCacheDir, true); } catch { }
    }

    [Fact]
    public void ComputeCacheKey_SameInputs_SameKey()
    {
        var data = "test shader source"u8.ToArray();
        var opts = new ShaderCompileOptions { Stage = ShaderStage.Compute };

        var key1 = ShaderCache.ComputeCacheKey(data, opts);
        var key2 = ShaderCache.ComputeCacheKey(data, opts);

        Assert.Equal(key1, key2);
        Assert.Equal(64, key1.Length);  // SHA-256 hex = 64 chars
    }

    [Fact]
    public void ComputeCacheKey_DifferentInputs_DifferentKey()
    {
        var opts = new ShaderCompileOptions { Stage = ShaderStage.Compute };

        var key1 = ShaderCache.ComputeCacheKey("shader A"u8.ToArray(), opts);
        var key2 = ShaderCache.ComputeCacheKey("shader B"u8.ToArray(), opts);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void ComputeCacheKey_DifferentOptions_DifferentKey()
    {
        var data = "same source"u8.ToArray();

        var key1 = ShaderCache.ComputeCacheKey(data,
            new ShaderCompileOptions { Stage = ShaderStage.Compute });
        var key2 = ShaderCache.ComputeCacheKey(data,
            new ShaderCompileOptions { Stage = ShaderStage.Vertex });

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void TryGet_Empty_ReturnsFalse()
    {
        Assert.False(_cache.TryGet("nonexistent_key", out _));
    }

    [Fact]
    public void Put_Then_TryGet_L1_Hits()
    {
        var key = "test_key_l1";
        var metallib = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var reflection = new byte[] { 0x01, 0x02 };

        var result = new ShaderCompileResult
        {
            MetallibData = metallib,
            ReflectionJson = reflection,
        };

        _cache.Put(key, result);

        Assert.True(_cache.TryGet(key, out var cached));
        Assert.NotNull(cached);
        Assert.True(cached.CacheHit);
        Assert.Equal(metallib, cached.MetallibData);
        Assert.Equal(reflection, cached.ReflectionJson);
    }

    [Fact]
    public void Put_Then_ClearL1_Then_TryGet_L2_Hits()
    {
        var key = "test_key_l2";
        var metallib = new byte[] { 0xCA, 0xFE };

        _cache.Put(key, new ShaderCompileResult { MetallibData = metallib });

        // 清除 L1
        _cache.ClearL1();
        Assert.Equal(0, _cache.L1Count);

        // L2 应仍然命中
        Assert.True(_cache.TryGet(key, out var cached));
        Assert.NotNull(cached);
        Assert.True(cached.CacheHit);
        Assert.Equal(metallib, cached.MetallibData);
    }

    [Fact]
    public void ClearAll_RemovesBothL1AndL2()
    {
        var key = "test_key_clear";
        _cache.Put(key, new ShaderCompileResult { MetallibData = new byte[] { 1 } });

        _cache.ClearAll();

        Assert.False(_cache.TryGet(key, out _));
        Assert.Equal(0, _cache.L1Count);
    }

    [Fact]
    public void L2_FilesExistOnDisk()
    {
        var key = "disk_check_key";
        _cache.Put(key, new ShaderCompileResult
        {
            MetallibData = new byte[] { 0xFF },
            ReflectionJson = new byte[] { 0xAA },
        });

        Assert.True(File.Exists(Path.Combine(_testCacheDir, key + ".metallib")));
        Assert.True(File.Exists(Path.Combine(_testCacheDir, key + ".reflect.json")));
    }

    [Fact]
    public void Put_NullMetallibData_Ignored()
    {
        var key = "null_key";
        _cache.Put(key, new ShaderCompileResult { MetallibData = null });

        Assert.False(_cache.TryGet(key, out _));
    }
}
