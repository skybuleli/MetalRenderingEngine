using System;
using System.Collections.Generic;
using System.Linq;

namespace MetalRenderingEngine.ShaderGen.Models;

/// <summary>着色器类型（Compute/Vertex/Fragment）。</summary>
internal enum ShaderKind { Compute, Vertex, Fragment }

/// <summary>资源字段分类。</summary>
internal enum ResourceKind { Scalar, ReadWriteBuffer, ReadOnlyBuffer, ConstantBuffer, Texture2D, SamplerState }

/// <summary>
/// 源生成器内部的着色器结构化模型。
/// 实现 IEquatable 支持 Roslyn 增量生成器的值比较缓存。
/// </summary>
internal sealed class ShaderInfo : IEquatable<ShaderInfo>
{
    /// <summary>结构体名称。</summary>
    public string StructName { get; set; } = "";

    /// <summary>命名空间。</summary>
    public string Namespace { get; set; } = "";

    /// <summary>着色器类型。</summary>
    public ShaderKind Kind { get; set; }

    /// <summary>Compute shader 线程组大小（仅 Compute 有效）。</summary>
    public int ThreadGroupX { get; set; } = 1;
    public int ThreadGroupY { get; set; } = 1;
    public int ThreadGroupZ { get; set; } = 1;

    /// <summary>所有字段（包括资源字段和标量字段）。</summary>
    public List<FieldInfo> Fields { get; set; } = new();

    /// <summary>Execute 方法体（原始 C# 源码片段，由翻译器处理）。</summary>
    public string ExecuteMethodBody { get; set; } = "";

    /// <summary>Execute 方法的参数类型列表（用于语义参数推断）。</summary>
    public List<string> ExecuteParameters { get; set; } = new();

    /// <summary>Execute 方法的参数名列表（与类型列表对应）。</summary>
    public List<string> ExecuteParameterNames { get; set; } = new();

    /// <summary>Vertex/Fragment 的泛型参数类型名。</summary>
    public string InputTypeName { get; set; } = "";
    public string OutputTypeName { get; set; } = "";

    /// <summary>Vertex/Fragment 输入结构体字段（从 TIn 中提取，含语义）。</summary>
    public List<StructFieldInfo> InputStructFields { get; set; } = new();

    /// <summary>Vertex/Fragment 输出结构体字段（从 TOut 中提取，含语义）。</summary>
    public List<StructFieldInfo> OutputStructFields { get; set; } = new();

    /// <summary>私有辅助方法（会被翻译为 Slang 函数）。</summary>
    public List<MethodInfo> HelperMethods { get; set; } = new();

    public bool Equals(ShaderInfo? other)
    {
        if (other is null) return false;
        return StructName == other.StructName
            && Namespace == other.Namespace
            && Kind == other.Kind
            && ThreadGroupX == other.ThreadGroupX
            && ThreadGroupY == other.ThreadGroupY
            && ThreadGroupZ == other.ThreadGroupZ
            && ExecuteMethodBody == other.ExecuteMethodBody
            && Fields.SequenceEqual(other.Fields)
            && ExecuteParameters.SequenceEqual(other.ExecuteParameters)
            && ExecuteParameterNames.SequenceEqual(other.ExecuteParameterNames)
            && InputStructFields.SequenceEqual(other.InputStructFields)
            && OutputStructFields.SequenceEqual(other.OutputStructFields)
            && HelperMethods.SequenceEqual(other.HelperMethods);
    }

    public override bool Equals(object? obj) => Equals(obj as ShaderInfo);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (StructName?.GetHashCode() ?? 0);
            hash = hash * 31 + (Namespace?.GetHashCode() ?? 0);
            hash = hash * 31 + Kind.GetHashCode();
            hash = hash * 31 + ThreadGroupX.GetHashCode();
            hash = hash * 31 + ThreadGroupY.GetHashCode();
            hash = hash * 31 + ThreadGroupZ.GetHashCode();
            hash = hash * 31 + (ExecuteMethodBody?.GetHashCode() ?? 0);
            hash = hash * 31 + (InputTypeName?.GetHashCode() ?? 0);
            hash = hash * 31 + (OutputTypeName?.GetHashCode() ?? 0);
            // Fields 列表用 count + 各元素 hash 聚合，与 Equals 保持一致
            hash = hash * 31 + Fields.Count.GetHashCode();
            foreach (var f in Fields)
                hash = hash * 31 + (f?.GetHashCode() ?? 0);
            return hash;
        }
    }
}

/// <summary>着色器字段信息。</summary>
internal sealed class FieldInfo : IEquatable<FieldInfo>
{
    /// <summary>字段名。</summary>
    public string Name { get; set; } = "";

    /// <summary>C# 类型名（全名，如 "MetalRenderingEngine.Shader.ReadWriteBuffer`1"）。</summary>
    public string TypeName { get; set; } = "";

    /// <summary>资源分类。</summary>
    public ResourceKind Kind { get; set; }

    /// <summary>泛型参数类型名（如 ReadWriteBuffer&lt;float&gt; 的 "float"）。</summary>
    public string? GenericArgument { get; set; }

    public bool Equals(FieldInfo? other)
    {
        if (other is null) return false;
        return Name == other.Name && TypeName == other.TypeName
            && Kind == other.Kind && GenericArgument == other.GenericArgument;
    }

    public override bool Equals(object? obj) => Equals(obj as FieldInfo);
    public override int GetHashCode() => (Name, TypeName, Kind).GetHashCode();
}

/// <summary>辅助方法信息（会被翻译为 Slang 函数）。</summary>
internal sealed class MethodInfo : IEquatable<MethodInfo>
{
    /// <summary>方法名。</summary>
    public string Name { get; set; } = "";

    /// <summary>返回类型的 Slang 名称。</summary>
    public string ReturnType { get; set; } = "";

    /// <summary>参数列表（名称, Slang类型）。</summary>
    public List<(string Name, string SlangType)> Parameters { get; set; } = new();

    /// <summary>方法体源码片段。</summary>
    public string Body { get; set; } = "";

    public bool Equals(MethodInfo? other)
    {
        if (other is null) return false;
        return Name == other.Name && Body == other.Body
            && Parameters.SequenceEqual(other.Parameters);
    }

    public override bool Equals(object? obj) => Equals(obj as MethodInfo);
    public override int GetHashCode() => (Name, Body).GetHashCode();
}

/// <summary>
/// Vertex/Fragment 输入输出结构体的字段信息（含语义绑定）。
/// </summary>
internal sealed class StructFieldInfo : IEquatable<StructFieldInfo>
{
    /// <summary>字段名。</summary>
    public string Name { get; set; } = "";

    /// <summary>Slang 类型名（如 float4, float3, float2）。</summary>
    public string SlangType { get; set; } = "";

    /// <summary>语义绑定（如 POSITION, SV_Position, TEXCOORD0, COLOR）。</summary>
    public string Semantic { get; set; } = "";

    /// <summary>是否为系统值语义（SV_Position, SV_Target 等，不需要用户提供）。</summary>
    public bool IsSystemValue { get; set; }

    public bool Equals(StructFieldInfo? other)
    {
        if (other is null) return false;
        return Name == other.Name && SlangType == other.SlangType && Semantic == other.Semantic;
    }

    public override bool Equals(object? obj) => Equals(obj as StructFieldInfo);
    public override int GetHashCode() => (Name, SlangType, Semantic).GetHashCode();
}
