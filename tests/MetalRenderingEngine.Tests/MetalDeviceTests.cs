using MetalRenderingEngine.Metal;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// 设备级 SafeHandle 生命周期与基础属性测试。
/// 这些测试要求 M1 / Apple Silicon Mac 真机环境（依赖 libmetal_bridge.dylib）。
/// </summary>
public class MetalDeviceTests
{
    [Fact]
    public void CreateSystemDefault_ReturnsValidDevice()
    {
        using var device = MetalDevice.CreateSystemDefault();

        Assert.False(device.IsInvalid);
        Assert.NotEqual<nuint>(0, device.Handle);
        Assert.False(string.IsNullOrEmpty(device.Name));
    }

    [Fact]
    public void HasUnifiedMemory_OnAppleSilicon_IsTrue()
    {
        using var device = MetalDevice.CreateSystemDefault();
        // 本项目目标平台是 Apple Silicon（AGENTS.md §1.3），UMA 必为 true
        Assert.True(device.HasUnifiedMemory);
    }

    [Fact]
    public void RecommendedMaxWorkingSetSize_IsPositive()
    {
        using var device = MetalDevice.CreateSystemDefault();
        Assert.True(device.RecommendedMaxWorkingSetSize > 0);
    }

    [Fact]
    public void NewBuffer_SharedStorage_HasContents()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var buffer = device.NewBuffer(1024, MTLResourceOptions.StorageModeShared);

        Assert.False(buffer.IsInvalid);
        Assert.Equal<ulong>(1024, buffer.Length);
        Assert.NotEqual(IntPtr.Zero, buffer.Contents);
        Assert.NotEqual<ulong>(0, buffer.GpuAddress); // Apple Silicon 上恒非零
    }

    [Fact]
    public void NewBuffer_ZeroLength_Throws()
    {
        using var device = MetalDevice.CreateSystemDefault();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            device.NewBuffer(0, MTLResourceOptions.StorageModeShared));
    }

    [Fact]
    public void Buffer_AsSpan_RoundTripsValues()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var buffer = device.NewBuffer(64 * sizeof(float), MTLResourceOptions.StorageModeShared);

        Span<float> data = buffer.AsSpan<float>();
        Assert.Equal(64, data.Length);

        for (int i = 0; i < 64; i++) data[i] = i * 0.5f;

        // 重读视图
        Span<float> data2 = buffer.AsSpan<float>();
        for (int i = 0; i < 64; i++) Assert.Equal(i * 0.5f, data2[i]);
    }

    [Fact]
    public void Dispose_TwiceIsSafe()
    {
        var device = MetalDevice.CreateSystemDefault();
        device.Dispose();
        device.Dispose(); // SafeHandle 保证幂等
        Assert.True(device.IsInvalid);
    }
}
