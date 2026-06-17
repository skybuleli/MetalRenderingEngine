using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.Metal;
using Hexa.NET.ImGui.Backends.OSX;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;
using MetalRenderingEngine.Shader;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Mandelbrot 集合演示：compute shader 计算像素 + 全屏四边形渲染到窗口 + ImGui 控制面板。
/// 使用 <c>src/MetalRenderingEngine.Shaders/Compute/Mandelbrot.slang</c>（compute）与
/// <c>MandelbrotDisplay.{vert,frag}.slang</c>（fullscreen quad）。
/// 单 buffer 方案：Output[0] 存参数 (cx,cy,scale,maxIter)，Output[1..] 存 float4 像素。
/// ImGui 浮动层可实时调整动态参数（缩放呼吸、中心漂移、迭代次数），制造强烈动态效果。
/// </summary>
internal static class MandelbrotDemo
{
    private const int Width = 1024;
    private const int Height = 768;
    private const int WindowWidth = 1024;
    private const int WindowHeight = 768;
    private const int ThreadGroupWidth = 16;
    private const int ThreadGroupHeight = 16;

    // MSC 4.0 把 top-level argument table 放在 buffer(2)
    private const ulong ArgumentTableBufferIndex = 2;

    public static unsafe int Run()
    {
        try
        {
            Console.WriteLine("=== Mandelbrot Compute + Display + ImGui Demo ===");

            // ── 1. 设备 + 窗口 ──────────────────────────────────
            using var device = MetalDevice.CreateSystemDefault();
            Console.WriteLine($"Device: {device.Name} (UMA: {device.HasUnifiedMemory})");

            using var window = NativeWindow.Create("Mandelbrot — Phase 5 + ImGui", WindowWidth, WindowHeight);
            var layer = new MetalLayer(window.LayerHandle);
            layer.SetDevice(device);
            layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
            layer.SetDrawableSize(WindowWidth, WindowHeight);

            // ── 2. Compute 管线（Mandelbrot.slang） ─────────────
            using var computeFn = MetalShaderLoader.GetFunction(device, "Mandelbrot", "main");
            using var computePso = device.NewComputePipelineState(computeFn);
            Console.WriteLine($"Compute pipeline: threadExecutionWidth={computePso.ThreadExecutionWidth}");

            // ── 3. Render 管线（MandelbrotDisplay.{vert,frag}.slang） ──
            using var vertFn = MetalShaderLoader.GetFunction(device, "MandelbrotDisplay.vert", "main");
            using var fragFn = MetalShaderLoader.GetFunction(device, "MandelbrotDisplay.frag", "main");

            var pipeDesc = new WMTRenderPipelineDesc { ColorCount = 1, SampleCount = 1 };
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
            using var renderPso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

            // ── 4. 单 buffer：Output[0] = 参数，Output[1..] = 像素 ──
            int totalElements = 1 + Width * Height;
            using var buffer = device.NewBuffer(
                (ulong)(totalElements * 16), // float4 = 16 bytes
                MTLResourceOptions.StorageModeShared);

            // ── 5. ImGui + Backends 初始化 ──────────────────────
            var ctx = ImGui.CreateContext();
            ImGui.SetCurrentContext(ctx);
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

            ImGuiImplOSX.SetCurrentContext(ctx);
            ImGuiImplOSX.Init((void*)window.ViewHandle);
            ImGuiImplMetal.SetCurrentContext(ctx);
            ImGuiImplMetal.Init((MTLDevice*)device.Handle);

            // ── 6. 动态参数状态 ─────────────────────────────────
            // 所有动态量都可由 ImGui 实时调整。基础值保证画面一开始就在动。
            float baseScale = 3.5f / Width;     // 初始缩放（覆盖整个集合）
            float zoomAmp = 0.6f;                // 缩放呼吸幅度（相对值）
            float zoomFreq = 0.25f;              // 缩放呼吸频率（Hz）
            float driftX = 0.0f, driftY = 0.0f;  // 中心点漂移幅度（复平面单位）
            float driftFreqX = 0.07f;            // X 方向漂移频率
            float driftFreqY = 0.11f;            // Y 方向漂移频率（与 X 不同形成李萨如轨迹）
            int baseMaxIter = 128;
            int iterAmp = 96;                    // 迭代次数呼吸幅度
            float iterFreq = 0.12f;              // 迭代呼吸频率（独立于缩放）
            float centerX0 = -0.5f, centerY0 = 0.0f;
            bool animate = true;
            bool showDemo = false;

            // 帧缓存：参数不变时跳过 compute dispatch（静态画面不重算）
            float prevCx = float.NaN, prevCy = float.NaN, prevScale = float.NaN;
            int prevMaxIter = -1;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            long startTime = stopwatch.ElapsedMilliseconds;
            long lastFrameTicks = stopwatch.ElapsedTicks;
            float fps = 0;
            int fpsCounter = 0;
            long lastFpsTime = startTime;
            int frame = 0;

            using var queue = device.NewCommandQueue();
            int displayW = WindowWidth, displayH = WindowHeight;

            while (true)
            {
                if (window.PollShouldClose()) goto ExitLoop;

                // 窗口尺寸变化检测
                var (curW, curH) = window.GetDrawableSize();
                if (curW != displayW || curH != displayH)
                {
                    displayW = curW; displayH = curH;
                    io.DisplaySize = new Vector2(displayW, displayH);
                    layer.SetDrawableSize(displayW, displayH);
                }

                float dt = (float)(stopwatch.ElapsedTicks - lastFrameTicks) / System.Diagnostics.Stopwatch.Frequency;
                lastFrameTicks = stopwatch.ElapsedTicks;
                float t = (stopwatch.ElapsedMilliseconds - startTime) / 1000f;

                var drawable = layer.NextDrawable();
                if (drawable == null) { Thread.Sleep(8); continue; }

                // 创建临时 render pass descriptor（供 ImGui Metal backend 使用）
                var rpDesc = MetalRenderPassDescriptor.CreateForTexture(drawable);
                try
                {
                    // ── ImGui 新帧 ───────────────────────────────
                    ImGuiImplOSX.NewFrame((void*)window.ViewHandle);
                    ImGuiImplMetal.NewFrame((MTLRenderPassDescriptor*)rpDesc.Handle);
                    ImGui.NewFrame();

                    // 控制面板
                    ImGui.SetNextWindowSize(new Vector2(340, 360), ImGuiCond.FirstUseEver);
                    ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
                    ImGui.Begin("Mandelbrot Controls");
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"FPS: {fps:F1}  |  Frame: {frame}");
                    ImGui.Text($"Device: {device.Name}");
                    ImGui.Separator();
                    ImGui.Checkbox("Animate", ref animate);
                    ImGui.Separator();
                    ImGui.Text("Zoom Breathing");
                    ImGui.SliderFloat("zoom amp",  ref zoomAmp,  0.0f, 0.95f);
                    ImGui.SliderFloat("zoom freq", ref zoomFreq, 0.0f, 2.0f);
                    ImGui.Separator();
                    ImGui.Text("Center Drift");
                    ImGui.SliderFloat("drift X",        ref driftX,       -1.0f, 1.0f);
                    ImGui.SliderFloat("drift Y",        ref driftY,       -1.0f, 1.0f);
                    ImGui.SliderFloat("drift freq X",   ref driftFreqX,    0.0f, 1.0f);
                    ImGui.SliderFloat("drift freq Y",   ref driftFreqY,    0.0f, 1.0f);
                    ImGui.Separator();
                    ImGui.Text("Iteration Breathing");
                    ImGui.SliderInt("iter base",  ref baseMaxIter, 16, 512);
                    ImGui.SliderInt("iter amp",   ref iterAmp,     0, 256);
                    ImGui.SliderFloat("iter freq", ref iterFreq,   0.0f, 2.0f);
                    ImGui.Separator();
                    ImGui.Text("View Center (base)");
                    ImGui.SliderFloat("center X", ref centerX0, -2.0f, 2.0f);
                    ImGui.SliderFloat("center Y", ref centerY0, -2.0f, 2.0f);
                    ImGui.Separator();
                    ImGui.Checkbox("Show ImGui Demo Window", ref showDemo);
                    ImGui.End();

                    if (showDemo) ImGui.ShowDemoWindow(ref showDemo);
                    ImGui.Render();

                    // ── 计算当前帧的 Mandelbrot 参数 ───────────
                    // 缩放呼吸：scale 在 baseScale*(1±zoomAmp) 之间振荡，越小越放大
                    float scale = baseScale;
                    float cx = centerX0, cy = centerY0;
                    int maxIter = baseMaxIter;
                    if (animate)
                    {
                        // 缩放：用平滑相位，让 zoom-in 看起来更剧烈
                        float zoomPhase = 0.5f * (1.0f - MathF.Cos(t * zoomFreq * 2.0f * MathF.PI)); // 0..1
                        scale = baseScale * (1.0f - zoomAmp * zoomPhase);

                        // 中心漂移：X/Y 不同频率形成李萨如封闭轨迹
                        cx = centerX0 + driftX * MathF.Sin(t * driftFreqX * 2.0f * MathF.PI);
                        cy = centerY0 + driftY * MathF.Sin(t * driftFreqY * 2.0f * MathF.PI);

                        // 迭代呼吸：独立频率，与缩放解耦
                        float iterPhase = 0.5f * (1.0f - MathF.Cos(t * iterFreq * 2.0f * MathF.PI));
                        maxIter = baseMaxIter + (int)(iterAmp * iterPhase);
                    }

                    // 帧缓存：参数未变时跳过 compute dispatch（静态画面不重算，省 GPU）
                    bool paramsChanged = cx != prevCx || cy != prevCy
                        || scale != prevScale || maxIter != prevMaxIter;
                    prevCx = cx; prevCy = cy; prevScale = scale; prevMaxIter = maxIter;

                    // 写参数到 Output[0]
                    Span<float4> data = buffer.AsSpan<float4>();
                    data[0] = new float4(cx, cy, scale, maxIter);

                    // ── 提交 GPU 工作 ───────────────────────────
                    using var cmdbuf = queue.CommandBuffer();

                    // Compute pass：仅当参数变化时才重算 Mandelbrot 像素（静态帧缓存）
                    if (paramsChanged)
                    {
                        using var cEnc = cmdbuf.ComputeCommandEncoder();
                        cEnc.SetComputePipelineState(computePso);
                        cEnc.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);

                        var uavDesc = new UavDescriptor
                        {
                            GpuAddress = buffer.GpuAddress,
                            Length = buffer.Length,
                            Stride = 16, // float4
                        };
                        cEnc.SetBytes(uavDesc, ArgumentTableBufferIndex);

                        int groupsX = (Width + ThreadGroupWidth - 1) / ThreadGroupWidth;
                        int groupsY = (Height + ThreadGroupHeight - 1) / ThreadGroupHeight;
                        cEnc.DispatchThreadgroups(
                            new WMTSize((ulong)groupsX, (ulong)groupsY, 1),
                            new WMTSize(ThreadGroupWidth, ThreadGroupHeight, 1));
                        cEnc.EndEncoding();
                    }

                    // Scene render pass：全屏四边形采样 buffer
                    var scenePassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: true);
                    var sceneEnc = cmdbuf.RenderCommandEncoder(scenePassDesc);
                    try
                    {
                        sceneEnc.SetRenderPipelineState(renderPso);
                        sceneEnc.SetViewport(0, 0, WindowWidth, WindowHeight, 0, 1);

                        var pixDesc = new UavDescriptor
                        {
                            GpuAddress = buffer.GpuAddress,
                            Length = buffer.Length,
                            Stride = 16, // float4
                        };
                        sceneEnc.SetFragmentBytes<UavDescriptor>(pixDesc, ArgumentTableBufferIndex);
                        sceneEnc.UseResource(buffer, MTLResourceUsage.Read, MTLRenderStages.Fragment);
                        sceneEnc.DrawTriangles(0, 3);
                    }
                    finally
                    {
                        sceneEnc.EndEncoding();
                        sceneEnc.Dispose();
                    }

                    // ImGui overlay pass：在 scene 之上叠加 UI
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

                // ── FPS 统计 ───────────────────────────────────
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
            ClearColor = new WMTClearColor(0.0f, 0.0f, 0.0f, 1f),
        };
        passDesc.SetColorAt(0, att);
        return passDesc;
    }
}
