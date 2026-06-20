using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering;

/// <summary>
/// Phase 8: Metal 后端命令录制器。
///
/// 关键设计：**执行路径走 MetalCommandList 批量回放**，不绕过它。
/// 每个命令方法录入 MetalCommandList（批量缓冲），在 EndRenderPass 时
/// 单次 P/Invoke 回放全部命令——保住 Phase 6 的 99.5% P/Invoke 优化。
///
/// 当前真实边界见 docs/command-recorder-boundaries.md。
/// 仍未进入 MetalCommandList 的低频命令暂时直接调 encoder：
/// SetScissor / SetVertexBuffer / SetFragmentBuffer / SetFragmentTexture。
/// 这是显式折中，不是架构漂移。
/// </summary>
public sealed class MetalCommandRecorder : ICommandRecorder
{
    private readonly MetalDevice _device;
    private readonly MetalCommandQueue _queue;

    private MetalCommandBuffer? _commandBuffer;
    private MetalRenderEncoder? _renderEncoder;
    private MetalCommandList? _renderList;

    /// <summary>已录制的命令数（用于可观测性）。</summary>
    public int CommandCount { get; private set; }

    /// <summary>最近一次 render pass 触发的 MetalCommandList replay 次数（测试/性能回归守护用）。</summary>
    internal int LastRenderReplayCallCount { get; private set; }

    public MetalCommandRecorder(MetalDevice device)
    {
        _device = device;
        _queue = device.NewCommandQueue();
    }

    /// <summary>获取当前 command buffer（用于 PresentDrawable 等窗口操作）。</summary>
    public MetalCommandBuffer CommandBuffer
        => _commandBuffer ?? throw new InvalidOperationException("BeginFrame() 后才能访问 CommandBuffer。");

    /// <summary>呈现 drawable 到屏幕（窗口模式，在 EndFrame 前调用）。</summary>
    public void PresentDrawable(MetalDrawable drawable)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        _commandBuffer?.PresentDrawable(drawable);
    }

    // ══════════ 帧控制 ══════════

    public void BeginFrame()
    {
        _commandBuffer = _queue.CommandBuffer();
        CommandCount = 0;
        LastRenderReplayCallCount = 0;
    }

    public void EndFrame()
    {
        // 如果 render pass 未显式结束，这里兜底
        if (_renderEncoder is not null)
            EndRenderPass();

        _commandBuffer?.Commit();
        _commandBuffer?.WaitUntilCompleted();
        _commandBuffer?.Dispose();
        _commandBuffer = null;
    }

    /// <summary>提交 command buffer 但不等待完成（窗口模式用，配合 PresentDrawable）。</summary>
    public void Submit()
    {
        if (_renderEncoder is not null)
            EndRenderPass();

        _commandBuffer?.Commit();
        _commandBuffer?.Dispose();
        _commandBuffer = null;
    }

    // ══════════ Render Pass ══════════

    public void BeginRenderPass(in WMTRenderPassDesc passDesc)
    {
        if (_commandBuffer is null)
            throw new InvalidOperationException("BeginFrame() 必须在 BeginRenderPass() 之前调用。");

        _renderEncoder = _commandBuffer.RenderCommandEncoder(passDesc);
        _renderList = new MetalCommandList();
        CommandCount = 0;
    }

    public void EndRenderPass()
    {
        if (_renderEncoder is null || _renderList is null)
            throw new InvalidOperationException("BeginRenderPass() 必须在 EndRenderPass() 之前调用。");

        // 单次 P/Invoke 回放全部批量命令
        _renderList.ReplayRender(_renderEncoder);
        LastRenderReplayCallCount = _renderList.RenderReplayCallCount;
        _renderList.Dispose();
        _renderList = null;

        _renderEncoder.EndEncoding();
        _renderEncoder.Dispose();
        _renderEncoder = null;
    }

    // ══════════ 管线状态 ══════════

    public void SetPipelineState(MetalRenderPipelineState pso)
    {
        ArgumentNullException.ThrowIfNull(pso);
        EnsureInPass();
        _renderList!.RecordSetRenderPipelineState(pso);
        CommandCount++;
    }

    // ══════════ 视口/裁剪 ══════════

    public void SetViewport(float x, float y, float width, float height, float znear = 0f, float zfar = 1f)
    {
        EnsureInPass();
        _renderList!.RecordSetViewport(x, y, width, height, znear, zfar);
        CommandCount++;
    }

    public void SetScissor(int x, int y, int width, int height)
    {
        EnsureInPass();
        // 低频状态，当前保留直通路径；同步约束记录在 docs/command-recorder-boundaries.md。
        _renderEncoder!.SetScissorRect(x, y, width, height);
        CommandCount++;
    }

    // ══════════ 光栅化状态 ══════════

    public void SetCullMode(MTLCullMode mode)
    {
        EnsureInPass();
        _renderList!.RecordSetCullMode(mode);
        CommandCount++;
    }

    public void SetFrontFacing(MTLWinding winding)
    {
        EnsureInPass();
        _renderList!.RecordSetFrontFacing(winding);
        CommandCount++;
    }

    public void SetDepthBias(float bias, float slopeScale, float clamp)
    {
        EnsureInPass();
        _renderList!.RecordSetDepthBias(bias, slopeScale, clamp);
        CommandCount++;
    }

    public void SetDepthClipMode(MTLDepthClipMode mode)
    {
        EnsureInPass();
        _renderList!.RecordSetDepthClipMode(mode);
        CommandCount++;
    }

    public void SetTriangleFillMode(MTLTriangleFillMode mode)
    {
        EnsureInPass();
        _renderList!.RecordSetTriangleFillMode(mode);
        CommandCount++;
    }

    // ══════════ 深度/模板状态 ══════════

    public void SetDepthStencilState(MetalDepthStencilState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureInPass();
        _renderList!.RecordSetDepthStencilState(state);
        CommandCount++;
    }

    public void SetStencilReference(uint front, uint back)
    {
        EnsureInPass();
        _renderList!.RecordSetStencilReference(front, back);
        CommandCount++;
    }

    // ══════════ 资源绑定 ══════════

    public void SetVertexBytes<T>(in T value, ulong index) where T : unmanaged
    {
        EnsureInPass();
        _renderList!.RecordSetVertexBytes(in value, index);
        CommandCount++;
    }

    public void SetVertexBytes(ReadOnlySpan<byte> data, ulong index)
    {
        EnsureInPass();
        _renderList!.RecordSetVertexBytes(data, index);
        CommandCount++;
    }

    public void SetVertexBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureInPass();
        // 低频状态，当前保留直通路径；同步约束记录在 docs/command-recorder-boundaries.md。
        _renderEncoder!.SetVertexBuffer(buffer, offset, index);
        CommandCount++;
    }

    public void SetFragmentBytes<T>(in T value, ulong index) where T : unmanaged
    {
        EnsureInPass();
        _renderList!.RecordSetFragmentBytes(in value, index);
        CommandCount++;
    }

    public void SetFragmentBytes(ReadOnlySpan<byte> data, ulong index)
    {
        EnsureInPass();
        _renderList!.RecordSetFragmentBytes(data, index);
        CommandCount++;
    }

    public void SetFragmentBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureInPass();
        // 低频状态，当前保留直通路径；同步约束记录在 docs/command-recorder-boundaries.md。
        _renderEncoder!.SetFragmentBuffer(buffer, offset, index);
        CommandCount++;
    }

    public void SetFragmentTexture(MetalTexture texture, ulong index)
    {
        ArgumentNullException.ThrowIfNull(texture);
        EnsureInPass();
        // 低频状态，当前保留直通路径；同步约束记录在 docs/command-recorder-boundaries.md。
        _renderEncoder!.SetFragmentTexture(texture, index);
        CommandCount++;
    }

    public void UseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureInPass();
        _renderList!.RecordUseResource(resource, usage, stages);
        CommandCount++;
    }

    // ══════════ 绘制 ══════════

    public void Draw(int primitiveType, ulong vertexStart, ulong vertexCount, ulong instanceCount = 1)
    {
        EnsureInPass();
        _renderList!.RecordDrawPrimitives(primitiveType, vertexStart, vertexCount, instanceCount);
        CommandCount++;
    }

    public void DrawIndexed(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        EnsureInPass();
        _renderList!.RecordDrawIndexedPrimitives(indexCount, is32Bit, indexBuffer, indexBufferOffset, instanceCount);
        CommandCount++;
    }

    public void DrawIndirect(MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        EnsureInPass();
        _renderList!.RecordDrawPrimitivesIndirect(indirectBuffer, offset);
        CommandCount++;
    }

    public void DrawIndexedIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        EnsureInPass();
        _renderList!.RecordDrawIndexedPrimitivesIndirect(indexBuffer, indirectBuffer, offset);
        CommandCount++;
    }

    // ══════════ 同步 ══════════

    public void WaitForFence(MetalFence fence, MTLRenderStages beforeStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        EnsureInPass();
        _renderEncoder!.WaitForFence(fence, beforeStages);
        CommandCount++;
    }

    public void UpdateFence(MetalFence fence, MTLRenderStages afterStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        EnsureInPass();
        _renderEncoder!.UpdateFence(fence, afterStages);
        CommandCount++;
    }

    // ══════════ 可观测性 ══════════

    /// <summary>MetalCommandRecorder 不维护文本日志，返回空字符串。</summary>
    public string GetCommandLog() => string.Empty;

    // ══════════ 资源释放 ══════════

    public void Dispose()
    {
        _renderList?.Dispose();
        _renderEncoder?.Dispose();
        _commandBuffer?.Dispose();
        _queue.Dispose();
    }

    // ══════════ 内部辅助 ══════════

    private void EnsureInPass()
    {
        if (_renderEncoder is null || _renderList is null)
            throw new InvalidOperationException("必须在 BeginRenderPass() .. EndRenderPass() 之间调用命令方法。");
    }
}
