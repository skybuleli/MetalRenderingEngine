using System.Runtime.InteropServices;

namespace MetalRenderingEngine.Metal.Interop;

/// <summary>
/// Phase 6: 批量命令编码器的命令结构体镜像（对应 native/bridge.h 的 wmtcmd_*）。
/// 所有结构体首部为 <see cref="WMTCommandBase"/>（type + next 指针），
/// bridge.m 按遍历链表、switch(type) 分发到 ObjC 调用。
/// 设计参照 DXMT winemetal.h:876-1110。
/// </summary>
#pragma warning disable CS8500 // 结构体含指针字段，需 unsafe 上下文（项目已启用 AllowUnsafeBlocks）

/// <summary>命令链表头。16 字节：type(2) + reserved(6) + next(8)。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTCommandBase
{
    public ushort Type;
    private ushort _r0, _r1, _r2;  // reserved[3]，对齐到 8 字节
    public WMTCommandBase* Next;   // 链表 next，null = 链尾
}

// ── Compute 命令类型 ──────────────────────────────────────────
public enum WMTComputeCmdType : ushort
{
    EndEncoding = 0,
    SetPipelineState,
    UseResource,
    SetBytes,
    Dispatch,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTComputeSetPso
{
    public WMTCommandBase Base;
    public nuint Pso;
    public WMTSize ThreadgroupSize;  // 随 PSO 缓存，供后续 Dispatch 消费
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTComputeUseResource
{
    public WMTCommandBase Base;
    public nuint Resource;
    public uint Usage;  // MTLResourceUsage 位或
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTComputeSetBytes
{
    public WMTCommandBase Base;
    public void* Bytes;   // 外挂 payload（调用方保持存活到回放返回）
    public ulong Length;
    public ulong Index;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTComputeDispatch
{
    public WMTCommandBase Base;
    public WMTSize ThreadgroupsPerGrid;
    public WMTSize ThreadsPerThreadgroup;
}

// ── Render 命令类型 ───────────────────────────────────────────
public enum WMTRenderCmdType : ushort
{
    EndEncoding = 0,
    SetPipelineState,
    SetViewport,
    SetVertexBytes,
    SetFragmentBytes,
    UseResource,
    DrawPrimitives,
    // Phase 7D: 光栅化状态
    SetCullMode,
    SetFrontFacing,
    SetDepthBias,
    SetDepthClipMode,
    SetTriangleFillMode,
    // Phase 7E: 深度/模板状态
    SetDepthStencilState,
    SetStencilReference,
    // Phase 7G/7H: 绘制变体
    DrawIndexedPrimitives,
    DrawIndirectPrimitives,
    DrawIndexedIndirectPrimitives,
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetPso
{
    public WMTCommandBase Base;
    public nuint Pso;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetViewport
{
    public WMTCommandBase Base;
    public float X, Y, W, H, Znear, Zfar;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetBytes
{
    public WMTCommandBase Base;
    public void* Bytes;
    public ulong Length;
    public ulong Index;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderUseResource
{
    public WMTCommandBase Base;
    public nuint Resource;
    public uint Usage;
    public uint Stages;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderDraw
{
    public WMTCommandBase Base;
    public int PrimitiveType;  // 0 = Triangle
    public ulong VertexStart;
    public ulong VertexCount;
    public ulong InstanceCount;  // Phase 7G: >1 = instanced
}

/// <summary>Phase 7G: 带实例化的 Indexed draw 命令。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderDrawIndexed
{
    public WMTCommandBase Base;
    public int PrimitiveType;
    public ulong IndexCount;
    public int IndexType;        // 0=uint16, 1=uint32
    public nuint IndexBuffer;
    public ulong IndexBufferOffset;
    public ulong InstanceCount;
}

/// <summary>Phase 7H: Indirect draw 命令。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderDrawIndirect
{
    public WMTCommandBase Base;
    public int PrimitiveType;
    public nuint IndirectBuffer;
    public ulong IndirectBufferOffset;
}

/// <summary>Phase 7H: Indexed indirect draw 命令。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderDrawIndexedIndirect
{
    public WMTCommandBase Base;
    public int PrimitiveType;
    public int IndexType;        // 0=uint16, 1=uint32
    public nuint IndexBuffer;
    public nuint IndirectBuffer;
    public ulong IndirectBufferOffset;
}

// ── Phase 7D: 光栅化状态命令 ──────────────────────────────────

/// <summary>SetCullMode 命令（单 int 参数）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetCullMode
{
    public WMTCommandBase Base;
    public int CullMode;  // MTLCullMode
}

/// <summary>SetFrontFacing 命令（单 int 参数）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetFrontFacing
{
    public WMTCommandBase Base;
    public int Winding;  // MTLWinding
}

/// <summary>SetDepthBias 命令（3 个 float）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetDepthBias
{
    public WMTCommandBase Base;
    public float Bias;
    public float SlopeScale;
    public float Clamp;
}

/// <summary>SetDepthClipMode 命令（单 int 参数）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetDepthClipMode
{
    public WMTCommandBase Base;
    public int ClipMode;  // MTLDepthClipMode
}

/// <summary>SetTriangleFillMode 命令（单 int 参数）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetTriangleFillMode
{
    public WMTCommandBase Base;
    public int FillMode;  // MTLTriangleFillMode
}

// ── Phase 7E: 深度/模板状态命令 ──────────────────────────────

/// <summary>SetDepthStencilState 命令（句柄参数）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetDepthStencilState
{
    public WMTCommandBase Base;
    public nuint State;
}

/// <summary>SetStencilReference 命令（前后两个 uint32）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct WMTRenderSetStencilReference
{
    public WMTCommandBase Base;
    public uint Front;
    public uint Back;
}
