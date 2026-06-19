using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 7C: 深度/模板像素格式与 MetalTexture.IsDepthFormat 测试。
/// </summary>
public class DepthTextureTests
{
    [Fact]
    public void Create_Depth32Float_TextureIsDepthFormat()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var info = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.Depth32Float,
            TextureType = (int)MTLTextureType.Type2D,
            Width = 256,
            Height = 256,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModePrivate,
        };

        using var tex = device.NewTexture(in info);
        Assert.Equal(MTLPixelFormat.Depth32Float, tex.PixelFormat);
        Assert.True(tex.IsDepthFormat);
    }

    [Fact]
    public void Create_Depth32FloatStencil8_TextureIsDepthFormat()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var info = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.Depth32Float_Stencil8,
            TextureType = (int)MTLTextureType.Type2D,
            Width = 256,
            Height = 256,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget,
            Options = (int)MTLResourceOptions.StorageModePrivate,
        };

        using var tex = device.NewTexture(in info);
        Assert.Equal(MTLPixelFormat.Depth32Float_Stencil8, tex.PixelFormat);
        Assert.True(tex.IsDepthFormat);
    }

    [Fact]
    public void Create_BGRA8Unorm_TextureIsNotDepthFormat()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var info = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = (int)MTLTextureType.Type2D,
            Width = 64,
            Height = 64,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };

        using var tex = device.NewTexture(in info);
        Assert.Equal(MTLPixelFormat.BGRA8Unorm, tex.PixelFormat);
        Assert.False(tex.IsDepthFormat);
    }
}
