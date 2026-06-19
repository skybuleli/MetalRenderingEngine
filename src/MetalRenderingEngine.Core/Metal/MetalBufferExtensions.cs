using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// <see cref="MetalBuffer"/> 扩展方法：MSC 4.0 argument-buffer 描述符生成。
/// </summary>
public static class MetalBufferExtensions
{
    /// <summary>
    /// 生成本 buffer 的 <see cref="UavDescriptor"/>（GPU 地址 + 长度 + 步长），
    /// 便于直接 <c>recorder.SetVertexBytes(buf.ToUavDescriptor(stride), index)</c>。
    /// </summary>
    /// <param name="buffer">目标 buffer。</param>
    /// <param name="stride">元素步长（字节）。0 或不传时使用 buffer 全长。</param>
    public static UavDescriptor ToUavDescriptor(this MetalBuffer buffer, ulong stride = 0)
        => new() { GpuAddress = buffer.GpuAddress, Length = buffer.Length, Stride = stride == 0 ? buffer.Length : stride };
}
