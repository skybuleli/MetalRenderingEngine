using System.Runtime.CompilerServices;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// Phase 6 资源池：MTLTexture 池。复用同样格式的 render target / staging texture，
/// 避免每帧重建（尤其 ImGui 字体图集、offscreen RT）。
///
/// 设计要点：
/// 1. 缓存 key = <see cref="TextureKey"/>（pixel format / type / 尺寸 / mip / sample / usage / options）。
///    <see cref="Rent"/> 精确匹配 key：命中栈则复用 master，否则 <see cref="MetalDevice.NewTexture"/> 新建。
/// 2. <b>Rent 时 Retain</b>（与 <see cref="BufferPool"/>、<see cref="MetalShaderLoader"/> 一致）：
///    池持一份 master 包装器（native +1），每次 Rent 对 master 调 Retain（+1）并构造借用包装器
///    交给调用方。归还时借用包装器 Dispose 释放 +1，master 复用。调用方误 Dispose 不影响池内 master。
/// 3. 不线程安全（与 <see cref="MetalCommandList"/> 一致）。
/// </summary>
public sealed class TexturePool : IDisposable
{
    private readonly MetalDevice _device;
    private readonly Dictionary<TextureKey, Stack<MetalTexture>> _cache = new();
    private readonly List<TextureKey> _keys = new();  // 用于 Dispose 时遍历
    private int _totalCreated;
    private bool _disposed;

    /// <summary>
    /// 创建池。
    /// </summary>
    /// <param name="device">Metal 设备（池持有其引用，不接管所有权）。</param>
    public TexturePool(MetalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>池内 master texture 总数（含空闲与已租出）。</summary>
    public int Count => _totalCreated;

    /// <summary>当前空闲可租数。</summary>
    public int AvailableCount => _cache.Values.Sum(s => s.Count);

    /// <summary>
    /// 租借一个匹配 <paramref name="info"/> 描述的 texture。命中则复用 master，否则新建。
    /// 返回的 <see cref="TextureLease"/> Dispose 即归还。
    /// </summary>
    public TextureLease Rent(WMTTextureInfo info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new TextureKey(in info);

        if (!_cache.TryGetValue(key, out var stack))
        {
            stack = new Stack<MetalTexture>();
            _cache[key] = stack;
            _keys.Add(key);
        }

        MetalTexture master;
        if (stack.Count > 0)
        {
            master = stack.Pop();
        }
        else
        {
            master = _device.NewTexture(in info);
            _totalCreated++;
        }

        master.Retain();
        var leaseWrapper = new MetalTexture(master.Handle, master.Width, master.Height, master.PixelFormat);
        return new TextureLease(this, leaseWrapper, master, key);
    }

    /// <summary>释放所有空闲 master（已租出的不动）。</summary>
    public void Trim()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var key in _keys)
        {
            if (!_cache.TryGetValue(key, out var stack)) continue;
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                _totalCreated--;
                t.Dispose();
            }
        }
    }

    /// <summary>归还 master（由 <see cref="TextureLease.Dispose"/> 调用）。</summary>
    internal void Return(MetalTexture master, TextureKey key)
    {
        if (_disposed)
        {
            if (!master.IsInvalid) master.Dispose();
            return;
        }
        if (master.IsInvalid)
        {
            _totalCreated--;
            return;
        }
        if (!_cache.TryGetValue(key, out var stack))
        {
            stack = new Stack<MetalTexture>();
            _cache[key] = stack;
            _keys.Add(key);
        }
        stack.Push(master);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var key in _keys)
        {
            if (!_cache.TryGetValue(key, out var stack)) continue;
            while (stack.Count > 0)
            {
                var t = stack.Pop();
                t.Dispose();
                _totalCreated--;
            }
        }
        _cache.Clear();
    }
}

/// <summary>
/// 从 <see cref="TexturePool"/> 租借的 texture 句柄。Dispose 即归还到池。
/// 暴露的 <see cref="Texture"/> 是借用包装器；调用方误 Dispose 不影响池内 master。
/// </summary>
public sealed class TextureLease : IDisposable
{
    private readonly TexturePool _pool;
    private readonly MetalTexture _master;
    private readonly TextureKey _key;
    private MetalTexture? _leaseTexture;
    private bool _disposed;

    internal TextureLease(TexturePool pool, MetalTexture leaseTexture, MetalTexture master, TextureKey key)
    {
        _pool = pool;
        _leaseTexture = leaseTexture;
        _master = master;
        _key = key;
    }

    /// <summary>借用的 MTLTexture 包装器。Dispose 后访问抛 <see cref="ObjectDisposedException"/>。</summary>
    public MetalTexture Texture
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _leaseTexture!;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _leaseTexture!.Dispose();  // 释放借用包装器的 +1
        _leaseTexture = null;
        _pool.Return(_master, _key);  // master 归还到池
    }
}

/// <summary>
/// <see cref="TexturePool"/> 的缓存 key：texture 描述符的所有可区分字段。
/// 值相等 = 可互换复用。
/// </summary>
internal readonly record struct TextureKey(
    int PixelFormat,
    int TextureType,
    ulong Width,
    ulong Height,
    int MipmapLevels,
    int SampleCount,
    int Usage,
    int Options)
{
    internal TextureKey(in WMTTextureInfo info)
        : this(info.PixelFormat, info.TextureType, info.Width, info.Height,
               info.MipmapLevels, info.SampleCount, info.Usage, info.Options) { }
}
