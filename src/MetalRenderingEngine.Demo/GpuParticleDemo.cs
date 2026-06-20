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
/// <item><b>SlangCompiler</b>：运行时编译 compute + vertex + fragment 着色器 (Slang → DXIL → metallib)</item>
/// <item><b>MSC Argument Buffer</b>：compute 和 render 均使用 argument buffer（UseResource 声明驻留）</item>
/// <item><b>ShaderCache</b>：两级缓存（L1 内存 + L2 磁盘），后续帧零编译开销</item>
/// <item><b>Compute</b>：GPU 粒子物理模拟（重力 + 风力 + 生命周期）</item>
/// <item><b>Instanced draw</b>：万级粒子一次 draw call</item>
/// </list>
///
/// 运行：dotnet run --project src/MetalRenderingEngine.Demo -- particles
/// <para><b>绑定路径</b>：本 Demo 使用 StructuredBuffer + 手工 UavDescriptor argument buffer
    /// （Phase 7-9 遗留路径）。新开发请使用 ResourceTable / ShaderBindingLayout
    /// （Phase 10 描述符堆路径），参考 <see cref="TexturedCubeDemo"/>。</para>
    
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
        public Vector2 Viewport;       // 0-7
        public float Time;             // 8-11
        public float DeltaTime;        // 12-15
        public Vector2 Gravity;        // 16-23
        public Vector2 Wind;           // 24-31
        public float EmitterX;         // 32-35
        public float EmitterY;         // 36-39
        public int ActiveCount;        // 40-43
        public int ColorMode;          // 44-47: 0=default, 1=fire, 2=snow, 3=aurora, 4=fireworks
        public float EmitterSpread;    // 48-51
        public float SpeedMultiplier;  // 52-55
    }

    // 粒子预设
    private enum ParticlePreset { Default, Fire, Snow, Aurora, Fireworks }

    private static readonly string[] PresetNames = { "Default", "Fire ❗", "Snow ❄", "Aurora ✨", "Fireworks 🎆" };

    private static void ApplyPreset(ParticlePreset preset, ref float gravityY, ref float windX,
        ref float emitterSpread, ref float speedMul)
    {
        (gravityY, windX, emitterSpread, speedMul) = preset switch
        {
            ParticlePreset.Fire => (120f, 0f, 20f, 0.5f),       // 热空气上升（负重力方向）
            ParticlePreset.Snow => (-30f, 20f, 400f, 0.3f),     // 缓慢飘落 + 风
            ParticlePreset.Aurora => (0f, 0f, 500f, 0.8f),      // 无重力，宽幅波浪
            ParticlePreset.Fireworks => (-100f, 0f, 5f, 1.5f),  // 爆射 + 重力
            _ => (80f, 0f, 100f, 1f),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    // 顶点 argument buffer：2 × UavDescriptor = 48 字节
    [StructLayout(LayoutKind.Sequential)]
    private struct VertArgBuffer
    {
        public UavDescriptor Srv0;  // particles
        public UavDescriptor Srv1;  // perFrame
    }

    // ============================================================
    //  Compute 着色器（Slang → MSC，argument buffer）
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
    int colorMode;
    float emitterSpread;
    float speedMul;
};

RWStructuredBuffer<Particle> particles;
StructuredBuffer<PerFrame> perFrame;

float hash(uint n) {
    n = (n << 13) ^ n;
    return 1.0 - float((n * (n * n * 15731u + 789221u) + 1376312589u) & 0x7fffffffu) / 1073741824.0;
}

float3 hsvToRgb(float h, float s, float v) {
    float c = v * s;
    float x = c * (1.0 - abs(fmod(h * 6.0, 2.0) - 1.0));
    float m = v - c;
    float3 rgb;
    float hh = h * 6.0;
    if (hh < 1.0) rgb = float3(c, x, 0);
    else if (hh < 2.0) rgb = float3(x, c, 0);
    else if (hh < 3.0) rgb = float3(0, c, x);
    else if (hh < 4.0) rgb = float3(0, x, c);
    else if (hh < 5.0) rgb = float3(x, 0, c);
    else rgb = float3(c, 0, x);
    return rgb + m;
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
        float t = perFrame[0].time;
        float angle = hash(idx * 73u + uint(t * 1000.0)) * 6.28318;
        float speed = (40.0 + hash(idx * 137u) * 120.0) * perFrame[0].speedMul;
        float spread = perFrame[0].emitterSpread;
        p.position = float2(
            perFrame[0].emitterX + hash(idx * 53u) * spread - spread * 0.5,
            perFrame[0].emitterY + hash(idx * 97u) * spread * 0.3 - spread * 0.15);
        p.velocity = float2(cos(angle), sin(angle)) * speed;
        // 默认模式向上偏
        if (perFrame[0].colorMode == 0) p.velocity.y -= 40.0;
        // 火焰模式强制向上
        if (perFrame[0].colorMode == 1) p.velocity.y -= 60.0;
        // 烟花模式爆射
        if (perFrame[0].colorMode == 4) p.velocity.y -= 80.0;
        p.life = p.maxLife;
        float h = hash(idx * 311u + uint(t * 100.0));
        // 重生时用预设基础色
        if (perFrame[0].colorMode == 1) // 火焰
            p.color = float4(0.8 + h * 0.2, 0.3 + h * 0.5, 0.05, 1.0);
        else if (perFrame[0].colorMode == 2) // 雪花
            p.color = float4(0.7 + h * 0.3, 0.8 + h * 0.2, 0.95 + h * 0.05, 0.8);
        else if (perFrame[0].colorMode == 3) // 极光
            p.color = float4(0.1, 0.6 + h * 0.4, 0.4 + h * 0.6, 0.7);
        else if (perFrame[0].colorMode == 4) // 烟花
            p.color = float4(hsvToRgb(hash(idx * 499u), 0.9, 1.0), 1.0);
        else // 默认
            p.color = float4(0.4 + h * 0.6, 0.2 + abs(h) * 0.5, 0.7 + h * 0.3, 1.0);
    }

    float2 vp = perFrame[0].viewport;
    if (p.position.x < 0 || p.position.x > vp.x) { p.velocity.x *= -0.6; p.position.x = clamp(p.position.x, 0, vp.x); }
    if (p.position.y > vp.y) { p.velocity.y *= -0.5; p.position.y = vp.y; }
    if (p.position.y < 0 && perFrame[0].colorMode != 2) { p.velocity.y *= -0.3; p.position.y = 0; }

    float lifeRatio = clamp(p.life / p.maxLife, 0.0, 1.0);
    float speed2 = length(p.velocity);
    p.color.a = clamp(lifeRatio, 0.05, 1.0);

    // 颜色渐变
    if (perFrame[0].colorMode == 1) { // 火焰：红→橙→黄→透明
        p.color.rgb = float3(1.0, 0.2 + lifeRatio * 0.8, lifeRatio * lifeRatio * 0.3) * (0.5 + speed2 * 0.005);
    } else if (perFrame[0].colorMode == 2) { // 雪花：白蓝微光
        float twinkle = 0.7 + 0.3 * sin(p.position.x * 0.1 + perFrame[0].time * 3.0);
        p.color.rgb = float3(0.7, 0.85, 1.0) * twinkle;
    } else if (perFrame[0].colorMode == 3) { // 极光：绿/青/紫波动
        float wave = sin(p.position.x * 0.01 + perFrame[0].time * 0.5) * 0.5 + 0.5;
        p.color.rgb = lerp(float3(0.1, 0.8, 0.5), float3(0.6, 0.2, 0.9), wave) * (0.6 + speed2 * 0.003);
    } else if (perFrame[0].colorMode == 4) { // 烟花：亮→暗
        p.color.rgb *= (0.3 + lifeRatio * 0.7);
    } else { // 默认
        p.color.r = clamp(speed2 / 200.0, 0.1, 1.0);
        p.color.g = lifeRatio * 0.6;
    }

    particles[idx] = p;
}
";

    // ============================================================
    //  渲染着色器（Slang → MSC，argument buffer + UseResource）
    // ============================================================

    private const string RenderShaderSource = @"
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
    int colorMode;
    float emitterSpread;
    float speedMul;
};

struct VSOut {
    float4 position : SV_Position;
    float4 color    : TEXCOORD0;
    float2 uv       : TEXCOORD1;
};

static const float2 offsets[6] = {
    float2(-1,-1), float2(-1,1), float2(1,-1),
    float2(1,-1),  float2(-1,1), float2(1,1)
};

[shader(""vertex"")]
VSOut main(uint vid : SV_VertexID, uint iid : SV_InstanceID,
           StructuredBuffer<Particle> particles,
           StructuredBuffer<PerFrame> pf)
{
    Particle p = particles[iid];
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

[shader(""fragment"")]
float4 frag_main(VSOut input) : SV_Target0
{
    float dist = length(input.uv);
    if (dist > 1.0) discard;
    float alpha = input.color.a * (1.0 - dist * dist);
    return float4(input.color.rgb * alpha, alpha);
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

        // ② 运行时编译渲染着色器（Slang → DXIL → MSC → metallib）
        Console.WriteLine("▸ Compiling render shaders via SlangCompiler (vertex + fragment)...");
        var t2 = Stopwatch.StartNew();
        var renderVertOpts = new ShaderCompileOptions { Stage = ShaderStage.Vertex, GenerateReflection = true };
        var renderFragOpts = new ShaderCompileOptions { Stage = ShaderStage.Fragment, EntryPoint = "frag_main" };
        var vertResult = compiler.CompileFromSource(
            System.Text.Encoding.UTF8.GetBytes(RenderShaderSource), "ParticleRender.slang", renderVertOpts);
        var fragResult = compiler.CompileFromSource(
            System.Text.Encoding.UTF8.GetBytes(RenderShaderSource), "ParticleRender.slang", renderFragOpts);
        t2.Stop();
        Console.WriteLine($"  Render compile: {t2.ElapsedMilliseconds}ms (vert hit={vertResult.CacheHit}, frag hit={fragResult.CacheHit})");

        if (vertResult.ReflectionJson != null)
        {
            var reflect = Shader.Reflection.MscReflectionParser.Parse(vertResult.ReflectionJson);
            Console.WriteLine($"  Vertex reflection: {reflect.ResourceCount} resources");
            foreach (var e in reflect.TopLevelArgumentBuffer)
                Console.WriteLine($"    [{e.Type}] Slot={e.Slot} Offset={e.EltOffset} Size={e.Size}");
        }

        // ③ 创建 PSO
        using var computeFn = device.NewLibrary(computeResult.MetallibData!).NewFunction("main");
        using var computePso = device.NewComputePipelineState(computeFn);
        Console.WriteLine($"  Compute PSO: maxThreads={computePso.MaxTotalThreadsPerThreadgroup}");

        using var vertFn = device.NewLibrary(vertResult.MetallibData!).NewFunction("main");
        using var fragFn = device.NewLibrary(fragResult.MetallibData!).NewFunction("frag_main");

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
        using var sync = new FrameSync(device);
        layer.SetDisplaySyncEnabled(true);
        layer.SetMaximumDrawableCount(sync.InFlightFrames);

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
        int presetIndex = 0;
        float emitterSpread = 100f;
        float speedMul = 1f;

        // 帧时间平滑（环形缓冲）
        float[] frameTimes = new float[60];
        int ftIdx = 0;

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

            // WaitFrame 在帧首阻塞直到最旧帧 GPU 完成（triple-buffer 帧限速）
            sync.WaitFrame();

            var drawable = layer.NextDrawable();
            if (drawable == null) { Thread.Sleep(1); continue; }

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

                ImGui.SetNextWindowSize(new Vector2(340, 340), ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
                ImGui.Begin("GPU Particles — Phase 9");
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), $"FPS: {fps:F1}  |  {frameMs:F1}ms  |  {activeCount} particles");

                // 平均帧时间
                float avgMs = 0;
                int ftCount = Math.Min(frame, frameTimes.Length);
                for (int i = 0; i < ftCount; i++) avgMs += frameTimes[i];
                avgMs = ftCount > 0 ? avgMs / ftCount : frameMs;
                ImGui.Text($"Avg: {avgMs:F1}ms");

                ImGui.Separator();

                // 预设选择器
                if (ImGui.Combo("Preset", ref presetIndex, PresetNames, PresetNames.Length))
                {
                    var preset = (ParticlePreset)presetIndex;
                    ApplyPreset(preset, ref gravityY, ref windX, ref emitterSpread, ref speedMul);
                }

                ImGui.SliderInt("Particles", ref activeCount, 100, MaxParticles);
                ImGui.SliderFloat("Gravity", ref gravityY, -200f, 200f);
                ImGui.SliderFloat("Wind", ref windX, -100f, 100f);
                ImGui.SliderFloat("Spread", ref emitterSpread, 0f, 500f);
                ImGui.SliderFloat("Speed", ref speedMul, 0.1f, 3f);
                ImGui.Checkbox("Paused", ref paused);

                if (ImGui.Button("Reset Particles"))
                {
                    for (int i = 0; i < MaxParticles; i++)
                    {
                        float a = (float)(rng.NextDouble() * Math.PI * 2);
                        float sp = 50f + (float)rng.NextDouble() * 150f * speedMul;
                        particles[i] = new Particle
                        {
                            Position = new Vector2(W / 2f, H * 0.65f),
                            Velocity = new Vector2(MathF.Cos(a) * sp, MathF.Sin(a) * sp - 60f),
                            Color = new Vector4(0.5f, 0.5f, 0.5f, 1f),
                            Life = 0.01f, MaxLife = 2f + (float)rng.NextDouble() * 4f,
                            Size = 6f + (float)rng.NextDouble() * 12f,
                        };
                    }
                }

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
                        EmitterX = W / 2f + MathF.Sin(time * 0.5f) * emitterSpread * 0.5f,
                        EmitterY = presetIndex == (int)ParticlePreset.Snow ? 0f : H * 0.65f,
                        ActiveCount = activeCount,
                        ColorMode = presetIndex,
                        EmitterSpread = emitterSpread,
                        SpeedMultiplier = speedMul,
                    };
                }

                using var cmdbuf = queue.CommandBuffer();

                // ---- Compute pass: GPU 粒子模拟 (Slang/MSC argument buffer) ----
                if (!paused)
                {
                    int groups = (activeCount + ThreadsPerGroup - 1) / ThreadsPerGroup;
                    using var computeEnc = cmdbuf.ComputeCommandEncoder();
                    computeEnc.SetComputePipelineState(computePso);
                    computeEnc.UseResource(particleBuffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
                    computeEnc.UseResource(perFrameBuffer, MTLResourceUsage.Read);
                    // 使用 SetBytes<T> 重载（struct 方式，避免 stackalloc）
                    var computeArg = new VertArgBuffer
                    {
                        Srv0 = new UavDescriptor { GpuAddress = perFrameBuffer.GpuAddress, Length = perFrameBuffer.Length, Stride = (ulong)perFrameSize },
                        Srv1 = new UavDescriptor { GpuAddress = particleBuffer.GpuAddress, Length = particleBuffer.Length, Stride = (ulong)particleSize },
                    };
                    computeEnc.SetBytes(in computeArg, ArgIndex);
                    computeEnc.DispatchThreadgroups(
                        new WMTSize((ulong)groups, 1, 1),
                        new WMTSize(ThreadsPerGroup, 1, 1));
                    computeEnc.EndEncoding();
                }

                // ---- Render pass: 绘制粒子 (Slang/MSC argument buffer + UseResource) ----
                var scenePassDesc = BuildRenderPassDesc(drawable.Texture.Handle, clear: true);
                var renderEnc = cmdbuf.RenderCommandEncoder(scenePassDesc);
                renderEnc.SetRenderPipelineState(renderPso);
                renderEnc.SetViewport(0, 0, displayW, displayH, 0, 1);

                // 声明资源驻留（MSC argument buffer 依赖 UseResource 让 GPU 能解析内嵌 GPU 地址）
                renderEnc.UseResource(particleBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex);
                renderEnc.UseResource(perFrameBuffer, MTLResourceUsage.Read, MTLRenderStages.Vertex);

                // argument buffer: particles @ slot 0, perFrame @ slot 1
                var renderArgBuf = new VertArgBuffer
                {
                    Srv0 = new UavDescriptor { GpuAddress = particleBuffer.GpuAddress, Length = particleBuffer.Length, Stride = (ulong)particleSize },
                    Srv1 = new UavDescriptor { GpuAddress = perFrameBuffer.GpuAddress, Length = perFrameBuffer.Length, Stride = (ulong)perFrameSize },
                };
                renderEnc.SetVertexBytes(in renderArgBuf, ArgIndex);

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

                sync.SignalFrame(cmdbuf);
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
            frameTimes[ftIdx++ % frameTimes.Length] = dt * 1000f;
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
