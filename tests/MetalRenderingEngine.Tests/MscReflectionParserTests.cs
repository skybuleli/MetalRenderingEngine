using MetalRenderingEngine.Shader;
using MetalRenderingEngine.Shader.Reflection;

namespace MetalRenderingEngine.Tests;

/// <summary>
/// MSC 反射 JSON 解析器测试。
/// 使用 compile_shaders.sh 生成的实际 reflect.json 文件验证解析。
/// </summary>
public class MscReflectionParserTests
{
    private static string ReflectJsonPath(string name)
        => Path.Combine(AppContext.BaseDirectory, "shaders", name);

    /// <summary>
    /// 解析 Multiply.reflect.json（compute shader，1 个 UAV）。
    /// </summary>
    [Fact]
    public void Parse_MultiplyReflect_ComputeShader()
    {
        var path = ReflectJsonPath("Multiply.reflect.json");
        Assert.True(File.Exists(path), $"反射文件不存在：{path}（先跑 ./build/compile_shaders.sh）");

        var json = File.ReadAllText(path);
        var reflection = MscReflectionParser.Parse(json);

        Assert.Equal("main", reflection.EntryPoint);
        Assert.Equal("Compute", reflection.ShaderType);
        Assert.Equal(ShaderStage.Compute, MscReflectionParser.ToShaderStage(reflection.ShaderType));

        // Multiply 有 1 个 UAV 资源
        Assert.Equal(1, reflection.ResourceCount);
        Assert.Single(reflection.TopLevelArgumentBuffer);

        var uav = reflection.TopLevelArgumentBuffer[0];
        Assert.Equal("UAV", uav.Type);
        Assert.Equal(MscResourceType.Uav, uav.ResourceType);
        Assert.Equal(0, uav.EltOffset);
        Assert.Equal(24, uav.Size);
        Assert.Equal(0, uav.Slot);

        Assert.Single(reflection.UnorderedAccessViewIndices);
        Assert.Equal(0, reflection.UnorderedAccessViewIndices[0]);
    }

    /// <summary>
    /// 解析 Triangle.frag.reflect.json（fragment shader，0 资源）。
    /// </summary>
    [Fact]
    public void Parse_TriangleFragReflect_NoResources()
    {
        var path = ReflectJsonPath("Triangle.frag.reflect.json");
        Assert.True(File.Exists(path), $"反射文件不存在：{path}");

        var reflection = MscReflectionParser.Parse(File.ReadAllBytes(path));

        Assert.Equal("Fragment", reflection.ShaderType);
        Assert.Equal(0, reflection.ResourceCount);
        Assert.Empty(reflection.TopLevelArgumentBuffer);
        Assert.Empty(reflection.UsedResources);
    }

    /// <summary>
    /// 解析 ImGui.frag.reflect.json（fragment shader，SRV + CBV）。
    /// </summary>
    [Fact]
    public void Parse_ImGuiFragReflect_SrvAndCbv()
    {
        var path = ReflectJsonPath("ImGui.frag.reflect.json");
        Assert.True(File.Exists(path), $"反射文件不存在：{path}");

        var reflection = MscReflectionParser.Parse(File.ReadAllText(path));

        Assert.Equal("Fragment", reflection.ShaderType);
        Assert.Equal(2, reflection.ResourceCount);
        Assert.Equal(2, reflection.TopLevelArgumentBuffer.Count);

        // 第一个是 SRV（纹理），第二个是 CBV
        Assert.Equal(MscResourceType.Srv, reflection.TopLevelArgumentBuffer[0].ResourceType);
        Assert.Equal(0, reflection.TopLevelArgumentBuffer[0].EltOffset);
        Assert.Equal(24, reflection.TopLevelArgumentBuffer[0].Size);

        Assert.Equal(MscResourceType.Cbv, reflection.TopLevelArgumentBuffer[1].ResourceType);
        Assert.Equal(24, reflection.TopLevelArgumentBuffer[1].EltOffset);
    }

    /// <summary>
    /// 按类型过滤 argument buffer 条目。
    /// </summary>
    [Fact]
    public void GetEntriesByType_ImGuiFrag_FiltersCorrectly()
    {
        var path = ReflectJsonPath("ImGui.frag.reflect.json");
        Assert.True(File.Exists(path));

        var reflection = MscReflectionParser.Parse(File.ReadAllText(path));

        var srvEntries = MscReflectionParser.GetEntriesByType(reflection, MscResourceType.Srv).ToList();
        var cbvEntries = MscReflectionParser.GetEntriesByType(reflection, MscResourceType.Cbv).ToList();
        var uavEntries = MscReflectionParser.GetEntriesByType(reflection, MscResourceType.Uav).ToList();

        Assert.Single(srvEntries);
        Assert.Single(cbvEntries);
        Assert.Empty(uavEntries);
    }

    /// <summary>
    /// 解析字节数组输入。
    /// </summary>
    [Fact]
    public void Parse_ByteArray_Works()
    {
        var json = """{"EntryPoint":"test","ShaderType":"Vertex","ShaderID":"123","ResourceCount":0,"ResourceUsages":0,"TopLevelArgumentBuffer":[],"ConstantBufferIndices":[],"ShaderResourceViewIndices":[],"UnorderedAccessViewIndices":[],"SamplerIndices":[],"UsedResources":[],"FunctionConstants":[],"NeedsFunctionConstants":false,"Features":0}""";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        var reflection = MscReflectionParser.Parse(bytes);

        Assert.Equal("test", reflection.EntryPoint);
        Assert.Equal("Vertex", reflection.ShaderType);
    }

    /// <summary>
    /// ToShaderStage 覆盖所有阶段。
    /// </summary>
    [Theory]
    [InlineData("Compute", ShaderStage.Compute)]
    [InlineData("Vertex", ShaderStage.Vertex)]
    [InlineData("Fragment", ShaderStage.Fragment)]
    public void ToShaderStage_AllStages(string input, ShaderStage expected)
    {
        Assert.Equal(expected, MscReflectionParser.ToShaderStage(input));
    }

    /// <summary>
    /// 无效的 ShaderType 抛出异常。
    /// </summary>
    [Fact]
    public void ToShaderStage_Invalid_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => MscReflectionParser.ToShaderStage("Geometry"));
    }
}
