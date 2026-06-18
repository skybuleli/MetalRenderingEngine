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
    public unsafe void RecordDrawPrimitives(int primitiveType, ulong vertexStart, ulong vertexCount)
    {
        var p = (WMTRenderDraw*)Alloc(sizeof(WMTRenderDraw));
        p->Base.Type = (ushort)WMTRenderCmdType.DrawPrimitives;
        p->PrimitiveType = primitiveType;
        p->VertexStart = vertexStart;
        p->VertexCount = vertexCount;
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
        MetalBridge.MTLComputeCommandEncoder_encodeCommands(encoder.Handle, _head);
        Clear();
    }

    /// <summary>一次 P/Invoke 回放所有 render 命令到 encoder，然后清空链表。</summary>
    public unsafe void ReplayRender(MetalRenderEncoder encoder)
    {
        if (_head == null) return;
        MetalBridge.MTLRenderCommandEncoder_encodeCommands(encoder.Handle, _head);
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
