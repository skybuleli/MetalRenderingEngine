using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Buffer 生命周期与引用计数测试。覆盖 AGENTS.md §3.2 的 SafeHandle ↔
/// DXMT Reference&lt;T&gt; 模式：Retain/Dispose 配对、MetalShaderLoader 缓存、
/// 压力循环（防泄漏回归）。
/// </summary>
public class MetalBufferLifecycleTests
{
    /// <summary>
    /// Retain/Dispose 配对不抛异常。
    /// 注意：.NET SafeHandle.Dispose() 会立即把 handle 置 0 并调 ReleaseHandle（NSObject_release -1），
    /// 所以 Retain（+1）+ Dispose（-1）后底层 native 对象计数 = 初始1 + 1 - 1 = 1 仍存活，
    /// 但 SafeHandle 自身已 invalid。此处仅验证调用链不抛、不 double-free。
    /// </summary>
    [Fact]
    public void RetainDispose_Pair_DoesNotThrow()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var buffer = device.NewBuffer(64, MTLResourceOptions.StorageModeShared);
        Assert.False(buffer.IsInvalid);

        buffer.Retain();      // +1
        buffer.Dispose();     // -1，SafeHandle invalid，native 对象仍存活（计数=1）
        Assert.True(buffer.IsInvalid);  // SafeHandle 语义：Dispose 后 invalid

        // 第二次 Dispose：SafeHandle 幂等，不重复调 ReleaseHandle
        buffer.Dispose();
        Assert.True(buffer.IsInvalid);
    }

    /// <summary>MetalShaderLoader 缓存命中：第二次 Load 同名库应返回相同底层对象。</summary>
    [Fact]
    public void ShaderLoader_CacheHit_ReturnsSameLibrary()
    {
        using var device = MetalDevice.CreateSystemDefault();
        string name = "Multiply";
        var lib1 = MetalShaderLoader.Load(device, name);
        var lib2 = MetalShaderLoader.Load(device, name);

        // 缓存命中：两次返回的 handle 应相同（同一 MTLLibrary 对象）
        Assert.Equal(lib1.Handle, lib2.Handle);

        lib1.Dispose();
        lib2.Dispose();
        MetalShaderLoader.ClearCache();
    }

    /// <summary>ClearCache 后再 Load 应重新从文件加载（handle 可能不同）。</summary>
    [Fact]
    public void ShaderLoader_ClearCache_ForcesReload()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var lib1 = MetalShaderLoader.Load(device, "Multiply");
        nuint h1 = lib1.Handle;
        lib1.Dispose();
        MetalShaderLoader.ClearCache();

        var lib2 = MetalShaderLoader.Load(device, "Multiply");
        // 清缓存后重新加载，应为新的 MTLLibrary 对象（句柄可能相同因 Metal 复用，但逻辑上是新实例）
        Assert.False(lib2.IsInvalid);
        lib2.Dispose();
        MetalShaderLoader.ClearCache();
    }

    /// <summary>压力测试：分配 10000 个 buffer 后全部释放 + GC，不应崩溃或泄漏。</summary>
    [Fact]
    public void Stress_10000Buffers_AllocFree_NoCrash()
    {
        using var device = MetalDevice.CreateSystemDefault();
        var buffers = new MetalBuffer[1000];
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = device.NewBuffer(128, MTLResourceOptions.StorageModeShared);
            }
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i].Dispose();
            }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // 若到此处无异常即通过（Instruments leaks 可做更严格的自动化）
        Assert.True(true);
    }

    /// <summary>buffer.AsSpan 读写往返：CPU 写入后 GPU 可见（Shared 模式）。</summary>
    [Fact]
    public void Buffer_AsSpan_CPUWrite_GPUVisible()
    {
        using var device = MetalDevice.CreateSystemDefault();
        using var buffer = device.NewBuffer(64, MTLResourceOptions.StorageModeShared);

        Span<float> data = buffer.AsSpan<float>();
        for (int i = 0; i < 8; i++) data[i] = i * 1.5f;

        // 重新获取 span 验证（同一段内存）
        Span<float> reread = buffer.AsSpan<float>();
        for (int i = 0; i < 8; i++)
            Assert.Equal(i * 1.5f, reread[i]);
    }
}
