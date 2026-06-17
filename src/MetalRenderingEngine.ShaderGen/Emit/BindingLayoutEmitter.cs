using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using MetalRenderingEngine.ShaderGen.Models;

namespace MetalRenderingEngine.ShaderGen.Emit;

/// <summary>
/// 从字段声明计算 HLSL 风格的 register 分配（u0/t0/b0/s0 自动递增）。
/// 每个资源槽位类型独立计数。
/// </summary>
internal sealed class BindingLayoutEmitter
{
    /// <summary>寄存器槽位分配结果。</summary>
    internal sealed class RegisterSlot
    {
        /// <summary>寄存器类型：u (UAV), t (SRV), b (ConstantBuffer), s (Sampler)。</summary>
        public string RegisterType { get; }
        public int Index { get; }
        public string Register => $"{RegisterType}{Index}";

        public RegisterSlot(string registerType, int index)
        {
            RegisterType = registerType;
            Index = index;
        }
    }

    /// <summary>每个字段名对应的寄存器槽位。</summary>
    public ImmutableDictionary<string, RegisterSlot> FieldSlots => _fieldSlots.ToImmutableDictionary();

    /// <summary>所有资源绑定的 Slang 声明文本列表。</summary>
    public ImmutableArray<string> BindingDeclarations => _bindings.Select(b => b.SlangDecl).ToImmutableArray();

    private readonly List<(string FieldName, string SlangDecl)> _bindings = new();
    private readonly Dictionary<string, RegisterSlot> _fieldSlots = new(StringComparer.Ordinal);
    private int _uavCounter;   // RWStructuredBuffer<T> → uN
    private int _srvCounter;   // StructuredBuffer<T> / Texture2D<T> → tN
    private int _cbvCounter;   // ConstantBuffer<T> → bN
    private int _samplerCounter; // SamplerState → sN

    public void AddField(FieldInfo field, string slangType)
    {
        RegisterSlot slot = field.Kind switch
        {
            ResourceKind.ReadWriteBuffer => new("u", _uavCounter++),
            ResourceKind.ReadOnlyBuffer => new("t", _srvCounter++),
            ResourceKind.ConstantBuffer => new("b", _cbvCounter++),
            ResourceKind.Texture2D => new("t", _srvCounter++),
            ResourceKind.SamplerState => new("s", _samplerCounter++),
            ResourceKind.Scalar => new("b", _cbvCounter++), // 标量合并到 ConstantBuffer
            _ => new("", -1),
        };

        _fieldSlots[field.Name] = slot;

        if (slot.RegisterType == "")
            return; // 未知类型，不生成绑定声明

        // 生成 Slang 声明带 register 绑定
        string decl;
        if (field.Kind == ResourceKind.Scalar)
        {
            // 标量字段合并到常量缓冲区：cbN.fieldName
            decl = $"    {{ {slangType} {field.Name}; }} : register({slot.Register});";
        }
        else
        {
            decl = $"{slangType} {field.Name} : register({slot.Register});";
        }

        _bindings.Add((field.Name, decl));
    }

    /// <summary>生成 Scalar 字段的隐式 ConstantBuffer 包裹（如 ScalarBuffer : register(b0)）。</summary>
    public string? EmitScalarConstantBuffer(string bufferName, IReadOnlyList<FieldInfo> scalarFields)
    {
        if (scalarFields.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"struct {bufferName}");
        sb.AppendLine("{");
        foreach (var f in scalarFields)
        {
            var slangType = Translation.TypeMap.ToSlangTypeString(
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseTypeName(f.TypeName));
            sb.AppendLine($"    {slangType} {f.Name};");
        }
        sb.Append($"}} ConstantBuffer<{bufferName}> _scalarBuffer : register(b0);");
        return sb.ToString();
    }
}
