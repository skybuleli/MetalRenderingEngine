using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLTexture 封装。支持创建、像素数据上传（ReplaceRegion）和只读回读。
/// </summary>
public sealed class MetalTexture : MetalObject
{
    /// <summary>纹理宽度（像素）。</summary>
    public uint Width { get; }

    /// <summary>纹理高度（像素）。</summary>
    public uint Height { get; }

    internal MetalTexture(nuint handle, uint width, uint height)
    {
        SetNativeHandle(handle);
        Width = width;
        Height = height;
    }

    /// <summary>
    /// 上传像素数据到纹理的指定区域和 mip 级别。
    /// </summary>
    /// <param name="x">区域起点 X。</param>
    /// <param name="y">区域起点 Y。</param>
    /// <param name="w">区域宽度。</param>
    /// <param name="h">区域高度。</param>
    /// <param name="mipLevel">mip 级别（0 = 最高分辨率）。</param>
    /// <param name="slice">数组切片（0 = 非数组纹理）。</param>
    /// <param name="pixels">像素数据（调用方确保格式匹配）。</param>
    /// <param name="bytesPerRow">每行字节数。</param>
    public unsafe void ReplaceRegion(int x, int y, int w, int h, int mipLevel, int slice,
                                      ReadOnlySpan<byte> pixels, ulong bytesPerRow)
    {
        var origin = new WMTOrigin((ulong)x, (ulong)y, 0);
        var size = new WMTSize((ulong)w, (ulong)h, 1);
        fixed (byte* p = pixels)
        {
            MetalBridge.MTLTexture_replaceRegion(Handle, origin, size,
                (ulong)mipLevel, (ulong)slice, p, bytesPerRow, 0);
        }
    }
}
