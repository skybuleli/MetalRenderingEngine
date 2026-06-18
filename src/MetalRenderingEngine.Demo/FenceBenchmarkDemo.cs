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
/// Phase 6: Fence 同步策略对比 benchmark。
/// 模拟 triple-buffer 场景（CPU 每帧准备数据 → GPU 重 compute → 必须等 GPU 完成才能覆写 slot），
/// 对比两种同步策略的主线程占用与帧时间：
///   • MTLFence 模式：GPU fence + 帧末 WaitUntilCompleted 阻塞主线程
///   • MTLSharedEvent 模式：GPU encodeWaitForEvent/encodeSignalEvent + CPU 异步回调，主线程不阻塞
/// 量化指标：CPU 主线程占用、GPU 等待、FPS。
///
/// 注意：MTLSharedEvent 在无签名命令行进程里可能不可用（GPU 沙箱），
/// 此时 demo 自动降级为仅 MTLFence 模式并提示。
/// </summary>
internal static class FenceBenchmarkDemo
{
    private const int WindowWidth = 800;
    private const int WindowHeight = 600;
    private const int SlotCount = 3;          // triple-buffer
    private const int ComputeElements = 1048576; // 1M 元素，重 compute 负载
    private const int ThreadsPerGroup = 64;
    private const int ComputeIterations = 32;  // 每帧 dispatch 次数（加重负载到 ~5ms）
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
            Console.WriteLine("=== Phase 6: Fence Benchmark (MTLFence vs MTLSharedEvent) ===");

            using var device = MetalDevice.CreateSystemDefault();
            Console.WriteLine($"Device: {device.Name} (UMA: {device.HasUnifiedMemory})");

            // 检测 SharedEvent 是否可用（沙箱环境可能不可用）
            MetalSharedEvent? sharedEvt = null;
            MetalSharedEventListener? listener = null;
            bool sharedEventAvailable = false;
            try
            {
                sharedEvt = device.NewSharedEvent();
                listener = MetalSharedEventListener.Create();
                sharedEventAvailable = true;
                Console.WriteLine("✅ MTLSharedEvent 可用");
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

            // Compute 管线（Multiply，作为 GPU 负载）
            using var computeFn = MetalShaderLoader.GetFunction(device, "Multiply", "main");
            using var pso = device.NewComputePipelineState(computeFn);

            // Triple-buffer 资源
            var buffers = new MetalBuffer[SlotCount];
            var fences = new MetalFence[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                buffers[i] = device.NewBuffer(
                    (ulong)(ComputeElements * sizeof(float)),
                    MTLResourceOptions.StorageModeShared);
                fences[i] = device.NewFence();
                // 初始化数据
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
            int mode = sharedEventAvailable ? 1 : 0;  // 0=Fence, 1=SharedEvent
            float fps = 0;
            int fpsCounter = 0;
            long lastFpsTime = 0;
            float cpuFrameMs = 0;       // CPU 主线程每帧总占用
            float cpuWaitMs = 0;        // CPU 等待 GPU 的时间（阻塞部分）
            float gpuBusyMs = 0;        // GPU compute 耗时估算
            float maxCpuWaitMs = 0;

            // SharedEvent 异步回调状态：每个 slot 一个 MRE
            var slotReady = new ManualResetEventSlim[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                slotReady[i] = new ManualResetEventSlim(true); // 初始可写（首帧无需等）
            }
            ulong sharedFrameCounter = 0;

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

                    ImGui.SetNextWindowSize(new Vector2(420, 260), ImGuiCond.FirstUseEver);
                    ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
                    ImGui.Begin("Phase 6 — Fence Benchmark");
                    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"FPS: {fps:F1}  |  Frame: {frame}");
                    ImGui.Text($"Compute: {ComputeElements} floats × {ComputeIterations} dispatches/frame");
                    ImGui.Separator();
                    if (sharedEventAvailable)
                    {
                        ImGui.RadioButton("MTLFence (blocking)", ref mode, 0);
                        ImGui.SameLine();
                        ImGui.RadioButton("MTLSharedEvent (async)", ref mode, 1);
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.6f, 0.2f, 1.0f), "SharedEvent 不可用，仅 Fence 模式");
                        mode = 0;
                    }
                    ImGui.Separator();
                    ImGui.Text($"CPU frame time:  {cpuFrameMs:F3} ms");
                    ImGui.Text($"CPU wait (block): {cpuWaitMs:F3} ms (max {maxCpuWaitMs:F3})");
                    ImGui.Text($"GPU busy (est):   {gpuBusyMs:F3} ms");
                    ImGui.Text($"CPU useful:       {cpuFrameMs - cpuWaitMs:F3} ms");
                    ImGui.End();
                    ImGui.Render();

                    long frameStart = stopwatch.ElapsedTicks;
                    long waitStart = 0, waitEnd = 0;
                    int slot = frame % SlotCount;

                    using var cmdbuf = queue.CommandBuffer();

                    if (mode == 0)
                    {
                        // ── MTLFence 模式：帧末 WaitUntilCompleted 阻塞主线程 ──
                        using (var cEnc = cmdbuf.ComputeCommandEncoder())
                        {
                            cEnc.SetComputePipelineState(pso);
                            cEnc.UseResource(buffers[slot], MTLResourceUsage.Read | MTLResourceUsage.Write);
                            var uav = new UavDescriptor
                            {
                                GpuAddress = buffers[slot].GpuAddress,
                                Length = buffers[slot].Length,
                                Stride = sizeof(float),
                            };
                            cEnc.SetBytes(uav, ArgumentTableBufferIndex);
                            int groups = ComputeElements / ThreadsPerGroup;
                            for (int iter = 0; iter < ComputeIterations; iter++)
                            {
                                cEnc.DispatchThreadgroups(
                                    new WMTSize((ulong)groups, 1, 1),
                                    new WMTSize(ThreadsPerGroup, 1, 1));
                            }
                            cEnc.EndEncoding();
                        }

                        // ImGui overlay（commit 之前编码）
                        EncodeImGuiOverlay(cmdbuf, drawable);

                        cmdbuf.PresentDrawable(drawable);
                        cmdbuf.Commit();

                        // 阻塞等待 GPU 完成（主线程空等）
                        waitStart = stopwatch.ElapsedTicks;
                        cmdbuf.WaitUntilCompleted();
                        waitEnd = stopwatch.ElapsedTicks;
                    }
                    else
                    {
                        // ── MTLSharedEvent 模式：异步回调，主线程不阻塞 ──
                        // 等 slot 可写（上一帧该 slot 的 GPU 工作完成）
                        waitStart = stopwatch.ElapsedTicks;
                        slotReady[slot].Wait();
                        slotReady[slot].Reset();
                        waitEnd = stopwatch.ElapsedTicks;

                        // 注册本帧完成回调（signal value = ++sharedFrameCounter）
                        sharedFrameCounter++;
                        ulong signalValue = sharedFrameCounter;
                        sharedEvt!.NotifyListener(listener!, signalValue, v =>
                        {
                            // listener 后台线程触发：标记 slot 可写
                            slotReady[slot].Set();
                        });

                        // GPU：先 wait 前一轮该 slot 的完成（value = signalValue - SlotCount）
                        if (signalValue > (ulong)SlotCount)
                        {
                            cmdbuf.EncodeWaitForEvent(sharedEvt, signalValue - (ulong)SlotCount);
                        }

                        using (var cEnc = cmdbuf.ComputeCommandEncoder())
                        {
                            cEnc.SetComputePipelineState(pso);
                            cEnc.UseResource(buffers[slot], MTLResourceUsage.Read | MTLResourceUsage.Write);
                            var uav = new UavDescriptor
                            {
                                GpuAddress = buffers[slot].GpuAddress,
                                Length = buffers[slot].Length,
                                Stride = sizeof(float),
                            };
                            cEnc.SetBytes(uav, ArgumentTableBufferIndex);
                            int groups = ComputeElements / ThreadsPerGroup;
                            for (int iter = 0; iter < ComputeIterations; iter++)
                            {
                                cEnc.DispatchThreadgroups(
                                    new WMTSize((ulong)groups, 1, 1),
                                    new WMTSize(ThreadsPerGroup, 1, 1));
                            }
                            cEnc.EndEncoding();
                        }

                        // GPU：完成后 signal
                        cmdbuf.EncodeSignalEvent(sharedEvt, signalValue);

                        // ImGui overlay（commit 之前编码）
                        EncodeImGuiOverlay(cmdbuf, drawable);

                        cmdbuf.PresentDrawable(drawable);
                        cmdbuf.Commit();
                        // 不调 WaitUntilCompleted —— 主线程立即继续下一帧
                    }

                    long frameEnd = stopwatch.ElapsedTicks;
                    cpuFrameMs = (float)(frameEnd - frameStart) * 1000f / System.Diagnostics.Stopwatch.Frequency;
                    cpuWaitMs = (float)(waitEnd - waitStart) * 1000f / System.Diagnostics.Stopwatch.Frequency;
                    if (cpuWaitMs > maxCpuWaitMs) maxCpuWaitMs = cpuWaitMs;
                    // GPU busy 估算：Fence 模式下 = cpuWaitMs；SharedEvent 模式下无法直接测，用 CPU frame - CPU useful 估算
                    gpuBusyMs = mode == 0 ? cpuWaitMs : Math.Max(0, cpuFrameMs - (cpuFrameMs - cpuWaitMs));
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
                    // 控制台汇总（供 bench 脚本采集）
                    Console.WriteLine($"[fence-bench] mode={(mode==0?"Fence":"SharedEvent")} fps={fps:F1} " +
                        $"cpuFrame={cpuFrameMs:F2}ms cpuWait={cpuWaitMs:F2}ms gpuBusy={gpuBusyMs:F2}ms");
                }
            }

        ExitLoop:
            ImGuiImplMetal.Shutdown();
            ImGuiImplOSX.Shutdown();
            sharedEvt?.Dispose();
            listener?.Dispose();
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

    /// <summary>编码 ImGui overlay pass（在 commit 之前调用）。</summary>
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
