using MetalRenderingEngine.Binding;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 10E: TexturedCube shader 的半自动绑定布局（手写子类）。
/// </summary>
/// <remarks>
/// <para>用强类型属性封装 (Type, Slot) 绑定，调用方无需记 slot 数字：</para>
/// <code>
/// var layout = new TexturedCubeBindingLayout();
/// layout.PerFrame = perFrameBuf;   // vert SRV slot=0
/// layout.ColorTex = cubeTex;       // frag SRV slot=0
/// layout.LinearSampler = sampler;  // frag Sampler slot=0
/// layout.Apply(recorder);          // 自动编码 + 绑定 buffer(2) + UseResource
/// </code>
/// <para>slot 来源：TexturedCube.vert/frag 的 MSC 反射（见 reflect.json）。
/// 若 shader 改了 register slot，只需改这里的声明，调用方代码不变。</para>
/// </remarks>
internal sealed class TexturedCubeBindingLayout : ShaderBindingLayout
{
    public TexturedCubeBindingLayout()
        : base(vertShaderName: "TexturedCube.vert", fragShaderName: "TexturedCube.frag")
    {
    }

    /// <summary>vertex stage 的 PerFrame StructuredBuffer（SRV slot=0）。</summary>
    public MetalBuffer PerFrame
    {
        set => DeclareVertexBuffer(slot: 0, value, MscResourceType.Srv);
    }

    /// <summary>fragment stage 的 colorTex Texture2D（SRV slot=0）。</summary>
    public MetalTexture ColorTex
    {
        set => DeclareFragmentTexture(slot: 0, value);
    }

    /// <summary>fragment stage 的 SamplerState（Sampler slot=0）。</summary>
    public MetalSamplerState LinearSampler
    {
        set => DeclareFragmentSampler(slot: 0, value);
    }
}
