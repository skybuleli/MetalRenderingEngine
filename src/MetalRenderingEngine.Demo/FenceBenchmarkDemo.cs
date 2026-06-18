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
/// Phase 6: Fence 同步策略对比 benchmark（三种模式）。
///
/// 模拟 triple-buffer + 重 compute 负载，对比：
///   • Mode 0 — MTLFence：帧末 WaitUntilCompleted 阻塞主线程
///   • Mode 1 — GpuFence AsyncCallback：帧间异步回调，主线程不阻塞（流水线化）
///   • Mode 2 — GpuFence BlockingWait：数据依赖同步，阻塞但精确唤醒（只等特定 value）
///
/// Mode 1/2 用 SharedEventPool（预分配少量 event + signaledValue 区分同步点），
/// 避免 Metal ≤64 活跃 event 上限。适合模拟器场景（单帧数百 fence）。
///
/// 量化指标：CPU 帧时间、CPU 阻塞等待、GPU busy、FPS。
/// </summary>
internal static class FenceBenchmarkDemo
{
    private const int WindowWidth = 800;
    private const int WindowHeight = 600;
    private const int SlotCount = 3;              // triple-buffer
    private const int ComputeElements = 1048576;  // 1M 元素，重 compute 负载
    private const int ThreadsPerGroup = 64;
    private const int ComputeIterations = 32;     // 每帧 dispatch 次数（GPU ~3ms）
    private const ulong ArgumentTableBufferIndex = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    public static unsafe int Run()
    {
        try
        {
            Console.WriteLine("=== Phase 6: Fence Benchmark (MTLFence / GpuFence Async / GpuFence Block) ===");

            using var device = MetalDevice.CreateSystemDefault();
            Console.WriteLine($"Device: {device.Name} (UMA: {device.HasUnifiedMemory})");

            // 检测 SharedEvent 是否可用（沙箱环境可能不可用）
            SharedEventPool? pool = null;
            bool sharedEventAvailable = false;
            try
            {
                pool = new SharedEventPool(device, eventCount: 8);
                sharedEventAvailable = true;
                Console.WriteLine("✅ SharedEventPool 可用（8 events）");
            }
            catch (MetalException)
            {
                Console.WriteLine("⚠️ MTLSharedEvent 不可用（需签名 .app bundle），降级为仅 MTLFence 模式");
            }

            using var window = NativeWindow.Create("Phase 6 — Fence Benchmark", WindowWidth, WindowHeight);
            var layer = new MetalLayer(window.LayerHandle);
            layer.SetDevice(device);
            layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
            layer.SetDrawableSize(WindowWidth, WindowHeight);

            using var computeFn = MetalShaderLoader.GetFunction(device, "Multiply", "main");
            using var pso = device.NewComputePipelineState(computeFn);

            var buffers = new MetalBuffer[SlotCount];
            var fences = new MetalFence[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                buffers[i] = device.NewBuffer(
                    (ulong)(ComputeElements * sizeof(float)),
                    MTLResourceOptions.StorageModeShared);
                fences[i] = device.NewFence();
                Span<float> d = buffers[i].AsSpan<float>();
                for (int j = 0; j < ComputeElements; j++) d[j] = 1.0f;
            }

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
            int mode = sharedEventAvailable ? 1 : 0;
            float fps = 0;
            int fpsCounter = 0;
            long lastFpsTime = 0;
            float cpuFrameMs = 0;
            float cpuWaitMs = 0;
            float gpuBusyMs = 0;
            float maxCpuWaitMs = 0;

            // AsyncCallback 模式的 slot 就绪信号（每个 slot 一个 MRE）
            var slotReady = new ManualResetEventSlim[SlotCount];
            for (int i = 0; i < SlotCount; i++) slotReady[i] = new ManualResetEventSlim(true);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            lastFpsTime = stopwatch.ElapsedMilliseconds;
            int frame = 0;

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

                var drawable = layer.NextDrawable();
                if (drawable == null) { Thread.Sleep(8); continue; }

                var rpDesc = MetalRenderPassDescriptor.CreateForTexture(drawable);
                try
                {
                    // ImGui 新帧
                    ImGuiImplOSX.NewFrame((void*)window.ViewHandle);
                    ImGuiImplMetal.NewFrame((MTLRenderPassDescriptor*)rpDesc.Handle);
                    ImGui.NewFrame();

                    ImGui.SetNextWindowSize(new Vector2(440, 280), ImGuiCond.FirstUseEver);
                    ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
                    ImGui.Begin("Phase 6 — Fence Benchmark");
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"FPS: {fps:F1}  |  Frame: {frame}");
                    ImGui.Text($"Compute: {ComputeElements} floats × {ComputeIterations} dispatches");
                    ImGui.Separator();
                    if (sharedEventAvailable)
                    {
                        ImGui.RadioButton("MTLFence (blocking)", ref mode, 0);
                        ImGui.RadioButton("GpuFence Async (pipeline)", ref mode, 1);
                        ImGui.RadioButton("GpuFence Block (data dep)", ref mode, 2);
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), "SharedEvent 不可用，仅 MTLFence");
                        mode = 0;
                    }
                    ImGui.Separator();
                    ImGui.Text($"CPU frame:   {cpuFrameMs:F3} ms");
                    ImGui.Text($"CPU wait:    {cpuWaitMs:F3} ms (max {maxCpuWaitMs:F3})");
                    ImGui.Text($"GPU busy:    {gpuBusyMs:F3} ms");
                    ImGui.Text($"CPU useful:  {cpuFrameMs - cpuWaitMs:F3} ms");
                    string modeName = mode switch
                    {
                        0 => "MTLFence (WaitUntilCompleted 阻塞)",
                        1 => "GpuFence Async (回调流水线)",
                        _ => "GpuFence Block (精确唤醒)",
                    };
                    ImGui.Text($"Mode: {modeName}");
                    ImGui.End();
                    ImGui.Render();

                    long frameStart = stopwatch.ElapsedTicks;
                    long waitStart = 0, waitEnd = 0;
                    int slot = frame % SlotCount;

                    using var cmdbuf = queue.CommandBuffer();
                    var uav = new UavDescriptor
                    {
                        GpuAddress = buffers[slot].GpuAddress,
                        Length = buffers[slot].Length,
                        Stride = sizeof(float),
                    };
                    int groups = ComputeElements / ThreadsPerGroup;

                    if (mode == 0)
                    {
                        // ── Mode 0: MTLFence — 帧末 WaitUntilCompleted 阻塞主线程 ──
                        EncodeCompute(cmdbuf, pso, buffers[slot], uav, groups);
                        EncodeImGuiOverlay(cmdbuf, drawable);
                        cmdbuf.PresentDrawable(drawable);
                        cmdbuf.Commit();

                        waitStart = stopwatch.ElapsedTicks;
                        cmdbuf.WaitUntilCompleted();
                        waitEnd = stopwatch.ElapsedTicks;
                    }
                    else if (mode == 1)
                    {
                        // ── Mode 1: GpuFence AsyncCallback — 帧间异步回调，主线程不阻塞 ──
                        waitStart = stopwatch.ElapsedTicks;
                        slotReady[slot].Wait();       // 等上一帧该 slot 完成
                        slotReady[slot].Reset();
                        waitEnd = stopwatch.ElapsedTicks;

                        var fence = GpuFence.Create(pool!);
                        int capturedSlot = slot;

                        using (var cEnc = cmdbuf.ComputeCommandEncoder())
                        {
                            cEnc.SetComputePipelineState(pso);
                            cEnc.UseResource(buffers[slot], MTLResourceUsage.Read | MTLResourceUsage.Write);
                            cEnc.SetBytes(uav, ArgumentTableBufferIndex);
                            for (int iter = 0; iter < ComputeIterations; iter++)
                                cEnc.DispatchThreadgroups(new WMTSize((ulong)groups, 1, 1), new WMTSize(ThreadsPerGroup, 1, 1));
                            cEnc.EndEncoding();
                        }
                        fence.Signal(cmdbuf);          // GPU 完成后 signal
                        fence.WaitAsync(() => slotReady[capturedSlot].Set());  // 回调标记 slot 可写
                        EncodeImGuiOverlay(cmdbuf, drawable);
                        cmdbuf.PresentDrawable(drawable);
                        cmdbuf.Commit();
                        // 不 WaitUntilCompleted —— 主线程立即继续
                    }
                    else
                    {
                        // ── Mode 2: GpuFence BlockingWait — 数据依赖，阻塞但精确唤醒 ──
                        // 模拟"游戏 CPU 必须读 GPU 结果"：signal 后 CPU 阻塞等该 value
                        var fence = GpuFence.Create(pool!);
                        using (var cEnc = cmdbuf.ComputeCommandEncoder())
                        {
                            cEnc.SetComputePipelineState(pso);
                            cEnc.UseResource(buffers[slot], MTLResourceUsage.Read | MTLResourceUsage.Write);
                            cEnc.SetBytes(uav, ArgumentTableBufferIndex);
                            for (int iter = 0; iter < ComputeIterations; iter++)
                                cEnc.DispatchThreadgroups(new WMTSize((ulong)groups, 1, 1), new WMTSize(ThreadsPerGroup, 1, 1));
                            cEnc.EndEncoding();
                        }
                        fence.Signal(cmdbuf);
                        EncodeImGuiOverlay(cmdbuf, drawable);
                        cmdbuf.PresentDrawable(drawable);
                        cmdbuf.Commit();

                        // 阻塞等特定 value（比 WaitUntilCompleted 更精确——只等 signal 那一刻）
                        waitStart = stopwatch.ElapsedTicks;
                        fence.Wait(5000);
                        waitEnd = stopwatch.ElapsedTicks;
                    }

                    long frameEnd = stopwatch.ElapsedTicks;
                    cpuFrameMs = (float)(frameEnd - frameStart) * 1000f / System.Diagnostics.Stopwatch.Frequency;
                    cpuWaitMs = (float)(waitEnd - waitStart) * 1000f / System.Diagnostics.Stopwatch.Frequency;
                    if (cpuWaitMs > maxCpuWaitMs) maxCpuWaitMs = cpuWaitMs;
                    gpuBusyMs = mode == 1 ? Math.Max(0, cpuFrameMs - (cpuFrameMs - cpuWaitMs)) : cpuWaitMs;
                }
                finally
                {
                    rpDesc.Dispose();
                    drawable.Dispose();
                }

                frame++;
                fpsCounter++;
                long nowMs = stopwatch.ElapsedMilliseconds;
                if (nowMs - lastFpsTime >= 1000)
                {
                    fps = fpsCounter * 1000f / (nowMs - lastFpsTime);
                    fpsCounter = 0;
                    lastFpsTime = nowMs;
                    Console.WriteLine($"[fence-bench] mode={mode} fps={fps:F1} " +
                        $"cpuFrame={cpuFrameMs:F2}ms cpuWait={cpuWaitMs:F2}ms gpuBusy={gpuBusyMs:F2}ms");
                }
            }

        ExitLoop:
            ImGuiImplMetal.Shutdown();
            ImGuiImplOSX.Shutdown();
            pool?.Dispose();
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

    private static void EncodeCompute(MetalCommandBuffer cmdbuf, MetalComputePipelineState pso,
        MetalBuffer buffer, UavDescriptor uav, int groups)
    {
        using var cEnc = cmdbuf.ComputeCommandEncoder();
        cEnc.SetComputePipelineState(pso);
        cEnc.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
        cEnc.SetBytes(uav, ArgumentTableBufferIndex);
        for (int iter = 0; iter < ComputeIterations; iter++)
            cEnc.DispatchThreadgroups(new WMTSize((ulong)groups, 1, 1), new WMTSize(ThreadsPerGroup, 1, 1));
        cEnc.EndEncoding();
    }

    private static unsafe void EncodeImGuiOverlay(MetalCommandBuffer cmdbuf, MetalDrawable drawable)
    {
        var uiPassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: false);
        var uiEnc = cmdbuf.RenderCommandEncoder(uiPassDesc);
        ImGuiImplMetal.RenderDrawData(
            ImGui.GetDrawData(),
            (MTLCommandBuffer*)cmdbuf.Handle,
            (MTLRenderCommandEncoder*)uiEnc.Handle);
        uiEnc.EndEncoding();
        uiEnc.Dispose();
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
