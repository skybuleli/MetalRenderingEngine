using System.Text;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering;

/// <summary>
/// Phase 8C: 装饰器模式命令录制器。
/// 包裹任意 <see cref="ICommandRecorder"/>，将每个命令以人类可读文本写入 <see cref="TextWriter"/>。
/// 适用于开发调试（#if DEBUG 自动启用）。
/// </summary>
public sealed class LoggingCommandRecorder : ICommandRecorder
{
    private readonly ICommandRecorder _inner;
    private readonly TextWriter _log;
    private readonly bool _verbose;
    private readonly StringBuilder _buffer = new();
    private int _count;

    /// <param name="inner">被包裹的实际录制器（如 MetalCommandRecorder）。</param>
    /// <param name="log">日志输出目标（Console.Out / StringWriter / 文件）。</param>
    /// <param name="verbose">true=每个命令都立即写入；false=累积到 EndFrame 时一次性写入。</param>
    public LoggingCommandRecorder(ICommandRecorder inner, TextWriter log, bool verbose = false)
    {
        _inner = inner;
        _log = log;
        _verbose = verbose;
    }

    public int CommandCount => _inner.CommandCount;

    public string GetCommandLog() => _inner.GetCommandLog();

    private void Log(string message)
    {
        _buffer.AppendLine($"  [{_count}] {message}");
        _count++;
        if (_verbose) Flush();
    }

    private void Flush()
    {
        if (_buffer.Length > 0)
        {
            _log.Write(_buffer.ToString());
            _buffer.Clear();
        }
    }

    // ══════════ 帧控制 ══════════

    public void BeginFrame()
    {
        _buffer.AppendLine("--- BeginFrame ---");
        _count = 0;
        _inner.BeginFrame();
        if (_verbose) Flush();
    }

    public void EndFrame()
    {
        _buffer.AppendLine($"--- EndFrame ({_count} commands) ---");
        _inner.EndFrame();
        Flush();
    }

    // ══════════ Render Pass ══════════

    public void BeginRenderPass(in WMTRenderPassDesc passDesc) { Log("BeginRenderPass"); _inner.BeginRenderPass(passDesc); }
    public void EndRenderPass() { Log("EndRenderPass"); _inner.EndRenderPass(); }

    // ══════════ 管线状态 ══════════

    public void SetPipelineState(MetalRenderPipelineState pso) { Log($"SetPipelineState(handle={pso.Handle})"); _inner.SetPipelineState(pso); }

    // ══════════ 视口/裁剪 ══════════

    public void SetViewport(float x, float y, float width, float height, float znear = 0f, float zfar = 1f) { Log($"SetViewport({x},{y},{width}x{height})"); _inner.SetViewport(x, y, width, height, znear, zfar); }
    public void SetScissor(int x, int y, int width, int height) { Log($"SetScissor({x},{y},{width}x{height})"); _inner.SetScissor(x, y, width, height); }

    // ══════════ 光栅化状态 ══════════

    public void SetCullMode(MTLCullMode mode) { Log($"SetCullMode({mode})"); _inner.SetCullMode(mode); }
    public void SetFrontFacing(MTLWinding winding) { Log($"SetFrontFacing({winding})"); _inner.SetFrontFacing(winding); }
    public void SetDepthBias(float bias, float slopeScale, float clamp) { Log($"SetDepthBias({bias},{slopeScale},{clamp})"); _inner.SetDepthBias(bias, slopeScale, clamp); }
    public void SetDepthClipMode(MTLDepthClipMode mode) { Log($"SetDepthClipMode({mode})"); _inner.SetDepthClipMode(mode); }
    public void SetTriangleFillMode(MTLTriangleFillMode mode) { Log($"SetTriangleFillMode({mode})"); _inner.SetTriangleFillMode(mode); }

    // ══════════ 深度/模板状态 ══════════

    public void SetDepthStencilState(MetalDepthStencilState state) { Log($"SetDepthStencilState(handle={state.Handle})"); _inner.SetDepthStencilState(state); }
    public void SetStencilReference(uint front, uint back) { Log($"SetStencilReference(front={front},back={back})"); _inner.SetStencilReference(front, back); }

    // ══════════ 资源绑定 ══════════

    public void SetVertexBytes<T>(in T value, ulong index) where T : unmanaged { Log($"SetVertexBytes<{typeof(T).Name}>(index={index})"); _inner.SetVertexBytes(in value, index); }
    public void SetVertexBuffer(MetalBuffer buffer, ulong offset, ulong index) { Log($"SetVertexBuffer(handle={buffer.Handle},index={index})"); _inner.SetVertexBuffer(buffer, offset, index); }
    public void SetFragmentBytes<T>(in T value, ulong index) where T : unmanaged { Log($"SetFragmentBytes<{typeof(T).Name}>(index={index})"); _inner.SetFragmentBytes(in value, index); }
    public void SetFragmentBuffer(MetalBuffer buffer, ulong offset, ulong index) { Log($"SetFragmentBuffer(handle={buffer.Handle},index={index})"); _inner.SetFragmentBuffer(buffer, offset, index); }
    public void SetFragmentTexture(MetalTexture texture, ulong index) { Log($"SetFragmentTexture(handle={texture.Handle},index={index})"); _inner.SetFragmentTexture(texture, index); }
    public void UseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages) { Log($"UseResource(handle={resource.Handle},usage={usage},stages={stages})"); _inner.UseResource(resource, usage, stages); }

    // ══════════ 绘制 ══════════

    public void Draw(int primitiveType, ulong vertexStart, ulong vertexCount, ulong instanceCount = 1) { Log($"Draw(verts={vertexCount},instances={instanceCount})"); _inner.Draw(primitiveType, vertexStart, vertexCount, instanceCount); }
    public void DrawIndexed(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount = 1) { Log($"DrawIndexed(indices={indexCount},instances={instanceCount})"); _inner.DrawIndexed(indexCount, is32Bit, indexBuffer, indexBufferOffset, instanceCount); }
    public void DrawIndirect(MetalBuffer indirectBuffer, ulong offset = 0) { Log($"DrawIndirect(offset={offset})"); _inner.DrawIndirect(indirectBuffer, offset); }
    public void DrawIndexedIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0) { Log($"DrawIndexedIndirect(offset={offset})"); _inner.DrawIndexedIndirect(indexBuffer, indirectBuffer, offset); }

    // ══════════ 同步 ══════════

    public void WaitForFence(MetalFence fence, MTLRenderStages beforeStages) { Log($"WaitForFence(stages={beforeStages})"); _inner.WaitForFence(fence, beforeStages); }
    public void UpdateFence(MetalFence fence, MTLRenderStages afterStages) { Log($"UpdateFence(stages={afterStages})"); _inner.UpdateFence(fence, afterStages); }

    // ══════════ 释放 ══════════

    public void Dispose()
    {
        Flush();
        _inner.Dispose();
    }
}
