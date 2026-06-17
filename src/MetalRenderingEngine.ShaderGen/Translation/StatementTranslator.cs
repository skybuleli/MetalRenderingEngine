using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MetalRenderingEngine.ShaderGen.Translation;

/// <summary>
/// C# 语句 → Slang 语句递归翻译器。
/// 依赖 ExpressionTranslator 处理子表达式。
/// </summary>
internal sealed class StatementTranslator
{
    private readonly ExpressionTranslator _exprTranslator;
    private readonly HashSet<string> _localVariables;

    public StatementTranslator(
        Dictionary<string, string>? threadIdParamMappings = null)
    {
        _localVariables = new HashSet<string>();
        _exprTranslator = new ExpressionTranslator(
            threadIdParamMappings ?? new Dictionary<string, string>(),
            _localVariables);
    }

    /// <summary>翻译语句块（方法体）。</summary>
    public string TranslateBlock(BlockSyntax? block)
    {
        if (block is null) return "{}";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");

        foreach (var statement in block.Statements)
        {
            var translated = Visit(statement);
            if (!string.IsNullOrEmpty(translated))
            {
                // 每行缩进 4 空格
                foreach (var line in translated.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.AppendLine($"    {line.TrimEnd()}");
                    else
                        sb.AppendLine();
                }
            }
        }

        sb.Append("}");
        return sb.ToString();
    }

    /// <summary>翻译单个语句（不含额外缩进，由调用方控制缩进）。</summary>
    public string TranslateSingle(StatementSyntax statement)
    {
        return Visit(statement);
    }

    private string Visit(StatementSyntax node)
    {
        return node switch
        {
            BlockSyntax block => TranslateBlock(block),
            ReturnStatementSyntax ret => VisitReturn(ret),
            LocalDeclarationStatementSyntax local => VisitLocalDeclaration(local),
            ExpressionStatementSyntax expr => VisitExpressionStatement(expr),
            IfStatementSyntax ifStmt => VisitIf(ifStmt),
            ForStatementSyntax forStmt => VisitFor(forStmt),
            WhileStatementSyntax whileStmt => VisitWhile(whileStmt),
            DoStatementSyntax doStmt => VisitDo(doStmt),
            BreakStatementSyntax => "break;",
            FixedStatementSyntax => ReportUnsupported(node, "fixed"),
            UsingStatementSyntax => ReportUnsupported(node, "using"),
            LockStatementSyntax => ReportUnsupported(node, "lock"),
            UnsafeStatementSyntax => ReportUnsupported(node, "unsafe"),
            CheckedStatementSyntax => ReportUnsupported(node, "checked/unchecked"),
            YieldStatementSyntax => ReportUnsupported(node, "yield"),
            TryStatementSyntax => ReportUnsupported(node, "try/catch"),
            ForEachStatementSyntax => ReportUnsupported(node, "foreach"),
            ForEachVariableStatementSyntax => ReportUnsupported(node, "foreach 解构"),
            SwitchStatementSyntax => ReportUnsupported(node, "switch"),
            ThrowStatementSyntax => ReportUnsupported(node, "throw"),
            GotoStatementSyntax => ReportUnsupported(node, "goto"),
            LabeledStatementSyntax => ReportUnsupported(node, "label"),
            EmptyStatementSyntax => "",

            _ => ReportUnsupported(node, $"未知语句类型 ({node.Kind()})"),
        };
    }

    // ─── return ───────────────────────────────────────────────

    private string VisitReturn(ReturnStatementSyntax node)
    {
        if (node.Expression is null)
            return "return;";
        var exprStr = _exprTranslator.Translate(node.Expression);
        return $"return {exprStr};";
    }

    // ─── 局部变量声明 ────────────────────────────────────────

    private string VisitLocalDeclaration(LocalDeclarationStatementSyntax node)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var variable in node.Declaration.Variables)
        {
            var typeStr = TypeMap.ToSlangTypeString(node.Declaration.Type);
            var varName = variable.Identifier.Text;
            _localVariables.Add(varName); // 注册到作用域

            if (variable.Initializer is not null)
            {
                var initStr = _exprTranslator.Translate(variable.Initializer.Value);
                sb.AppendLine($"{typeStr} {varName} = {initStr};");
            }
            else
            {
                sb.AppendLine($"{typeStr} {varName};");
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    // ─── 表达式语句 ───────────────────────────────────────────

    private string VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var exprStr = _exprTranslator.Translate(node.Expression);
        return $"{exprStr};";
    }

    // ─── if/else ──────────────────────────────────────────────

    private string VisitIf(IfStatementSyntax node)
    {
        var cond = _exprTranslator.Translate(node.Condition);
        var sb = new System.Text.StringBuilder();

        if (node.Statement is BlockSyntax ifBlock)
        {
            var blockStr = TranslateBlock(ifBlock);
            sb.AppendLine($"if ({cond})");
            sb.AppendLine(blockStr);
        }
        else
        {
            var stmtStr = Visit(node.Statement);
            sb.AppendLine($"if ({cond})");
            sb.AppendLine("{");
            sb.AppendLine($"    {stmtStr}");
            sb.Append("}");
        }

        if (node.Else is not null)
        {
            var elseClause = node.Else;
            sb.AppendLine();
            sb.Append("else ");
            if (elseClause.Statement is IfStatementSyntax nestedIf)
            {
                sb.Append(VisitIf(nestedIf).TrimStart());
            }
            else if (elseClause.Statement is BlockSyntax elseBlock)
            {
                sb.AppendLine();
                sb.Append(TranslateBlock(elseBlock));
            }
            else
            {
                sb.AppendLine();
                sb.Append("{");
                sb.AppendLine($"    {Visit(elseClause.Statement)}");
                sb.Append("}");
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    // ─── for ──────────────────────────────────────────────────

    private string VisitFor(ForStatementSyntax node)
    {
        var sb = new System.Text.StringBuilder();

        // 初始化
        string init;
        if (node.Declaration is not null)
        {
            // 注册循环变量到作用域
            foreach (var v in node.Declaration.Variables)
                _localVariables.Add(v.Identifier.Text);

            var typeStr = TypeMap.ToSlangTypeString(node.Declaration.Type);
            var decls = string.Join(", ", node.Declaration.Variables.Select(v =>
            {
                var initStr = v.Initializer is not null
                    ? _exprTranslator.Translate(v.Initializer.Value)
                    : "";
                return string.IsNullOrEmpty(initStr)
                    ? $"{v.Identifier.Text}"
                    : $"{v.Identifier.Text} = {initStr}";
            }));
            init = $"{typeStr} {decls}";
        }
        else
        {
            init = string.Join(", ", node.Initializers.Select(i => _exprTranslator.Translate(i)));
        }

        // 条件
        var cond = node.Condition is not null
            ? _exprTranslator.Translate(node.Condition)
            : "";

        // 迭代
        var iter = string.Join(", ", node.Incrementors.Select(i => _exprTranslator.Translate(i)));

        var header = $"for ({init}; {cond}; {iter})";
        sb.AppendLine(header);

        if (node.Statement is BlockSyntax forBlock)
        {
            sb.AppendLine(TranslateBlock(forBlock));
        }
        else
        {
            sb.AppendLine("{");
            sb.AppendLine($"    {Visit(node.Statement)}");
            sb.Append("}");
        }

        return sb.ToString().TrimEnd('\n');
    }

    // ─── while ────────────────────────────────────────────────

    private string VisitWhile(WhileStatementSyntax node)
    {
        var cond = _exprTranslator.Translate(node.Condition);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"while ({cond})");

        if (node.Statement is BlockSyntax whileBlock)
        {
            sb.AppendLine(TranslateBlock(whileBlock));
        }
        else
        {
            sb.AppendLine("{");
            sb.AppendLine($"    {Visit(node.Statement)}");
            sb.Append("}");
        }

        return sb.ToString().TrimEnd('\n');
    }

    // ─── do/while ─────────────────────────────────────────────

    private string VisitDo(DoStatementSyntax node)
    {
        var cond = _exprTranslator.Translate(node.Condition);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("do");

        if (node.Statement is BlockSyntax doBlock)
        {
            sb.AppendLine(TranslateBlock(doBlock));
        }
        else
        {
            sb.AppendLine("{");
            sb.AppendLine($"    {Visit(node.Statement)}");
            sb.Append("}");
        }

        sb.AppendLine($"while ({cond});");
        return sb.ToString().TrimEnd('\n');
    }

    // ─── 错误报告 ────────────────────────────────────────────

    private static string ReportUnsupported(StatementSyntax node, string description)
    {
        throw new TranslationException(node.GetLocation(), $"不支持的语句: {description}");
    }
}

/// <summary>
/// 翻译过程中抛出的异常，由 Emit 阶段捕获并转换为 MSGEN 诊断。
/// </summary>
internal sealed class TranslationException : Exception
{
    public Location? SourceLocation { get; }

    public TranslationException(Location? location, string message)
        : base(message)
    {
        SourceLocation = location;
    }
}
