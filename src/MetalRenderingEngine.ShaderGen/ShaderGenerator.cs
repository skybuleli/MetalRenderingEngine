using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MetalRenderingEngine.ShaderGen.Diagnostics;
using MetalRenderingEngine.ShaderGen.Models;

namespace MetalRenderingEngine.ShaderGen;

/// <summary>
/// C# 着色器源生成器主入口。
/// 扫描带 [Shader] 属性的 partial struct，提取模型，生成 Slang 代码和 C# Binding 类。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ShaderGenerator : IIncrementalGenerator
{
    private const string ShaderAttributeFullName = "MetalRenderingEngine.Shader.ShaderAttribute";
    private const string ComputeInterfaceName = "MetalRenderingEngine.Shader.IComputeShader";
    private const string VertexInterfaceName = "MetalRenderingEngine.Shader.IVertexShader";
    private const string FragmentInterfaceName = "MetalRenderingEngine.Shader.IFragmentShader";
    private const string ThreadGroupSizeAttributeFullName = "MetalRenderingEngine.Shader.ThreadGroupSizeAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. 过滤：找出所有带 [Shader] 属性的 struct 声明
        var shaderDeclarations = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ShaderAttributeFullName,
                predicate: static (node, _) => node is StructDeclarationSyntax,
                transform: static (ctx, ct) => ExtractShaderInfo(ctx, ct))
            .Where(static result => result.HasValue)
            .Select(static (result, _) => result);

        // 2. 注册输出
        context.RegisterSourceOutput(shaderDeclarations, static (spc, result) =>
        {
            // 报告 transform 阶段收集的诊断
            foreach (var d in result.Diagnostics)
                spc.ReportDiagnostic(d);

            var shaderInfo = result.Info;
            if (shaderInfo is null) return;

            // 调用 SlangEmitter 生成 Slang 源码
            var slangSource = Emit.SlangEmitter.Emit(shaderInfo, out var emitDiagnostics);
            
            // 报告 Emit 阶段产生的诊断
            foreach (var d in emitDiagnostics)
                spc.ReportDiagnostic(d);

            if (slangSource is null) return;

            // 生成 Slang 内容嵌入 C# 源码
            var slangConstClass = GenerateSlangConstClass(shaderInfo, slangSource);
            spc.AddSource($"{shaderInfo.StructName}.Slang.g.cs", slangConstClass);

            // 生成 C# Binding 类
            if (shaderInfo.Kind == ShaderKind.Compute)
            {
                var bindingClass = Emit.BindingClassEmitter.GenerateBinding(shaderInfo);
                spc.AddSource($"{shaderInfo.StructName}.Binding.g.cs", bindingClass);
            }
        });
    }

    /// <summary>
    /// Transform 阶段的返回值包装器，将诊断信息从 transform 传递到输出阶段。
    /// </summary>
    internal sealed class ShaderInfoResult
    {
        public ShaderInfoResult(ShaderInfo? info, ImmutableArray<Diagnostic> diagnostics)
        {
            Info = info;
            Diagnostics = diagnostics;
        }

        public ShaderInfo? Info { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
        public bool HasValue => Info is not null;
    }

    /// <summary>
    /// 从 Roslyn 语义模型提取 ShaderInfo（transform 阶段调用）。
    /// </summary>
    private static ShaderInfoResult ExtractShaderInfo(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return new(null, ImmutableArray<Diagnostic>.Empty);
        if (ctx.TargetNode is not StructDeclarationSyntax structDecl)
            return new(null, ImmutableArray<Diagnostic>.Empty);

        var diagnostics = new List<Diagnostic>();

        // 验证 partial
        if (!structDecl.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            diagnostics.Add(Diagnostic.Create(
                ShaderDiagnostics.ShaderMustBePartialStruct,
                structDecl.Identifier.GetLocation(),
                typeSymbol.Name));
            return new(null, diagnostics.ToImmutableArray());
        }

        // 确定着色器类型
        var shaderKind = DetermineShaderKind(typeSymbol, out var inputTypeSym, out var outputTypeSym);
        if (shaderKind is null)
        {
            diagnostics.Add(Diagnostic.Create(
                ShaderDiagnostics.ShaderMustImplementInterface,
                structDecl.Identifier.GetLocation(),
                typeSymbol.Name));
            return new(null, diagnostics.ToImmutableArray());
        }

        var inputTypeStr = inputTypeSym?.ToDisplayString() ?? "";
        var outputTypeStr = outputTypeSym?.ToDisplayString() ?? "";

        var info = new ShaderInfo
        {
            StructName = typeSymbol.Name,
            Namespace = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            Kind = shaderKind.Value,
            InputTypeName = inputTypeStr,
            OutputTypeName = outputTypeStr,
        };

        // 提取 Vertex/Fragment 输入输出结构体字段
        if (shaderKind == ShaderKind.Vertex || shaderKind == ShaderKind.Fragment)
        {
            if (inputTypeSym is not null)
            {
                foreach (var field in inputTypeSym.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic || field.IsConst) continue;
                    info.InputStructFields.Add(new Models.StructFieldInfo
                    {
                        Name = field.Name,
                        SlangType = Translation.TypeMap.ToSlangType(field.Type),
                    });
                }
            }

            if (outputTypeSym is not null)
            {
                // 检查输出类型是否为简单标量类型（如 float4），如果是则不提取 struct 字段
                var isSimpleOutput = outputTypeStr is "float4"
                    or "MetalRenderingEngine.Shader.float4"
                    or "float" or "float3" or "float2"
                    or "int" or "uint";

                if (!isSimpleOutput)
                {
                    foreach (var field in outputTypeSym.GetMembers().OfType<IFieldSymbol>())
                    {
                        if (field.IsStatic || field.IsConst) continue;
                        info.OutputStructFields.Add(new Models.StructFieldInfo
                        {
                            Name = field.Name,
                            SlangType = Translation.TypeMap.ToSlangType(field.Type),
                        });
                    }
                }
            }
        }

        // 提取 ThreadGroupSize（仅 Compute）
        if (shaderKind == ShaderKind.Compute)
        {
            var tgAttr = typeSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == ThreadGroupSizeAttributeFullName);
            if (tgAttr is null || tgAttr.ConstructorArguments.Length != 3)
            {
                diagnostics.Add(Diagnostic.Create(
                    ShaderDiagnostics.ComputeShaderMissingThreadGroupSize,
                    structDecl.Identifier.GetLocation(),
                    typeSymbol.Name));
                return new(null, diagnostics.ToImmutableArray());
            }
            info.ThreadGroupX = (int)(tgAttr.ConstructorArguments[0].Value ?? 1);
            info.ThreadGroupY = (int)(tgAttr.ConstructorArguments[1].Value ?? 1);
            info.ThreadGroupZ = (int)(tgAttr.ConstructorArguments[2].Value ?? 1);
        }

        // 提取字段
        foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsStatic || member.IsConst) continue;
            info.Fields.Add(ExtractFieldInfo(member));
        }

        // 提取 Execute 方法体
        var executeMethod = typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Name == "Execute" && !m.IsStatic);
        if (executeMethod is not null)
        {
            var executeSyntax = executeMethod.DeclaringSyntaxReferences
                .FirstOrDefault()?.GetSyntax(ct) as MethodDeclarationSyntax;
            if (executeSyntax?.Body is not null)
            {
                info.ExecuteMethodBody = executeSyntax.Body.ToFullString();
            }
            else if (executeSyntax?.ExpressionBody is not null)
            {
                info.ExecuteMethodBody = "{ " + executeSyntax.ExpressionBody.ToFullString() + " }";
            }

            info.ExecuteParameters = executeMethod.Parameters
                .Select(p => p.Type?.ToDisplayString() ?? "")
                .ToList();

            info.ExecuteParameterNames = executeMethod.Parameters
                .Select(p => p.Name)
                .ToList();
        }

        // 提取辅助方法
        foreach (var method in typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.Name != "Execute" && !m.IsStatic && m.MethodKind == MethodKind.Ordinary))
        {
            var methodSyntax = method.DeclaringSyntaxReferences
                .FirstOrDefault()?.GetSyntax(ct) as MethodDeclarationSyntax;
            if (methodSyntax is null) continue;

            var mi = new MethodInfo
            {
                Name = method.Name,
                ReturnType = Translation.TypeMap.ToSlangType(method.ReturnType),
                Body = methodSyntax.Body?.ToFullString()
                    ?? (methodSyntax.ExpressionBody is not null
                        ? "{ return " + methodSyntax.ExpressionBody.ToFullString() + " }"
                        : "{}"),
                Parameters = method.Parameters
                    .Select(p => (p.Name, Translation.TypeMap.ToSlangType(p.Type)))
                    .ToList(),
            };
            info.HelperMethods.Add(mi);
        }

        return new(info, diagnostics.ToImmutableArray());
    }

    private static ShaderKind? DetermineShaderKind(INamedTypeSymbol typeSymbol, out ITypeSymbol? inputType, out ITypeSymbol? outputType)
    {
        inputType = null;
        outputType = null;

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var originalDef = iface.OriginalDefinition;
            var fullName = originalDef.ToDisplayString();

            // 泛型接口（如 IVertexShader<TIn,TOut>）的 ToDisplayString 可能包含类型参数名
            // 使用 MetadataName 做精确匹配
            var metadataName = originalDef.MetadataName;
            var ns = originalDef.ContainingNamespace?.ToDisplayString() ?? "";

            if (fullName == ComputeInterfaceName)
                return ShaderKind.Compute;

            if ((fullName.StartsWith(VertexInterfaceName) || $"{ns}.{metadataName}" == VertexInterfaceName)
                && iface.TypeArguments.Length == 2)
            {
                inputType = iface.TypeArguments[0];
                outputType = iface.TypeArguments[1];
                return ShaderKind.Vertex;
            }

            if ((fullName.StartsWith(FragmentInterfaceName) || $"{ns}.{metadataName}" == FragmentInterfaceName)
                && iface.TypeArguments.Length == 2)
            {
                inputType = iface.TypeArguments[0];
                outputType = iface.TypeArguments[1];
                return ShaderKind.Fragment;
            }
        }
        return null;
    }

    private static FieldInfo ExtractFieldInfo(IFieldSymbol field)
    {
        var typeName = field.Type.ToDisplayString();
        var kind = ResourceKind.Scalar;
        string? genericArg = null;

        if (field.Type is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            var originalDef = namedType.OriginalDefinition.ToDisplayString();
            if (originalDef.Contains("ReadWriteBuffer"))
            {
                kind = ResourceKind.ReadWriteBuffer;
                genericArg = namedType.TypeArguments[0].ToDisplayString();
            }
            else if (originalDef.Contains("ReadOnlyBuffer"))
            {
                kind = ResourceKind.ReadOnlyBuffer;
                genericArg = namedType.TypeArguments[0].ToDisplayString();
            }
            else if (originalDef.Contains("ConstantBuffer"))
            {
                kind = ResourceKind.ConstantBuffer;
                genericArg = namedType.TypeArguments[0].ToDisplayString();
            }
            else if (originalDef.Contains("Texture2D"))
            {
                kind = ResourceKind.Texture2D;
                genericArg = namedType.TypeArguments[0].ToDisplayString();
            }
        }
        else if (typeName.Contains("SamplerState"))
        {
            kind = ResourceKind.SamplerState;
        }

        return new FieldInfo
        {
            Name = field.Name,
            TypeName = typeName,
            Kind = kind,
            GenericArgument = genericArg,
        };
    }

    /// <summary>生成嵌入 Slang 内容的 C# const class。</summary>
    private static string GenerateSlangConstClass(ShaderInfo info, string slangSource)
    {
        var stageSuffix = info.Kind switch
        {
            ShaderKind.Compute => "",
            ShaderKind.Vertex => ".vert",
            ShaderKind.Fragment => ".frag",
            _ => "",
        };
        var fileName = $"{info.StructName}{stageSuffix}.slang";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"namespace {info.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"internal static class {info.StructName}SlangSource");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string FileName = \"{fileName}\";");
        sb.AppendLine($"    public const string Source = @\"{slangSource.Replace("\"", "\"\"")}\";");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
