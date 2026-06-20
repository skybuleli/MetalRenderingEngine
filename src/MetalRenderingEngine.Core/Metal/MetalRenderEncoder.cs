using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTL4RenderCommandEncoder 封装。
/// 资源绑定走 argument table，不再使用 per-encoder setVertexBuffer / setFragmentBuffer / set*Bytes。
/// </summary>
public sealed class MetalRenderEncoder : MetalObject
{
    private readonly MetalCommandBuffer _commandBuffer;

    internal MetalRenderEncoder(nuint handle, MetalCommandBuffer commandBuffer)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        _commandBuffer = commandBuffer;
        SetNativeHandle(handle);
        MetalBridge.MTLRenderCommandEncoder_setArgumentTable(Handle, commandBuffer.VertexArgumentTableHandle, 1);
        MetalBridge.MTLRenderCommandEncoder_setArgumentTable(Handle, commandBuffer.FragmentArgumentTableHandle, 2);
    }

    public void SetRenderPipelineState(MetalRenderPipelineState pso)
    {
        ArgumentNullException.ThrowIfNull(pso);
        MetalBridge.MTLRenderCommandEncoder_setRenderPipelineState(Handle, pso.Handle);
    }

    public void SetVertexBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _commandBuffer.TrackResidency(buffer);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.VertexArgumentTableHandle, buffer.GpuAddress + offset, index);
    }

    public void SetViewport(float x, float y, float w, float h, float znear = 0, float zfar = 1)
        => MetalBridge.MTLRenderCommandEncoder_setViewport(Handle, x, y, w, h, znear, zfar);

    public void SetScissorRect(int x, int y, int w, int h)
        => MetalBridge.MTLRenderCommandEncoder_setScissorRect(Handle, x, y, w, h);

    public void DrawPrimitives(int primitiveType, ulong vertexStart, ulong vertexCount)
        => MetalBridge.MTLRenderCommandEncoder_drawPrimitives(Handle, primitiveType, vertexStart, vertexCount);

    /// <summary>Instanced 绘制图元（Phase 7G）。</summary>
    public void DrawPrimitives(int primitiveType, ulong vertexStart, ulong vertexCount, ulong instanceCount)
        => MetalBridge.MTLRenderCommandEncoder_drawPrimitivesInstanced(Handle, primitiveType, vertexStart, vertexCount, instanceCount);

    public void DrawTriangles(ulong vertexStart, ulong vertexCount)
        => DrawPrimitives(0, vertexStart, vertexCount);

    /// <summary>使用 index buffer 绘制三角形。</summary>
    public void DrawIndexedTriangles(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        _commandBuffer.TrackResidency(indexBuffer);
        ulong indexAddress = indexBuffer.GpuAddress + indexBufferOffset;
        ulong indexLength = indexBuffer.Length - indexBufferOffset;
        MetalBridge.MTLRenderCommandEncoder_drawIndexedPrimitives(
            Handle, 0, indexCount, is32Bit ? 1 : 0, indexAddress, indexLength);
    }

    /// <summary>使用 index buffer 绘制 instanced 三角形。</summary>
    public void DrawIndexedTriangles(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        _commandBuffer.TrackResidency(indexBuffer);
        ulong indexAddress = indexBuffer.GpuAddress + indexBufferOffset;
        ulong indexLength = indexBuffer.Length - indexBufferOffset;
        MetalBridge.MTLRenderCommandEncoder_drawIndexedPrimitivesInstanced(
            Handle, 0, indexCount, is32Bit ? 1 : 0, indexAddress, indexLength, instanceCount);
    }

    /// <summary>Indirect 绘制。</summary>
    public void DrawPrimitivesIndirect(MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        _commandBuffer.TrackResidency(indirectBuffer);
        MetalBridge.MTLRenderCommandEncoder_drawPrimitivesIndirect(Handle, 0, indirectBuffer.GpuAddress + offset);
    }

    /// <summary>Indexed indirect 绘制。</summary>
    public void DrawIndexedTrianglesIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        _commandBuffer.TrackResidency(indexBuffer);
        _commandBuffer.TrackResidency(indirectBuffer);
        MetalBridge.MTLRenderCommandEncoder_drawIndexedPrimitivesIndirect(
            Handle, 0, 1, indexBuffer.GpuAddress, indexBuffer.Length, indirectBuffer.GpuAddress + offset);
    }

    public void EndEncoding()
        => MetalBridge.MTLRenderCommandEncoder_endEncoding(Handle);

    // ============================================================
    // Phase 3 扩展
    // ============================================================

    public unsafe void SetVertexBytes(ReadOnlySpan<byte> data, ulong index)
    {
        ulong address = _commandBuffer.AllocateScratch(data);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.VertexArgumentTableHandle, address, index);
    }

    public unsafe void SetVertexBytes<T>(in T value, ulong index) where T : unmanaged
    {
        T local = value;
        ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>((byte*)&local, sizeof(T));
        ulong address = _commandBuffer.AllocateScratch(bytes);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.VertexArgumentTableHandle, address, index);
    }

    public void SetFragmentBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _commandBuffer.TrackResidency(buffer);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.FragmentArgumentTableHandle, buffer.GpuAddress + offset, index);
    }

    public void SetFragmentBytes(ReadOnlySpan<byte> data, ulong index)
    {
        ulong address = _commandBuffer.AllocateScratch(data);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.FragmentArgumentTableHandle, address, index);
    }

    public unsafe void SetFragmentBytes<T>(in T value, ulong index) where T : unmanaged
    {
        T local = value;
        ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>((byte*)&local, sizeof(T));
        ulong address = _commandBuffer.AllocateScratch(bytes);
        MetalBridge.MTLArgumentTable_setAddress(_commandBuffer.FragmentArgumentTableHandle, address, index);
    }

    public void SetFragmentTexture(MetalTexture texture, ulong index)
    {
        ArgumentNullException.ThrowIfNull(texture);
        _commandBuffer.TrackResidency(texture);
        MetalBridge.MTLArgumentTable_setTexture(_commandBuffer.FragmentArgumentTableHandle, texture.GpuResourceID, index);
    }

    public void SetFragmentSamplerState(MetalSamplerState sampler, ulong index)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        _commandBuffer.TrackResidency(sampler);
        MetalBridge.MTLArgumentTable_setSamplerState(_commandBuffer.FragmentArgumentTableHandle, sampler.GpuResourceID, index);
    }

    /// <summary>声明资源驻留（Metal 4 走 residency set）。</summary>
    public void UseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages = MTLRenderStages.Vertex | MTLRenderStages.Fragment)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _commandBuffer.TrackResidency(resource);
    }

    public void WaitForFence(MetalFence fence, MTLRenderStages beforeStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        MetalBridge.MTLRenderCommandEncoder_waitForFence(Handle, fence.Handle, (uint)beforeStages);
    }

    public void UpdateFence(MetalFence fence, MTLRenderStages afterStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        MetalBridge.MTLRenderCommandEncoder_updateFence(Handle, fence.Handle, (uint)afterStages);
    }

    // ============================================================
    // Phase 7D: 光栅化状态 setters
    // ============================================================

    public void SetCullMode(MTLCullMode mode)
        => MetalBridge.MTLRenderCommandEncoder_setCullMode(Handle, (int)mode);

    public void SetFrontFacing(MTLWinding winding)
        => MetalBridge.MTLRenderCommandEncoder_setFrontFacingWinding(Handle, (int)winding);

    public void SetDepthBias(float bias, float slopeScale, float clamp)
        => MetalBridge.MTLRenderCommandEncoder_setDepthBias(Handle, bias, slopeScale, clamp);

    public void SetDepthClipMode(MTLDepthClipMode mode)
        => MetalBridge.MTLRenderCommandEncoder_setDepthClipMode(Handle, (int)mode);

    public void SetTriangleFillMode(MTLTriangleFillMode mode)
        => MetalBridge.MTLRenderCommandEncoder_setTriangleFillMode(Handle, (int)mode);

    // ============================================================
    // Phase 7E: 深度/模板状态 setters
    // ============================================================

    public void SetDepthStencilState(MetalDepthStencilState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        MetalBridge.MTLRenderCommandEncoder_setDepthStencilState(Handle, state.Handle);
    }

    public void SetStencilReference(uint front, uint back)
        => MetalBridge.MTLRenderCommandEncoder_setStencilReferenceValue(Handle, front, back);
}
