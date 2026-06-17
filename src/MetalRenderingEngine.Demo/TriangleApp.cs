using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 2 验证：SDL3 窗口 + Metal 渲染彩色三角形。
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

            // 2) SDL3 窗口 + CAMetalLayer
            using var sdlWindow = SDL3Window.Create("Metal Triangle — Phase 2 (SDL3)", WindowWidth, WindowHeight);
            var layer = new MetalLayer(sdlWindow.LayerHandle);
            layer.SetDevice(device);
            layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
            layer.SetDrawableSize(WindowWidth, WindowHeight);

            // 3) 加载着色器
            using var vertFn = MetalShaderLoader.GetFunction(device, "Triangle.vert", "main");
            using var fragFn = MetalShaderLoader.GetFunction(device, "Triangle.frag", "main");

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
                pipeDesc.Colors[0] = ca;
            }
            using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

            // 5) 主循环（持续运行直到按 ESC 或关闭窗口）
            using var queue = device.NewCommandQueue();
            int frame = 0;

            while (true)
            {
                // 轮询 SDL3 事件；返回 true 表示用户请求关闭
                if (sdlWindow.PollShouldClose())
                    break;
                var drawable = layer.NextDrawable();
                if (drawable == null)
                {
                    Thread.Sleep(8);
                    continue;
                }

                using (drawable)
                {
                    using var cmdbuf = queue.CommandBuffer();

                    var passDesc = BuildRenderPassDesc(drawable.Texture.Handle);
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

            Console.WriteLine($"✅ Rendered {frame} frames. Triangle visible in SDL3 window.");
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
        passDesc.SetColorAt(0, colorAtt);
        return passDesc;
    }
}
