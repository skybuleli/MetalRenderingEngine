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

    /// <summary>添加指定索引的颜色附件（Load=Clear, Store=Store）。</summary>
    public RenderPassBuilder ColorAt(int index, MetalTexture texture, WMTClearColor clearColor)
    {
        _desc.SetColorAt(index, new WMTRenderPassAttachment
        {
            Texture = texture.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = clearColor,
        });
        return this;
    }

    /// <summary>添加颜色附件（Load=Clear, Store=Store）。</summary>
    public RenderPassBuilder Color(MetalTexture texture, WMTClearColor clearColor)
    {
        return ColorAt(0, texture, clearColor);
    }

    /// <summary>添加指定索引的 MSAA 颜色附件；可选 resolve 目标。</summary>
    public RenderPassBuilder MsaaColorAt(int index, MetalTexture msaaTexture, MetalTexture? resolveTexture, WMTClearColor clearColor)
    {
        _desc.SetColorAt(index, new WMTRenderPassAttachment
        {
            Texture = msaaTexture.Handle,
            ResolveTexture = resolveTexture?.Handle ?? 0,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = resolveTexture is null
                ? (int)MTLStoreAction.Store
                : (int)MTLStoreAction.MultisampleResolve,
            ClearColor = clearColor,
        });
        return this;
    }

    /// <summary>添加 MSAA 颜色附件（渲染到 msaaTex，resolve 到 resolveTex）。</summary>
    public RenderPassBuilder MsaaColor(MetalTexture msaaTexture, MetalTexture resolveTexture, WMTClearColor clearColor)
    {
        return MsaaColorAt(0, msaaTexture, resolveTexture, clearColor);
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
