using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MetalRenderingEngine.ShaderGen.Translation;

/// <summary>
/// C# 表达式 → Slang 表达式递归翻译器。
/// 使用 CSharpSyntaxVisitor 模式，不依赖 SemanticModel（纯语法树翻译）。
/// </summary>
internal sealed class ExpressionTranslator
{
    /// <summary>已知的 ThreadId 参数名 → 其子成员映射（如 id.DispatchThreadID → id）。</summary>
    private readonly Dictionary<string, string> _threadIdParamMappings;

    /// <summary>当前作用域内的局部变量名集合（用于区分字段访问和局部变量引用）。</summary>
    private readonly HashSet<string> _localVariables;

    public ExpressionTranslator(
        Dictionary<string, string>? threadIdParamMappings = null,
        HashSet<string>? localVariables = null)
    {
        _threadIdParamMappings = threadIdParamMappings ?? new();
        _localVariables = localVariables ?? new();
    }

    public string Translate(ExpressionSyntax expression)
    {
        return Visit(expression);
    }

    private string Visit(ExpressionSyntax node)
    {
        return node switch
        {
            LiteralExpressionSyntax lit => VisitLiteral(lit),
            IdentifierNameSyntax id => VisitIdentifier(id),
            MemberAccessExpressionSyntax ma => VisitMemberAccess(ma),
            InvocationExpressionSyntax inv => VisitInvocation(inv),
            BinaryExpressionSyntax bin => VisitBinary(bin),
            PrefixUnaryExpressionSyntax pre => VisitPrefixUnary(pre),
            PostfixUnaryExpressionSyntax post => VisitPostfixUnary(post),
            CastExpressionSyntax cast => VisitCast(cast),
            ObjectCreationExpressionSyntax obj => VisitObjectCreation(obj),
            ElementAccessExpressionSyntax elem => VisitElementAccess(elem),
            ConditionalExpressionSyntax cond => VisitConditional(cond),
            ParenthesizedExpressionSyntax paren => $"({Visit(paren.Expression)})",
            AssignmentExpressionSyntax assign => VisitAssignment(assign),
            ThisExpressionSyntax => "this",
            DefaultExpressionSyntax def => VisitDefault(def),
            InterpolatedStringExpressionSyntax => ReportUnsupported(node, "内插字符串"),
            AnonymousFunctionExpressionSyntax => ReportUnsupported(node, "匿名函数/lambda"),
            AwaitExpressionSyntax => ReportUnsupported(node, "await"),
            TupleExpressionSyntax => ReportUnsupported(node, "元组"),
            SwitchExpressionSyntax => ReportUnsupported(node, "switch 表达式"),
            StackAllocArrayCreationExpressionSyntax => ReportUnsupported(node, "stackalloc"),
            BaseExpressionSyntax => ReportUnsupported(node, "base"),

            // 其他表达式类型
            TypeOfExpressionSyntax => ReportUnsupported(node, "typeof"),
            SizeOfExpressionSyntax => ReportUnsupported(node, "sizeof"),
            CheckedExpressionSyntax => ReportUnsupported(node, "checked/unchecked"),
            MakeRefExpressionSyntax => ReportUnsupported(node, "__makeref"),
            RefTypeExpressionSyntax => ReportUnsupported(node, "__reftype"),
            RefValueExpressionSyntax => ReportUnsupported(node, "__refvalue"),
            RangeExpressionSyntax => ReportUnsupported(node, "范围表达式 .."),
            IsPatternExpressionSyntax => ReportUnsupported(node, "is 模式匹配"),
            ThrowExpressionSyntax => ReportUnsupported(node, "throw 表达式"),
            WithExpressionSyntax => ReportUnsupported(node, "with 表达式"),
            ImplicitObjectCreationExpressionSyntax => ReportUnsupported(node, "隐式 new"),

            _ => ReportUnsupported(node, $"未知表达式 ({node.Kind()})"),
        };
    }

    // ─── 字面量 ──────────────────────────────────────────────

    private static string VisitLiteral(LiteralExpressionSyntax node)
    {
        // float 字面量去掉 'f' 后缀：1.5f → 1.5
        if (node.IsKind(SyntaxKind.NumericLiteralExpression))
        {
            var text = node.Token.Text;
            if (text.EndsWith("f", StringComparison.OrdinalIgnoreCase)
                || text.EndsWith("F", StringComparison.OrdinalIgnoreCase))
                return text.Substring(0, text.Length - 1);
            return text;
        }

        if (node.IsKind(SyntaxKind.TrueLiteralExpression))
            return "true";
        if (node.IsKind(SyntaxKind.FalseLiteralExpression))
            return "false";
        if (node.IsKind(SyntaxKind.NullLiteralExpression))
            return "null";
        if (node.IsKind(SyntaxKind.DefaultLiteralExpression))
            return "0";

        return node.Token.Text;
    }

    // ─── 标识符 ──────────────────────────────────────────────

    private string VisitIdentifier(IdentifierNameSyntax node)
    {
        var name = node.Identifier.Text;

        // Slang 关键字转义（如 in/out 是 Slang 关键字）
        return name switch
        {
            "in" => "@in",
            "out" => "@out",
            _ => name,
        };
    }

    // ─── 成员访问 ────────────────────────────────────────────

    private string VisitMemberAccess(MemberAccessExpressionSyntax node)
    {
        var exprStr = Visit(node.Expression);
        var memberName = node.Name.Identifier.Text;

        // 检查是否是 ThreadId 参数的特殊映射
        // 如 id.DispatchThreadID.X → id.x
        if (_threadIdParamMappings.TryGetValue(exprStr, out var mapped))
        {
            if (memberName is "DispatchThreadID" or "GroupThreadID" or "GroupID")
            {
                // id.DispatchThreadID → 直接使用 id（第一个组件）
                // 后面的 .X/Y/Z 会在递归中处理
                return mapped;
            }
        }

        // 处理字段的 DispatchThreadID 引用（如 _threadId.DispatchThreadID.X）
        if (memberName is "DispatchThreadID" or "GroupThreadID" or "GroupID")
        {
            // 将这种复杂路径简化为基础参数名
            // 比如 id.DispatchThreadID → id（假设 id 在映射表中）
            // 或者 fields.threadId.DispatchThreadID → id
            // 这里保持简单：DispatchThreadID.X → dispatchThreadId.x
            return memberName switch
            {
                "DispatchThreadID" => "dispatchThreadId",
                "GroupThreadID" => "groupThreadId",
                "GroupID" => "groupId",
                _ => memberName,
            };
        }

        // 向量 swizzle：float4 的 .X/.Y/.Z/.W → .x/.y/.z/.w
        if (memberName is "X" or "Y" or "Z" or "W")
            return $"{exprStr}.{memberName.ToLowerInvariant()}";

        return $"{exprStr}.{memberName}";
    }

    // ─── 方法调用 ────────────────────────────────────────────

    private string VisitInvocation(InvocationExpressionSyntax node)
    {
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;
            var containingExpr = memberAccess.Expression;
            var args = string.Join(", ", node.ArgumentList.Arguments.Select(a => Visit(a.Expression)));

            // 尝试映射为内建函数
            string? containingType = containingExpr switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => Visit(ma),
                _ => null,
            };

            if (containingType is not null)
            {
                if (MethodIntrinsics.TryGetIntrinsic(containingType, methodName, out var slangFunc))
                {
                    return $"{slangFunc}({args})";
                }
            }

            // 始终尝试匹配"方法名"模式（不指定包含类型）
            if (MethodIntrinsics.TryGetIntrinsic("", methodName, out var slangFunc2))
            {
                return $"{slangFunc2}({args})";
            }

            // 资源方法（buffer[i] → RWStructuredBuffer[i] 等已在元素访问中处理）
            // 纹理方法：texture.Sample(sampler, coord)
            if (methodName is "Sample" or "Load")
            {
                return $"{Visit(containingExpr)}.{methodName.ToLowerInvariant()}({args})";
            }

            return ReportUnsupported(node, $"方法调用 {containingType}.{methodName}");
        }

        // 函数名直接调用（如 sin(x)）
        if (node.Expression is IdentifierNameSyntax funcName)
        {
            var args = string.Join(", ", node.ArgumentList.Arguments.Select(a => Visit(a.Expression)));
            return $"{funcName.Identifier.Text}({args})";
        }

        return ReportUnsupported(node, "复杂方法调用");
    }

    // ─── 二元运算 ────────────────────────────────────────────

    private string VisitBinary(BinaryExpressionSyntax node)
    {
        var left = Visit(node.Left);
        var right = Visit(node.Right);
        var op = node.Kind() switch
        {
            SyntaxKind.AddExpression => "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            SyntaxKind.EqualsExpression => "==",
            SyntaxKind.NotEqualsExpression => "!=",
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            SyntaxKind.GreaterThanOrEqualExpression => ">=",
            SyntaxKind.LogicalAndExpression => "&&",
            SyntaxKind.LogicalOrExpression => "||",
            SyntaxKind.BitwiseAndExpression => "&",
            SyntaxKind.BitwiseOrExpression => "|",
            SyntaxKind.ExclusiveOrExpression => "^",
            SyntaxKind.LeftShiftExpression => "<<",
            SyntaxKind.RightShiftExpression => ">>",
            SyntaxKind.CoalesceExpression => ReportUnsupported(node, "?? (null 合并)"),
            SyntaxKind.IsExpression => ReportUnsupported(node, "is"),
            SyntaxKind.AsExpression => ReportUnsupported(node, "as"),
            _ => ReportUnsupported(node, $"二元运算符 {node.Kind()}"),
        };

        // Slang 整数除法不会自动提升为 float，保持原样
        return $"({left} {op} {right})";
    }

    // ─── 一元运算 ────────────────────────────────────────────

    private string VisitPrefixUnary(PrefixUnaryExpressionSyntax node)
    {
        var operand = Visit(node.Operand);
        return node.Kind() switch
        {
            SyntaxKind.UnaryPlusExpression => $"(+{operand})",
            SyntaxKind.UnaryMinusExpression => $"(-{operand})",
            SyntaxKind.LogicalNotExpression => $"(!{operand})",
            SyntaxKind.BitwiseNotExpression => $"(~{operand})",
            SyntaxKind.PreIncrementExpression => $"(++{operand})",
            SyntaxKind.PreDecrementExpression => $"(--{operand})",
            SyntaxKind.AddressOfExpression => ReportUnsupported(node, "& (取地址)"),
            SyntaxKind.PointerIndirectionExpression => ReportUnsupported(node, "* (指针解引用)"),
            _ => ReportUnsupported(node, $"前缀一元 {node.Kind()}"),
        };
    }

    private string VisitPostfixUnary(PostfixUnaryExpressionSyntax node)
    {
        var operand = Visit(node.Operand);
        return node.Kind() switch
        {
            SyntaxKind.PostIncrementExpression => $"({operand}++)",
            SyntaxKind.PostDecrementExpression => $"({operand}--)",
            _ => ReportUnsupported(node, $"后缀一元 {node.Kind()}"),
        };
    }

    // ─── 类型转换 ────────────────────────────────────────────

    private string VisitCast(CastExpressionSyntax node)
    {
        var typeStr = TypeMap.ToSlangTypeString(node.Type);
        var operand = Visit(node.Expression);

        // Slang 使用 C 风格类型转换
        if (string.IsNullOrEmpty(typeStr))
            return $"({operand})";
        return $"({typeStr})({operand})";
    }

    // ─── new 表达式 ──────────────────────────────────────────

    private string VisitObjectCreation(ObjectCreationExpressionSyntax node)
    {
        var typeStr = TypeMap.ToSlangTypeString(node.Type);
        var args = string.Join(", ", node.ArgumentList?.Arguments.Select(a => Visit(a.Expression)) ?? Enumerable.Empty<string>());

        // new float4(x, y, z, w) → float4(x, y, z, w)
        if (!string.IsNullOrEmpty(typeStr))
            return $"{typeStr}({args})";

        return ReportUnsupported(node, $"new {node.Type}");
    }

    // ─── 元素访问（索引器）───────────────────────────────────────

    private string VisitElementAccess(ElementAccessExpressionSyntax node)
    {
        var expr = Visit(node.Expression);
        var index = string.Join(", ", node.ArgumentList.Arguments.Select(a => Visit(a.Expression)));
        return $"{expr}[{index}]";
    }

    // ─── 条件表达式 ──────────────────────────────────────────

    private string VisitConditional(ConditionalExpressionSyntax node)
    {
        var cond = Visit(node.Condition);
        var whenTrue = Visit(node.WhenTrue);
        var whenFalse = Visit(node.WhenFalse);
        return $"({cond} ? {whenTrue} : {whenFalse})";
    }

    // ─── 赋值表达式 ──────────────────────────────────────────

    private string VisitAssignment(AssignmentExpressionSyntax node)
    {
        var left = Visit(node.Left);
        var right = Visit(node.Right);
        var op = node.Kind() switch
        {
            SyntaxKind.SimpleAssignmentExpression => "=",
            SyntaxKind.AddAssignmentExpression => "+=",
            SyntaxKind.SubtractAssignmentExpression => "-=",
            SyntaxKind.MultiplyAssignmentExpression => "*=",
            SyntaxKind.DivideAssignmentExpression => "/=",
            SyntaxKind.ModuloAssignmentExpression => "%=",
            SyntaxKind.AndAssignmentExpression => "&=",
            SyntaxKind.OrAssignmentExpression => "|=",
            SyntaxKind.ExclusiveOrAssignmentExpression => "^=",
            SyntaxKind.LeftShiftAssignmentExpression => "<<=",
            SyntaxKind.RightShiftAssignmentExpression => ">>=",
            _ => ReportUnsupported(node, $"复合赋值 {node.Kind()}"),
        };
        return $"{left} {op} {right}";
    }

    // ─── default ──────────────────────────────────────────────

    private static string VisitDefault(DefaultExpressionSyntax node)
    {
        var typeStr = TypeMap.ToSlangTypeString(node.Type);
        if (!string.IsNullOrEmpty(typeStr))
            return $"({typeStr})0";
        return "0";
    }

    // ─── 错误报告 ────────────────────────────────────────────

    private string ReportUnsupported(ExpressionSyntax node, string description)
    {
        // 抛出异常，由 StatementTranslator 或 Emit 阶段捕获并生成 MSGEN010 诊断
        throw new TranslationException(node.GetLocation(), $"不支持的表达式: {description}");
    }
}
