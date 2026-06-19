using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLRenderCommandEncoder 封装。
/// </summary>
public sealed class MetalRenderEncoder : MetalObject
{
    internal MetalRenderEncoder(nuint handle) { SetNativeHandle(handle); }

    public void SetRenderPipelineState(MetalRenderPipelineState pso)
    {
        ArgumentNullException.ThrowIfNull(pso);
        MetalBridge.MTLRenderCommandEncoder_setRenderPipelineState(Handle, pso.Handle);
    }

    public void SetVertexBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        MetalBridge.MTLRenderCommandEncoder_setVertexBuffer(Handle, buffer.Handle, offset, index);
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
        MetalBridge.MTLRenderCommandEncoder_drawIndexedPrimitives(
            Handle, 0, indexCount, is32Bit ? 1 : 0, indexBuffer.Handle, indexBufferOffset);
    }

    /// <summary>使用 index buffer 绘制 instanced 三角形。</summary>
    public void DrawIndexedTriangles(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        MetalBridge.MTLRenderCommandEncoder_drawIndexedPrimitivesInstanced(
            Handle, 0, indexCount, is32Bit ? 1 : 0, indexBuffer.Handle, indexBufferOffset, instanceCount);
    }

    /// <summary>Indirect 绘制。</summary>
    public void DrawPrimitivesIndirect(MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        MetalBridge.MTLRenderCommandEncoder_drawPrimitivesIndirect(Handle, 0, indirectBuffer.Handle, offset);
    }

    /// <summary>Indexed indirect 绘制。</summary>
    public void DrawIndexedTrianglesIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        MetalBridge.MTLRenderCommandEncoder_drawIndexedPrimitivesIndirect(
            Handle, 0, 1, indexBuffer.Handle, indirectBuffer.Handle, offset);
    }

    public void EndEncoding()
        => MetalBridge.MTLRenderCommandEncoder_endEncoding(Handle);

    // ============================================================
    // Phase 3 扩展
    // ============================================================

    /// <summary>内联字节数据到 vertex buffer slot（常用于 push constants）。</summary>
    public unsafe void SetVertexBytes(ReadOnlySpan<byte> data, ulong index)
    {
        fixed (byte* p = data)
            MetalBridge.MTLRenderCommandEncoder_setVertexBytes(Handle, p, (ulong)data.Length, index);
    }

    /// <summary>内联结构体到 vertex buffer slot。</summary>
    public unsafe void SetVertexBytes<T>(in T value, ulong index) where T : unmanaged
    {
        T local = value;
        MetalBridge.MTLRenderCommandEncoder_setVertexBytes(Handle, &local, (ulong)sizeof(T), index);
    }

    /// <summary>绑定 fragment buffer。</summary>
    public void SetFragmentBuffer(MetalBuffer buffer, ulong offset, ulong index)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        MetalBridge.MTLRenderCommandEncoder_setFragmentBuffer(Handle, buffer.Handle, offset, index);
    }

    /// <summary>内联字节数据到 fragment buffer slot。</summary>
    public unsafe void SetFragmentBytes(ReadOnlySpan<byte> data, ulong index)
    {
        fixed (byte* p = data)
            MetalBridge.MTLRenderCommandEncoder_setFragmentBytes(Handle, p, (ulong)data.Length, index);
    }

    /// <summary>内联结构体到 fragment buffer slot。</summary>
    public unsafe void SetFragmentBytes<T>(in T value, ulong index) where T : unmanaged
    {
        T local = value;
        MetalBridge.MTLRenderCommandEncoder_setFragmentBytes(Handle, &local, (ulong)sizeof(T), index);
    }

    /// <summary>绑定 fragment texture。</summary>
    public void SetFragmentTexture(MetalTexture texture, ulong index)
    {
        ArgumentNullException.ThrowIfNull(texture);
        MetalBridge.MTLRenderCommandEncoder_setFragmentTexture(Handle, texture.Handle, index);
    }

    /// <summary>绑定 fragment sampler。</summary>
    public void SetFragmentSamplerState(MetalSamplerState sampler, ulong index)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        MetalBridge.MTLRenderCommandEncoder_setFragmentSamplerState(Handle, sampler.Handle, index);
    }

    /// <summary>声明资源驻留（render encoder 版本，包含 stage 参数）。</summary>
    public void UseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages = MTLRenderStages.Vertex | MTLRenderStages.Fragment)
    {
        ArgumentNullException.ThrowIfNull(resource);
        MetalBridge.MTLRenderCommandEncoder_useResource(Handle, resource.Handle, (uint)usage, (uint)stages);
    }

    /// <summary>等待 Fence（GPU 执行到此处前必须完成指定 stage）。</summary>
    public void WaitForFence(MetalFence fence, MTLRenderStages beforeStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        MetalBridge.MTLRenderCommandEncoder_waitForFence(Handle, fence.Handle, (uint)beforeStages);
    }

    /// <summary>更新 Fence（GPU 完成指定 stage 后标记）。</summary>
    public void UpdateFence(MetalFence fence, MTLRenderStages afterStages)
    {
        ArgumentNullException.ThrowIfNull(fence);
        MetalBridge.MTLRenderCommandEncoder_updateFence(Handle, fence.Handle, (uint)afterStages);
    }

    // ============================================================
    // Phase 7D: 光栅化状态 setters
    // ============================================================

    /// <summary>设置背面剔除模式（None/Front/Back）。</summary>
    public void SetCullMode(MTLCullMode mode)
        => MetalBridge.MTLRenderCommandEncoder_setCullMode(Handle, (int)mode);

    /// <summary>设置正面朝向绕序（顺时针/逆时针）。</summary>
    public void SetFrontFacing(MTLWinding winding)
        => MetalBridge.MTLRenderCommandEncoder_setFrontFacingWinding(Handle, (int)winding);

    /// <summary>设置深度偏移（用于阴影贴图消除 acne）。</summary>
    public void SetDepthBias(float bias, float slopeScale, float clamp)
        => MetalBridge.MTLRenderCommandEncoder_setDepthBias(Handle, bias, slopeScale, clamp);

    /// <summary>设置深度裁剪模式（Clip=超出 near/far 丢弃，Clamp=钳位）。</summary>
    public void SetDepthClipMode(MTLDepthClipMode mode)
        => MetalBridge.MTLRenderCommandEncoder_setDepthClipMode(Handle, (int)mode);

    /// <summary>设置三角形填充模式（Fill/Lines 即线框）。</summary>
    public void SetTriangleFillMode(MTLTriangleFillMode mode)
        => MetalBridge.MTLRenderCommandEncoder_setTriangleFillMode(Handle, (int)mode);

    // ============================================================
    // Phase 7E: 深度/模板状态 setters
    // ============================================================

    /// <summary>绑定深度/模板状态对象。</summary>
    public void SetDepthStencilState(MetalDepthStencilState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        MetalBridge.MTLRenderCommandEncoder_setDepthStencilState(Handle, state.Handle);
    }

    /// <summary>设置 stencil 参考值（前后分离）。</summary>
    public void SetStencilReference(uint front, uint back)
        => MetalBridge.MTLRenderCommandEncoder_setStencilReferenceValue(Handle, front, back);
}
