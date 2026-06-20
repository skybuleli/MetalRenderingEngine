using System.Collections.Concurrent;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Binding;

/// <summary>
/// Phase 10D: 从输出目录 <c>shaders/&lt;name&gt;.reflect.json</c> 加载 MSC 反射。
/// 与 <see cref="MetalRenderingEngine.Metal.MetalShaderLoader"/> 并列协作：
/// MetalShaderLoader 加载 <c>.metallib</c>，ReflectionLoader 加载同名 <c>.reflect.json</c>。
/// 结果按 name 缓存（<see cref="MscReflection"/> 是纯数据对象，缓存安全）。
/// </summary>
/// <remarks>
/// 路径约定：<c>shaders/&lt;name&gt;.reflect.json</c>，<paramref name="name"/> 含或不含
/// <c>.reflect.json</c> 后缀均可，与 MetalShaderLoader 对 <c>.metallib</c> 的处理对称。
/// 手写着色器 name 如 <c>"Multiply"</c>，生成着色器如 <c>"MandelbrotShader.generated"</c>。
/// </remarks>
public static class ReflectionLoader
{
    private static readonly ConcurrentDictionary<string, MscReflection> s_cache
        = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, BindingMetadata> s_bindingCache
        = new(StringComparer.Ordinal);

    private static string GetReflectName(string name)
        => name.EndsWith(".reflect.json", StringComparison.OrdinalIgnoreCase)
            ? name : name + ".reflect.json";

    private static string GetBindingName(string name)
        => name.EndsWith(".bindings.json", StringComparison.OrdinalIgnoreCase)
            ? name : name + ".bindings.json";

    private static string ResolvePath(string name)
        => Path.Combine(AppContext.BaseDirectory, "shaders", GetReflectName(name));

    private static string ResolveBindingPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "shaders", GetBindingName(name));

    /// <summary>
    /// 加载 <c>shaders/&lt;name&gt;.reflect.json</c> 并返回 <see cref="MscReflection"/>。
    /// 按 <paramref name="name"/> 缓存，重复调用返回缓存的同一实例。
    /// </summary>
    /// <param name="name">反射名称（含或不含 .reflect.json 后缀，与传给 MetalShaderLoader 的 name 一致）。</param>
    /// <returns>解析后的 MSC 反射。</returns>
    /// <exception cref="FileNotFoundException">reflect.json 文件不存在。</exception>
    public static MscReflection Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return s_cache.GetOrAdd(name, LoadFromDisk);
    }

    /// <summary>
    /// 尝试加载反射，找不到文件返回 <c>null</c>（不抛异常）。
    /// 适用于某些着色器可能无反射文件（如纯 vertex 无资源）的场景。
    /// </summary>
    public static MscReflection? TryLoad(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (s_cache.TryGetValue(name, out var cached))
            return cached;

        string path = ResolvePath(name);
        if (!File.Exists(path))
            return null;

        var reflection = MscReflectionParser.Parse(File.ReadAllBytes(path));
        s_cache[name] = reflection;
        return reflection;
    }

    /// <summary>
    /// 加载 <c>shaders/&lt;name&gt;.bindings.json</c> 并返回绑定元数据。
    /// bindings.json 是编译后从 MSC reflection 提炼出的稳定运行时绑定表。
    /// </summary>
    public static BindingMetadata LoadBindings(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return s_bindingCache.GetOrAdd(name, LoadBindingsFromDisk);
    }

    /// <summary>尝试加载绑定元数据，找不到文件返回 null。</summary>
    public static BindingMetadata? TryLoadBindings(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (s_bindingCache.TryGetValue(name, out var cached))
            return cached;

        string path = ResolveBindingPath(name);
        if (!File.Exists(path))
            return null;

        var metadata = BindingMetadataParser.Parse(File.ReadAllBytes(path));
        s_bindingCache[name] = metadata;
        return metadata;
    }

    /// <summary>清空反射缓存。</summary>
    public static void ClearCache()
    {
        s_cache.Clear();
        s_bindingCache.Clear();
    }

    private static MscReflection LoadFromDisk(string name)
    {
        string path = ResolvePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"反射文件不存在：{path}", path);
        return MscReflectionParser.Parse(File.ReadAllBytes(path));
    }

    private static BindingMetadata LoadBindingsFromDisk(string name)
    {
        string path = ResolveBindingPath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"绑定元数据文件不存在：{path}", path);
        return BindingMetadataParser.Parse(File.ReadAllBytes(path));
    }
}
