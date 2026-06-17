using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// CAMetalLayer 封装；管理可呈现的 Metal 表面。
/// </summary>
public sealed class MetalLayer : MetalObject
{
    internal MetalLayer(nuint handle) { SetNativeHandle(handle); }

    public void SetDevice(MetalDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        MetalBridge.CAMetalLayer_setDevice(Handle, device.Handle);
    }

    public void SetPixelFormat(MTLPixelFormat format)
        => MetalBridge.CAMetalLayer_setPixelFormat(Handle, (int)format);

    public void SetDrawableSize(float width, float height)
        => MetalBridge.CAMetalLayer_setDrawableSize(Handle, width, height);

    public MetalDrawable? NextDrawable()
    {
        nuint h = MetalBridge.CAMetalLayer_nextDrawable(Handle);
        return h == 0 ? null : new MetalDrawable(h);
    }
}

/// <summary>
/// CAMetalDrawable 封装；一帧的可呈现表面。
/// </summary>
public sealed class MetalDrawable : MetalObject
{
    internal MetalDrawable(nuint handle) { SetNativeHandle(handle); }

    /// <summary>
    /// 获取 drawable 关联的 MTLTexture。返回的 MetalTexture 经过 retain，独立于 drawable 生命周期。
    /// </summary>
    public MetalTexture Texture
    {
        get
        {
            nuint h = MetalBridge.CAMetalDrawable_texture(Handle);
            if (h == 0)
                throw new InvalidOperationException("CAMetalDrawable_texture returned null handle.");
            MetalBridge.NSObject_retain(h); // drawable 内部是 autorelease，这里做独立 retain
            return new MetalTexture(h, 0, 0); // width/height 由 MetalTexture 内部从 native 获取
        }
    }
}
