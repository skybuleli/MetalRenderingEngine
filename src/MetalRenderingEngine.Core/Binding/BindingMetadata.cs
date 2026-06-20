using System.Text.Json;
using System.Text.Json.Serialization;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Binding;

/// <summary>
/// 编译后生成的绑定元数据（*.bindings.json）。
/// 比 MSC 原始 reflect.json 更稳定：只保留运行时绑定层需要的 stage、bind point、资源表。
/// </summary>
public sealed class BindingMetadata
{
    /// <summary>元数据格式版本。</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>着色器名称（不含 .bindings.json 后缀）。</summary>
    [JsonPropertyName("shader")]
    public string Shader { get; set; } = string.Empty;

    /// <summary>着色器阶段。</summary>
    [JsonPropertyName("stage")]
    public string Stage { get; set; } = string.Empty;

    /// <summary>MSC 顶层 argument buffer 固定绑定点。</summary>
    [JsonPropertyName("argumentBufferBindPoint")]
    public ulong ArgumentBufferBindPoint { get; set; } = ResourceTable.ArgumentBufferBindPoint;

    /// <summary>资源绑定条目。</summary>
    [JsonPropertyName("resources")]
    public List<BindingResourceMetadata> Resources { get; set; } = [];

    /// <summary>转换为运行时需要的 ShaderStage。</summary>
    public Shader.ShaderStage ShaderStage => MscReflectionParser.ToShaderStage(Stage);
}

/// <summary>单个资源绑定元数据。</summary>
public sealed class BindingResourceMetadata
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("slot")]
    public int Slot { get; set; }

    [JsonPropertyName("space")]
    public int Space { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonIgnore]
    public MscResourceType ResourceType => Type switch
    {
        "UAV" => MscResourceType.Uav,
        "SRV" => MscResourceType.Srv,
        "CBV" => MscResourceType.Cbv,
        "Sampler" => MscResourceType.Sampler,
        _ => MscResourceType.Unknown,
    };
}

/// <summary>BindingMetadata JSON 解析器。</summary>
public static class BindingMetadataParser
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从 JSON 字节解析绑定元数据。</summary>
    public static BindingMetadata Parse(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<BindingMetadata>(json, s_options)
            ?? throw new InvalidOperationException("bindings.json 反序列化返回 null。");
    }
}
