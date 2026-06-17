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
