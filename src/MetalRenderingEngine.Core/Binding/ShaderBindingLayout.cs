using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Binding;

/// <summary>
/// Phase 10E: 半自动绑定布局基类（类型安全的便利层）。
/// </summary>
/// <remarks>
/// <para><b>定位</b>：蓝图 §10.4 原期望源生成器自动生成 BindingLayout 子类（消费 Slang 反射），
/// 但构建顺序问题（源生成器在 CoreCompile 前跑，反射尚未生成）使其走不通。
/// 本基类改为<b>手写但类型安全</b>的便利层：子类用强类型属性封装 (Type, Slot) 绑定，
/// 调用方无需记 slot 数字，且属性名即语义（如 <c>PerFrame</c>、<c>AlbedoMap</c>）。</para>
/// <para><b>与 <see cref="ResourceTable"/> 的关系</b>：BindingLayout 是 ResourceTable 的封装，
/// 内部持 vert/frag 两个 ResourceTable + 对应反射。子类通过 protected 声明方法注册绑定，
/// <see cref="Apply"/> 一次性 Apply 两个 stage。</para>
/// <para><b>反射来源</b>：构造时从 <see cref="ReflectionLoader"/> 加载预编译 reflect.json
/// （Phase 10D），子类只需传 shader 名（如 "TexturedCube.vert"）。</para>
/// </remarks>
public abstract class ShaderBindingLayout
{
    private readonly ResourceTable _vertTable = new();
    private readonly ResourceTable _fragTable = new();
    private readonly MscReflection? _vertReflection;
    private readonly MscReflection? _fragReflection;

    /// <summary>
    /// 构造绑定布局，从预编译 reflect.json 加载反射。
    /// </summary>
    /// <param name="vertShaderName">vertex shader 名（如 "TexturedCube.vert"，传给 ReflectionLoader）。</param>
    /// <param name="fragShaderName">fragment shader 名。</param>
    /// <param name="hasVertexStage">是否有 vertex stage（纯 fragment/compute shader 传 false）。</param>
    protected ShaderBindingLayout(string? vertShaderName, string? fragShaderName, bool hasVertexStage = true)
    {
        if (hasVertexStage && vertShaderName is not null)
            _vertReflection = ReflectionLoader.Load(vertShaderName);
        if (fragShaderName is not null)
            _fragReflection = ReflectionLoader.Load(fragShaderName);
    }

    // ── 子类声明绑定用的 protected 方法（封装 ResourceTable.Bind*，隐藏 slot）──

    /// <summary>声明 vertex stage 的 buffer 资源（SRV/UAV/CBV）。</summary>
    protected void DeclareVertexBuffer(int slot, MetalBuffer buffer, MscResourceType type)
        => _vertTable.BindBuffer(slot, buffer, type);

    /// <summary>声明 vertex stage 的 texture 资源。</summary>
    protected void DeclareVertexTexture(int slot, MetalTexture texture, float minLodClamp = 0f)
        => _vertTable.BindTexture(slot, texture, minLodClamp);

    /// <summary>声明 fragment stage 的 buffer 资源。</summary>
    protected void DeclareFragmentBuffer(int slot, MetalBuffer buffer, MscResourceType type)
        => _fragTable.BindBuffer(slot, buffer, type);

    /// <summary>声明 fragment stage 的 texture 资源。</summary>
    protected void DeclareFragmentTexture(int slot, MetalTexture texture, float minLodClamp = 0f)
        => _fragTable.BindTexture(slot, texture, minLodClamp);

    /// <summary>声明 fragment stage 的 sampler 资源。</summary>
    protected void DeclareFragmentSampler(int slot, MetalSamplerState sampler, float lodBias = 0f)
        => _fragTable.BindSampler(slot, sampler, lodBias);

    /// <summary>
    /// 应用所有声明的绑定到命令录制器：自动编码描述符堆 + 绑定 buffer(2) + 声明驻留。
    /// 必须在 <c>BeginRenderPass</c>..<c>EndRenderPass</c> 之间调用。
    /// </summary>
    public void Apply(ICommandRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (_vertReflection is not null)
            _vertTable.Apply(recorder, _vertReflection, ShaderStage.Vertex);
        if (_fragReflection is not null)
            _fragTable.Apply(recorder, _fragReflection, ShaderStage.Fragment);
    }
}
