using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.Metal;
using Hexa.NET.ImGui.Backends.OSX;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;
using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Compilers;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// Phase 9 综合 Demo：GPU 粒子系统。
///
/// <para>展示引擎全栈能力：</para>
/// <list type="bullet">
/// <item><b>SlangCompiler</b>：运行时编译 compute 着色器 (Slang → DXIL → metallib)</item>
/// <item><b>MSL 运行时编译</b>：渲染着色器使用 MSL 直接绑定 (newLibraryWithSource)</item>
/// <item><b>ShaderCache</b>：两级缓存（L1 内存 + L2 磁盘），后续帧零编译开销</item>
/// <item><b>Compute</b>：GPU 粒子物理模拟（重力 + 风力 + 生命周期）</item>
/// <item><b>Instanced draw</b>：万级粒子一次 draw call</item>
/// </list>
///
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- particles
/// </summary>
internal static class GpuParticleDemo
{
    private const int W = 1024, H = 768;
    private const int MaxParticles = 10000;
    private const int ThreadsPerGroup = 64;
    private const ulong ArgIndex = 2;  // MSC argument buffer at buffer(2)

    // ============================================================
    //  GPU 数据结构
    // ============================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct Particle
    {
        public Vector2 Position;   // 0-7
        public Vector2 Velocity;   // 8-15
        public Vector4 Color;      // 16-31
        public float Life;         // 32-35
        public float MaxLife;      // 36-39
        public float Size;         // 40-43
        public float Pad;          // 44-47 → 48 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrameCB
    {
        public Vector2 Viewport;   // 0-7
        public float Time;         // 8-11
        public float DeltaTime;    // 12-15
        public Vector2 Gravity;    // 16-23
        public Vector2 Wind;       // 24-31
        public float EmitterX;     // 32-35
        public float EmitterY;     // 36-39
        public int ActiveCount;    // 40-43
        public float Pad1, Pad2, Pad3; // 44-55
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    // ============================================================
    //  Compute 着色器（Slang → MSC，argument buffer 在 compute 中工作正常）
    // ============================================================

    private const string ComputeShaderSource = @"
struct Particle {
    float2 position;
    float2 velocity;
    float4 color;
    float life;
    float maxLife;
    float size;
    float pad;
};
struct PerFrame {
    float2 viewport;
    float time;
    float deltaTime;
    float2 gravity;
    float2 wind;
    float emitterX;
    float emitterY;
    int activeCount;
    float pad1, pad2, pad3;
};

RWStructuredBuffer<Particle> particles;
StructuredBuffer<PerFrame> perFrame;

float hash(uint n) {
    n = (n << 13) ^ n;
    return 1.0 - float((n * (n * n * 15731u + 789221u) + 1376312589u) & 0x7fffffffu) / 1073741824.0;
}

[numthreads(64, 1, 1)]
void main(uint3 tid : SV_DispatchThreadID) {
    uint idx = tid.x;
    if (idx >= uint(perFrame[0].activeCount)) return;

    Particle p = particles[idx];
    float dt = perFrame[0].deltaTime;

    p.velocity += perFrame[0].gravity * dt;
    p.velocity += perFrame[0].wind * dt * 0.3;
    p.velocity *= 0.998;
    p.position += p.velocity * dt;
    p.life -= dt;

    if (p.life <= 0) {
        float angle = hash(idx * 73u + uint(perFrame[0].time * 1000.0)) * 6.28318;
        float speed = 40.0 + hash(idx * 137u) * 120.0;
        p.position = float2(perFrame[0].emitterX, perFrame[0].emitterY);
        p.velocity = float2(cos(angle), sin(angle)) * speed;
        p.velocity.y -= 40.0;
        p.life = p.maxLife;
        float h = hash(idx * 311u + uint(perFrame[0].time * 100.0));
        p.color = float4(0.4 + h * 0.6, 0.2 + abs(h) * 0.5, 0.7 + h * 0.3, 1.0);
    }

    float2 vp = perFrame[0].viewport;
    if (p.position.x < 0 || p.position.x > vp.x) { p.velocity.x *= -0.6; p.position.x = clamp(p.position.x, 0, vp.x); }
    if (p.position.y > vp.y) { p.velocity.y *= -0.5; p.position.y = vp.y; }

    float speed2 = length(p.velocity);
    p.color.r = clamp(speed2 / 200.0, 0.1, 1.0);
    p.color.g = clamp(p.life / p.maxLife, 0.0, 1.0) * 0.6;
    p.color.a = clamp(p.life / p.maxLife, 0.05, 1.0);

    particles[idx] = p;
}
";

    // ============================================================
    //  渲染着色器（MSL 直接绑定 — 绕过 MSC argument buffer 兼容性问题）
    // ============================================================

    private const string RenderVertMSL = @"
#include <metal_stdlib>
using namespace metal;

struct Particle {
    float2 position;
    float2 velocity;
    float4 color;
    float life;
    float maxLife;
    float size;
    float pad;
};

struct PerFrame {
    float2 viewport;
    float time;
    float deltaTime;
    float2 gravity;
    float2 wind;
    float emitterX;
    float emitterY;
    int activeCount;
    float p1, p2, p3;
};

struct VSOut {
    float4 position [[position]];
    float4 color;
    float2 uv;
};

vertex VSOut particle_vs(
    uint vid [[vertex_id]],
    uint iid [[instance_id]],
    const device Particle* particles [[buffer(0)]],
    const device PerFrame* pf [[buffer(1)]])
{
    Particle p = particles[iid];

    float2 offsets[6] = {
        float2(-1,-1), float2(-1,1), float2(1,-1),
        float2(1,-1), float2(-1,1), float2(1,1)
    };
    float2 off = offsets[vid];

    float2 vp = pf[0].viewport;
    float2 ndc = (p.position / vp) * 2.0 - 1.0;
    ndc.y = -ndc.y;

    float sz = p.size;
    float2 pxSize = sz * 2.0 / vp;

    VSOut o;
    o.position = float4(ndc + off * pxSize, 0, 1);
    o.color = p.color;
    o.uv = off;
    return o;
}
";

    private const string RenderFragMSL = @"
#include <metal_stdlib>
using namespace metal;

struct VSOut {
    float4 position [[position]];
    float4 color;
    float2 uv;
};

fragment float4 particle_fs(VSOut in [[stage_in]]) {
    float dist = length(in.uv);
    if (dist > 1.0) discard_fragment;
    float alpha = in.color.a * (1.0 - dist * dist);
    return float4(in.color.rgb * alpha, alpha);
}
";

    // ============================================================
    //  Demo 入口
    // ============================================================

    public static unsafe int Run()
    {
        Console.WriteLine("=== GPU Particle System Demo (Phase 9 Showcase) ===");
        var sw = Stopwatch.StartNew();

        using var device = MetalDevice.CreateSystemDefault();
        Console.WriteLine($"Device: {device.Name} (UMA: {device.HasUnifiedMemory})");

        // ① 运行时编译 compute 着色器（SlangCompiler + ShaderCache）
        Console.WriteLine("▸ Compiling compute shader via SlangCompiler (Slang → DXIL → metallib)...");
        var compiler = new CachingShaderCompiler(new SlangCompiler(), new ShaderCache());
        var computeOpts = new ShaderCompileOptions { Stage = ShaderStage.Compute, GenerateReflection = true };

        var t0 = Stopwatch.StartNew();
        var computeResult = compiler.CompileFromSource(
            System.Text.Encoding.UTF8.GetBytes(ComputeShaderSource), "ParticleUpdate.slang", computeOpts);
        t0.Stop();
        Console.WriteLine($"  Compute compile: {t0.ElapsedMilliseconds}ms");

        if (computeResult.ReflectionJson != null)
        {
            var reflect = Shader.Reflection.MscReflectionParser.Parse(computeResult.ReflectionJson);
            Console.WriteLine($"  Reflection: {reflect.ResourceCount} resources, " +
                              $"{reflect.TopLevelArgumentBuffer.Count} arg buffer entries");
            foreach (var e in reflect.TopLevelArgumentBuffer)
                Console.WriteLine($"    [{e.Type}] Slot={e.Slot} Offset={e.EltOffset} Size={e.Size}");
        }

        // 缓存命中验证
        var t1 = Stopwatch.StartNew();
        var cached = compiler.CompileFromSource(
            System.Text.Encoding.UTF8.GetBytes(ComputeShaderSource), "ParticleUpdate.slang", computeOpts);
        t1.Stop();
        Console.WriteLine($"  Cache hit: {t1.ElapsedMilliseconds}ms (hit={cached.CacheHit})");

        // ② 运行时编译渲染着色器（MSL → newLibraryWithSource）
        Console.WriteLine("▸ Compiling render shaders via MSL (newLibraryWithSource)...");
        var t2 = Stopwatch.StartNew();
        using var vertLib = device.NewLibraryWithSource(RenderVertMSL);
        using var fragLib = device.NewLibraryWithSource(RenderFragMSL);
        t2.Stop();
        Console.WriteLine($"  MSL compile: {t2.ElapsedMilliseconds}ms");

        // ③ 创建 PSO
        using var computeFn = device.NewLibrary(computeResult.MetallibData!).NewFunction("main");
        using var computePso = device.NewComputePipelineState(computeFn);
        Console.WriteLine($"  Compute PSO: maxThreads={computePso.MaxTotalThreadsPerThreadgroup}");

        using var vertFn = vertLib.NewFunction("particle_vs");
        using var fragFn = fragLib.NewFunction("particle_fs");

        var pipeDesc = new WMTRenderPipelineDesc { ColorCount = 1, SampleCount = 1 };
        pipeDesc.ColorAttachmentAt(0) = new WMTColorAttachment
        {
            PixelFormat = (int)MTLPixelFormat.BGRA8Unorm,
            WriteMask = 0xF,
            BlendingEnabled = 1,
            SrcRgbBlendFactor = (int)MTLBlendFactor.One,       // Additive
            DstRgbBlendFactor = (int)MTLBlendFactor.One,       // Additive
            SrcAlphaBlendFactor = (int)MTLBlendFactor.One,
            DstAlphaBlendFactor = (int)MTLBlendFactor.One,
            RgbBlendOp = (int)MTLBlendOperation.Add,
            AlphaBlendOp = (int)MTLBlendOperation.Add,
        };
        using var renderPso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        // ④ Buffers
        int particleSize = Marshal.SizeOf<Particle>();
        int perFrameSize = Marshal.SizeOf<PerFrameCB>();

        using var particleBuffer = device.NewBuffer(
            (ulong)(MaxParticles * particleSize), MTLResourceOptions.StorageModeShared);
        using var perFrameBuffer = device.NewBuffer(
            (ulong)perFrameSize, MTLResourceOptions.StorageModeShared);

        // 初始化粒子
        var rng = new Random(42);
        Span<Particle> particles = particleBuffer.AsSpan<Particle>();
        for (int i = 0; i < MaxParticles; i++)
        {
            float angle = (float)(rng.NextDouble() * Math.PI * 2);
            float speed = 50f + (float)rng.NextDouble() * 150f;
            particles[i] = new Particle
            {
                Position = new Vector2(W / 2f, H * 0.65f),
                Velocity = new Vector2(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed - 60f),
                Color = new Vector4(
                    0.3f + (float)rng.NextDouble() * 0.7f,
                    0.1f + (float)rng.NextDouble() * 0.5f,
                    0.5f + (float)rng.NextDouble() * 0.5f, 1f),
                Life = 1f + (float)rng.NextDouble() * 4f,
                MaxLife = 2f + (float)rng.NextDouble() * 4f,
                Size = 6f + (float)rng.NextDouble() * 12f,
            };
        }

        // ⑤ 窗口 + ImGui
        using var window = NativeWindow.Create("GPU Particles — Phase 9", W, H);
        var layer = new MetalLayer(window.LayerHandle);
        layer.SetDevice(device);
        layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
        layer.SetDrawableSize(W, H);

        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        ImGuiImplOSX.SetCurrentContext(ctx);
        ImGuiImplOSX.Init((void*)window.ViewHandle);
        ImGuiImplMetal.SetCurrentContext(ctx);
        ImGuiImplMetal.Init((MTLDevice*)device.Handle);

        // 控制参数
        int activeCount = MaxParticles;
        float gravityY = 80f;
        float windX = 0f;
        bool paused = false;

        using var queue = device.NewCommandQueue();
        var stopwatch2 = Stopwatch.StartNew();
        long lastFrameTicks = stopwatch2.ElapsedTicks;
        int frame = 0;
        float fps = 0;
        int fpsCounter = 0;
        long lastFpsTime = stopwatch2.ElapsedMilliseconds;
        float frameMs = 0;
        int displayW = W, displayH = H;

        Console.WriteLine("▸ Entering render loop... (close window to exit)");

        while (true)
        {
            if (window.PollShouldClose()) break;

            var (curW, curH) = window.GetDrawableSize();
            if (curW != displayW || curH != displayH)
            {
                displayW = curW; displayH = curH;
                io.DisplaySize = new Vector2(displayW, displayH);
                layer.SetDrawableSize(displayW, displayH);
            }

            var drawable = layer.NextDrawable();
            if (drawable == null) { Thread.Sleep(8); continue; }

            long curTicks = stopwatch2.ElapsedTicks;
            float dt = (float)(curTicks - lastFrameTicks) / Stopwatch.Frequency;
            lastFrameTicks = curTicks;
            if (dt > 0.05f) dt = 0.05f;
            float time = (float)stopwatch2.Elapsed.TotalSeconds;

            var rpDesc = MetalRenderPassDescriptor.CreateForTexture(drawable);
            try
            {
                ImGuiImplOSX.NewFrame((void*)window.ViewHandle);
                ImGuiImplMetal.NewFrame((MTLRenderPassDescriptor*)rpDesc.Handle);
                ImGui.NewFrame();

                ImGui.SetNextWindowSize(new Vector2(320, 240), ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
                ImGui.Begin("GPU Particles");
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), $"FPS: {fps:F1}  |  Frame: {frame}");
                ImGui.Text($"Frame: {frameMs:F2}ms");
                ImGui.Separator();
                ImGui.SliderInt("Particles", ref activeCount, 100, MaxParticles);
                ImGui.SliderFloat("Gravity", ref gravityY, -200f, 200f);
                ImGui.SliderFloat("Wind", ref windX, -100f, 100f);
                ImGui.Checkbox("Paused", ref paused);
                ImGui.End();
                ImGui.Render();

                // 更新 PerFrame
                if (!paused)
                {
                    perFrameBuffer.AsSpan<PerFrameCB>()[0] = new PerFrameCB
                    {
                        Viewport = new Vector2(W, H),
                        Time = time,
                        DeltaTime = dt,
                        Gravity = new Vector2(0, gravityY),
                        Wind = new Vector2(windX, 0),
                        EmitterX = W / 2f + MathF.Sin(time * 0.5f) * 100f,
                        EmitterY = H * 0.65f,
                        ActiveCount = activeCount,
                    };
                }

                using var cmdbuf = queue.CommandBuffer();

                // ---- Compute pass: GPU 粒子模拟 (Slang/MSC argument buffer) ----
                if (!paused)
                {
                    int groups = (activeCount + ThreadsPerGroup - 1) / ThreadsPerGroup;
                    var computeArgBuf = new UavDescriptor[2]
                    {
                        new UavDescriptor { GpuAddress = perFrameBuffer.GpuAddress, Length = perFrameBuffer.Length, Stride = (ulong)perFrameSize },
                        new UavDescriptor { GpuAddress = particleBuffer.GpuAddress, Length = particleBuffer.Length, Stride = (ulong)particleSize },
                    };
                    using var computeEnc = cmdbuf.ComputeCommandEncoder();
                    computeEnc.SetComputePipelineState(computePso);
                    computeEnc.UseResource(particleBuffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
                    computeEnc.UseResource(perFrameBuffer, MTLResourceUsage.Read);
                    fixed (UavDescriptor* p = computeArgBuf)
                        MetalBridge.MTLComputeCommandEncoder_setBytes(
                            computeEnc.Handle, p, (ulong)(computeArgBuf.Length * 24), ArgIndex);
                    computeEnc.DispatchThreadgroups(
                        new WMTSize((ulong)groups, 1, 1),
                        new WMTSize(ThreadsPerGroup, 1, 1));
                    computeEnc.EndEncoding();
                }

                // ---- Render pass: 绘制粒子 (MSL 直接 buffer 绑定) ----
                var scenePassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: true);
                var renderEnc = cmdbuf.RenderCommandEncoder(scenePassDesc);
                renderEnc.SetRenderPipelineState(renderPso);
                renderEnc.SetViewport(0, 0, displayW, displayH, 0, 1);
                renderEnc.SetVertexBuffer(particleBuffer, 0, 0);   // buffer(0) = particles
                renderEnc.SetVertexBuffer(perFrameBuffer, 0, 1);   // buffer(1) = perFrame
                renderEnc.DrawPrimitives(0, 0, 6, (ulong)activeCount);
                renderEnc.EndEncoding();
                renderEnc.Dispose();

                // ---- ImGui overlay pass ----
                var uiPassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: false);
                var uiEnc = cmdbuf.RenderCommandEncoder(uiPassDesc);
                ImGuiImplMetal.RenderDrawData(
                    ImGui.GetDrawData(),
                    (MTLCommandBuffer*)cmdbuf.Handle,
                    (MTLRenderCommandEncoder*)uiEnc.Handle);
                uiEnc.EndEncoding();
                uiEnc.Dispose();

                cmdbuf.PresentDrawable(drawable);
                cmdbuf.Commit();
            }
            finally
            {
                rpDesc.Dispose();
                drawable.Dispose();
            }

            frame++;
            fpsCounter++;
            long nowMs = stopwatch2.ElapsedMilliseconds;
            if (nowMs - lastFpsTime >= 500)
            {
                fps = fpsCounter * 1000f / (nowMs - lastFpsTime);
                fpsCounter = 0;
                lastFpsTime = nowMs;
            }
            frameMs = dt * 1000f;

            if (frame % 120 == 0)
                Console.WriteLine($"  frame {frame}: {activeCount} particles, {fps:F1} FPS, {frameMs:F1}ms");
            Thread.Sleep(8);
        }

        ImGuiImplMetal.Shutdown();
        ImGuiImplOSX.Shutdown();
        Console.WriteLine($"✅ Rendered {frame} frames, {activeCount} GPU particles.");
        return 0;
    }

    // ============================================================
    //  辅助
    // ============================================================

    private static WMTRenderPassDesc BuildRenderPassDesc(nuint textureHandle, bool clear)
    {
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = textureHandle,
            LoadAction = clear ? (int)MTLLoadAction.Clear : (int)MTLLoadAction.Load,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0.02f, 0.02f, 0.06f, 1f),
        });
        return passDesc;
    }
}
