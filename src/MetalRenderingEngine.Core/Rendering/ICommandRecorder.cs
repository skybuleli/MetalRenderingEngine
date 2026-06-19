using MetalRenderingEngine.Metal;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Rendering;

/// <summary>
/// Phase 8: 统一命令录制接口（类型化版本）。
/// 所有渲染/计算命令通过此接口录入，具体实现决定是批量回放（Metal）还是捕获/日志。
/// </summary>
/// <remarks>
/// 设计原则：
/// <list type="bullet">
/// <item>类型化参数（MetalBuffer/MetalTexture/枚举），非裸 nint</item>
/// <item>只覆盖已实现能力，不超前声明未实现 API</item>
/// <item>MetalCommandRecorder 实现走 MetalCommandList 批量回放，保住 Phase 6 的 99.5% P/Invoke 优化</item>
/// </list>
/// </remarks>
public interface ICommandRecorder : IDisposable
{
    // ══════════ 帧控制 ══════════

    /// <summary>开始一帧。创建 command buffer，准备录制。</summary>
    void BeginFrame();

    /// <summary>结束一帧。回放批量命令、提交 command buffer。</summary>
    void EndFrame();

    // ══════════ Render Pass ══════════

    /// <summary>开始 render pass，创建 render encoder。</summary>
    void BeginRenderPass(in WMTRenderPassDesc passDesc);

    /// <summary>结束 render pass。回放批量命令、EndEncoding。</summary>
    void EndRenderPass();

    // ══════════ 管线状态 ══════════

    /// <summary>设置渲染管线状态。</summary>
    void SetPipelineState(MetalRenderPipelineState pso);

    // ══════════ 视口/裁剪 ══════════

    /// <summary>设置视口。</summary>
    void SetViewport(float x, float y, float width, float height, float znear = 0f, float zfar = 1f);

    /// <summary>设置裁剪矩形。</summary>
    void SetScissor(int x, int y, int width, int height);

    // ══════════ 光栅化状态（Phase 7D） ══════════

    /// <summary>设置背面剔除模式。</summary>
    void SetCullMode(MTLCullMode mode);

    /// <summary>设置正面朝向绕序。</summary>
    void SetFrontFacing(MTLWinding winding);

    /// <summary>设置深度偏移。</summary>
    void SetDepthBias(float bias, float slopeScale, float clamp);

    /// <summary>设置深度裁剪模式。</summary>
    void SetDepthClipMode(MTLDepthClipMode mode);

    /// <summary>设置三角形填充模式。</summary>
    void SetTriangleFillMode(MTLTriangleFillMode mode);

    // ══════════ 深度/模板状态（Phase 7E） ══════════

    /// <summary>绑定深度/模板状态对象。</summary>
    void SetDepthStencilState(MetalDepthStencilState state);

    /// <summary>设置 stencil 参考值（前后分离）。</summary>
    void SetStencilReference(uint front, uint back);

    // ══════════ 资源绑定 ══════════

    /// <summary>设置 vertex bytes（内联数据，泛型版本）。</summary>
    void SetVertexBytes<T>(in T value, ulong index) where T : unmanaged;

    /// <summary>设置 vertex buffer。</summary>
    void SetVertexBuffer(MetalBuffer buffer, ulong offset, ulong index);

    /// <summary>设置 fragment bytes（内联数据，泛型版本）。</summary>
    void SetFragmentBytes<T>(in T value, ulong index) where T : unmanaged;

    /// <summary>设置 fragment buffer。</summary>
    void SetFragmentBuffer(MetalBuffer buffer, ulong offset, ulong index);

    /// <summary>设置 fragment texture。</summary>
    void SetFragmentTexture(MetalTexture texture, ulong index);

    /// <summary>声明资源驻留（GPU 在指定 stage 访问前确保资源可用）。</summary>
    void UseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages);

    // ══════════ 绘制（Phase 7G/7H） ══════════

    /// <summary>绘制图元（支持 instanced）。</summary>
    void Draw(int primitiveType, ulong vertexStart, ulong vertexCount, ulong instanceCount = 1);

    /// <summary>Indexed 绘制（支持 instanced）。</summary>
    void DrawIndexed(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount = 1);

    /// <summary>Indirect 绘制。</summary>
    void DrawIndirect(MetalBuffer indirectBuffer, ulong offset = 0);

    /// <summary>Indexed indirect 绘制。</summary>
    void DrawIndexedIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0);

    // ══════════ 同步 ══════════

    /// <summary>等待 Fence（GPU 在指定 stage 前等待）。</summary>
    void WaitForFence(MetalFence fence, MTLRenderStages beforeStages);

    /// <summary>更新 Fence（GPU 完成指定 stage 后标记）。</summary>
    void UpdateFence(MetalFence fence, MTLRenderStages afterStages);

    // ══════════ 可观测性 ══════════

    /// <summary>已录制的命令数（不含 EndFrame 提交的命令）。</summary>
    int CommandCount { get; }

    /// <summary>获取命令日志（人类可读）。无日志能力时返回空字符串。</summary>
    string GetCommandLog();
}
