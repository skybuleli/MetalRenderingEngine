using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering;

/// <summary>
/// 流式构造 <see cref="WMTRenderPassDesc"/>。
/// 用法：<code>var desc = new RenderPassBuilder().Color(tex, clearColor).MsaaColor(msaaTex, drawableTex, clearColor).Depth(depthTex, clear: 1f).Build();</code>
/// </summary>
public sealed class RenderPassBuilder
{
    private WMTRenderPassDesc _desc = new();

    /// <summary>添加颜色附件（Load=Clear, Store=Store）。</summary>
    public RenderPassBuilder Color(MetalTexture texture, WMTClearColor clearColor)
    {
        _desc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = texture.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = clearColor,
        });
        return this;
    }

    /// <summary>添加 MSAA 颜色附件（渲染到 msaaTex，resolve 到 resolveTex）。</summary>
    public RenderPassBuilder MsaaColor(MetalTexture msaaTexture, MetalTexture resolveTexture, WMTClearColor clearColor)
    {
        _desc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = msaaTexture.Handle,
            ResolveTexture = resolveTexture.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.MultisampleResolve,
            ClearColor = clearColor,
        });
        return this;
    }

    /// <summary>添加深度附件（Load=Clear, Store=Store）。</summary>
    public RenderPassBuilder Depth(MetalTexture texture, float clearDepth = 1f)
    {
        _desc.Depth = new WMTRenderPassAttachment
        {
            Texture = texture.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearDepth = clearDepth,
        };
        return this;
    }

    /// <summary>构建不可变的描述符副本。</summary>
    public WMTRenderPassDesc Build() => _desc;
}
