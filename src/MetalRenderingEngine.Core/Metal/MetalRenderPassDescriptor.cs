using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLRenderPassDescriptor 封装（Phase 3.5：用于 ImGui Metal 后端）。
/// </summary>
public sealed class MetalRenderPassDescriptor : IDisposable
{
    internal MetalRenderPassDescriptor(nuint handle) { SetNativeHandle(handle); }

    /// <summary>为指定纹理创建 render pass descriptor（Load/Store 单 color attachment）。</summary>
    public static MetalRenderPassDescriptor CreateForTexture(MetalDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        nuint h = MetalBridge.MTLRenderPassDescriptor_createForTexture(drawable.Texture.Handle);
        if (h == 0) throw new MetalException("Failed to create MTLRenderPassDescriptor.");
        return new MetalRenderPassDescriptor(h);
    }

    public void Dispose()
    {
        if (!IsInvalid)
        {
            MetalBridge.MTLRenderPassDescriptor_release(Handle);
            SetNativeHandle(0);
        }
    }

    /// <summary>底层 MTLRenderPassDescriptor 原生句柄（供 ImGui 等互操作场景使用）。</summary>
    public nuint Handle { get; private set; }

    private bool IsInvalid => Handle == 0;

    private void SetNativeHandle(nuint handle) => Handle = handle;
}
