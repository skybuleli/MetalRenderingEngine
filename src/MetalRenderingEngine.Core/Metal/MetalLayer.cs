using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// CAMetalLayer 封装；管理可呈现的 Metal 表面。
/// </summary>
public sealed class MetalLayer : MetalObject
{
    public MetalLayer(nuint handle) { SetNativeHandle(handle); }

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

    public nuint TextureHandle => MetalBridge.CAMetalDrawable_texture(Handle);
}
