using MetalRenderingEngine.Binding;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 10E Demo: MultiTextureCube shader 的半自动绑定布局（4 资源）。
/// </summary>
/// <remarks>
/// <para>对比 TexturedCubeBindingLayout（3 资源），这里多一张 DetailMap，体现多资源封装价值。
/// 调用方用 4 个语义属性绑定，无需记 slot：</para>
/// <code>
/// var layout = new MultiTextureCubeBindingLayout
/// {
///     PerFrame = perFrameBuf,      // vert SRV slot=0
///     AlbedoMap = albedoTex,       // frag SRV slot=0
///     DetailMap = detailTex,       // frag SRV slot=1
///     LinearSampler = sampler,     // frag Sampler slot=0
/// };
/// layout.Apply(recorder);          // 自动编码 + 绑定 buffer(2) + UseResource
/// </code>
/// <para>若用 ResourceTable（Phase 10C）写同样绑定，需手记 4 个 slot + 分 vert/frag 两表 +
/// 手动加载 2 个反射 + 分两次 Apply——资源越多，BindingLayout 的封装优势越明显。</para>
/// </remarks>
internal sealed class MultiTextureCubeBindingLayout : ShaderBindingLayout
{
    public MultiTextureCubeBindingLayout()
        : base("MultiTextureCube.vert", "MultiTextureCube.frag")
    {
    }

    /// <summary>vertex stage 的 PerFrame StructuredBuffer（SRV slot=0）。</summary>
    public MetalBuffer PerFrame
    {
        set => DeclareVertexBuffer(slot: 0, value, MscResourceType.Srv);
    }

    /// <summary>fragment stage 的 AlbedoMap Texture2D（SRV slot=0）。</summary>
    public MetalTexture AlbedoMap
    {
        set => DeclareFragmentTexture(slot: 0, value);
    }

    /// <summary>fragment stage 的 DetailMap Texture2D（SRV slot=1）。</summary>
    public MetalTexture DetailMap
    {
        set => DeclareFragmentTexture(slot: 1, value);
    }

    /// <summary>fragment stage 的 SamplerState（Sampler slot=0）。</summary>
    public MetalSamplerState LinearSampler
    {
        set => DeclareFragmentSampler(slot: 0, value);
    }
}
