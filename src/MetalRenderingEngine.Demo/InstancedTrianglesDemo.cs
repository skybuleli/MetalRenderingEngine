using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.Metal;
using Hexa.NET.ImGui.Backends.OSX;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 6: 多三角形演示 —— 验证批量命令编码器（MetalCommandList）的 P/Invoke 优化价值。
/// 1000 个三角形（3000 顶点，vertex shader 按 vid 定位），两种模式对比：
///   • 逐次模式：循环 1000 次 encoder.DrawPrimitives（= 1000 次 P/Invoke）
///   • 批量模式：MetalCommandList 录制 1000 个 draw，1 次 ReplayRender（= 1 次 P/Invoke）
/// 两种模式画面完全一致（相同的 1000 个三角形），纯对比 P/Invoke 开销。
/// ImGui 实时显示当前模式、帧时间、P/Invoke 计数。
/// </summary>
internal static class InstancedTrianglesDemo
{
    private const int WindowWidth = 1024;
    private const int WindowHeight = 768;
    private const int TriangleCount = 1000;
    private const int VerticesPerTriangle = 3;
    private const int TotalVertices = TriangleCount * VerticesPerTriangle;

    public static unsafe int Run()
    {
        try
        {
            Console.WriteLine("=== Phase 6: Instanced Triangles (batched vs direct) ===");

            using var device = MetalDevice.CreateSystemDefault();
            Console.WriteLine($"Device: {device.Name} (UMA: {device.HasUnifiedMemory})");

            using var window = NativeWindow.Create("Phase 6 — Instanced Triangles", WindowWidth, WindowHeight);
            var layer = new MetalLayer(window.LayerHandle);
            layer.SetDevice(device);
            layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
            layer.SetDrawableSize(WindowWidth, WindowHeight);

            using var vertFn = MetalShaderLoader.GetFunction(device, "InstancedTriangle.vert", "main");
            using var fragFn = MetalShaderLoader.GetFunction(device, "InstancedTriangle.frag", "main");
            var pipeDesc = new WMTRenderPipelineDesc { ColorCount = 1, SampleCount = 1 };
            pipeDesc.ColorAttachmentAt(0) = new WMTColorAttachment
            {
                PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
                WriteMask = 0xF,
                BlendingEnabled = 0,
            };
            using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

            // ImGui 初始化
            var ctx = ImGui.CreateContext();
            ImGui.SetCurrentContext(ctx);
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            ImGuiImplOSX.SetCurrentContext(ctx);
            ImGuiImplOSX.Init((void*)window.ViewHandle);
            ImGuiImplMetal.SetCurrentContext(ctx);
            ImGuiImplMetal.Init((MTLDevice*)device.Handle);

            // 状态
            bool useBatched = true;           // true=批量模式, false=逐次模式
            int pinvokeCount = 0;
            float fps = 0;
            int fpsCounter = 0;
            float frameMs = 0;
            float maxFrameMs = 0;
            long lastFpsTime = 0;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            lastFpsTime = stopwatch.ElapsedMilliseconds;
            long lastFrameTicks = stopwatch.ElapsedTicks;
            int frame = 0;

            // 批量模式预录制命令链（draw 命令每帧不变，录一次复用）
            // 容量：每个 draw 命令 ~32 字节 × 1000 + PSO/viewport ≈ 33KB，给 64KB 余量
            using var cmdList = new MetalCommandList(initialCapacity: 64 * 1024);
            cmdList.RecordSetRenderPipelineState(pso);
            // viewport 每帧可能变，不预录；draw 命令固定，预录
            for (int i = 0; i < TriangleCount; i++)
            {
                cmdList.RecordDrawPrimitives(0, (ulong)(i * VerticesPerTriangle), VerticesPerTriangle);
            }
            // 注意：viewport 在 ReplayRender 前用单独的 SetViewport 设置（1 P/Invoke），
            // 因为预录的 viewport 会在窗口缩放后失效。

            using var queue = device.NewCommandQueue();
            int displayW = WindowWidth, displayH = WindowHeight;

            while (true)
            {
                if (window.PollShouldClose()) goto ExitLoop;

                var (curW, curH) = window.GetDrawableSize();
                if (curW != displayW || curH != displayH)
                {
                    displayW = curW; displayH = curH;
                    io.DisplaySize = new Vector2(displayW, displayH);
                    layer.SetDrawableSize(displayW, displayH);
                }

                lastFrameTicks = stopwatch.ElapsedTicks;

                var drawable = layer.NextDrawable();
                if (drawable == null) { Thread.Sleep(8); continue; }

                var rpDesc = MetalRenderPassDescriptor.CreateForTexture(drawable);
                try
                {
                    // ImGui 新帧
                    ImGuiImplOSX.NewFrame((void*)window.ViewHandle);
                    ImGuiImplMetal.NewFrame((MTLRenderPassDescriptor*)rpDesc.Handle);
                    ImGui.NewFrame();

                    ImGui.SetNextWindowSize(new Vector2(380, 220), ImGuiCond.FirstUseEver);
                    ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
                    ImGui.Begin("Phase 6 — Batched Encoder");
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f),
                        $"FPS: {fps:F1}  |  Frame: {frame}");
                    ImGui.Text($"Frame time: {frameMs:F3} ms (max {maxFrameMs:F3})");
                    ImGui.Checkbox("Use Batched (MetalCommandList)", ref useBatched);
                    ImGui.Separator();
                    ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f),
                        $"P/Invokes this frame: {pinvokeCount}");
                    if (useBatched)
                        ImGui.Text("Mode: BATCHED — 1 P/Invoke for 1000 draws");
                    else
                        ImGui.Text("Mode: DIRECT — 1000 P/Invokes");
                    ImGui.Text($"Triangles: {TriangleCount}  Vertices: {TotalVertices}");
                    ImGui.End();
                    ImGui.Render();

                    long frameStart = stopwatch.ElapsedTicks;

                    var scenePassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: true);
                    using var cmdbuf = queue.CommandBuffer();
                    var sceneEnc = cmdbuf.RenderCommandEncoder(scenePassDesc);
                    pinvokeCount = 2; // CommandBuffer + RenderCommandEncoder

                    if (useBatched)
                    {
                        // 批量模式：viewport 单独设（1 P/Invoke），然后 1 次 ReplayRender 回放 1000 draw
                        sceneEnc.SetViewport(0, 0, displayW, displayH, 0, 1);
                        cmdList.ReplayRender(sceneEnc);
                        pinvokeCount += 2; // SetViewport + ReplayRender
                    }
                    else
                    {
                        // 逐次模式：viewport + PSO + 1000 次 DrawPrimitives
                        sceneEnc.SetRenderPipelineState(pso);
                        sceneEnc.SetViewport(0, 0, displayW, displayH, 0, 1);
                        pinvokeCount += 2; // SetPSO + SetViewport
                        for (int i = 0; i < TriangleCount; i++)
                        {
                            sceneEnc.DrawPrimitives(0, (ulong)(i * VerticesPerTriangle), VerticesPerTriangle);
                        }
                        pinvokeCount += TriangleCount; // 1000 次 draw
                    }

                    sceneEnc.EndEncoding();
                    pinvokeCount += 1;
                    sceneEnc.Dispose();

                    // ImGui overlay pass
                    var uiPassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: false);
                    var uiEnc = cmdbuf.RenderCommandEncoder(uiPassDesc);
                    ImGuiImplMetal.RenderDrawData(
                        ImGui.GetDrawData(),
                        (MTLCommandBuffer*)cmdbuf.Handle,
                        (MTLRenderCommandEncoder*)uiEnc.Handle);
                    uiEnc.EndEncoding();
                    uiEnc.Dispose();
                    pinvokeCount += 3; // render enc + renderdrawdata + end

                    cmdbuf.PresentDrawable(drawable);
                    cmdbuf.Commit();
                    pinvokeCount += 2;

                    long frameEnd = stopwatch.ElapsedTicks;
                    frameMs = (float)(frameEnd - frameStart) * 1000f / System.Diagnostics.Stopwatch.Frequency;
                    if (frameMs > maxFrameMs) maxFrameMs = frameMs;
                }
                finally
                {
                    rpDesc.Dispose();
                    drawable.Dispose();
                }

                frame++;
                fpsCounter++;
                long nowMs = stopwatch.ElapsedMilliseconds;
                if (nowMs - lastFpsTime >= 500)
                {
                    fps = fpsCounter * 1000f / (nowMs - lastFpsTime);
                    fpsCounter = 0;
                    lastFpsTime = nowMs;
                }
                Thread.Sleep(8);
            }

        ExitLoop:
            ImGuiImplMetal.Shutdown();
            ImGuiImplOSX.Shutdown();
            Console.WriteLine($"✅ Rendered {frame} frames.");
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

    private static unsafe WMTRenderPassDesc BuildRenderPassDesc(nuint textureHandle, bool clear)
    {
        var passDesc = new WMTRenderPassDesc();
        var att = new WMTRenderPassAttachment
        {
            Texture = textureHandle,
            LoadAction = clear ? (int)MTLLoadAction.Clear : (int)MTLLoadAction.Load,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0.05f, 0.05f, 0.08f, 1f),
        };
        passDesc.SetColorAt(0, att);
        return passDesc;
    }
}
