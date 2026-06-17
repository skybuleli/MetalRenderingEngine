using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// 着色器加载工具类。从输出目录 <c>shaders/</c> 加载 .metallib，
/// 缓存 <see cref="MetalLibrary"/> 避免重复文件 I/O。
/// </summary>
public static class MetalShaderLoader
{
    private static readonly ConcurrentDictionary<string, (MetalLibrary Library, string Path)> s_cache
        = new(StringComparer.Ordinal);

    private static string GetMetallibName(string name)
        => name.EndsWith(".metallib", StringComparison.OrdinalIgnoreCase) ? name : name + ".metallib";

    private static string ResolvePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "shaders", GetMetallibName(name));

    /// <summary>
    /// 加载 <c>shaders/&lt;name&gt;.metallib</c> 并返回 <see cref="MetalLibrary"/>。
    /// 结果按 <paramref name="name"/> 缓存，重复调用返回缓存的库。
    /// </summary>
    /// <param name="device">Metal 设备。</param>
    /// <param name="name">库名称（含或不含 .metallib 后缀）。</param>
    /// <returns>缓存的 MetalLibrary。调用方应在不再使用时 Dispose。
    /// 注意：缓存的库引用计数通过 SafeHandle 管理，重复调用不会泄漏。</returns>
    /// <exception cref="FileNotFoundException">.metallib 文件不存在。</exception>
    public static MetalLibrary Load(MetalDevice device, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // 缓存命中直接返回（增加引用计数）
        if (s_cache.TryGetValue(name, out var entry) && !entry.Library.IsInvalid)
        {
            entry.Library.Retain();
            return entry.Library;
        }

        // 从文件加载
        string path = ResolvePath(name);
        byte[] data = File.ReadAllBytes(path);
        var lib = device.NewLibrary(data);

        // 缓存（替换旧条目前先释放旧的，避免 SafeHandle 泄漏）
        if (s_cache.TryGetValue(name, out var oldEntry) && !oldEntry.Library.IsInvalid)
        {
            // 注意：缓存的库可能被外部持有（通过 Retain），此处 Dispose 仅移除缓存自身的引用
            oldEntry.Library.Dispose();
        }
        s_cache[name] = (lib, path);

        // 返回时增加一次引用（调用方负责 Dispose）
        lib.Retain();
        return lib;
    }

    /// <summary>
    /// 一站式加载 .metallib 并提取指定入口函数。
    /// </summary>
    /// <param name="device">Metal 设备。</param>
    /// <param name="name">库名称。</param>
    /// <param name="entry">函数入口名称。</param>
    /// <returns>MetalFunction。调用方负责 Dispose。</returns>
    public static MetalFunction GetFunction(MetalDevice device, string name, string entry)
    {
        var lib = Load(device, name);
        try
        {
            return lib.NewFunction(entry);
        }
        catch
        {
            lib.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 清除缓存，释放所有缓存的 Library。
    /// </summary>
    public static void ClearCache()
    {
        foreach (var kv in s_cache)
        {
            if (!kv.Value.Library.IsInvalid)
                kv.Value.Library.Dispose();
        }
        s_cache.Clear();
    }
}
