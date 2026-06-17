using MetalRenderingEngine.Shader;

namespace MetalRenderingEngine.Demo.Shaders;

/// <summary>
/// Multiply compute shader: Data[i] = Data[i] * 2.0f
/// 验证源生成器从 C# → Slang → metallib 的完整端到端流程。
/// </summary>
[Shader]
[ThreadGroupSize(64, 1, 1)]
#pragma warning disable CS0649
partial struct MultiplyShader : IComputeShader
{
    public ReadWriteBuffer<float> Data;

    public void Execute(ThreadId id)
    {
        var index = (int)id.DispatchThreadID.X;
        Data[index] = Data[index] * 2.0f;
    }
}
