using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Mandelbrot compute kernel 数值正确性测试。
/// 验证 src/MetalRenderingEngine.Shaders/Compute/Mandelbrot.slang 的计算结果：
/// 集合内点为黑色，集合外点按迭代上色；Output[0] 参数槽原样回读。
/// </summary>
public class MandelbrotKernelTests
{
    private const ulong ArgumentTableBufferIndex = 2;
    // shader 硬编码 1024×768（见 Mandelbrot.slang），测试须与之对齐
    private const int Width = 1024;
    private const int Height = 768;
    private const int ThreadGroupSize = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct UavDescriptor
    {
        public ulong GpuAddress;
        public ulong Length;
        public ulong Stride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Float4
    {
        public float X, Y, Z, W;
    }

    private static string MetallibPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Mandelbrot.metallib");

    /// <summary>
    /// 对中心点 (-0.5, 0)（Mandelbrot 集合内）计算，该区域像素应为黑色 (0,0,0,1)。
    /// </summary>
    [Fact]
    public void Mandelbrot_InteriorPoint_IsBlack()
    {
        Assert.True(File.Exists(MetallibPath),
            $"metallib 不存在：{MetallibPath}（先跑 ./build/compile_shaders.sh）");

        using var device = MetalDevice.CreateSystemDefault();
        byte[] metallib = File.ReadAllBytes(MetallibPath);
        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("main");
        using var pso = device.NewComputePipelineState(function);

        int totalElements = 1 + Width * Height;  // Output[0] 参数 + 像素
        using var buffer = device.NewBuffer(
            (ulong)(totalElements * 16),  // float4 = 16 bytes
            MTLResourceOptions.StorageModeShared);

        Span<Float4> data = buffer.AsSpan<Float4>();
        // Output[0] = (cx, cy, scale, maxIter)：中心 (-0.5, 0)，scale 使整个集合可见
        data[0] = new Float4 { X = -0.5f, Y = 0.0f, Z = 3.5f / Width, W = 128.0f };

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var enc = cmdbuf.ComputeCommandEncoder())
        {
            enc.SetComputePipelineState(pso);
            enc.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
            enc.SetBytes(new UavDescriptor
            {
                GpuAddress = buffer.GpuAddress,
                Length = buffer.Length,
                Stride = 16,
            }, ArgumentTableBufferIndex);

            int groupsX = (Width + ThreadGroupSize - 1) / ThreadGroupSize;
            int groupsY = (Height + ThreadGroupSize - 1) / ThreadGroupSize;
            enc.DispatchThreadgroups(
                new WMTSize((ulong)groupsX, (ulong)groupsY, 1),
                new WMTSize(ThreadGroupSize, ThreadGroupSize, 1));
            enc.EndEncoding();
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        using (var err = cmdbuf.Error()) Assert.Null(err);
        Assert.Equal(MTLCommandBufferStatus.Completed, cmdbuf.Status);

        // 采样中心区域像素（应在集合内 → 黑色）
        // 中心点对应像素坐标 (Width/2, Height/2)
        int centerIdx = 1 + (Height / 2) * Width + (Width / 2);
        Float4 centerPixel = data[centerIdx];

        // 集合内点：颜色应为 (0,0,0,1)（shader 中 i>=maxIter 分支）
        Assert.Equal(0.0f, centerPixel.X, 0.001f);
        Assert.Equal(0.0f, centerPixel.Y, 0.001f);
        Assert.Equal(0.0f, centerPixel.Z, 0.001f);
        Assert.Equal(1.0f, centerPixel.W, 0.001f);
    }

    /// <summary>远离集合的点（如左上角）应非黑（迭代很快逃逸）。</summary>
    [Fact]
    public void Mandelbrot_ExteriorPoint_IsNonBlack()
    {
        Assert.True(File.Exists(MetallibPath));

        using var device = MetalDevice.CreateSystemDefault();
        byte[] metallib = File.ReadAllBytes(MetallibPath);
        using var library = device.NewLibrary(metallib);
        using var function = library.NewFunction("main");
        using var pso = device.NewComputePipelineState(function);

        int totalElements = 1 + Width * Height;
        using var buffer = device.NewBuffer(
            (ulong)(totalElements * 16), MTLResourceOptions.StorageModeShared);

        Span<Float4> data = buffer.AsSpan<Float4>();
        data[0] = new Float4 { X = -0.5f, Y = 0.0f, Z = 3.5f / Width, W = 128.0f };

        using var queue = device.NewCommandQueue();
        using var cmdbuf = queue.CommandBuffer();
        using (var enc = cmdbuf.ComputeCommandEncoder())
        {
            enc.SetComputePipelineState(pso);
            enc.UseResource(buffer, MTLResourceUsage.Read | MTLResourceUsage.Write);
            enc.SetBytes(new UavDescriptor
            {
                GpuAddress = buffer.GpuAddress,
                Length = buffer.Length,
                Stride = 16,
            }, ArgumentTableBufferIndex);
            enc.DispatchThreadgroups(
                new WMTSize((ulong)((Width + 15) / 16), (ulong)((Height + 15) / 16), 1),
                new WMTSize(16, 16, 1));
            enc.EndEncoding();
        }
        cmdbuf.Commit();
        cmdbuf.WaitUntilCompleted();

        // 远离集合的边界点应非黑。注意 shader 语义：|c|²>4 的远处点 iter=0
        // （break 在 iter++ 前），t=0 → 纯黑；只有 |c|²<=4 但迭代后逃逸的点才有颜色。
        // 故采样多个候选像素，至少一个应非黑（证明 compute 产出了彩色输出）。
        bool anyNonBlack = false;
        for (int sample = 0; sample < 50; sample++)
        {
            // 沿水平中线采样（y=Height/2=384），x 从 0 到 Width
            int x = sample * (Width / 50);
            int y = Height / 2;
            int idx = 1 + y * Width + x;
            if (idx >= data.Length) continue;
            Float4 px = data[idx];
            if (px.X >= 0.001f || px.Y >= 0.001f || px.Z >= 0.001f)
            {
                anyNonBlack = true;
                break;
            }
        }
        Assert.True(anyNonBlack, "应存在非黑像素（集合外边界点的彩色输出）");
    }
}
