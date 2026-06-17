using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MetalRenderingEngine.ShaderGen.Translation;

/// <summary>
/// C# 类型全名 → Slang 类型字符串的映射表。
/// </summary>
internal static class TypeMap
{
    private static readonly Dictionary<string, string> s_map = new()
    {
        // 标量
        ["float"] = "float",
        ["System.Single"] = "float",
        ["double"] = "double",
        ["System.Double"] = "double",
        ["int"] = "int",
        ["System.Int32"] = "int",
        ["uint"] = "uint",
        ["System.UInt32"] = "uint",
        ["bool"] = "bool",
        ["System.Boolean"] = "bool",

        // 向量类型（自定义 struct）
        ["MetalRenderingEngine.Shader.float2"] = "float2",
        ["MetalRenderingEngine.Shader.float3"] = "float3",
        ["MetalRenderingEngine.Shader.float4"] = "float4",
        ["MetalRenderingEngine.Shader.int2"] = "int2",
        ["MetalRenderingEngine.Shader.int3"] = "int3",
        ["MetalRenderingEngine.Shader.int4"] = "int4",
        ["MetalRenderingEngine.Shader.uint3"] = "uint3",
        ["MetalRenderingEngine.Shader.float4x4"] = "float4x4",
    };

    /// <summary>
    /// 将 ITypeSymbol 翻译为 Slang 类型名。
    /// 对泛型资源类型（ReadWriteBuffer&lt;T&gt; 等）做特殊处理。
    /// </summary>
    public static string ToSlangType(ITypeSymbol? type)
    {
        if (type is null) return "void";

        var fullName = type.ToDisplayString();

        // 先查精确匹配
        if (s_map.TryGetValue(fullName, out var slang))
            return slang;

        // 处理 short name（C# 关键字如 float, int）
        if (s_map.TryGetValue(type.Name, out var slangShort))
            return slangShort;

        // 泛型资源类型
        if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            var origDef = namedType.OriginalDefinition.ToDisplayString();
            var argType = ToSlangType(namedType.TypeArguments[0]);

            if (origDef.Contains("ReadWriteBuffer"))
                return $"RWStructuredBuffer<{argType}>";
            if (origDef.Contains("ReadOnlyBuffer"))
                return $"StructuredBuffer<{argType}>";
            if (origDef.Contains("ConstantBuffer"))
                return $"ConstantBuffer<{argType}>";
            if (origDef.Contains("Texture2D"))
                return $"Texture2D<{argType}>";
        }

        // SamplerState
        if (fullName.Contains("SamplerState"))
            return "SamplerState";

        // 未知类型，返回原始名（让调用方报 MSGEN012）
        return fullName;
    }

    /// <summary>判断类型是否为着色器资源类型（buffer/texture/sampler）。</summary>
    public static bool IsResourceType(ITypeSymbol type)
    {
        var fullName = type.ToDisplayString();
        return fullName.Contains("ReadWriteBuffer")
            || fullName.Contains("ReadOnlyBuffer")
            || fullName.Contains("ConstantBuffer")
            || fullName.Contains("Texture2D")
            || fullName.Contains("SamplerState");
    }

    /// <summary>
    /// 从类型语法节点推测 Slang 类型名（不依赖 SemanticModel）。
    /// 用于 ExpressionTranslator / StatementTranslator 在纯语法树层面的翻译。
    /// </summary>
    public static string ToSlangTypeString(TypeSyntax typeSyntax)
    {
        switch (typeSyntax)
        {
            case PredefinedTypeSyntax pre:
                return pre.Keyword.Kind() switch
                {
                    SyntaxKind.FloatKeyword => "float",
                    SyntaxKind.DoubleKeyword => "double",
                    SyntaxKind.IntKeyword => "int",
                    SyntaxKind.UIntKeyword => "uint",
                    SyntaxKind.BoolKeyword => "bool",
                    SyntaxKind.VoidKeyword => "void",
                    SyntaxKind.ByteKeyword => "uint",
                    SyntaxKind.SByteKeyword => "int",
                    SyntaxKind.ShortKeyword => "int",
                    SyntaxKind.UShortKeyword => "uint",
                    SyntaxKind.LongKeyword => "int64_t",
                    SyntaxKind.ULongKeyword => "uint64_t",
                    SyntaxKind.StringKeyword => ReportUnresolvable(typeSyntax, "string"),
                    SyntaxKind.CharKeyword => ReportUnresolvable(typeSyntax, "char"),
                    SyntaxKind.DecimalKeyword => ReportUnresolvable(typeSyntax, "decimal"),
                    _ => typeSyntax.ToString(),
                };

            case IdentifierNameSyntax id:
            {
                var name = id.Identifier.Text;
                return name switch
                {
                    "float2" => "float2",
                    "float3" => "float3",
                    "float4" => "float4",
                    "int2" => "int2",
                    "int3" => "int3",
                    "int4" => "int4",
                    "uint3" => "uint3",
                    "float4x4" => "float4x4",
                    "Matrix4x4" => "float4x4",
                    "ThreadId" => "uint3",
                    "DispatchThreadID" => "uint3",
                    "GroupThreadID" => "uint3",
                    "GroupID" => "uint3",
                    _ => name,
                };
            }

            case GenericNameSyntax generic:
            {
                var name = generic.Identifier.Text;
                var typeArgs = new List<string>();
                foreach (var arg in generic.TypeArgumentList.Arguments)
                    typeArgs.Add(ToSlangTypeString(arg));
                var args = string.Join(", ", typeArgs);
                return (name) switch
                {
                    "ReadWriteBuffer" => $"RWStructuredBuffer<{args}>",
                    "ReadOnlyBuffer" => $"StructuredBuffer<{args}>",
                    "ConstantBuffer" => $"ConstantBuffer<{args}>",
                    "Texture2D" => $"Texture2D<{args}>",
                    "Nullable" or "Nullable" => $"{args}",  // int? → 去包装
                    _ => $"{name}<{args}>",
                };
            }

            case ArrayTypeSyntax arr:
            {
                var elementType = ToSlangTypeString(arr.ElementType);
                var sb = new System.Text.StringBuilder();
                foreach (var spec in arr.RankSpecifiers)
                {
                    sb.Append('[');
                    for (int i = 1; i < spec.Rank; i++)
                        sb.Append(',');
                    sb.Append(']');
                }
                return $"{elementType}{sb}";
            }

            case NullableTypeSyntax nullable:
                return ToSlangTypeString(nullable.ElementType);  // int? → int

            case QualifiedNameSyntax qualified:
            {
                // 处理 Shader.float4 这样的限定名
                var right = qualified.Right.Identifier.Text;
                return right switch
                {
                    "float2" or "float3" or "float4" => right,
                    "int2" or "int3" or "int4" => right,
                    "uint3" => right,
                    "float4x4" or "Matrix4x4" => "float4x4",
                    _ => qualified.ToString(),
                };
            }

            case TupleTypeSyntax:
                return ReportUnresolvable(typeSyntax, "元组类型");

            case FunctionPointerTypeSyntax:
                return ReportUnresolvable(typeSyntax, "函数指针");

            default:
                return typeSyntax.ToString();
        }
    }

    private static string ReportUnresolvable(TypeSyntax typeSyntax, string description)
    {
        throw new TranslationException(typeSyntax.GetLocation(), $"无法解析的类型: {description}");
    }
}
