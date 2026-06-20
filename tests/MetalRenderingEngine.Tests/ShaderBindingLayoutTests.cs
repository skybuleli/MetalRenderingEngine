using MetalRenderingEngine.Binding;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Rendering;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 10E: ShaderBindingLayout 测试。
/// 验证半自动绑定布局：强类型属性封装 slot，Apply 一次性应用 vert+frag 绑定。
/// </summary>
public class ShaderBindingLayoutTests
{
    private const int RtW = 8, RtH = 8;

    /// <summary>
    /// 测试用 BindingLayout 子类：复用 TexturedCube 的预编译反射（vert 1 SRV, frag 1 SRV + 1 Sampler）。
    /// </summary>
    private sealed class TestTexturedCubeLayout : ShaderBindingLayout
    {
        public TestTexturedCubeLayout() : base("TexturedCube.vert", "TexturedCube.frag") { }

        public MetalBuffer PerFrame { set => DeclareVertexBuffer(0, value, MscResourceType.Srv); }
        public MetalTexture ColorTex { set => DeclareFragmentTexture(0, value); }
        public MetalSamplerState LinearSampler { set => DeclareFragmentSampler(0, value); }
    }

    /// <summary>
    /// 完整绑定 + Apply：验证命令数 = vert(1 SetVertexBytes + 1 UseResource) + frag(1 SetFragmentBytes + 1 UseResource)。
    /// </summary>
    [Fact]
    public void Apply_FullBinding_RecordsExpectedCommands()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var perFrameBuf = device.NewBuffer(128, MTLResourceOptions.StorageModeShared);
        using var tex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.RGBA8Unorm, 4, 4, MTLTextureUsage.ShaderRead, MTLResourceOptions.StorageModeShared));
        using var sampler = device.NewSamplerState(new WMTSamplerInfo
        {
            MinFilter = (int)MTLSamplerMinMagFilter.Nearest, MagFilter = (int)MTLSamplerMinMagFilter.Nearest,
            MipFilter = (int)MTLSamplerMipFilter.NotMipmapped,
            SAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            TAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            RAddressMode = (int)MTLSamplerAddressMode.ClampToEdge,
            MaxAnisotropy = 1, CompareFunction = -1, LodMinClamp = 0f, LodMaxClamp = float.MaxValue,
        });

        ReflectionLoader.ClearCache();
        var layout = new TestTexturedCubeLayout
        {
            PerFrame = perFrameBuf,
            ColorTex = tex,
            LinearSampler = sampler,
        };

        // 用 MetalCommandRecorder 录制，检查 CommandCount
        using var recorder = new MetalCommandRecorder(device);
        using var rtTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.BGRA8Unorm, RtW, RtH, MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModeShared));
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = rtTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear, StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        layout.Apply(recorder);
        // vert: 1 SetVertexBytes + 1 UseResource(perFrameBuf)
        // frag: 1 SetFragmentBytes + 1 UseResource(tex)  (sampler 不需 UseResource)
        Assert.Equal(4, recorder.CommandCount);
        recorder.EndRenderPass();
        recorder.EndFrame();
    }

    /// <summary>缺绑定（只设 PerFrame，缺 ColorTex/LinearSampler）→ Apply 抛 InvalidOperationException。</summary>
    [Fact]
    public void Apply_MissingFragmentBinding_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var perFrameBuf = device.NewBuffer(128, MTLResourceOptions.StorageModeShared);

        ReflectionLoader.ClearCache();
        var layout = new TestTexturedCubeLayout { PerFrame = perFrameBuf };  // 缺 frag 绑定

        using var recorder = new MetalCommandRecorder(device);
        using var rtTex = device.NewTexture(WMTTextureInfo.Create2D(
            MTLPixelFormat.BGRA8Unorm, RtW, RtH, MTLTextureUsage.RenderTarget, MTLResourceOptions.StorageModeShared));
        var passDesc = new WMTRenderPassDesc();
        passDesc.SetColorAt(0, new WMTRenderPassAttachment
        {
            Texture = rtTex.Handle,
            LoadAction = (int)MTLLoadAction.Clear, StoreAction = (int)MTLStoreAction.Store,
            ClearColor = new WMTClearColor(0, 0, 0, 1),
        });

        recorder.BeginFrame();
        recorder.BeginRenderPass(passDesc);
        // vert Apply 成功（PerFrame 已绑），frag Apply 抛异常（缺 ColorTex/Sampler）
        Assert.Throws<InvalidOperationException>(() => layout.Apply(recorder));
        recorder.EndRenderPass();
        recorder.EndFrame();
    }
}
