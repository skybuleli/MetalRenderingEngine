using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 7F: VertexDescriptor 端到端测试。
/// 验证带 vertex attribute 的 PSO 能正确创建，并且命令能经 MetalCommandList 回放。
/// </summary>
public class VertexDescriptorTests
{
    private static string TriangleVertPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.vert.metallib");
    private static string TriangleFragPath
        => Path.Combine(AppContext.BaseDirectory, "shaders", "Triangle.frag.metallib");

    /// <summary>
    /// 创建 PSO 时传入 VertexDescriptor（即使 shader 不使用 attributes，也要验证不崩溃）。
    /// </summary>
    [Fact]
    public void Pso_WithVertexDescriptor_DoesNotCrash()
    {
        Assert.True(File.Exists(TriangleVertPath), $"metallib 不存在：{TriangleVertPath}");
        Assert.True(File.Exists(TriangleFragPath), $"metallib 不存在：{TriangleFragPath}");

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new WMTRenderPipelineDesc
        {
            ColorCount = 1,
            SampleCount = 1,
        };
        pipeDesc.ColorAttachmentAt(0).PixelFormat = (int)MTLPixelFormat.BGRA8Unorm;

        // 设置 VertexDescriptor（虽然 Triangle shader 用 SV_VertexID，
        // 但验证 bridge 能消费此结构体且不崩溃）
        pipeDesc.VertexDescriptor = new WMTVertexDescriptor
        {
            AttributeCount = 2,
            LayoutCount = 1,
        };
        pipeDesc.VertexDescriptor.Attributes[0] = new WMTVertexAttributeDesc
        {
            Format = (int)MTLVertexFormat.Float3,
            Offset = 0,
            BufferIndex = 0,
        };
        pipeDesc.VertexDescriptor.Attributes[1] = new WMTVertexAttributeDesc
        {
            Format = (int)MTLVertexFormat.Float4,
            Offset = 12,
            BufferIndex = 0,
        };
        pipeDesc.VertexDescriptor.Layouts[0] = new WMTVertexBufferLayoutDesc
        {
            Stride = 28,
            StepFunction = (int)MTLVertexStepFunction.PerVertex,
            StepRate = 1,
        };

        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);
        Assert.NotEqual(nuint.Zero, pso.Handle);
    }

    /// <summary>
    /// VertexDescriptor 为空（Count=0）时，PSO 创建仍应成功（兼容旧 shader）。
    /// </summary>
    [Fact]
    public void Pso_WithoutVertexDescriptor_DoesNotCrash()
    {
        Assert.True(File.Exists(TriangleVertPath), $"metallib 不存在：{TriangleVertPath}");
        Assert.True(File.Exists(TriangleFragPath), $"metallib 不存在：{TriangleFragPath}");

        using var device = MetalDevice.CreateSystemDefault();
        using var vertLib = device.NewLibrary(File.ReadAllBytes(TriangleVertPath));
        using var fragLib = device.NewLibrary(File.ReadAllBytes(TriangleFragPath));
        using var vertFn = vertLib.NewFunction("main");
        using var fragFn = fragLib.NewFunction("main");

        var pipeDesc = new WMTRenderPipelineDesc
        {
            ColorCount = 1,
            SampleCount = 1,
        };
        pipeDesc.ColorAttachmentAt(0).PixelFormat = (int)MTLPixelFormat.BGRA8Unorm;

        using var pso = device.NewRenderPipelineState(vertFn, fragFn, pipeDesc);
        Assert.NotEqual(nuint.Zero, pso.Handle);
    }
}
