using System.Buffers.Binary;
using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Binding;

/// <summary>
/// 资源绑定记录（union 式）。
/// 调用方按 MSC 反射 <see cref="MscReflection.TopLevelArgumentBuffer"/> 的 EltOffset 升序
/// 构造绑定数组，传入 <see cref="ArgumentBufferEncoder.Encode"/>。
/// </summary>
public readonly struct ResourceBinding
{
    /// <summary>资源类型（与反射条目的 <see cref="MscArgumentBufferEntry.ResourceType"/> 对应）。</summary>
    public MscResourceType Type { get; init; }

    /// <summary>Buffer 资源（Type=Srv/Uav/Cbv 且是 buffer 时非 null）。</summary>
    public MetalBuffer? Buffer { get; init; }

    /// <summary>Texture 资源（Type=Srv 且是 Texture2D 时非 null）。</summary>
    public MetalTexture? Texture { get; init; }

    /// <summary>Sampler 资源（Type=Sampler 时非 null）。</summary>
    public MetalSamplerState? Sampler { get; init; }

    /// <summary>Texture 的最小 LOD clamp（默认 0）。</summary>
    public float MinLodClamp { get; init; }

    /// <summary>Sampler 的 LOD bias（默认 0）。</summary>
    public float LodBias { get; init; }

    // 便利工厂
    public static ResourceBinding ForTexture(MetalTexture texture, float minLodClamp = 0f)
        => new() { Type = MscResourceType.Srv, Texture = texture, MinLodClamp = minLodClamp };

    public static ResourceBinding ForBuffer(MetalBuffer buffer, MscResourceType type)
        => new() { Type = type, Buffer = buffer };

    public static ResourceBinding ForSampler(MetalSamplerState sampler, float lodBias = 0f)
        => new() { Type = MscResourceType.Sampler, Sampler = sampler, LodBias = lodBias };
}

/// <summary>
/// Phase 10B: MSC 描述符堆序列化器。
/// 纯函数式：接收 MSC 反射 + 资源绑定列表，按反射 <c>EltOffset</c> 把每个资源编码成
/// 24 字节 <c>IRDescriptorTableEntry</c>，平铺返回 <c>byte[]</c>。
/// 调用方负责 <c>SetFragmentBytes</c>/<c>SetVertexBytes</c> 绑定到 <c>buffer(2)</c>，
/// 以及对 texture/buffer 调 <c>UseResource</c> 声明驻留。
/// </summary>
/// <remarks>
/// 字段语义对齐 MSC 官方 runtime 头文件 <c>metal_irconverter_runtime.h</c> 的
/// <c>IRDescriptorTableSetBuffer/SetTexture/SetSampler</c>。详见
/// <c>docs/argument-buffer-layout.md</c>。
/// </remarks>
public static class ArgumentBufferEncoder
{
    /// <summary>单个描述符条目大小（字节）= 3 × uint64。</summary>
    public const int EntrySize = 24;

    /// <summary>
    /// 按反射顺序编码资源绑定，返回平铺的描述符堆字节。
    /// </summary>
    /// <param name="reflection">MSC 反射（<see cref="MscReflection.TopLevelArgumentBuffer"/> 决定布局）。</param>
    /// <param name="bindings">资源绑定，顺序必须与 <c>TopLevelArgumentBuffer</c> 一致。</param>
    /// <returns>描述符堆字节，可直接 <c>SetFragmentBytes/SetVertexBytes</c> 绑到 buffer(2)。</returns>
    public static byte[] Encode(MscReflection reflection, params ResourceBinding[] bindings)
    {
        ArgumentNullException.ThrowIfNull(reflection);
        ArgumentNullException.ThrowIfNull(bindings);

        var entries = reflection.TopLevelArgumentBuffer;
        if (entries.Count == 0)
        {
            if (bindings.Length != 0)
                throw new ArgumentException("反射无资源条目，但传入了绑定。", nameof(bindings));
            return Array.Empty<byte>();
        }

        if (bindings.Length != entries.Count)
            throw new ArgumentException(
                $"绑定数 ({bindings.Length}) 与反射条目数 ({entries.Count}) 不一致。", nameof(bindings));

        // 总大小 = 最后一个 entry 的 EltOffset + Size
        var last = entries[^1];
        int totalSize = last.EltOffset + last.Size;

        byte[] buffer = new byte[totalSize];
        Span<byte> span = buffer.AsSpan();

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var binding = bindings[i];

            // 校验类型匹配（SRV 可能是 texture 或 buffer，不强校验 sub-type）
            if (binding.Type != entry.ResourceType)
                throw new ArgumentException(
                    $"绑定[{i}] 类型 {binding.Type} 与反射条目类型 {entry.ResourceType} 不匹配。", nameof(bindings));

            WriteEntry(span.Slice(entry.EltOffset, entry.Size), in binding, entry.ResourceType);
        }

        return buffer;
    }

    /// <summary>
    /// 返回需要 <c>UseResource</c> 声明驻留的资源（buffer + texture，sampler 不含）。
    /// </summary>
    public static IReadOnlyList<MetalObject> GetResourcesRequiringResidency(ResourceBinding[] bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var list = new List<MetalObject>(bindings.Length);
        foreach (var b in bindings)
        {
            if (b.Buffer is { } buf) list.Add(buf);
            else if (b.Texture is { } tex) list.Add(tex);
            // sampler 不需要 UseResource（MSC/DXMT 均不调）
        }
        return list;
    }

    /// <summary>
    /// 把单个绑定编码成 24 字节 <c>IRDescriptorTableEntry</c>，写入 <paramref name="dst"/>。
    /// 布局：+0 gpuVA, +8 textureViewID, +16 metadata（小端）。
    /// </summary>
    private static void WriteEntry(Span<byte> dst, in ResourceBinding b, MscResourceType type)
    {
        System.Diagnostics.Debug.Assert(dst.Length >= EntrySize);

        ulong gpuVA, textureViewID, metadata;
        switch (type)
        {
            case MscResourceType.Srv when b.Texture is not null:
                // IRDescriptorTableSetTexture: gpuVA=0, textureViewID=gpuResourceID,
                // metadata = (minLODClamp 的 float 位模式) | ((uint64)metadata << 32)
                gpuVA = 0;
                textureViewID = b.Texture.GpuResourceID;
                metadata = FloatBits(b.MinLodClamp);
                break;

            case MscResourceType.Srv when b.Buffer is not null:
                // IRDescriptorTableSetBuffer: gpuVA=gpuAddress, textureViewID=0, metadata=0
                // （简单 StructuredBuffer SRV，无 typed buffer view，metadata=0）
                gpuVA = b.Buffer.GpuAddress;
                textureViewID = 0;
                metadata = 0;
                break;

            case MscResourceType.Uav when b.Buffer is not null:
                // IRDescriptorTableSetBuffer（UAV 同 SRV buffer 路径）
                gpuVA = b.Buffer.GpuAddress;
                textureViewID = 0;
                metadata = 0;
                break;

            case MscResourceType.Cbv when b.Buffer is not null:
                // CBV 走 IRDescriptorTableSetBuffer：{gpuAddress, 0, 0}
                gpuVA = b.Buffer.GpuAddress;
                textureViewID = 0;
                metadata = 0;
                break;

            case MscResourceType.Sampler when b.Sampler is not null:
                // IRDescriptorTableSetSampler: gpuVA=sampler.gpuResourceID,
                // textureViewID=0, metadata=lodBias 的 float 位模式
                gpuVA = b.Sampler.GpuResourceID;
                textureViewID = 0;
                metadata = FloatBits(b.LodBias);
                break;

            default:
                throw new InvalidOperationException(
                    $"资源类型 {type} 与绑定内容不匹配（Buffer/Texture/Sampler 均为 null 或类型不符）。");
        }

        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(0, 8), gpuVA);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(8, 8), textureViewID);
        BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(16, 8), metadata);
    }

    /// <summary>float → uint32 位模式（等价 C 的 *(uint32_t*)&f），再零扩展到 ulong。</summary>
    private static ulong FloatBits(float f)
    {
        uint bits = BitConverter.SingleToUInt32Bits(f);
        return bits;
    }
}
