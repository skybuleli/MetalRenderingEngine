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
}
