using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering;

/// <summary>
/// Metal 4 后端命令录制器。
/// 直接驱动 command buffer / render encoder / compute encoder，不再走旧的回放链表。
/// </summary>
public sealed class MetalCommandRecorder : ICommandRecorder
{
    private readonly MetalDevice _device;
    private readonly MetalCommandQueue _queue;

    private MetalCommandBuffer? _commandBuffer;
    private MetalRenderEncoder? _renderEncoder;
    private MetalComputeCommandEncoder? _computeEncoder;
    private MetalDrawable? _pendingDrawable;

    /// <summary>已录制的命令数（用于可观测性）。</summary>
    public int CommandCount { get; private set; }

    /// <summary>最近一次渲染回放触发次数；兼容旧测试断言。</summary>
    public int LastRenderReplayCallCount { get; private set; }

    /// <summary>最近一次计算回放触发次数；兼容旧测试断言。</summary>
    public int LastComputeReplayCallCount { get; private set; }

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
        _pendingDrawable = drawable;
        _commandBuffer?.PresentDrawable(drawable);
    }

    public void BeginFrame()
    {
        _commandBuffer = _queue.CommandBuffer();
        _renderEncoder = null;
        _computeEncoder = null;
        _pendingDrawable = null;
        CommandCount = 0;
        LastRenderReplayCallCount = 0;
        LastComputeReplayCallCount = 0;
    }

    public void EndFrame()
    {
        if (_renderEncoder is not null)
            EndRenderPass();

        _commandBuffer?.Commit();
        _commandBuffer?.WaitUntilCompleted();
        _commandBuffer?.Dispose();
        _commandBuffer = null;
    }

    public void Submit()
    {
        if (_renderEncoder is not null)
            EndRenderPass();

        _commandBuffer?.Commit();
        _commandBuffer?.Dispose();
        _commandBuffer = null;
    }

    public void BeginRenderPass(in WMTRenderPassDesc passDesc)
    {
        if (_commandBuffer is null)
            throw new InvalidOperationException("BeginFrame() 必须在 BeginRenderPass() 之前调用。");

        _renderEncoder = _commandBuffer.RenderCommandEncoder(passDesc);
        _computeEncoder = null;
        CommandCount = 0;
    }

    public void EndRenderPass()
    {
        if (_renderEncoder is null)
            throw new InvalidOperationException("BeginRenderPass() 必须在 EndRenderPass() 之前调用。");

        _renderEncoder.EndEncoding();
        _renderEncoder.Dispose();
        _renderEncoder = null;
        LastRenderReplayCallCount++;
    }

    public void SetPipelineState(MetalRenderPipelineState pso)
    {
        ArgumentNullException.ThrowIfNull(pso);
        EnsureRenderPass();
        _renderEncoder!.SetRenderPipelineState(pso);
        CommandCount++;
    }

    public void SetViewport(float x, float y, float width, float height, float znear = 0f, float zfar = 1f)
    {
        EnsureRenderPass();
        _renderEncoder!.SetViewport(x, y, width, height, znear, zfar);
        CommandCount++;
    }

    public void SetScissor(int x, int y, int width, int height)
    {
        EnsureRenderPass();
        _renderEncoder!.SetScissorRect(x, y, width, height);
        CommandCount++;
    }

    public void SetCullMode(MTLCullMode mode)
    {
        EnsureRenderPass();
        _renderEncoder!.SetCullMode(mode);
        CommandCount++;
    }

    public void SetFrontFacing(MTLWinding winding)
    {
        EnsureRenderPass();
        _renderEncoder!.SetFrontFacing(winding);
        CommandCount++;
    }

    public void SetDepthBias(float bias, float slopeScale, float clamp)
    {
        EnsureRenderPass();
        _renderEncoder!.SetDepthBias(bias, slopeScale, clamp);
        CommandCount++;
    }

    public void SetDepthClipMode(MTLDepthClipMode mode)
    {
        EnsureRenderPass();
        _renderEncoder!.SetDepthClipMode(mode);
        CommandCount++;
    }

    public void SetTriangleFillMode(MTLTriangleFillMode mode)
    {
        EnsureRenderPass();
        _renderEncoder!.SetTriangleFillMode(mode);
        CommandCount++;
    }

    public void SetDepthStencilState(MetalDepthStencilState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureRenderPass();
        _renderEncoder!.SetDepthStencilState(state);
        CommandCount++;
    }

    public void SetStencilReference(uint front, uint back)
    {
        EnsureRenderPass();
        _renderEncoder!.SetStencilReference(front, back);
        CommandCount++;
    }

    public void SetVertexBytes<T>(in T value, ulong index) where T : unmanaged
    {
        EnsureRenderPass();
        _renderEncoder!.SetVertexBytes(in value, index);
        CommandCount++;
    }

    public void SetVertexBytes(ReadOnlySpan<byte> data, ulong index)
    {
        EnsureRenderPass();
        _renderEncoder!.SetVertexBytes(data, index);
        CommandCount++;
    }

    public void SetVertexBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureRenderPass();
        _renderEncoder!.SetVertexBuffer(buffer, offset, index);
        CommandCount++;
    }

    public void SetFragmentBytes<T>(in T value, ulong index) where T : unmanaged
    {
        EnsureRenderPass();
        _renderEncoder!.SetFragmentBytes(in value, index);
        CommandCount++;
    }

    public void SetFragmentBytes(ReadOnlySpan<byte> data, ulong index)
    {
        EnsureRenderPass();
        _renderEncoder!.SetFragmentBytes(data, index);
        CommandCount++;
    }

    public void SetFragmentBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        EnsureRenderPass();
        _renderEncoder!.SetFragmentBuffer(buffer, offset, index);
        CommandCount++;
    }

    public void SetFragmentTexture(MetalTexture texture, ulong index)
    {
        ArgumentNullException.ThrowIfNull(texture);
        EnsureRenderPass();
        _renderEncoder!.SetFragmentTexture(texture, index);
        CommandCount++;
    }

    public void UseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages)
    {
        ArgumentNullException.ThrowIfNull(resource);
        EnsureRenderPass();
        _renderEncoder!.UseResource(resource, usage, stages);
        CommandCount++;
    }

    public void Draw(int primitiveType, ulong vertexStart, ulong vertexCount, ulong instanceCount = 1)
    {
        EnsureRenderPass();
        if (instanceCount > 1)
            _renderEncoder!.DrawPrimitives(primitiveType, vertexStart, vertexCount, instanceCount);
        else
            _renderEncoder!.DrawPrimitives(primitiveType, vertexStart, vertexCount);
        CommandCount++;
    }

    public void DrawIndexed(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        EnsureRenderPass();
        if (instanceCount > 1)
            _renderEncoder!.DrawIndexedTriangles(indexCount, is32Bit, indexBuffer, indexBufferOffset, instanceCount);
        else
            _renderEncoder!.DrawIndexedTriangles(indexCount, is32Bit, indexBuffer, indexBufferOffset);
        CommandCount++;
    }

    public void DrawIndirect(MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        EnsureRenderPass();
        _renderEncoder!.DrawPrimitivesIndirect(indirectBuffer, offset);
        CommandCount++;
    }

    public void DrawIndexedIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        EnsureRenderPass();
        _renderEncoder!.DrawIndexedTrianglesIndirect(indexBuffer, indirectBuffer, offset);
        CommandCount++;
    }

    public void WaitForFence(MetalFence fence, MTLRenderStages beforeStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        EnsureRenderPass();
        _renderEncoder!.WaitForFence(fence, beforeStages);
        CommandCount++;
    }

    public void UpdateFence(MetalFence fence, MTLRenderStages afterStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        EnsureRenderPass();
        _renderEncoder!.UpdateFence(fence, afterStages);
        CommandCount++;
    }

    public string GetCommandLog() => string.Empty;

    public void Dispose()
    {
        _renderEncoder?.Dispose();
        _computeEncoder?.Dispose();
        _commandBuffer?.Dispose();
        _queue.Dispose();
        GC.SuppressFinalize(this);
    }

    private void EnsureRenderPass()
    {
        if (_renderEncoder is null)
            throw new InvalidOperationException("BeginRenderPass() 必须在录制渲染命令之前调用。");
    }
}
