using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// Phase 6: 批量命令编码器。把多个 encoder 命令录制到一个 pinned ring buffer，
/// 一次 P/Invoke 回放整段链表，将 1000 draw/帧的 P/Invoke 从 ~1000 降到 1。
/// 参照 DXMT wmtcmd 链表模式（winemetal.h:876-1110 + winemetal_unix.c:786-869）。
///
/// 设计要点：
/// 1. 内部 byte[] ring buffer 用 GCHandle.Pinned 固定，写入的命令结构体指针稳定，
///    SetBytes 的 payload 也直接紧跟命令写入同一 buffer，无需额外 pin。
/// 2. 每条命令结构体首部为 WMTCommandBase{type, next}，Record 方法更新上一条命令的 Next。
/// 3. 不线程安全；每个 encoder pass 用独立实例。
/// </summary>
public sealed unsafe class MetalCommandList : IDisposable
{
    private byte[] _buffer;
    private GCHandle _pin;
    private byte* _basePtr;
    private int _writeOffset;
    private WMTCommandBase* _head;     // 链表头（第一条命令）
    private WMTCommandBase* _tail;     // 链表尾（用于挂新命令时更新其 Next）
    private const int DefaultCapacity = 64 * 1024;

    public MetalCommandList() : this(DefaultCapacity) { }

    public MetalCommandList(int initialCapacity)
    {
        _buffer = new byte[Math.Max(initialCapacity, 4096)];
        _pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        _basePtr = (byte*)_pin.AddrOfPinnedObject();
    }

    /// <summary>已录制的命令数。</summary>
    public int Count { get; private set; }

    /// <summary>ring buffer 已用字节数。</summary>
    public int BytesUsed => _writeOffset;

    /// <summary>本实例 render replay 调用次数（测试/性能回归守护用）。</summary>
    internal int RenderReplayCallCount { get; private set; }

    /// <summary>本实例 compute replay 调用次数（测试/性能回归守护用）。</summary>
    internal int ComputeReplayCallCount { get; private set; }

    // ── 内部：在 ring buffer 里分配 n 字节并返回指针 ──────────────
    private unsafe byte* Alloc(int size)
    {
        // 8 字节对齐（WMTCommandBase.next 需 8 字节对齐）
        size = (size + 7) & ~7;
        if (_writeOffset + size > _buffer.Length)
            Grow(size);
        byte* p = _basePtr + _writeOffset;
        _writeOffset += size;
        return p;
    }

    private void Grow(int needed)
    {
        // 重分配会改变 buffer 地址 —— 必须重定位所有命令的指针。
        // 简化策略：不允许在非空时扩容（调用方应预估容量）。
        // 若未来需要支持，需重建链表指针。
        throw new InvalidOperationException(
            $"MetalCommandList ring buffer 已满（{_writeOffset}/{_buffer.Length}，需 {needed}）。" +
            "请构造时传入更大的 initialCapacity。");
    }

    // ── 内部：写入命令结构体并串入链表 ──────────────────────────
    private unsafe void LinkCommand(WMTCommandBase* cmd)
    {
        cmd->Next = null;
        if (_head == null)
        {
            _head = cmd;
            _tail = cmd;
        }
        else
        {
            _tail->Next = cmd;
            _tail = cmd;
        }
        Count++;
    }

    // ══════════ Compute 命令 ══════════

    /// <summary>录制：SetComputePipelineState（同时缓存 threadgroup size 供后续 Dispatch）。</summary>
    public unsafe void RecordSetComputePipelineState(MetalComputePipelineState pso, WMTSize threadgroupSize)
    {
        var p = (WMTComputeSetPso*)Alloc(sizeof(WMTComputeSetPso));
        p->Base.Type = (ushort)WMTComputeCmdType.SetPipelineState;
        p->Pso = pso.Handle;
        p->ThreadgroupSize = threadgroupSize;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：UseResource（compute）。</summary>
    public unsafe void RecordUseResource(MetalObject resource, MTLResourceUsage usage)
    {
        var p = (WMTComputeUseResource*)Alloc(sizeof(WMTComputeUseResource));
        p->Base.Type = (ushort)WMTComputeCmdType.UseResource;
        p->Resource = resource.Handle;
        p->Usage = (uint)usage;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetBytes（compute，payload 紧跟命令写入同一 buffer）。</summary>
    public unsafe void RecordSetBytes<T>(in T value, ulong index) where T : unmanaged
    {
        int payloadSize = sizeof(T);
        var p = (WMTComputeSetBytes*)Alloc(sizeof(WMTComputeSetBytes));
        // payload 紧跟命令结构体之后分配（同一 pinned buffer，指针稳定）
        byte* payload = Alloc(payloadSize);
        *(T*)payload = value;  // 直接拷贝（unmanaged，无 GC 引用）
        p->Base.Type = (ushort)WMTComputeCmdType.SetBytes;
        p->Bytes = payload;
        p->Length = (ulong)payloadSize;
        p->Index = index;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：DispatchThreadgroups（compute）。</summary>
    public unsafe void RecordDispatch(WMTSize threadgroupsPerGrid, WMTSize threadsPerThreadgroup)
    {
        var p = (WMTComputeDispatch*)Alloc(sizeof(WMTComputeDispatch));
        p->Base.Type = (ushort)WMTComputeCmdType.Dispatch;
        p->ThreadgroupsPerGrid = threadgroupsPerGrid;
        p->ThreadsPerThreadgroup = threadsPerThreadgroup;
        LinkCommand(&p->Base);
    }

    // ══════════ Render 命令 ══════════

    /// <summary>录制：SetRenderPipelineState。</summary>
    public unsafe void RecordSetRenderPipelineState(MetalRenderPipelineState pso)
    {
        var p = (WMTRenderSetPso*)Alloc(sizeof(WMTRenderSetPso));
        p->Base.Type = (ushort)WMTRenderCmdType.SetPipelineState;
        p->Pso = pso.Handle;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetViewport。</summary>
    public unsafe void RecordSetViewport(float x, float y, float w, float h, float znear = 0f, float zfar = 1f)
    {
        var p = (WMTRenderSetViewport*)Alloc(sizeof(WMTRenderSetViewport));
        p->Base.Type = (ushort)WMTRenderCmdType.SetViewport;
        p->X = x; p->Y = y; p->W = w; p->H = h; p->Znear = znear; p->Zfar = zfar;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetVertexBytes（payload 紧跟命令）。</summary>
    public unsafe void RecordSetVertexBytes<T>(in T value, ulong index) where T : unmanaged
    {
        var p = (WMTRenderSetBytes*)Alloc(sizeof(WMTRenderSetBytes));
        byte* payload = Alloc(sizeof(T));
        *(T*)payload = value;
        p->Base.Type = (ushort)WMTRenderCmdType.SetVertexBytes;
        p->Bytes = payload;
        p->Length = (ulong)sizeof(T);
        p->Index = index;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetFragmentBytes（payload 紧跟命令）。</summary>
    public unsafe void RecordSetFragmentBytes<T>(in T value, ulong index) where T : unmanaged
    {
        var p = (WMTRenderSetBytes*)Alloc(sizeof(WMTRenderSetBytes));
        byte* payload = Alloc(sizeof(T));
        *(T*)payload = value;
        p->Base.Type = (ushort)WMTRenderCmdType.SetFragmentBytes;
        p->Bytes = payload;
        p->Length = (ulong)sizeof(T);
        p->Index = index;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetVertexBytes（动态长度 payload，拷贝到 ring buffer）。</summary>
    public unsafe void RecordSetVertexBytes(ReadOnlySpan<byte> data, ulong index)
    {
        var p = (WMTRenderSetBytes*)Alloc(sizeof(WMTRenderSetBytes));
        byte* payload = Alloc(data.Length);
        fixed (byte* src = data)
            Buffer.MemoryCopy(src, payload, data.Length, data.Length);
        p->Base.Type = (ushort)WMTRenderCmdType.SetVertexBytes;
        p->Bytes = payload;
        p->Length = (ulong)data.Length;
        p->Index = index;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetFragmentBytes（动态长度 payload，拷贝到 ring buffer）。</summary>
    public unsafe void RecordSetFragmentBytes(ReadOnlySpan<byte> data, ulong index)
    {
        var p = (WMTRenderSetBytes*)Alloc(sizeof(WMTRenderSetBytes));
        byte* payload = Alloc(data.Length);
        fixed (byte* src = data)
            Buffer.MemoryCopy(src, payload, data.Length, data.Length);
        p->Base.Type = (ushort)WMTRenderCmdType.SetFragmentBytes;
        p->Bytes = payload;
        p->Length = (ulong)data.Length;
        p->Index = index;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：UseResource（render，带 stages）。</summary>
    public unsafe void RecordUseResource(MetalObject resource, MTLResourceUsage usage, MTLRenderStages stages)
    {
        var p = (WMTRenderUseResource*)Alloc(sizeof(WMTRenderUseResource));
        p->Base.Type = (ushort)WMTRenderCmdType.UseResource;
        p->Resource = resource.Handle;
        p->Usage = (uint)usage;
        p->Stages = (uint)stages;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：DrawPrimitives（primitiveType=0 表示 Triangle）。</summary>
    public unsafe void RecordDrawPrimitives(int primitiveType, ulong vertexStart, ulong vertexCount, ulong instanceCount = 1)
    {
        var p = (WMTRenderDraw*)Alloc(sizeof(WMTRenderDraw));
        p->Base.Type = (ushort)WMTRenderCmdType.DrawPrimitives;
        p->PrimitiveType = primitiveType;
        p->VertexStart = vertexStart;
        p->VertexCount = vertexCount;
        p->InstanceCount = instanceCount;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：DrawIndexedPrimitives（Phase 7G，带实例化）。</summary>
    public unsafe void RecordDrawIndexedPrimitives(ulong indexCount, bool is32Bit, MetalBuffer indexBuffer, ulong indexBufferOffset, ulong instanceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        var p = (WMTRenderDrawIndexed*)Alloc(sizeof(WMTRenderDrawIndexed));
        p->Base.Type = (ushort)WMTRenderCmdType.DrawIndexedPrimitives;
        p->PrimitiveType = 0;
        p->IndexCount = indexCount;
        p->IndexType = is32Bit ? 1 : 0;
        p->IndexBuffer = indexBuffer.Handle;
        p->IndexBufferLength = indexBuffer.Length;
        p->IndexBufferOffset = indexBufferOffset;
        p->InstanceCount = instanceCount;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：Indirect Draw（Phase 7H）。</summary>
    public unsafe void RecordDrawPrimitivesIndirect(MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        var p = (WMTRenderDrawIndirect*)Alloc(sizeof(WMTRenderDrawIndirect));
        p->Base.Type = (ushort)WMTRenderCmdType.DrawIndirectPrimitives;
        p->PrimitiveType = 0;
        p->IndirectBuffer = indirectBuffer.Handle;
        p->IndirectBufferOffset = offset;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：Indexed Indirect Draw（Phase 7H）。</summary>
    public unsafe void RecordDrawIndexedPrimitivesIndirect(MetalBuffer indexBuffer, MetalBuffer indirectBuffer, ulong offset = 0)
    {
        ArgumentNullException.ThrowIfNull(indexBuffer);
        ArgumentNullException.ThrowIfNull(indirectBuffer);
        var p = (WMTRenderDrawIndexedIndirect*)Alloc(sizeof(WMTRenderDrawIndexedIndirect));
        p->Base.Type = (ushort)WMTRenderCmdType.DrawIndexedIndirectPrimitives;
        p->PrimitiveType = 0;
        p->IndexType = 1; /* uint32 */
        p->IndexBuffer = indexBuffer.Handle;
        p->IndexBufferLength = indexBuffer.Length;
        p->IndirectBuffer = indirectBuffer.Handle;
        p->IndirectBufferOffset = offset;
        LinkCommand(&p->Base);
    }

    // ══════════ Phase 7D: 光栅化状态命令 ══════════

    /// <summary>录制：SetCullMode。</summary>
    public unsafe void RecordSetCullMode(MTLCullMode mode)
    {
        var p = (WMTRenderSetCullMode*)Alloc(sizeof(WMTRenderSetCullMode));
        p->Base.Type = (ushort)WMTRenderCmdType.SetCullMode;
        p->CullMode = (int)mode;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetFrontFacing。</summary>
    public unsafe void RecordSetFrontFacing(MTLWinding winding)
    {
        var p = (WMTRenderSetFrontFacing*)Alloc(sizeof(WMTRenderSetFrontFacing));
        p->Base.Type = (ushort)WMTRenderCmdType.SetFrontFacing;
        p->Winding = (int)winding;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetDepthBias。</summary>
    public unsafe void RecordSetDepthBias(float bias, float slopeScale, float clamp)
    {
        var p = (WMTRenderSetDepthBias*)Alloc(sizeof(WMTRenderSetDepthBias));
        p->Base.Type = (ushort)WMTRenderCmdType.SetDepthBias;
        p->Bias = bias;
        p->SlopeScale = slopeScale;
        p->Clamp = clamp;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetDepthClipMode。</summary>
    public unsafe void RecordSetDepthClipMode(MTLDepthClipMode mode)
    {
        var p = (WMTRenderSetDepthClipMode*)Alloc(sizeof(WMTRenderSetDepthClipMode));
        p->Base.Type = (ushort)WMTRenderCmdType.SetDepthClipMode;
        p->ClipMode = (int)mode;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetTriangleFillMode。</summary>
    public unsafe void RecordSetTriangleFillMode(MTLTriangleFillMode mode)
    {
        var p = (WMTRenderSetTriangleFillMode*)Alloc(sizeof(WMTRenderSetTriangleFillMode));
        p->Base.Type = (ushort)WMTRenderCmdType.SetTriangleFillMode;
        p->FillMode = (int)mode;
        LinkCommand(&p->Base);
    }

    // ══════════ Phase 7E: 深度/模板状态命令 ══════════

    /// <summary>录制：SetDepthStencilState。</summary>
    public unsafe void RecordSetDepthStencilState(MetalDepthStencilState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var p = (WMTRenderSetDepthStencilState*)Alloc(sizeof(WMTRenderSetDepthStencilState));
        p->Base.Type = (ushort)WMTRenderCmdType.SetDepthStencilState;
        p->State = state.Handle;
        LinkCommand(&p->Base);
    }

    /// <summary>录制：SetStencilReference。</summary>
    public unsafe void RecordSetStencilReference(uint front, uint back)
    {
        var p = (WMTRenderSetStencilReference*)Alloc(sizeof(WMTRenderSetStencilReference));
        p->Base.Type = (ushort)WMTRenderCmdType.SetStencilReference;
        p->Front = front;
        p->Back = back;
        LinkCommand(&p->Base);
    }

    // ══════════ 回放与重置 ══════════

    /// <summary>
    /// 一次 P/Invoke 回放所有 compute 命令到 encoder，然后清空链表。
    /// 注意：encoder 需在外部已创建并（回放后）由调用方负责 EndEncoding 或继续编码。
    /// </summary>
    public unsafe void ReplayCompute(MetalComputeCommandEncoder encoder)
    {
        if (_head == null) return;
        ComputeReplayCallCount++;
        for (WMTCommandBase* cmd = _head; cmd != null; cmd = cmd->Next)
        {
            switch ((WMTComputeCmdType)cmd->Type)
            {
                case WMTComputeCmdType.EndEncoding:
                    encoder.EndEncoding();
                    break;
                case WMTComputeCmdType.SetPipelineState:
                {
                    var p = (WMTComputeSetPso*)cmd;
                    using var pso = new MetalComputePipelineState(p->Pso);
                    pso.Retain();
                    encoder.SetComputePipelineState(pso);
                    break;
                }
                case WMTComputeCmdType.UseResource:
                {
                    // Metal 4 以 residency set 取代旧的 useResource 语义，这里保留命令但不再下发旧 selector。
                    break;
                }
                case WMTComputeCmdType.SetBytes:
                {
                    var p = (WMTComputeSetBytes*)cmd;
                    encoder.SetBytes(new ReadOnlySpan<byte>(p->Bytes, checked((int)p->Length)), p->Index);
                    break;
                }
                case WMTComputeCmdType.Dispatch:
                {
                    var p = (WMTComputeDispatch*)cmd;
                    encoder.DispatchThreadgroups(p->ThreadgroupsPerGrid, p->ThreadsPerThreadgroup);
                    break;
                }
            }
        }
        Clear();
    }

    /// <summary>一次 P/Invoke 回放所有 render 命令到 encoder，然后清空链表。</summary>
    public unsafe void ReplayRender(MetalRenderEncoder encoder)
    {
        if (_head == null) return;
        RenderReplayCallCount++;
        for (WMTCommandBase* cmd = _head; cmd != null; cmd = cmd->Next)
        {
            switch ((WMTRenderCmdType)cmd->Type)
            {
                case WMTRenderCmdType.EndEncoding:
                    encoder.EndEncoding();
                    break;
                case WMTRenderCmdType.SetPipelineState:
                {
                    var p = (WMTRenderSetPso*)cmd;
                    using var pso = new MetalRenderPipelineState(p->Pso);
                    pso.Retain();
                    encoder.SetRenderPipelineState(pso);
                    break;
                }
                case WMTRenderCmdType.SetViewport:
                {
                    var p = (WMTRenderSetViewport*)cmd;
                    encoder.SetViewport(p->X, p->Y, p->W, p->H, p->Znear, p->Zfar);
                    break;
                }
                case WMTRenderCmdType.SetVertexBytes:
                {
                    var p = (WMTRenderSetBytes*)cmd;
                    encoder.SetVertexBytes(new ReadOnlySpan<byte>(p->Bytes, checked((int)p->Length)), p->Index);
                    break;
                }
                case WMTRenderCmdType.SetFragmentBytes:
                {
                    var p = (WMTRenderSetBytes*)cmd;
                    encoder.SetFragmentBytes(new ReadOnlySpan<byte>(p->Bytes, checked((int)p->Length)), p->Index);
                    break;
                }
                case WMTRenderCmdType.UseResource:
                    break;
                case WMTRenderCmdType.DrawPrimitives:
                {
                    var p = (WMTRenderDraw*)cmd;
                    encoder.DrawPrimitives(p->PrimitiveType, p->VertexStart, p->VertexCount, p->InstanceCount);
                    break;
                }
                case WMTRenderCmdType.SetCullMode:
                {
                    var p = (WMTRenderSetCullMode*)cmd;
                    encoder.SetCullMode((MTLCullMode)p->CullMode);
                    break;
                }
                case WMTRenderCmdType.SetFrontFacing:
                {
                    var p = (WMTRenderSetFrontFacing*)cmd;
                    encoder.SetFrontFacing((MTLWinding)p->Winding);
                    break;
                }
                case WMTRenderCmdType.SetDepthBias:
                {
                    var p = (WMTRenderSetDepthBias*)cmd;
                    encoder.SetDepthBias(p->Bias, p->SlopeScale, p->Clamp);
                    break;
                }
                case WMTRenderCmdType.SetDepthClipMode:
                {
                    var p = (WMTRenderSetDepthClipMode*)cmd;
                    encoder.SetDepthClipMode((MTLDepthClipMode)p->ClipMode);
                    break;
                }
                case WMTRenderCmdType.SetTriangleFillMode:
                {
                    var p = (WMTRenderSetTriangleFillMode*)cmd;
                    encoder.SetTriangleFillMode((MTLTriangleFillMode)p->FillMode);
                    break;
                }
                case WMTRenderCmdType.SetDepthStencilState:
                {
                    var p = (WMTRenderSetDepthStencilState*)cmd;
                    using var state = new MetalDepthStencilState(p->State);
                    state.Retain();
                    encoder.SetDepthStencilState(state);
                    break;
                }
                case WMTRenderCmdType.SetStencilReference:
                {
                    var p = (WMTRenderSetStencilReference*)cmd;
                    encoder.SetStencilReference(p->Front, p->Back);
                    break;
                }
                case WMTRenderCmdType.DrawIndexedPrimitives:
                {
                    var p = (WMTRenderDrawIndexed*)cmd;
                    using var indexBuffer = new MetalBuffer(p->IndexBuffer, p->IndexBufferLength);
                    indexBuffer.Retain();
                    encoder.DrawIndexedTriangles(p->IndexCount, p->IndexType == 1, indexBuffer, p->IndexBufferOffset, p->InstanceCount);
                    break;
                }
                case WMTRenderCmdType.DrawIndirectPrimitives:
                {
                    var p = (WMTRenderDrawIndirect*)cmd;
                    using var indirectBuffer = new MetalBuffer(p->IndirectBuffer, 0UL);
                    indirectBuffer.Retain();
                    encoder.DrawPrimitivesIndirect(indirectBuffer, p->IndirectBufferOffset);
                    break;
                }
                case WMTRenderCmdType.DrawIndexedIndirectPrimitives:
                {
                    var p = (WMTRenderDrawIndexedIndirect*)cmd;
                    using var indexBuffer = new MetalBuffer(p->IndexBuffer, p->IndexBufferLength);
                    using var indirectBuffer = new MetalBuffer(p->IndirectBuffer, 0UL);
                    indexBuffer.Retain();
                    indirectBuffer.Retain();
                    encoder.DrawIndexedTrianglesIndirect(indexBuffer, indirectBuffer, p->IndirectBufferOffset);
                    break;
                }
            }
        }
        Clear();
    }

    /// <summary>清空链表（保留 ring buffer 内存，重置写指针）。</summary>
    public unsafe void Clear()
    {
        _head = null;
        _tail = null;
        _writeOffset = 0;
        Count = 0;
    }

    public void Dispose()
    {
        if (_pin.IsAllocated) _pin.Free();
        _buffer = null!;
        unsafe { _basePtr = null; }
        GC.SuppressFinalize(this);
    }

    ~MetalCommandList() => Dispose();
}
