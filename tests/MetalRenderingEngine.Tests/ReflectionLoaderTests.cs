using MetalRenderingEngine.Binding;
using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// Phase 10D: ReflectionLoader 测试。
/// 验证从预编译的 reflect.json 加载 MscReflection，与 MetalShaderLoader 路径约定一致。
/// </summary>
public class ReflectionLoaderTests
{
    /// <summary>加载预编译的 Multiply.compute 反射（1 个 UAV）。</summary>
    [Fact]
    public void Load_PrecompiledMultiply_ReturnsValidReflection()
    {
        // 清缓存确保独立测试
        ReflectionLoader.ClearCache();

        var reflection = ReflectionLoader.Load("Multiply");

        Assert.Equal("Compute", reflection.ShaderType);
        Assert.Equal("main", reflection.EntryPoint);
        Assert.Equal(1, reflection.ResourceCount);
        Assert.Single(reflection.TopLevelArgumentBuffer);
        var entry = reflection.TopLevelArgumentBuffer[0];
        Assert.Equal(MscResourceType.Uav, entry.ResourceType);
        Assert.Equal(0, entry.EltOffset);
        Assert.Equal(24, entry.Size);
    }

    /// <summary>缓存验证：重复 Load 返回同一实例。</summary>
    [Fact]
    public void Load_Twice_ReturnsCachedInstance()
    {
        ReflectionLoader.ClearCache();

        var first = ReflectionLoader.Load("Multiply");
        var second = ReflectionLoader.Load("Multiply");

        Assert.Same(first, second);
    }

    /// <summary>TryLoad 不存在的反射文件返回 null。</summary>
    [Fact]
    public void TryLoad_NonExistent_ReturnsNull()
    {
        ReflectionLoader.ClearCache();

        var result = ReflectionLoader.TryLoad("NonExistentShaderXYZ");
        Assert.Null(result);
    }

    /// <summary>ClearCache 后再 Load 返回新实例。</summary>
    [Fact]
    public void ClearCache_ForcesReload()
    {
        ReflectionLoader.ClearCache();
        var first = ReflectionLoader.Load("Multiply");
        ReflectionLoader.ClearCache();
        var second = ReflectionLoader.Load("Multiply");

        Assert.NotSame(first, second);
    }

    /// <summary>
    /// 端到端：预编译 metallib + ReflectionLoader 加载反射 + ArgumentBufferEncoder 编码。
    /// 验证"预编译路径"（不经 SlangCompiler 运行时编译）能拿到反射并驱动绑定层。
    /// 用 Multiply（compute，1 UAV）验证：反射 → encoder 编码 → 字节布局正确。
    /// </summary>
    [Fact]
    public void PrecompiledPath_ReflectionLoaderDrivesEncoder()
    {
        ReflectionLoader.ClearCache();

        // 1. 从预编译 reflect.json 加载反射（不经运行时编译）
        var reflection = ReflectionLoader.Load("Multiply");
        Assert.Single(reflection.TopLevelArgumentBuffer);

        // 2. 创建一个 buffer 资源，用反射驱动 encoder 编码
        using var device = MetalDevice.CreateSystemDefault();
        using var buffer = device.NewBuffer(1024, MTLResourceOptions.StorageModeShared);

        // 反射：1 个 UAV @ EltOffset 0, Size 24
        byte[] encoded = ArgumentBufferEncoder.Encode(reflection,
            ResourceBinding.ForBuffer(buffer, MscResourceType.Uav));

        // 3. 断言编码字节符合 IRDescriptorTableEntry 布局
        Assert.Equal(24, encoded.Length);
        // +0 gpuVA = buffer.GpuAddress
        Assert.Equal(buffer.GpuAddress, BitConverter.ToUInt64(encoded, 0));
        // +8 textureViewID = 0（buffer 路径）
        Assert.Equal(0UL, BitConverter.ToUInt64(encoded, 8));
        // +16 metadata = 0（简单 buffer）
        Assert.Equal(0UL, BitConverter.ToUInt64(encoded, 16));

        // 4. 端到端：用预编译 metallib（MetalShaderLoader）+ 反射（ReflectionLoader）组合
        //    验证预编译路径的两个 loader 协作：MetalShaderLoader 加载 metallib，
        //    ReflectionLoader 加载反射，二者用同一个 name。
        using var lib = MetalShaderLoader.GetFunction(device, "Multiply", "main");
        Assert.NotNull(lib);
        // 若执行到这里无异常，预编译路径的 metallib + 反射协作验证通过
    }

    /// <summary>name 含 .reflect.json 后缀也能正确加载。</summary>
    [Fact]
    public void Load_WithSuffix_Works()
    {
        ReflectionLoader.ClearCache();

        var withSuffix = ReflectionLoader.Load("Multiply.reflect.json");
        var withoutSuffix = ReflectionLoader.Load("Multiply");

        // 两者解析到同一文件，缓存键不同但内容相同
        Assert.Equal(withoutSuffix.ResourceCount, withSuffix.ResourceCount);
        Assert.Equal(withoutSuffix.ShaderType, withSuffix.ShaderType);
    }

    /// <summary>加载编译后生成的 bindings.json，验证它与 reflect.json 的关键布局一致。</summary>
    [Fact]
    public void LoadBindings_PrecompiledMultiply_ReturnsStableMetadata()
    {
        ReflectionLoader.ClearCache();

        var reflection = ReflectionLoader.Load("Multiply");
        var bindings = ReflectionLoader.LoadBindings("Multiply");

        Assert.Equal(1, bindings.Version);
        Assert.Equal("Multiply", bindings.Shader);
        Assert.Equal(reflection.ShaderType, bindings.Stage);
        Assert.Equal(ResourceTable.ArgumentBufferBindPoint, bindings.ArgumentBufferBindPoint);
        Assert.Single(bindings.Resources);

        var reflected = reflection.TopLevelArgumentBuffer[0];
        var resource = bindings.Resources[0];
        Assert.Equal(reflected.ResourceType, resource.ResourceType);
        Assert.Equal(reflected.Slot, resource.Slot);
        Assert.Equal(reflected.Space, resource.Space);
        Assert.Equal(reflected.EltOffset, resource.Offset);
        Assert.Equal(reflected.Size, resource.Size);
    }
}
