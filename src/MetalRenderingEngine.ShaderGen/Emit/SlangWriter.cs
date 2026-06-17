using System;
using System.Text;

namespace MetalRenderingEngine.ShaderGen.Emit;

/// <summary>
/// 带缩进的 Slang 代码写入器。
/// 管理缩进层级，自动添加换行符和缩进前缀。
/// </summary>
internal sealed class SlangWriter
{
    private readonly StringBuilder _sb = new();
    private int _indentLevel;

    public int IndentLevel => _indentLevel;

    public SlangWriter(int initialIndent = 0)
    {
        _indentLevel = initialIndent;
    }

    /// <summary>增加缩进。</summary>
    public void Indent() => _indentLevel++;

    /// <summary>减少缩进。</summary>
    public void Unindent()
    {
        if (_indentLevel > 0) _indentLevel--;
    }

    /// <summary>写入缩进前缀 + 字符串。</summary>
    public void Write(string text)
    {
        if (_sb.Length == 0 || _sb[_sb.Length - 1] == '\n')
            _sb.Append(new string(' ', _indentLevel * 4));
        _sb.Append(text);
    }

    /// <summary>写入行（自动添加换行符 + 缩进前缀）。</summary>
    public void WriteLine(string line = "")
    {
        if (line.Length == 0)
        {
            _sb.AppendLine();
            return;
        }

        _sb.Append(new string(' ', _indentLevel * 4));
        _sb.AppendLine(line);
    }

    /// <summary>写入块：写入 "{", 缩进, 返回 IDisposable 在 Dispose 时取消缩进并写入 "}"。</summary>
    public BlockScope BeginBlock()
    {
        WriteLine("{");
        _indentLevel++;
        return new BlockScope(this);
    }

    /// <summary>获取完整输出。</summary>
    public override string ToString() => _sb.ToString();

    /// <summary>Dispose 时自动取消缩进并写入 "}"。</summary>
    internal sealed class BlockScope : IDisposable
    {
        private readonly SlangWriter _writer;
        private bool _disposed;

        public BlockScope(SlangWriter writer) => _writer = writer;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _writer._indentLevel--;
            _writer.WriteLine("}");
        }
    }
}
