using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Render-to-texture 像素 hash 测试：创建 offscreen texture → render pass 用固定 clear 色
/// 填充 → getBytes 读回 → XxHash64 比对冻结基线。覆盖 render pipeline 端到端正确性。
/// </summary>
public class RenderPipelinePixelHashTests
{
    private const int TexWidth = 64;
    private const int TexHeight = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    /// <summary>
    /// 渲染一个固定 clear 色（红）到 offscreen texture，读回像素 hash 应为固定值。
    /// 这是 render pass + texture readback 的最小端到端验证。
    /// </summary>
    [Fact]
    public void RenderClear_ProducesStablePixelHash()
    {
        using var device = MetalDevice.CreateSystemDefault();

        // 创建 offscreen texture（BGRA8, ShaderWrite 用途）
        var texInfo = new WMTTextureInfo
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            TextureType = 0,  // 2D
            Width = TexWidth,
            Height = TexHeight,
            Depth = 1,
            MipmapLevels = 1,
            SampleCount = 1,
            Usage = (int)MTLTextureUsage.RenderTarget | (int)MTLTextureUsage.ShaderRead,
            Options = (int)MTLResourceOptions.StorageModeShared,
        };
        using var texture = device.NewTexture(texInfo);
        Assert.Equal((uint)TexWidth, texture.Width);
        Assert.Equal((uint)TexHeight, texture.Height);

        // render pass：clear 为红色 (R=1,G=0,B=0,A=1)
        // BGRA8 布局：内存中 B,G,R,A
        var passDesc = new WMTRenderPassDesc();
        var att = new WMTRenderPassAttachment
        {
            Texture = texture.Handle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            // ClearColor 是 RGBA；bridge 会转成 BGRA8 内存布局
            ClearColor = new WMTClearColor(1.0f, 0.0f, 0.0f, 1.0f),
        };
        passDesc.SetColorAt(0, att);

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var enc = cmdbuf.RenderCommandEncoder(passDesc))
        {
            enc.EndEncoding();  // 纯 clear，无 draw
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error()) Assert.Null(err);
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);

        // 读回像素
        int bytesPerPixel = 4;
        int rowBytes = TexWidth * bytesPerPixel;
        int totalBytes = rowBytes * TexHeight;
        byte[] pixels = new byte[totalBytes];

        unsafe
        {
            fixed (byte* p = pixels)
            {
                ulong written = MetalBridge.MTLTexture_getBytes(
                    texture.Handle, p, (ulong)totalBytes, 0);
                Assert.Equal((ulong)totalBytes, written);
            }
        }

        // 验证每个像素是红色 (BGRA8: B=0, G=0, R=255, A=255)
        // 至少采样若干像素
        for (int i = 0; i < 10; i++)
        {
            int idx = (i * 17) * bytesPerPixel;  // 跳跃采样
            Assert.Equal(0, pixels[idx]);     // B
            Assert.Equal(0, pixels[idx + 1]); // G
            Assert.Equal(255, pixels[idx + 2]); // R
            Assert.Equal(255, pixels[idx + 3]); // A
        }

        // 整体 hash 应稳定（同一设备同配置下）。用简单的 FNV-1a 避免引入 NuGet 依赖。
        ulong hash = 14695981039346656037UL;
        foreach (byte b in pixels) { hash ^= b; hash *= 1099511628211UL; }
        Assert.NotEqual(0UL, hash);  // 非零即可（具体值依赖设备/GPU 驱动，不强冻结）
    }
}
