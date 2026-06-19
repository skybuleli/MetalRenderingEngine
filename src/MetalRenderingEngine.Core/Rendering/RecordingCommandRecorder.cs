using System.Text;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering;

/// <summary>
/// Phase 8C: 命令记录的抽象表示（Memento 模式）。
/// 每条命令是一个不可变值对象，可序列化、比较、回放。
/// </summary>
public abstract class RecordedCommand
{
    /// <summary>命令类型名称（用于日志和 Diff）。</summary>
    public abstract string Name { get; }

    /// <summary>追加人类可读日志行。</summary>
    public abstract void AppendLog(StringBuilder sb);
}

// ── 具体命令记录（精选常用命令；完整覆盖可按需扩展） ──

public sealed class SetPipelineStateCommand : RecordedCommand
{
    public nuint Handle { get; }
    public SetPipelineStateCommand(nuint handle) => Handle = handle;
    public override string Name => "SetPipelineState";
    public override void AppendLog(StringBuilder sb) => sb.AppendLine($"  SetPipelineState(handle={Handle})");
}

public sealed class SetViewportCommand : RecordedCommand
{
    public float X { get; } public float Y { get; } public float W { get; } public float H { get; }
    public float Znear { get; } public float Zfar { get; }
    public SetViewportCommand(float x, float y, float w, float h, float zn, float zf) { X=x; Y=y; W=w; H=h; Znear=zn; Zfar=zf; }
    public override string Name => "SetViewport";
    public override void AppendLog(StringBuilder sb) => sb.AppendLine($"  SetViewport({X},{Y},{W}x{H})");
}

public sealed class SetCullModeCommand : RecordedCommand
{
    public MTLCullMode Mode { get; }
    public SetCullModeCommand(MTLCullMode mode) => Mode = mode;
    public override string Name => "SetCullMode";
    public override void AppendLog(StringBuilder sb) => sb.AppendLine($"  SetCullMode({Mode})");
}

public sealed class SetDepthStencilStateCommand : RecordedCommand
{
    public nuint Handle { get; }
    public SetDepthStencilStateCommand(nuint handle) => Handle = handle;
    public override string Name => "SetDepthStencilState";
    public override void AppendLog(StringBuilder sb) => sb.AppendLine($"  SetDepthStencilState(handle={Handle})");
}

public sealed class DrawCommand : RecordedCommand
{
    public int PrimitiveType { get; } public ulong VertexStart { get; }
    public ulong VertexCount { get; } public ulong InstanceCount { get; }
    public DrawCommand(int pt, ulong vs, ulong vc, ulong ic) { PrimitiveType=pt; VertexStart=vs; VertexCount=vc; InstanceCount=ic; }
    public override string Name => "Draw";
    public override void AppendLog(StringBuilder sb) => sb.AppendLine($"  Draw(verts={VertexCount},instances={InstanceCount})");
}

public sealed class UseResourceCommand : RecordedCommand
{
    public nuint Handle { get; } public MTLResourceUsage Usage { get; } public MTLRenderStages Stages { get; }
    public UseResourceCommand(nuint h, MTLResourceUsage u, MTLRenderStages s) { Handle=h; Usage=u; Stages=s; }
    public override string Name => "UseResource";
    public override void AppendLog(StringBuilder sb) => sb.AppendLine($"  UseResource(handle={Handle},usage={Usage})");
}

/// <summary>
/// Phase 8C: Memento 模式录制器。
/// 将命令捕获到内存列表（不执行），用于测试/调试/golden-frame 对比。
/// 可通过 <see cref="CommandReplayer"/> 回放到实际录制器执行。
/// </summary>
public sealed class RecordingCommandRecorder : ICommandRecorder
{
    private readonly List<RecordedCommand> _commands = new(256);
    private readonly StringBuilder _log = new();

    /// <summary>已捕获的命令列表（只读视图）。</summary>
    public IReadOnlyList<RecordedCommand> Commands => _commands;

    public int CommandCount => _commands.Count;

    public string GetCommandLog()
    {
        var sb = new StringBuilder();
        foreach (var cmd in _commands) cmd.AppendLog(sb);
        return sb.ToString();
    }

    private void Capture(RecordedCommand cmd)
    {
        _commands.Add(cmd);
        cmd.AppendLog(_log);
    }

    /// <summary>与另一个录制器的命令序列做 Diff，返回差异行列表（空=完全一致）。</summary>
    public List<string> Diff(RecordingCommandRecorder other)
    {
        var diffs = new List<string>();
        int max = Math.Max(_commands.Count, other._commands.Count);
        for (int i = 0; i < max; i++)
        {
            var a = i < _commands.Count ? _commands[i] : null;
            var b = i < other._commands.Count ? other._commands[i] : null;

            // 比较完整日志输出（含参数值）
            var aSb = new StringBuilder();
            var bSb = new StringBuilder();
            a?.AppendLog(aSb);
            b?.AppendLog(bSb);
            string aLog = aSb.ToString().TrimEnd();
            string bLog = bSb.ToString().TrimEnd();

            if (aLog != bLog)
                diffs.Add($"  [{i}] {aLog} vs {bLog}");
        }
        return diffs;
    }

    // ══════════ ICommandRecorder 实现（仅捕获，不执行） ══════════

    public void BeginFrame() { /* 无操作 */ }
    public void EndFrame() { /* 无操作 */ }
    public void BeginRenderPass(in WMTRenderPassDesc passDesc) { /* 无操作 */ }
    public void EndRenderPass() { /* 无操作 */ }

    public void SetPipelineState(MetalRenderPipelineState pso) => Capture(new SetPipelineStateCommand(pso.Handle));
    public void SetViewport(float x, float y, float width, float height, float znear = 0f, float zfar = 1f) => Capture(new SetViewportCommand(x, y, width, height, znear, zfar));
    public void SetScissor(int x, int y, int width, int height) { /* 不捕获 */ }
    public void SetCullMode(MTLCullMode mode) => Capture(new SetCullModeCommand(mode));
    public void SetFrontFacing(MTLWinding winding) { /* 不捕获 */ }
    public void SetDepthBias(float bias, float slopeScale, float clamp) { /* 不捕获 */ }
    public void SetDepthClipMode(MTLDepthClipMode mode) { /* 不捕获 */ }
    public void SetTriangleFillMode(MTLTriangleFillMode mode) { /* 不捕获 */ }
    public void SetDepthStencilState(MetalDepthStencilState state) => Capture(new SetDepthStencilStateCommand(state.Handle));
    public void SetStencilReference(uint front, uint back) { /* 不捕获 */ }

    public void SetVertexBytes<T>(in T value, ulong index) where T : unmanaged { /* 不捕获 */ }
    public void SetVertexBuffer(MetalBuffer buffer, ulong offset, ulong index) { /* 不捕获 */ }
    public void SetFragmentBytes<T>(in T value, ulong index) where T : unmanaged { /* 不捕获 */ }
    public void SetFragmentBuffer(MetalBuffer buffer, ulong offset, ulong index) { /* 不捕获 */ }
    public void SetFragmentTexture(MetalTexture texture, ulong index) { /* 不捕获 */ }
    public void UseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages) => Capture(new UseResourceCommand(resource.Handle, usage, stages));

    public void Draw(int primitiveType, ulong vertexStart, ulong vertexCount, ulong instanceCount = 1) => Capture(new DrawCommand(primitiveType, vertexStart, vertexCount, instanceCount));
    public void DrawIndexed(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount = 1) { /* 不捕获 */ }
    public void DrawIndirect(MetalBuffer indirectBuffer, ulong offset = 0) { /* 不捕获 */ }
    public void DrawIndexedIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0) { /* 不捕获 */ }

    public void WaitForFence(MetalFence fence, MTLRenderStages beforeStages) { /* 不捕获 */ }
    public void UpdateFence(MetalFence fence, MTLRenderStages afterStages) { /* 不捕获 */ }

    public void Dispose() { _commands.Clear(); _log.Clear(); }
}

/// <summary>
/// Phase 8C: 命令回放器。将 <see cref="RecordingCommandRecorder"/> 捕获的命令
/// 回放到实际录制器（如 MetalCommandRecorder）执行。
/// </summary>
public static class CommandReplayer
{
    /// <summary>将 source 中捕获的命令回放到 target 执行。</summary>
    public static void Replay(RecordingCommandRecorder source, ICommandRecorder target)
    {
        foreach (var cmd in source.Commands)
        {
            switch (cmd)
            {
                case SetPipelineStateCommand c: target.SetPipelineState(new MetalRenderPipelineState(c.Handle)); break;
                case SetViewportCommand c: target.SetViewport(c.X, c.Y, c.W, c.H, c.Znear, c.Zfar); break;
                case SetCullModeCommand c: target.SetCullMode(c.Mode); break;
                case SetDepthStencilStateCommand c: target.SetDepthStencilState(new MetalDepthStencilState(c.Handle)); break;
                case UseResourceCommand c: target.UseResource(new MetalBuffer(c.Handle, 0), c.Usage, c.Stages); break;
                case DrawCommand c: target.Draw(c.PrimitiveType, c.VertexStart, c.VertexCount, c.InstanceCount); break;
            }
        }
    }
}
