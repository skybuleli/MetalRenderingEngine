using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Platform;

namespace MetalRenderingEngine.Demo;

/// <summary>
/// 简易 4×4 列主序矩阵（兼容 Metal / HLSL 内存布局）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Matrix4x4
{
    public float M11, M12, M13, M14;   // column 0
    public float M21, M22, M23, M24;   // column 1
    public float M31, M32, M33, M34;   // column 2
    public float M41, M42, M43, M44;   // column 3

    public static Matrix4x4 Identity => new() { M11 = 1, M22 = 1, M33 = 1, M44 = 1 };

    public static unsafe Matrix4x4 Multiply(in Matrix4x4 a, in Matrix4x4 b)
    {
        Matrix4x4 la = a, lb = b, r = default;
        float* pa = &la.M11, pb = &lb.M11, pr = &r.M11;
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
                pr[c * 4 + row] =
                    pa[row] * pb[c * 4] + pa[4 + row] * pb[c * 4 + 1] +
                    pa[8 + row] * pb[c * 4 + 2] + pa[12 + row] * pb[c * 4 + 3];
        return r;
    }

    /// <summary>
    /// 右手透视投影（Metal NDC: z ∈ [0, 1]，观察方向 −Z）。
    /// </summary>
    public static Matrix4x4 PerspectiveFovRH(float fovY, float aspect, float nearZ, float farZ)
    {
        float f = 1f / MathF.Tan(fovY * 0.5f);
        var m = new Matrix4x4();
        m.M11 = f / aspect;
        m.M22 = f;
        m.M33 = farZ / (nearZ - farZ);
        m.M34 = -1f;
        m.M43 = nearZ * farZ / (nearZ - farZ);
        return m;
    }

    /// <summary>右手 LookAt 视图矩阵。</summary>
    public static Matrix4x4 LookAtRH(float3 eye, float3 target, float3 up)
    {
        var f = normalize(target - eye);
        var s = normalize(cross(f, up));
        var u = cross(s, f);
        return new Matrix4x4
        {
            M11 = s.x, M12 = u.x, M13 = -f.x,
            M21 = s.y, M22 = u.y, M23 = -f.y,
            M31 = s.z, M32 = u.z, M33 = -f.z,
            M41 = -dot(s, eye), M42 = -dot(u, eye), M43 = dot(f, eye), M44 = 1,
        };
    }

    /// <summary>绕 Y 轴旋转矩阵。</summary>
    public static Matrix4x4 RotationY(float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Matrix4x4 { M11 = c, M13 = -s, M22 = 1, M31 = s, M33 = c, M44 = 1 };
    }

    // ---- float3 内联辅助 ----
    public readonly record struct float3(float x, float y, float z)
    {
        public static float3 operator -(float3 a, float3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
    }
    private static float3 cross(float3 a, float3 b) => new(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
    private static float dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
    private static float3 normalize(float3 v) { float l = MathF.Sqrt(dot(v, v)); return l > 1e-8f ? new(v.x / l, v.y / l, v.z / l) : default; }
}

/// <summary>
/// PerFrameCB — 与 TexturedQuad.vert.slang 中 PerFrameCB 严格对齐（80 字节）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PerFrameCB
{
    public Matrix4x4 mvp;   // 64 bytes
    public float time;       // 4 bytes
    // Metal 256-byte alignment not needed for inline bytes / small buffers
}

/// <summary>
/// Phase 3 验证：旋转贴图四边形 + triple-buffer Fence 同步 + StructuredBuffer 纹理采样。
/// </summary>
internal static class TexturedApp
{
    private const int W = 800, H = 600;
    private const int TexSize = 64;
    private const int BufferCount = 3;
    private const ulong ArgIndex = 2; // MSC top_level_global_ab 在 buffer(2)

    public static int Run()
    {
        // ── 1. 设备 + 窗口 ──────────────────────────────────
        using var device = MetalDevice.CreateSystemDefault();
        Console.WriteLine($"Device: {device.Name}");

        using var sdlWindow = SDL3Window.Create("Metal Textured Quad — Phase 3", W, H);
        var layer = new MetalLayer(sdlWindow.LayerHandle);
        layer.SetDevice(device);
        layer.SetPixelFormat(MTLPixelFormat.BGRA8Unorm);
        layer.SetDrawableSize(W, H);

        // ── 2. 着色器 + 管线 ────────────────────────────────
        using var vertFn = MetalShaderLoader.GetFunction(device, "TexturedQuad.vert", "main");
        using var fragFn = MetalShaderLoader.GetFunction(device, "TexturedQuad.frag", "main");

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
        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);

        // ── 3. 程序化棋盘格纹理 → StructuredBuffer ─────────
        int texBytes = TexSize * TexSize * sizeof(uint);
        using var texBuffer = device.NewBuffer(
            (ulong)texBytes, MTLResourceOptions.StorageModeShared);
        FillCheckerboard(texBuffer.AsSpan<uint>(), TexSize);

        // ── 4. Triple-buffer uniform + Fence ────────────────
        ulong uboSize = (ulong)Marshal.SizeOf<PerFrameCB>();
        var uniformBuffers = new MetalBuffer[BufferCount];
        var fences = new MetalFence[BufferCount];
        for (int i = 0; i < BufferCount; i++)
        {
            uniformBuffers[i] = device.NewBuffer(uboSize, MTLResourceOptions.StorageModeShared);
            fences[i] = device.NewFence();
        }

        // ── 5. 主渲染循环 ───────────────────────────────────
        using var queue = device.NewCommandQueue();
        int frame = 0;
        long startTime = Environment.TickCount64;

        while (true)
        {
            if (sdlWindow.PollShouldClose()) break;
            var drawable = layer.NextDrawable();
            if (drawable == null) { Thread.Sleep(8); continue; }

            using (drawable)
            {
                int slot = frame % BufferCount;

                // MVP 旋转
                float t = (Environment.TickCount64 - startTime) / 1000f;
                float angle = t * MathF.PI * 0.5f;
                var proj = Matrix4x4.PerspectiveFovRH(
                    MathF.PI / 4f, (float)W / H, 0.1f, 100f);
                var view = Matrix4x4.LookAtRH(
                    new(0, 0, 3), new(0, 0, 0), new(0, 1, 0));
                var model = Matrix4x4.RotationY(angle);
                var mvp = Matrix4x4.Multiply(
                    Matrix4x4.Multiply(proj, view), model);

                var cb = new PerFrameCB { mvp = mvp, time = t };
                uniformBuffers[slot].AsSpan<PerFrameCB>()[0] = cb;

                using var cmdbuf = queue.CommandBuffer();
                var passDesc = BuildRenderPassDesc(drawable.Texture.Handle);

                using (var enc = cmdbuf.RenderCommandEncoder(passDesc))
                {
                    enc.SetRenderPipelineState(pso);
                    enc.SetViewport(0, 0, W, H, 0, 1);

                    // Fence: 确保 GPU 不再读此 slot 后再编码
                    enc.WaitForFence(fences[slot], MTLRenderStages.Vertex);

                    // Vertex stage: MVP descriptor at buffer(2)
                    var mvpDesc = new UavDescriptor
                    {
                        GpuAddress = uniformBuffers[slot].GpuAddress,
                        Length = uniformBuffers[slot].Length,
                        Stride = uboSize,
                    };
                    enc.SetVertexBytes<UavDescriptor>(mvpDesc, ArgIndex);

                    // Fragment stage: 纹理 descriptor at buffer(2) + useResource
                    var texDesc = new UavDescriptor
                    {
                        GpuAddress = texBuffer.GpuAddress,
                        Length = texBuffer.Length,
                        Stride = sizeof(uint),
                    };
                    enc.SetFragmentBytes<UavDescriptor>(texDesc, ArgIndex);
                    enc.UseResource(texBuffer, MTLResourceUsage.Read,
                        MTLRenderStages.Fragment);

                    enc.DrawTriangles(0, 6);

                    // Fence: 标记 GPU 完成 vertex stage 后可安全覆写
                    enc.UpdateFence(fences[slot], MTLRenderStages.Vertex);
                    enc.EndEncoding();
                }

                cmdbuf.PresentDrawable(drawable);
                cmdbuf.Commit();
            }

            frame++;
            if (frame % 300 == 0)
                Console.WriteLine($"  frame {frame}");
            Thread.Sleep(16);
        }

        Console.WriteLine($"✅ Rendered {frame} frames. Textured quad visible.");
        return 0;
    }

    // ── 辅助方法 ────────────────────────────────────────────

    /// <summary>生成 64×64 棋盘格（红/白），RGBA8 打包到 uint。</summary>
    private static void FillCheckerboard(Span<uint> pixels, int size)
    {
        const uint Red = 0xFF0000FF;     // RGBA: R=255, A=255
        const uint White = 0xFFFFFFFF;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = ((x / 8 + y / 8) & 1) == 0 ? Red : White;
    }

    private static unsafe WMTRenderPassDesc BuildRenderPassDesc(nuint textureHandle)
    {
        var passDesc = new WMTRenderPassDesc();
        var att = new WMTRenderPassAttachment
        {
            Texture = textureHandle,
            LoadAction = (int)MTLLoadAction.Clear,
            StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0.15f, 0.18f, 0.22f, 1f),
        };
        passDesc.SetColorAt(0, att);
        return passDesc;
    }
}
