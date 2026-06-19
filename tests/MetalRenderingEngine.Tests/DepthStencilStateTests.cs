using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 7A: MTLDepthStencilState 生命周期测试。
/// </summary>
public class DepthStencilStateTests
{
    [Fact]
    public void Create_DefaultDepthWriteDisabled_HandleIsNonZero()
    {
        using var device = MetalDevice.CreateSystemDefault();

        var desc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 0,
        };

        using var state = device.NewDepthStencilState(desc);
        Assert.NotNull(state);
        Assert.NotEqual(nuint.Zero, state.Handle);
    }

    [Fact]
    public void Create_DepthWriteEnabled_HandleIsNonZero()
    {
        using var device = MetalDevice.CreateSystemDefault();

        var desc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
        };

        using var state = device.NewDepthStencilState(desc);
        Assert.NotNull(state);
        Assert.NotEqual(nuint.Zero, state.Handle);
    }

    [Fact]
    public void Create_WithStencil_HandleIsNonZero()
    {
        using var device = MetalDevice.CreateSystemDefault();

        var desc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Less,
            DepthWriteEnabled = 1,
            FrontFaceStencil = new WMTStencilDescriptor
            {
                StencilCompareFunction = (int)MTLCompareFunction.Equal,
                StencilFailureOperation = (int)MTLStencilOperation.Keep,
                DepthFailureOperation = (int)MTLStencilOperation.Keep,
                DepthStencilPassOperation = (int)MTLStencilOperation.Replace,
                ReadMask = 0xFF,
                WriteMask = 0xFF,
            },
            BackFaceStencil = new WMTStencilDescriptor
            {
                StencilCompareFunction = (int)MTLCompareFunction.Always,
                StencilFailureOperation = (int)MTLStencilOperation.Keep,
                DepthFailureOperation = (int)MTLStencilOperation.Keep,
                DepthStencilPassOperation = (int)MTLStencilOperation.Keep,
                ReadMask = 0x00,
                WriteMask = 0x00,
            },
        };

        using var state = device.NewDepthStencilState(desc);
        Assert.NotNull(state);
        Assert.NotEqual(nuint.Zero, state.Handle);
    }

    /// <summary>
    /// 确定性释放：在 using 作用域结束后 SafeHandle 应能安全释放，不抛异常。
    /// </summary>
    [Fact]
    public void Dispose_DoesNotThrow()
    {
        using var device = MetalDevice.CreateSystemDefault();

        var desc = new WMTDepthStencilDesc
        {
            DepthCompareFunction = (int)MTLCompareFunction.Never,
            DepthWriteEnabled = 0,
        };

        var state = device.NewDepthStencilState(desc);
        state.Dispose();
        Assert.True(state.IsClosed);
    }
}
