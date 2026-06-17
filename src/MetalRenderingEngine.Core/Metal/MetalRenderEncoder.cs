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

    public void DrawTriangles(ulong vertexStart, ulong vertexCount)
        => DrawPrimitives(0, vertexStart, vertexCount);

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
}
