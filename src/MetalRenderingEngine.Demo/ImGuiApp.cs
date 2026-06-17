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
/// Phase 3.5 验证：旋转贴图四边形 + ImGui 调试 UI 叠加。
/// 使用 Hexa.NET.ImGui.Backends（OSX + Metal 后端）处理所有 ImGui 输入和渲染。
/// </summary>
internal static class ImGuiApp
{
    private const int W = 1280, H = 800;
    private const int TexSize = 64;
    private const int BufferCount = 3;
    private const ulong ArgIndex = 2;

    public static unsafe int Run()
    {
        try
        {
            // ── 1. 设备 ─────────────────────────────────────────
            using var device = MetalDevice.CreateSystemDefault();
            Console.WriteLine($"Device: {device.Name}");

            // ── 2. 窗口 ─────────────────────────────────────────
            using var nativeWindow = NativeWindow.Create("Metal ImGui — Phase 3.5", W, H);
            var layer = new MetalLayer(nativeWindow.LayerHandle);
            layer.SetDevice(device);
            layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
            layer.SetDrawableSize(W, H);

            // ── 3. 场景着色器 + 管线 ────────────────────────────
            using var vertFn = MetalShaderLoader.GetFunction(device, "TexturedQuad.vert", "main");
            using var fragFn = MetalShaderLoader.GetFunction(device, "TexturedQuad.frag", "main");

            var scenePipeDesc = new WMTRenderPipelineDesc { ColorCount = 1, SampleCount = 1 };
            unsafe
            {
                var ca = new WMTColorAttachment
                {
                    PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
                    WriteMask = 0xF,
                    BlendingEnabled = 0,
                };
                scenePipeDesc.Colors[0] = ca;
            }
            using var scenePso = device.NewRenderPipelineState(vertFn, fragFn, scenePipeDesc);

            // ── 4. 程序化棋盘格纹理 ─────────────────────────────
            int texBytes = TexSize * TexSize * sizeof(uint);
            using var texBuffer = device.NewBuffer(
                (ulong)texBytes, MTLResourceOptions.StorageModeShared);
            FillCheckerboard(texBuffer.AsSpan<uint>(), TexSize);

            // ── 5. Triple-buffer uniform + Fence ────────────────
            ulong uboSize = (ulong)Marshal.SizeOf<PerFrameCB>();
            var uniformBuffers = new MetalBuffer[BufferCount];
            var fences = new MetalFence[BufferCount];
            for (int i = 0; i < BufferCount; i++)
            {
                uniformBuffers[i] = device.NewBuffer(uboSize, MTLResourceOptions.StorageModeShared);
                fences[i] = device.NewFence();
            }

            // ── 6. ImGui + Backends 初始化 ──────────────────────
            var ctx = ImGui.CreateContext();
            ImGui.SetCurrentContext(ctx);

            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            io.ConfigViewportsNoAutoMerge = true;

            // OSX 后端：传入原生 NSView（由 bridge.m Cocoa_CreateMetalWindow 创建）
            ImGuiImplOSX.SetCurrentContext(ctx);
            ImGuiImplOSX.Init((void*)nativeWindow.ViewHandle);

            // Metal 后端：传入 MTLDevice
            ImGuiImplMetal.SetCurrentContext(ctx);
            ImGuiImplMetal.Init((MTLDevice*)device.Handle);

            // ── 7. 主渲染循环 ───────────────────────────────────
            using var queue = device.NewCommandQueue();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long lastFrameTicks = stopwatch.ElapsedTicks;
            int frame = 0;
            long startTime = stopwatch.ElapsedMilliseconds;
            long lastFpsTime = startTime;
            int fpsCounter = 0;
            float fps = 0;
            bool showDemo = false;

            int displayW = W, displayH = H;

            while (true)
            {
                // ── 7a. 轮询事件 ──────────────────────────────
                if (nativeWindow.PollShouldClose())
                    goto ExitLoop;

                // 检测窗口 resize
                var (curW, curH) = nativeWindow.GetDrawableSize();
                if (curW != displayW || curH != displayH)
                {
                    displayW = curW;
                    displayH = curH;
                    io.DisplaySize = new Vector2(displayW, displayH);
                    layer.SetDrawableSize(displayW, displayH);
                }

                // ── 7b. 计算帧间隔 ─────────────────────────────
                long nowTicks = stopwatch.ElapsedTicks;
                float deltaTime = (float)(nowTicks - lastFrameTicks) / System.Diagnostics.Stopwatch.Frequency;
                lastFrameTicks = nowTicks;

                // ── 7c. ImGui 新帧 ─────────────────────────────
                var drawable = layer.NextDrawable();
                if (drawable == null) { Thread.Sleep(8); continue; }

                // 创建临时 render pass descriptor 用于 ImGui backend（不用 using 声明，避免与 goto ExitLoop 冲突）
                var rpDesc = MetalRenderPassDescriptor.CreateForTexture(drawable);
                try
                {
                    ImGuiImplOSX.NewFrame((void*)nativeWindow.ViewHandle);
                    ImGuiImplMetal.NewFrame((MTLRenderPassDescriptor*)rpDesc.Handle);
                    ImGui.NewFrame();

                    // ImGui 控件
                    ImGui.SetNextWindowSize(new Vector2(340, 220), ImGuiCond.FirstUseEver);
                    ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
                    ImGui.Begin("Debug Overlay");
                    ImGui.Text($"ImGui {ImGui.GetVersionS()}  |  {device.Name}");
                    ImGui.Separator();
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"FPS: {fps:F1}");
                    ImGui.Text($"Frame: {frame}");
                    ImGui.Text($"Window: {displayW}x{displayH}");
                    ImGui.Separator();
                    ImGui.Text($"GPU UMA: {device.HasUnifiedMemory}");
                    ImGui.Separator();
                    ImGui.Checkbox("Show ImGui Demo", ref showDemo);
                    ImGui.End();

                    if (showDemo) ImGui.ShowDemoWindow(ref showDemo);

                    ImGui.Render();

                    // ── 7d. 场景渲染 ───────────────────────────
                    int slot = frame % BufferCount;
                    float t = (Environment.TickCount64 - startTime) / 1000f;
                    float angle = t * MathF.PI * 0.5f;
                    var proj = Matrix4x4.PerspectiveFovRH(
                        MathF.PI / 4f, (float)displayW / displayH, 0.1f, 100f);
                    var view = Matrix4x4.LookAtRH(
                        new(0, 0, 3), new(0, 0, 0), new(0, 1, 0));
                    var model = Matrix4x4.RotationY(angle);
                    var mvp = Matrix4x4.Multiply(
                        Matrix4x4.Multiply(proj, view), model);

                    var cb = new PerFrameCB { mvp = mvp, time = t };
                    uniformBuffers[slot].AsSpan<PerFrameCB>()[0] = cb;

                    using var cmdbuf = queue.CommandBuffer();

                    // Scene pass
                    var scenePassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: true);
                    var sceneEnc = cmdbuf.RenderCommandEncoder(scenePassDesc);
                    try
                    {
                        sceneEnc.SetRenderPipelineState(scenePso);
                        sceneEnc.SetViewport(0, 0, displayW, displayH, 0, 1);
                        sceneEnc.WaitForFence(fences[slot], MTLRenderStages.Vertex);

                        var mvpDesc = new UavDescriptor
                        {
                            GpuAddress = uniformBuffers[slot].GpuAddress,
                            Length = uniformBuffers[slot].Length,
                            Stride = uboSize,
                        };
                        sceneEnc.SetVertexBytes<UavDescriptor>(mvpDesc, ArgIndex);

                        var texDesc = new UavDescriptor
                        {
                            GpuAddress = texBuffer.GpuAddress,
                            Length = texBuffer.Length,
                            Stride = sizeof(uint),
                        };
                        sceneEnc.SetFragmentBytes<UavDescriptor>(texDesc, ArgIndex);
                        sceneEnc.UseResource(texBuffer, MTLResourceUsage.Read, MTLRenderStages.Fragment);

                        sceneEnc.DrawTriangles(0, 6);
                        sceneEnc.UpdateFence(fences[slot], MTLRenderStages.Vertex);
                    }
                    finally
                    {
                        sceneEnc.EndEncoding();
                        sceneEnc.Dispose();
                    }

                    // ImGui overlay pass
                    var uiPassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: false);
                    var uiEnc = cmdbuf.RenderCommandEncoder(uiPassDesc);
                    try
                    {
                        ImGuiImplMetal.RenderDrawData(
                            ImGui.GetDrawData(),
                            (MTLCommandBuffer*)cmdbuf.Handle,
                            (MTLRenderCommandEncoder*)uiEnc.Handle);
                    }
                    finally
                    {
                        uiEnc.EndEncoding();
                        uiEnc.Dispose();
                    }

                    cmdbuf.PresentDrawable(drawable);
                    cmdbuf.Commit();
                }
                finally
                {
                    rpDesc.Dispose();
                    drawable.Dispose();
                }

                // ── FPS 计算 ───────────────────────────────────
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
            // 清理后端
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

    // ── 辅助方法 ────────────────────────────────────────────

    private static void FillCheckerboard(Span<uint> pixels, int size)
    {
        const uint Red = 0xFF0000FF;
        const uint White = 0xFFFFFFFF;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = ((x / 8 + y / 8) & 1) == 0 ? Red : White;
    }

    private static unsafe WMTRenderPassDesc BuildRenderPassDesc(nuint textureHandle, bool clear)
    {
        var passDesc = new WMTRenderPassDesc();
        var att = new WMTRenderPassAttachment
        {
            Texture = textureHandle,
            LoadAction = clear ? (int)MTLLoadAction.Clear : (int)MTLLoadAction.Load,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0.15f, 0.18f, 0.22f, 1f),
        };
        passDesc.SetColorAt(0, att);
        return passDesc;
    }
}
