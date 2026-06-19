namespace MetalRenderingEngine.Metal;

/// <summary>
/// MTLResourceOptions 子集（位标志），与 native/bridge.h 中 <c>WMTResourceOptions</c> 一致。
/// </summary>
[Flags]
public enum MTLResourceOptions : uint
{
    CPUCacheModeDefault       = 0,
    CPUCacheModeWriteCombined = 1,
    StorageModeShared         = 0 << 4,
    StorageModeManaged        = 1 << 4,
    StorageModePrivate        = 2 << 4,
    StorageModeMemoryless     = 3 << 4,
}

/// <summary>
/// MTLCommandBufferStatus；与 native/bridge.h 中 <c>WMTCommandBufferStatus</c> 一致，
/// 也与 ObjC 原始枚举值对齐。
/// </summary>
public enum MTLCommandBufferStatus : int
{
    NotEnqueued = 0,
    Enqueued    = 1,
    Committed   = 2,
    Scheduled   = 3,
    Completed   = 4,
    Error       = 5,
}

/// <summary>
/// MTLResourceUsage 位标志：用于 useResource: 的 usage 参数。
/// </summary>
[Flags]
public enum MTLResourceUsage : uint
{
    Read   = 1,
    Write  = 2,
    Sample = 4,
}

/// <summary>
/// MTLPixelFormat 子集（与 native/bridge.h 中 WMTPixelFormat 对齐）。
/// </summary>
public enum MTLPixelFormat : int
{
    Invalid      = 0,
    R8Unorm      = 10,   /* Phase 3.5: ImGui font atlas */
    RGBA8Unorm   = 70,
    BGRA8Unorm   = 80,
    RGBA32Float  = 125,
    Depth32Float = 252,
    Depth32Float_Stencil8 = 260,  /* Phase 7C: packed depth+stencil (Apple Silicon 原生) */
}

/// <summary>
/// MTLLoadAction（与 native/bridge.h 中 WMTLoadAction 对齐）。
/// </summary>
public enum MTLLoadAction : int
{
    DontCare = 0,
    Load     = 1,
    Clear    = 2,
}

/// <summary>
/// MTLStoreAction（与 native/bridge.h 中 WMTStoreAction 对齐）。
/// </summary>
public enum MTLStoreAction : int
{
    DontCare                    = 0,
    Store                       = 1,
    MultisampleResolve          = 2,
    StoreAndMultisampleResolve  = 3,  // Phase 7K: MSAA
}

// ============================================================
// Phase 3 枚举
// ============================================================

/// <summary>MTLTextureType 子集。</summary>
public enum MTLTextureType : int
{
    Type2D = 2,
    Type2DMultisample = 5,  // Phase 7K: MSAA
}

/// <summary>MTLTextureUsage 位标志。</summary>
[Flags]
public enum MTLTextureUsage : int
{
    ShaderRead   = 1,
    ShaderWrite  = 2,
    RenderTarget = 4,
}

/// <summary>MTLSamplerMinMagFilter。</summary>
public enum MTLSamplerMinMagFilter : int
{
    Nearest = 0,
    Linear  = 1,
}

/// <summary>MTLSamplerMipFilter。</summary>
public enum MTLSamplerMipFilter : int
{
    NotMipmapped = 0,
    Nearest      = 1,
    Linear       = 2,
}

/// <summary>MTLSamplerAddressMode。</summary>
public enum MTLSamplerAddressMode : int
{
    ClampToEdge   = 0,
    Repeat        = 2,
    MirrorRepeat  = 3,
}

/// <summary>MTLCompareFunction。</summary>
public enum MTLCompareFunction : int
{
    Never    = 0,
    Less     = 1,
    Equal    = 2,
    LEqual   = 3,
    Greater  = 4,
    NotEqual = 5,
    GEqual   = 6,
    Always   = 7,
}

/// <summary>MTLStencilOperation（与 native/bridge.h 中 WMTStencilOperation 对齐）。</summary>
public enum MTLStencilOperation : int
{
    Keep            = 0,
    Zero            = 1,
    Replace         = 2,
    IncrementClamp  = 3,
    DecrementClamp  = 4,
    Invert          = 5,
    IncrementWrap   = 6,
    DecrementWrap   = 7,
}

/// <summary>MTLRenderStages 位标志。</summary>
[Flags]
public enum MTLRenderStages : uint
{
    Vertex   = 1,
    Fragment = 2,
}

// ============================================================
// Phase 7D: 光栅化状态枚举
// ============================================================

/// <summary>MTLCullMode（与 native/bridge.h 中 WMTCullMode 对齐）。</summary>
public enum MTLCullMode : int
{
    None  = 0,
    Front = 1,
    Back  = 2,
}

/// <summary>MTLWinding（与 native/bridge.h 中 WMTWinding 对齐）。</summary>
public enum MTLWinding : int
{
    Clockwise        = 0,
    CounterClockwise = 1,
}

/// <summary>MTLDepthClipMode（与 native/bridge.h 中 WMTDepthClipMode 对齐）。</summary>
public enum MTLDepthClipMode : int
{
    Clip  = 0,
    Clamp = 1,
}

/// <summary>MTLTriangleFillMode（与 native/bridge.h 中 WMTTriangleFillMode 对齐）。</summary>
public enum MTLTriangleFillMode : int
{
    Fill  = 0,
    Lines = 1,
}

// ============================================================
// Phase 7F: VertexDescriptor 枚举
// ============================================================

/// <summary>MTLVertexFormat 子集（与 native/bridge.h 中 WMTVertexFormat 对齐）。</summary>
public enum MTLVertexFormat : int
{
    Invalid          = 0,
    Float2           = 29,
    Float3           = 30,
    Float4           = 31,
    UChar4           = 12,
    UChar4Normalized = 13,
    UInt             = 36,
}

/// <summary>MTLVertexStepFunction（与 native/bridge.h 中 WMTVertexStepFunction 对齐）。</summary>
public enum MTLVertexStepFunction : int
{
    Constant    = 0,
    PerVertex   = 1,
    PerInstance = 2,
}

// ============================================================
// Phase 3.5 枚举
// ============================================================

/// <summary>MTLBlendFactor（与 native/bridge.h 中 WMTBlendFactor 对齐）。</summary>
public enum MTLBlendFactor : int
{
    Zero                     = 0,
    One                      = 1,
    SourceColor              = 2,
    OneMinusSourceColor      = 3,
    SourceAlpha              = 4,
    OneMinusSourceAlpha      = 5,
    DestinationAlpha         = 6,
    OneMinusDestinationAlpha = 7,
    DestinationColor         = 8,
    OneMinusDestinationColor = 9,
}

/// <summary>MTLBlendOperation（与 native/bridge.h 中 WMTBlendOperation 对齐）。</summary>
public enum MTLBlendOperation : int
{
    Add              = 0,
    Subtract         = 1,
    ReverseSubtract  = 2,
    Min              = 3,
    Max              = 4,
}
