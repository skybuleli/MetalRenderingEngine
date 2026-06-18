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
}
