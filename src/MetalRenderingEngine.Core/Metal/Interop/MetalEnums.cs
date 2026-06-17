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
    BGRA8Unorm   = 80,
    RGBA8Unorm   = 70,
    RGBA32Float  = 125,
    Depth32Float = 252,
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
    DontCare           = 0,
    Store              = 1,
    MultisampleResolve = 2,
}
