using System.Text.Json;
using System.Text.Json.Serialization;

namespace MetalRenderingEngine.Shader.Reflection;

/// <summary>
/// MSC（metal-shaderconverter）反射数据的 C# 模型。
/// 对应 <c>*.reflect.json</c> 的顶层结构。
/// </summary>
public sealed class MscReflection
{
    /// <summary>入口函数名（通常为 "main"）。</summary>
    [JsonPropertyName("EntryPoint")]
    public string EntryPoint { get; set; } = string.Empty;

    /// <summary>着色器类型（"Compute" / "Vertex" / "Fragment"）。</summary>
    [JsonPropertyName("ShaderType")]
    public string ShaderType { get; set; } = string.Empty;

    /// <summary>着色器唯一标识（数字字符串）。</summary>
    [JsonPropertyName("ShaderID")]
    public string ShaderID { get; set; } = string.Empty;

    /// <summary>资源总数。</summary>
    [JsonPropertyName("ResourceCount")]
    public int ResourceCount { get; set; }

    /// <summary>资源使用标志位（bitmask：1=Read, 2=Write, 4=Sample 等）。</summary>
    [JsonPropertyName("ResourceUsages")]
    public int ResourceUsages { get; set; }

    /// <summary>顶层 argument buffer 布局（MSC 重写后的实际绑定）。</summary>
    [JsonPropertyName("TopLevelArgumentBuffer")]
    public List<MscArgumentBufferEntry> TopLevelArgumentBuffer { get; set; } = [];

    /// <summary>CBV 在 argument buffer 中的索引列表。</summary>
    [JsonPropertyName("ConstantBufferIndices")]
    public List<int> ConstantBufferIndices { get; set; } = [];

    /// <summary>SRV（纹理/结构化缓冲区）在 argument buffer 中的索引列表。</summary>
    [JsonPropertyName("ShaderResourceViewIndices")]
    public List<int> ShaderResourceViewIndices { get; set; } = [];

    /// <summary>UAV（无序访问视图）在 argument buffer 中的索引列表。</summary>
    [JsonPropertyName("UnorderedAccessViewIndices")]
    public List<int> UnorderedAccessViewIndices { get; set; } = [];

    /// <summary>采样器在 argument buffer 中的索引列表。</summary>
    [JsonPropertyName("SamplerIndices")]
    public List<int> SamplerIndices { get; set; } = [];

    /// <summary>使用的资源详情列表。</summary>
    [JsonPropertyName("UsedResources")]
    public List<MscUsedResource> UsedResources { get; set; } = [];

    /// <summary>函数常量列表。</summary>
    [JsonPropertyName("FunctionConstants")]
    public List<MscFunctionConstant> FunctionConstants { get; set; } = [];

    /// <summary>是否需要函数常量。</summary>
    [JsonPropertyName("NeedsFunctionConstants")]
    public bool NeedsFunctionConstants { get; set; }

    /// <summary>着色器特性标志。</summary>
    [JsonPropertyName("Features")]
    public int Features { get; set; }

    /// <summary>着色器状态信息（因 ShaderType 而异）。</summary>
    [JsonPropertyName("state")]
    public JsonElement? State { get; set; }
}

/// <summary>
/// argument buffer 中的单个资源条目。
/// </summary>
public sealed class MscArgumentBufferEntry
{
    /// <summary>字节偏移（在 argument buffer 内）。</summary>
    [JsonPropertyName("EltOffset")]
    public int EltOffset { get; set; }

    /// <summary>资源名称（可能为空字符串）。</summary>
    [JsonPropertyName("Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>描述符大小（字节，典型值 24）。</summary>
    [JsonPropertyName("Size")]
    public int Size { get; set; }

    /// <summary>buffer slot（MSC 重写后的实际索引）。</summary>
    [JsonPropertyName("Slot")]
    public int Slot { get; set; }

    /// <summary>寄存器空间（通常为 0）。</summary>
    [JsonPropertyName("Space")]
    public int Space { get; set; }

    /// <summary>资源类型（"UAV" / "SRV" / "CBV" / "Sampler"）。</summary>
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>转换为强类型枚举。</summary>
    public MscResourceType ResourceType => Type switch
    {
        "UAV" => MscResourceType.Uav,
        "SRV" => MscResourceType.Srv,
        "CBV" => MscResourceType.Cbv,
        "Sampler" => MscResourceType.Sampler,
        _ => MscResourceType.Unknown,
    };
}

/// <summary>
/// MSC 资源类型。
/// </summary>
public enum MscResourceType
{
    Unknown,
    /// <summary>Unordered Access View（可读写缓冲区/纹理）。</summary>
    Uav,
    /// <summary>Shader Resource View（只读纹理/缓冲区）。</summary>
    Srv,
    /// <summary>Constant Buffer View（常量缓冲区）。</summary>
    Cbv,
    /// <summary>采样器。</summary>
    Sampler,
}

/// <summary>
/// 使用的资源详情。
/// </summary>
public sealed class MscUsedResource
{
    /// <summary>绑定索引（在 argument buffer 中的位置）。</summary>
    [JsonPropertyName("bindingIndex")]
    public int BindingIndex { get; set; }

    /// <summary>是否在 Global Root Signature 中。</summary>
    [JsonPropertyName("isInGRS")]
    public bool IsInGRS { get; set; }

    /// <summary>表长度。</summary>
    [JsonPropertyName("tableLength")]
    public int TableLength { get; set; }

    /// <summary>表起始索引（uint32 max 表示无显式表）。</summary>
    [JsonPropertyName("tableStartIndex")]
    public uint TableStartIndex { get; set; }
}

/// <summary>
/// 函数常量。
/// </summary>
public sealed class MscFunctionConstant
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// MSC 反射 JSON 解析器。
/// </summary>
public static class MscReflectionParser
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 从 JSON 字节解析 MSC 反射数据。
    /// </summary>
    public static MscReflection Parse(byte[] reflectJson)
    {
        ArgumentNullException.ThrowIfNull(reflectJson);
        return JsonSerializer.Deserialize<MscReflection>(reflectJson, s_options)
            ?? throw new ShaderCompileException("反射 JSON 反序列化返回 null。");
    }

    /// <summary>
    /// 从 JSON 字符串解析 MSC 反射数据。
    /// </summary>
    public static MscReflection Parse(string reflectJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(reflectJson);
        return JsonSerializer.Deserialize<MscReflection>(reflectJson, s_options)
            ?? throw new ShaderCompileException("反射 JSON 反序列化返回 null。");
    }

    /// <summary>
    /// 将 MSC ShaderType 字符串转为 <see cref="ShaderStage"/>。
    /// </summary>
    public static ShaderStage ToShaderStage(string shaderType) => shaderType switch
    {
        "Compute" => ShaderStage.Compute,
        "Vertex" => ShaderStage.Vertex,
        "Fragment" => ShaderStage.Fragment,
        _ => throw new ArgumentException($"未知的 ShaderType: {shaderType}"),
    };

    /// <summary>
    /// 获取 argument buffer 中指定类型的所有条目。
    /// </summary>
    public static IEnumerable<MscArgumentBufferEntry> GetEntriesByType(
        MscReflection reflection, MscResourceType type)
    {
        return reflection.TopLevelArgumentBuffer.Where(e => e.ResourceType == type);
    }

    /// <summary>
    /// 获取 argument buffer 中指定名称的条目（大小写敏感）。
    /// </summary>
    public static MscArgumentBufferEntry? GetEntryByName(
        MscReflection reflection, string name)
    {
        return reflection.TopLevelArgumentBuffer.FirstOrDefault(
            e => string.Equals(e.Name, name, StringComparison.Ordinal));
    }
}
