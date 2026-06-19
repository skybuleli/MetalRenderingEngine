using System.Diagnostics;

namespace MetalRenderingEngine.Shader;

/// <summary>
/// 带两级缓存的着色器编译器。装饰 <see cref="IShaderCompiler"/> 实现，
/// 在编译前查缓存、编译后写缓存。
///
/// <para>流程：cache key = SHA-256(source + options) → L1 → L2 → 实际编译 → 双写。</para>
/// </summary>
public sealed class CachingShaderCompiler : IShaderCompiler
{
    private readonly IShaderCompiler _inner;
    private readonly ShaderCache _cache;

    public CachingShaderCompiler(IShaderCompiler inner, ShaderCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <inheritdoc/>
    public ShaderFormat SourceFormat => _inner.SourceFormat;

    /// <inheritdoc/>
    public ShaderCompileResult Compile(string sourcePath, ShaderCompileOptions options)
    {
        var sourceData = File.ReadAllBytes(sourcePath);
        var cacheKey = ShaderCache.ComputeCacheKey(sourceData, options);

        if (_cache.TryGet(cacheKey, out var cached))
            return cached!;

        var result = _inner.Compile(sourcePath, options);
        _cache.Put(cacheKey, result);
        return result;
    }

    /// <inheritdoc/>
    public ShaderCompileResult CompileFromSource(byte[] sourceCode, string sourceName, ShaderCompileOptions options)
    {
        var cacheKey = ShaderCache.ComputeCacheKey(sourceCode, options);

        if (_cache.TryGet(cacheKey, out var cached))
            return cached!;

        var sw = Stopwatch.StartNew();
        var result = _inner.CompileFromSource(sourceCode, sourceName, options);
        sw.Stop();

        // 用实际编译时间覆盖（缓存命中时为 0）
        result = new ShaderCompileResult
        {
            MetallibData = result.MetallibData,
            ReflectionJson = result.ReflectionJson,
            ElapsedMilliseconds = sw.Elapsed.TotalMilliseconds,
            CacheHit = false,
        };

        _cache.Put(cacheKey, result);
        return result;
    }
}
