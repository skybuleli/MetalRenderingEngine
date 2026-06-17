using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 2 验证：Cocoa 窗口 + Metal 渲染彩色三角形。
/// SDL3 在当前环境初始化失败，使用 Cocoa 直接创建窗口。
/// </summary>
internal static class TriangleApp
{
    private const int WindowWidth = 800;
    private const int WindowHeight = 600;

    public static int Run()
    {
        try
        {
            // 1) 设备
            using var device = MetalDevice.CreateSystemDefault();
            Console.WriteLine($"Device: {device.Name}");

            // 2) Cocoa 窗口 + CAMetalLayer（通过 bridge.m）
            using var cocoaWindow = CocoaWindow.Create("Metal Triangle — Phase 2", WindowWidth, WindowHeight);
            var layer = new MetalLayer(cocoaWindow.LayerHandle);
            layer.SetDevice(device);
            layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
            layer.SetDrawableSize(WindowWidth, WindowHeight);

            // 3) 加载着色器
            string vertPath = Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.vert.metallib");
            string fragPath = Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.frag.metallib");
            if (!File.Exists(vertPath) || !File.Exists(fragPath))
            {
                Console.Error.WriteLine("Missing metallib files. Run ./build/compile_shaders.sh");
                return 1;
            }

            using var vertLib = device.NewLibrary(File.ReadAllBytes(vertPath));
            using var fragLib = device.NewLibrary(File.ReadAllBytes(fragPath));
            using var vertFn = vertLib.NewFunction("main");
            using var fragFn = fragLib.NewFunction("main");

            // 4) 创建 render pipeline
            var pipeDesc = new WMTRenderPipelineDesc();
            pipeDesc.ColorCount = 1;
            pipeDesc.SampleCount = 1;
            unsafe
            {
                var ca = new WMTColorAttachment
                {
                    PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
                    WriteMask = 0xF,
                    BlendingEnabled = 0,
                };
                *(WMTColorAttachment*)pipeDesc.ColorsRaw = ca;
            }
            using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

            // 5) 主循环（简化为固定帧数，无事件处理）
            using var queue = device.NewCommandQueue();
            int frame = 0;
            const int targetFrames = 180; // ~3s @ 60fps

            while (frame < targetFrames)
            {
                var drawable = layer.NextDrawable();
                if (drawable == null)
                {
                    Thread.Sleep(8);
                    continue;
                }

                using (drawable)
                {
                    using var cmdbuf = queue.CommandBuffer();

                    var passDesc = BuildRenderPassDesc(drawable.TextureHandle);
                    using (var encoder = cmdbuf.RenderCommandEncoder(passDesc))
                    {
                        encoder.SetRenderPipelineState(pso);
                        encoder.SetViewport(0, 0, WindowWidth, WindowHeight, 0, 1);
                        encoder.DrawTriangles(0, 3);
                        encoder.EndEncoding();
                    }

                    cmdbuf.PresentDrawable(drawable);
                    cmdbuf.Commit();
                }

                frame++;
                // 简单限帧 ~60fps
                Thread.Sleep(16);
            }

            Console.WriteLine($"✅ Rendered {frame} frames. Triangle visible in Cocoa window.");
            return 0;
        }
        catch (MetalException ex)
        {
            Console.Error.WriteLine($"❌ Metal error: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Unexpected error: {ex}");
            return 3;
        }
    }

    private static unsafe WMTRenderPassDesc BuildRenderPassDesc(nuint textureHandle)
    {
        var passDesc = new WMTRenderPassDesc();
        var colorAtt = new WMTRenderPassAttachment
        {
            Texture = textureHandle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0.15f, 0.18f, 0.22f, 1.0f),
        };
        *(WMTRenderPassAttachment*)passDesc.ColorsRaw = colorAtt;
        return passDesc;
    }
}
