using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MetalRenderingEngine.Shader;

/// <summary>
/// 两级着色器缓存：L1 内存（<see cref="ConcurrentDictionary{TKey,TValue}"/>）+ L2 磁盘（LRU 淘汰）。
///
/// <para>缓存键 = SHA-256(源码字节 + 编译选项序列化)；
/// 缓存值 = metallib 字节 + 可选 reflect.json 字节。</para>
///
/// <para>线程安全：所有公开方法可并发调用。</para>
/// </summary>
public sealed class ShaderCache
{
    /// <summary>L2 磁盘缓存默认目录。</summary>
    private static readonly string s_defaultCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".metal-rendering-engine", "shader-cache");

    /// <summary>L2 磁盘缓存最大总字节数（默认 256 MB）。</summary>
    private const long MaxCacheSizeBytes = 256L * 1024 * 1024;

    /// <summary>L1 内存缓存（按 hash 键）。</summary>
    private readonly ConcurrentDictionary<string, CachedShader> _l1 = new(StringComparer.Ordinal);

    /// <summary>L2 磁盘目录。</summary>
    private readonly string _cacheDir;

    /// <summary>
    /// 创建缓存实例。
    /// </summary>
    /// <param name="cacheDir">L2 磁盘缓存目录；null 使用默认路径。</param>
    public ShaderCache(string? cacheDir = null)
    {
        _cacheDir = cacheDir ?? s_defaultCacheDir;
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// 计算缓存键（SHA-256）。
    /// </summary>
    /// <param name="sourceData">源码字节。</param>
    /// <param name="options">编译选项。</param>
    public static string ComputeCacheKey(byte[] sourceData, ShaderCompileOptions options)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(sourceData, 0, sourceData.Length, null, 0);

        // 将选项序列化为简单字符串参与 hash
        var optionBytes = Encoding.UTF8.GetBytes(
            $"{options.EntryPoint}|{options.Stage}|{options.Profile}|" +
            $"{options.GenerateReflection}|" +
            $"{(options.ExtraSlangcArgs != null ? string.Join(",", options.ExtraSlangcArgs) : "")}|" +
            $"{(options.ExtraMscArgs != null ? string.Join(",", options.ExtraMscArgs) : "")}");
        sha.TransformFinalBlock(optionBytes, 0, optionBytes.Length);

        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>
    /// 尝试从缓存获取。依次检查 L1 → L2。
    /// </summary>
    /// <param name="cacheKey">缓存键。</param>
    /// <param name="result">命中时填充的编译结果。</param>
    /// <returns>true 表示命中。</returns>
    public bool TryGet(string cacheKey, out ShaderCompileResult? result)
    {
        // L1 命中
        if (_l1.TryGetValue(cacheKey, out var cached))
        {
            result = new ShaderCompileResult
            {
                MetallibData = cached.MetallibData,
                ReflectionJson = cached.ReflectionJson,
                ElapsedMilliseconds = 0,
                CacheHit = true,
            };
            return true;
        }

        // L2 命中
        var metallibPath = GetL2MetallibPath(cacheKey);
        var reflectPath = GetL2ReflectPath(cacheKey);

        if (File.Exists(metallibPath))
        {
            var metallibData = File.ReadAllBytes(metallibPath);
            byte[]? reflectionData = File.Exists(reflectPath) ? File.ReadAllBytes(reflectPath) : null;

            // 回填 L1
            _l1[cacheKey] = new CachedShader(metallibData, reflectionData);

            // 更新 L2 访问时间（用于 LRU）
            File.SetLastAccessTimeUtc(metallibPath, DateTime.UtcNow);

            result = new ShaderCompileResult
            {
                MetallibData = metallibData,
                ReflectionJson = reflectionData,
                ElapsedMilliseconds = 0,
                CacheHit = true,
            };
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// 将编译结果写入缓存（L1 + L2 双写）。
    /// </summary>
    /// <param name="cacheKey">缓存键。</param>
    /// <param name="result">编译结果。</param>
    public void Put(string cacheKey, ShaderCompileResult result)
    {
        if (result.MetallibData is null) return;

        // L1
        _l1[cacheKey] = new CachedShader(result.MetallibData, result.ReflectionJson);

        // L2
        var metallibPath = GetL2MetallibPath(cacheKey);
        File.WriteAllBytes(metallibPath, result.MetallibData);

        if (result.ReflectionJson is not null)
        {
            var reflectPath = GetL2ReflectPath(cacheKey);
            File.WriteAllBytes(reflectPath, result.ReflectionJson);
        }

        // LRU 淘汰
        EvictIfNeeded();
    }

    /// <summary>清除 L1 内存缓存。</summary>
    public void ClearL1() => _l1.Clear();

    /// <summary>清除 L2 磁盘缓存。</summary>
    public void ClearL2()
    {
        if (Directory.Exists(_cacheDir))
        {
            foreach (var file in Directory.GetFiles(_cacheDir))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    /// <summary>清除全部缓存（L1 + L2）。</summary>
    public void ClearAll()
    {
        ClearL1();
        ClearL2();
    }

    /// <summary>L1 缓存条目数。</summary>
    public int L1Count => _l1.Count;

    /// <summary>
    /// LRU 淘汰：当磁盘缓存超过 <see cref="MaxCacheSizeBytes"/> 时删除最旧的文件。
    /// </summary>
    private void EvictIfNeeded()
    {
        var files = Directory.GetFiles(_cacheDir);
        if (files.Length == 0) return;

        long totalSize = 0;
        var entries = new List<(string Path, DateTime AccessTime, long Size)>(files.Length);

        foreach (var f in files)
        {
            try
            {
                var info = new FileInfo(f);
                totalSize += info.Length;
                entries.Add((f, info.LastAccessTimeUtc, info.Length));
            }
            catch { }
        }

        if (totalSize <= MaxCacheSizeBytes) return;

        // 按访问时间升序排列（最旧的先删）
        entries.Sort((a, b) => a.AccessTime.CompareTo(b.AccessTime));

        foreach (var (path, _, size) in entries)
        {
            if (totalSize <= MaxCacheSizeBytes) break;
            try
            {
                File.Delete(path);
                totalSize -= size;
            }
            catch { }
        }
    }

    private string GetL2MetallibPath(string cacheKey) => Path.Combine(_cacheDir, cacheKey + ".metallib");
    private string GetL2ReflectPath(string cacheKey) => Path.Combine(_cacheDir, cacheKey + ".reflect.json");

    /// <summary>L1 内存缓存条目。</summary>
    private sealed record CachedShader(byte[] MetallibData, byte[]? ReflectionJson);
}
