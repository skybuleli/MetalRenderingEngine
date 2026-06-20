using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Binding;

/// <summary>
/// 资源绑定条目（按 (ResourceType, Slot) 索引存储）。
/// </summary>
public readonly struct ResourceSlotBinding
{
    public MscResourceType Type { get; init; }
    public MetalBuffer? Buffer { get; init; }
    public MetalTexture? Texture { get; init; }
    public MetalSamplerState? Sampler { get; init; }
    public float MinLodClamp { get; init; }
    public float LodBias { get; init; }
}

/// <summary>
/// Phase 10C: 按 (ResourceType, Slot) 绑定资源，Apply 时自动编码 + 绑定到 buffer(2) + 声明驻留。
/// 一个 ResourceTable 对应一个 shader stage 的反射布局。
/// Apply 必须在 <c>BeginRenderPass</c>..<c>EndRenderPass</c> 之间调用。
/// </summary>
/// <remarks>
/// MSC 反射的 <c>TopLevelArgumentBuffer[i].Name</c> 为空，故按 (Type, Slot) 绑定而非按名字。
/// 绑定目标固定为 <c>buffer(2)</c>（MSC 的 <c>top_level_global_ab</c> / <c>kIRArgumentBufferBindPoint</c>）。
/// </remarks>
public sealed class ResourceTable
{
    /// <summary>MSC 顶层 argument buffer 的固定绑定索引。</summary>
    public const ulong ArgumentBufferBindPoint = 2;

    private readonly Dictionary<(MscResourceType, int), ResourceSlotBinding> _bindings = new();

    /// <summary>绑定 buffer 资源（SRV StructuredBuffer / UAV RWStructuredBuffer / CBV ConstantBuffer）。</summary>
    /// <param name="slot">资源的 register slot（与反射 <see cref="MscArgumentBufferEntry.Slot"/> 对应）。</param>
    /// <param name="buffer">目标 buffer。</param>
    /// <param name="type">资源类型（Srv/Uav/Cbv，必须与反射一致）。</param>
    public void BindBuffer(int slot, MetalBuffer buffer, MscResourceType type)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (type is not (MscResourceType.Srv or MscResourceType.Uav or MscResourceType.Cbv))
            throw new ArgumentException($"BindBuffer 的 type 必须是 Srv/Uav/Cbv，当前为 {type}。", nameof(type));
        _bindings[(type, slot)] = new ResourceSlotBinding { Type = type, Buffer = buffer };
    }

    /// <summary>绑定 texture 资源（SRV Texture2D）。</summary>
    public void BindTexture(int slot, MetalTexture texture, float minLodClamp = 0f)
    {
        ArgumentNullException.ThrowIfNull(texture);
        _bindings[(MscResourceType.Srv, slot)] = new ResourceSlotBinding
        {
            Type = MscResourceType.Srv,
            Texture = texture,
            MinLodClamp = minLodClamp,
        };
    }

    /// <summary>绑定 sampler 资源。</summary>
    public void BindSampler(int slot, MetalSamplerState sampler, float lodBias = 0f)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        _bindings[(MscResourceType.Sampler, slot)] = new ResourceSlotBinding
        {
            Type = MscResourceType.Sampler,
            Sampler = sampler,
            LodBias = lodBias,
        };
    }

    /// <summary>
    /// 编码已绑定资源 + 绑定到 <c>buffer(2)</c> + 对 buffer/texture 声明驻留。
    /// 必须在 <c>BeginRenderPass</c>..<c>EndRenderPass</c> 之间调用。
    /// </summary>
    /// <param name="recorder">命令录制器。</param>
    /// <param name="reflection">该 stage 的 MSC 反射。</param>
    /// <param name="stage">Vertex 或 Fragment（决定 SetVertexBytes/SetFragmentBytes）。</param>
    public void Apply(ICommandRecorder recorder, MscReflection reflection, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(reflection);

        var entries = reflection.TopLevelArgumentBuffer;
        if (entries.Count == 0) return;  // 无资源，无需绑定

        // 1. 按反射顺序构造 ResourceBinding[]，用 (Type, Slot) 查绑定表
        var bindings = new ResourceBinding[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var key = (entry.ResourceType, entry.Slot);
            if (!_bindings.TryGetValue(key, out var slotBinding))
                throw new InvalidOperationException(
                    $"反射条目[{i}] (Type={entry.ResourceType}, Slot={entry.Slot}) 未绑定资源。" +
                    $"请先调用 BindBuffer/BindTexture/BindSampler 绑定。");

            bindings[i] = ToResourceBinding(in slotBinding);
        }

        // 2. 编码描述符堆字节
        byte[] encoded = ArgumentBufferEncoder.Encode(reflection, bindings);

        // 3. 绑定到 buffer(2)（按 stage 选择 vertex/fragment）
        switch (stage)
        {
            case ShaderStage.Vertex:
                recorder.SetVertexBytes(encoded, ArgumentBufferBindPoint);
                break;
            case ShaderStage.Fragment:
                recorder.SetFragmentBytes(encoded, ArgumentBufferBindPoint);
                break;
            default:
                throw new ArgumentException($"ResourceTable.Apply 暂不支持 stage={stage}（仅 Vertex/Fragment）。", nameof(stage));
        }

        // 4. 对 buffer/texture 声明驻留（sampler 不需要；统一 Vertex|Fragment stage）
        var residency = ArgumentBufferEncoder.GetResourcesRequiringResidency(bindings);
        foreach (var res in residency)
        {
            recorder.UseResource(res, MTLResourceUsage.Read,
                MTLRenderStages.Vertex | MTLRenderStages.Fragment);
        }
    }

    private static ResourceBinding ToResourceBinding(in ResourceSlotBinding s) => s.Type switch
    {
        MscResourceType.Srv when s.Texture is not null => ResourceBinding.ForTexture(s.Texture, s.MinLodClamp),
        MscResourceType.Srv when s.Buffer is not null => ResourceBinding.ForBuffer(s.Buffer, MscResourceType.Srv),
        MscResourceType.Uav => ResourceBinding.ForBuffer(s.Buffer!, MscResourceType.Uav),
        MscResourceType.Cbv => ResourceBinding.ForBuffer(s.Buffer!, MscResourceType.Cbv),
        MscResourceType.Sampler => ResourceBinding.ForSampler(s.Sampler!, s.LodBias),
        _ => throw new InvalidOperationException($"ResourceSlotBinding 状态无效：Type={s.Type}"),
    };
}
