/*
 * bridge.h — Metal 桥接层 C ABI（Phase 1：compute 子集）
 *
 * 设计原则（详见 csharp-metal-rendering-blueprint.md §4.1 与
 * /Users/liliang/.claude/plans/csharp-metal-rendering-blueprint-md-temporal-sphinx.md
 * 的 "DXMT 参考映射"章节）：
 *   1. 命名风格借鉴 DXMT winemetal.h：MTLClass_methodName / NSClass_methodName
 *   2. 句柄类型统一为 mtl_handle_t（uint64），便于 C# 端用 nuint marshal
 *   3. 每个函数只做"句柄→ObjC 调用→返回句柄"的薄包装，正文不超过 20 行
 *   4. 错误通过 mtl_handle_t* err_out 回传（0 = 成功），不抛 ObjC 异常
 *   5. 所有 newXxx 函数返回 retained handle（__bridge_retained），调用方负责 release
 */

#ifndef METAL_BRIDGE_H
#define METAL_BRIDGE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ============================================================
 *  类型与常量
 * ============================================================ */

/* 不透明句柄：在 macOS 上即 uintptr_t（与 id 等宽，64 位） */
typedef uintptr_t mtl_handle_t;

#define MTL_NULL_HANDLE ((mtl_handle_t)0)

/* MTLResourceOptions 子集（与 MTLResourceOptions 位定义一致） */
enum WMTResourceOptions {
    WMTResourceCPUCacheModeDefault       = 0,
    WMTResourceCPUCacheModeWriteCombined = 1,
    WMTResourceStorageModeShared         = 0 << 4,
    WMTResourceStorageModeManaged        = 1 << 4,
    WMTResourceStorageModePrivate        = 2 << 4,
    WMTResourceStorageModeMemoryless     = 3 << 4,
};

/* MTLCommandBufferStatus（顺序与 ObjC 枚举对齐） */
enum WMTCommandBufferStatus {
    WMTCommandBufferStatusNotEnqueued = 0,
    WMTCommandBufferStatusEnqueued    = 1,
    WMTCommandBufferStatusCommitted   = 2,
    WMTCommandBufferStatusScheduled   = 3,
    WMTCommandBufferStatusCompleted   = 4,
    WMTCommandBufferStatusError       = 5,
};

/* Buffer 创建参数（C 标准布局，C# 端 LayoutKind.Sequential 对应） */
struct WMTBufferInfo {
    uint64_t length;          /* 字节数 */
    uint32_t options;         /* WMTResourceOptions 位或 */
    uint32_t reserved;        /* 对齐填充，保持 16 字节倍数 */
};

/* 三维尺寸（对应 MTLSize） */
struct WMTSize {
    uint64_t width;
    uint64_t height;
    uint64_t depth;
};

/* ============================================================
 *  Blend State（Phase 3.5 新增）
 * ============================================================ */

enum WMTBlendFactor {
    WMTBlendFactorZero                     = 0,
    WMTBlendFactorOne                      = 1,
    WMTBlendFactorSourceColor              = 2,
    WMTBlendFactorOneMinusSourceColor      = 3,
    WMTBlendFactorSourceAlpha              = 4,
    WMTBlendFactorOneMinusSourceAlpha      = 5,
    WMTBlendFactorDestinationAlpha         = 6,
    WMTBlendFactorOneMinusDestinationAlpha = 7,
    WMTBlendFactorDestinationColor         = 8,
    WMTBlendFactorOneMinusDestinationColor = 9,
};

enum WMTBlendOperation {
    WMTBlendOperationAdd              = 0,
    WMTBlendOperationSubtract         = 1,
    WMTBlendOperationReverseSubtract  = 2,
    WMTBlendOperationMin              = 3,
    WMTBlendOperationMax              = 4,
};

/* ============================================================
 *  引用计数（对应 NSObject -retain / -release）
 * ============================================================ */

void NSObject_retain(mtl_handle_t obj);
void NSObject_release(mtl_handle_t obj);

/* ============================================================
 *  NSError
 * ============================================================ */

/* 拷贝 -localizedDescription 到 buffer（UTF-8），返回实际写入字节数（含 \0）；
 * 若 buffer 为 NULL 或 max_length 为 0，仅返回所需字节数（含 \0）。 */
uint64_t NSError_localizedDescription(mtl_handle_t error, char *buffer, uint64_t max_length);

/* ============================================================
 *  MTLDevice
 * ============================================================ */

/* 对应 ObjC 的 MTLCreateSystemDefaultDevice()；改名避开 Metal.framework 同名符号冲突 */
mtl_handle_t MTLDevice_createSystemDefault(void);

/* 拷贝 device.name 到 buffer（UTF-8），语义与 NSError_localizedDescription 一致 */
uint64_t MTLDevice_name(mtl_handle_t device, char *buffer, uint64_t max_length);

int MTLDevice_hasUnifiedMemory(mtl_handle_t device);

uint64_t MTLDevice_recommendedMaxWorkingSetSize(mtl_handle_t device);

/* ============================================================
 *  MTLLibrary / MTLFunction
 * ============================================================ */

/* 从预编译 .metallib 字节流创建 library；
 * 失败时返回 MTL_NULL_HANDLE 并把 NSError 句柄写入 *err_out（retained，调用方释放）。
 * 成功时 *err_out 写 MTL_NULL_HANDLE。 */
mtl_handle_t MTLDevice_newLibrary(mtl_handle_t device,
                                  const void *data,
                                  uint64_t length,
                                  mtl_handle_t *err_out);

/* name 为 UTF-8 C 字符串；找不到返回 MTL_NULL_HANDLE */
mtl_handle_t MTLLibrary_newFunctionWithName(mtl_handle_t library, const char *name);

/* Phase 9E: 从 MSL 源码创建 library（仅 SpirvCross 路径使用）。
 * 失败时返回 MTL_NULL_HANDLE 并把 NSError 句柄写入 *err_out。
 * 成功时 *err_out 写 MTL_NULL_HANDLE。 */
mtl_handle_t MTLDevice_newLibraryWithSource(mtl_handle_t device,
                                             const char *source,
                                             mtl_handle_t *err_out);

/* ============================================================
 *  MTLComputePipelineState
 * ============================================================ */

mtl_handle_t MTLDevice_newComputePipelineState(mtl_handle_t device,
                                               mtl_handle_t function,
                                               mtl_handle_t *err_out);

uint64_t MTLComputePipelineState_maxTotalThreadsPerThreadgroup(mtl_handle_t pso);
uint64_t MTLComputePipelineState_threadExecutionWidth(mtl_handle_t pso);

/* ============================================================
 *  MTLBuffer
 * ============================================================ */

mtl_handle_t MTLDevice_newBuffer(mtl_handle_t device, const struct WMTBufferInfo *info);

/* 返回 CPU 可访问的内存指针（StorageModeShared/Managed）；Private 模式返回 NULL */
void *MTLBuffer_contents(mtl_handle_t buffer);

/* Apple Silicon 上 buffer 的 64 位 GPU 虚拟地址；M1+ 必为非零。
 * MSC 输出的 metallib 通过 top-level argument buffer 间接寻址资源，
 * 调用方通常需要把此地址 setBytes 到 buffer(0) 的 argument buffer 中。
 * 详见 docs/slang-reflection-binding-design.md §3.2 + memory/msc-binding-model.md */
uint64_t MTLBuffer_gpuAddress(mtl_handle_t buffer);

/* Managed 模式下显式标记 CPU 端修改区间（Shared/Private 模式空操作） */
void MTLBuffer_didModifyRange(mtl_handle_t buffer, uint64_t offset, uint64_t length);

/* ============================================================
 *  MTLCommandQueue / MTLCommandBuffer
 * ============================================================ */

mtl_handle_t MTLDevice_newCommandQueue(mtl_handle_t device);

mtl_handle_t MTLCommandQueue_commandBuffer(mtl_handle_t queue);

void MTLCommandBuffer_commit(mtl_handle_t cmdbuf);
void MTLCommandBuffer_waitUntilCompleted(mtl_handle_t cmdbuf);

int MTLCommandBuffer_status(mtl_handle_t cmdbuf);

/* 返回当前命令缓冲的错误对象（retained，调用方释放）；无错误返回 MTL_NULL_HANDLE */
mtl_handle_t MTLCommandBuffer_error(mtl_handle_t cmdbuf);

/* ============================================================
 *  MTLComputeCommandEncoder
 * ============================================================ */

mtl_handle_t MTLCommandBuffer_computeCommandEncoder(mtl_handle_t cmdbuf);

void MTLComputeCommandEncoder_setComputePipelineState(mtl_handle_t encoder, mtl_handle_t pso);
void MTLComputeCommandEncoder_setBuffer(mtl_handle_t encoder, mtl_handle_t buffer, uint64_t offset, uint64_t index);
void MTLComputeCommandEncoder_setBytes(mtl_handle_t encoder, const void *bytes, uint64_t length, uint64_t index);
void MTLComputeCommandEncoder_setTexture(mtl_handle_t encoder, mtl_handle_t texture, uint64_t index);

/* 让 GPU 在本 encoder pass 内驻留某个资源（用于通过 GPU 地址间接访问的场景）。
 * usage 为 MTLResourceUsage 位或：1=Read, 2=Write, 4=Sample */
void MTLComputeCommandEncoder_useResource(mtl_handle_t encoder, mtl_handle_t resource, uint32_t usage);

void MTLComputeCommandEncoder_dispatchThreadgroups(mtl_handle_t encoder,
                                                   struct WMTSize threadgroups_per_grid,
                                                   struct WMTSize threads_per_threadgroup);

void MTLComputeCommandEncoder_endEncoding(mtl_handle_t encoder);

/* ============================================================
 *  Phase 7A: MTLDepthStencilState
 * ============================================================ */

/* MTLStencilOperation（与 ObjC 枚举对齐） */
enum WMTStencilOperation {
    WMTStencilOperationKeep            = 0,
    WMTStencilOperationZero            = 1,
    WMTStencilOperationReplace         = 2,
    WMTStencilOperationIncrementClamp  = 3,
    WMTStencilOperationDecrementClamp  = 4,
    WMTStencilOperationInvert          = 5,
    WMTStencilOperationIncrementWrap   = 6,
    WMTStencilOperationDecrementWrap   = 7,
};

/* 单面 stencil 状态描述（对应 MTLStencilDescriptor）。
 * readMask/writeMask 与 Metal 一致：比较前先 (ref & readMask) op (stencil & readMask)。 */
struct WMTStencilDescriptor {
    uint32_t stencilFailureOperation;   /* WMTStencilOperation */
    uint32_t depthFailureOperation;     /* WMTStencilOperation */
    uint32_t depthStencilPassOperation; /* WMTStencilOperation */
    uint32_t stencilCompareFunction;    /* WMTCompareFunction */
    uint32_t readMask;
    uint32_t writeMask;
};

/* 深度/模板状态描述（对应 MTLDepthStencilDescriptor）。
 * 内存布局：4(depthCompareFn) + 1(depthWrite) + 3(pad) +
 *           24(frontFaceStencil) + 24(backFaceStencil) = 56 字节。
 * C# 端用 LayoutKind.Sequential + 显式 padding 对齐。 */
struct WMTDepthStencilDesc {
    uint32_t depthCompareFunction;   /* WMTCompareFunction；Never=0 表示禁用深度测试 */
    uint8_t  depthWriteEnabled;      /* 0=禁用写入, 1=启用 */
    uint8_t  _pad[3];                /* 对齐填充 */
    struct WMTStencilDescriptor frontFaceStencil;
    struct WMTStencilDescriptor backFaceStencil;
};

/* 从描述创建 MTLDepthStencilState（retained handle，调用方负责释放）。
 * 对应 ObjC: [MTLDevice newDepthStencilStateWithDescriptor:] */
mtl_handle_t MTLDevice_newDepthStencilState(mtl_handle_t device,
                                             const struct WMTDepthStencilDesc *desc);

/* ============================================================
 *  MTLRenderPipelineState
 * ============================================================ */

/* 关键像素格式（与 MTLPixelFormat 原始值对齐） */
enum WMTPixelFormat {
    WMTPixelFormatInvalid      = 0,
    WMTPixelFormatR8Unorm      = 10,     /* Phase 3.5: ImGui font atlas */
    WMTPixelFormatRGBA8Unorm   = 70,
    WMTPixelFormatBGRA8Unorm   = 80,
    WMTPixelFormatRGBA16Float  = 115,
    WMTPixelFormatRGBA32Float  = 125,
    WMTPixelFormatDepth32Float = 252,
    WMTPixelFormatDepth32Float_Stencil8 = 260,  /* Phase 7C: packed depth+stencil (Apple Silicon 原生) */
};

/* 颜色附件描述（Phase 2 基础 + Phase 3.5 blend 扩展） */
struct WMTColorAttachment {
    int pixel_format;       /* WMTPixelFormat */
    int write_mask;         /* 默认 0xF（RGBA 全写） */
    int blending_enabled;   /* 0=禁用, 1=启用 */
    int src_rgb_blend_factor;   /* WMTBlendFactor */
    int dst_rgb_blend_factor;   /* WMTBlendFactor */
    int src_alpha_blend_factor; /* WMTBlendFactor */
    int dst_alpha_blend_factor; /* WMTBlendFactor */
    int rgb_blend_op;           /* WMTBlendOperation */
    int alpha_blend_op;         /* WMTBlendOperation */
};

/* Phase 7F: VertexDescriptor（需在 WMTRenderPipelineDesc 之前声明） */
enum WMTVertexFormat {
    WMTVertexFormatInvalid = 0,
    WMTVertexFormatFloat2  = 29,
    WMTVertexFormatFloat3  = 30,
    WMTVertexFormatFloat4  = 31,
    WMTVertexFormatUChar4  = 12,
    WMTVertexFormatUChar4Normalized = 13,
    WMTVertexFormatUInt    = 36,
};

/* 注意：值必须与 MTLVertexStepFunction 原枚举对齐
 * (Constant=0, PerVertex=1, PerInstance=2) */
enum WMTVertexStepFunction {
    WMTVertexStepFunctionConstant    = 0,
    WMTVertexStepFunctionPerVertex   = 1,
    WMTVertexStepFunctionPerInstance = 2,
};

struct WMTVertexAttributeDesc {
    int format;
    uint64_t offset;
    uint32_t bufferIndex;
    uint32_t _pad;
};

struct WMTVertexBufferLayoutDesc {
    uint64_t stride;
    uint32_t stepFunction;
    uint32_t stepRate;
};

struct WMTVertexDescriptor {
    struct WMTVertexAttributeDesc attributes[8];
    uint32_t attributeCount;
    struct WMTVertexBufferLayoutDesc layouts[8];
    uint32_t layoutCount;
};

struct WMTRenderPipelineDesc {
    struct WMTColorAttachment colors[8];
    int color_count;              /* 实际使用的颜色附件数 */
    int depth_pixel_format;       /* 0 = None */
    int stencil_pixel_format;     /* 0 = None */
    int sample_count;             /* 默认 1 */
    struct WMTVertexDescriptor vertex_descriptor;  /* Phase 7F */
};

mtl_handle_t MTLDevice_newRenderPipelineState(mtl_handle_t device,
                                              mtl_handle_t vertex_func,
                                              mtl_handle_t fragment_func,
                                              const struct WMTRenderPipelineDesc *desc,
                                              mtl_handle_t *err_out);

/* ============================================================
 *  MTLRenderCommandEncoder
 * ============================================================ */

enum WMTLoadAction {
    WMTLoadActionDontCare = 0,
    WMTLoadActionLoad     = 1,
    WMTLoadActionClear    = 2,
};

enum WMTStoreAction {
    WMTStoreActionDontCare          = 0,
    WMTStoreActionStore             = 1,
    WMTStoreActionMultisampleResolve = 2,
};

struct WMTClearColor {
    float r, g, b, a;
};

struct WMTRenderPassAttachment {
    mtl_handle_t texture;       /* MTLTexture */
    int load_action;            /* WMTLoadAction */
    int store_action;           /* WMTStoreAction */
    struct WMTClearColor clear_color;
    float clear_depth;
    int clear_stencil;
    mtl_handle_t resolve_texture;  /* Phase 7K: MSAA resolve target */
};

struct WMTRenderPassDesc {
    struct WMTRenderPassAttachment colors[8];
    struct WMTRenderPassAttachment depth;
    struct WMTRenderPassAttachment stencil;
};

mtl_handle_t MTLCommandBuffer_renderCommandEncoder(mtl_handle_t cmdbuf,
                                                    const struct WMTRenderPassDesc *desc);

void MTLRenderCommandEncoder_setRenderPipelineState(mtl_handle_t encoder, mtl_handle_t pso);
void MTLRenderCommandEncoder_setVertexBuffer(mtl_handle_t encoder, mtl_handle_t buffer,
                                              uint64_t offset, uint64_t index);
void MTLRenderCommandEncoder_setViewport(mtl_handle_t encoder,
                                          float x, float y, float w, float h,
                                          float znear, float zfar);
void MTLRenderCommandEncoder_setScissorRect(mtl_handle_t encoder,
                                             int x, int y, int w, int h);
void MTLRenderCommandEncoder_drawPrimitives(mtl_handle_t encoder,
                                             int primitive_type,   /* 0=triangle */
                                             uint64_t vertex_start,
                                             uint64_t vertex_count);

/* Phase 7G: Instanced draw */
void MTLRenderCommandEncoder_drawPrimitivesInstanced(mtl_handle_t encoder,
                                                      int primitive_type,
                                                      uint64_t vertex_start,
                                                      uint64_t vertex_count,
                                                      uint64_t instance_count);

void MTLRenderCommandEncoder_drawIndexedPrimitives(mtl_handle_t encoder,
                                                    int primitive_type,      /* 0=triangle */
                                                    uint64_t index_count,
                                                    int index_type,          /* 0=uint16, 1=uint32 */
                                                    mtl_handle_t index_buffer,
                                                    uint64_t index_buffer_offset);

/* Phase 7G: Instanced indexed draw */
void MTLRenderCommandEncoder_drawIndexedPrimitivesInstanced(mtl_handle_t encoder,
                                                             int primitive_type,
                                                             uint64_t index_count,
                                                             int index_type,
                                                             mtl_handle_t index_buffer,
                                                             uint64_t index_buffer_offset,
                                                             uint64_t instance_count);

/* Phase 7H: Indirect draw */
void MTLRenderCommandEncoder_drawPrimitivesIndirect(mtl_handle_t encoder,
                                                     int primitive_type,
                                                     mtl_handle_t indirect_buffer,
                                                     uint64_t indirect_buffer_offset);

void MTLRenderCommandEncoder_drawIndexedPrimitivesIndirect(mtl_handle_t encoder,
                                                            int primitive_type,
                                                            int index_type,
                                                            mtl_handle_t index_buffer,
                                                            mtl_handle_t indirect_buffer,
                                                            uint64_t indirect_buffer_offset);
void MTLRenderCommandEncoder_endEncoding(mtl_handle_t encoder);

/* ============================================================
 *  CAMetalLayer / CAMetalDrawable
 * ============================================================ */

/* 创建 NSWindow（800×600）+ NSView + CAMetalLayer；
 * 返回窗口句柄；*out_layer 写入 retained layer 句柄。
 * 标题为 UTF-8 C 字符串。 */
mtl_handle_t Cocoa_CreateMetalWindow(const char *title, float width, float height, mtl_handle_t *out_layer);

/* 配置 layer */
void CAMetalLayer_setDevice(mtl_handle_t layer, mtl_handle_t device);
void CAMetalLayer_setPixelFormat(mtl_handle_t layer, int pixel_format);
void CAMetalLayer_setDrawableSize(mtl_handle_t layer, float width, float height);

/* 获取下一帧可呈现的 drawable */
mtl_handle_t CAMetalLayer_nextDrawable(mtl_handle_t layer);

/* drawable 的 texture */
mtl_handle_t CAMetalDrawable_texture(mtl_handle_t drawable);

/* 在 cmdbuf 提交前呈现 */
void MTLCommandBuffer_presentDrawable(mtl_handle_t cmdbuf, mtl_handle_t drawable);

/* 轮询一次 Cocoa 事件队列；返回 0 = 窗口仍打开，1 = 用户请求关闭 */
int Cocoa_PollEvents(void);

/* 获取 window 的 contentView（NSView*），用于传给 ImGuiImplOSX.Init。
 * 返回的句柄不增加引用计数（view 由 window 持有）。 */
mtl_handle_t Cocoa_WindowContentView(mtl_handle_t window);

/* 获取 view 的 drawable 尺寸（像素，已考虑 HiDPI），
 * *out_width / *out_height 由调用方提供指针。 */
void Cocoa_ViewDrawableSize(mtl_handle_t view, float *out_width, float *out_height);

/* ============================================================
 *  MTLTexture（只读回读）
 * ============================================================ */

/* 获取纹理尺寸（像素） */
uint64_t MTLTexture_width(mtl_handle_t texture);
uint64_t MTLTexture_height(mtl_handle_t texture);

/* 从纹理的指定 mip 级别读取像素数据到 dst（调用方分配，至少 dst_size 字节）。
 * 内部调用 [MTLTexture getBytes:bytesPerRow:fromRegion:mipmapLevel:]。
 * 返回实际写入字节数；失败返回 0。 */
uint64_t MTLTexture_getBytes(mtl_handle_t texture, void *dst, uint64_t dst_size, uint64_t mip_level);

/* 获取纹理的行字节数（bytesPerRow），用于计算读取缓冲区大小 */
uint64_t MTLTexture_bytesPerRow(mtl_handle_t texture, uint64_t mip_level);

/* 获取 texture 的 GPU 资源标识（MTLResourceID._impl，macOS 13+）。
 * 用于将 texture 写入 MSC 描述符堆条目。需要 texture 具备 ShaderRead usage。 */
uint64_t MTLTexture_gpuResourceID(mtl_handle_t texture);

/* ============================================================
 *  Phase 3 枚举
 * ============================================================ */

enum WMTTextureType {
    WMTTextureType2D = 2,
};

enum WMTTextureUsage {
    WMTTextureUsageShaderRead   = 1,
    WMTTextureUsageShaderWrite  = 2,
    WMTTextureUsageRenderTarget = 4,
};

enum WMTSamplerMinMagFilter {
    WMTSamplerMinMagFilterNearest = 0,
    WMTSamplerMinMagFilterLinear  = 1,
};

enum WMTSamplerMipFilter {
    WMTSamplerMipFilterNotMipmapped = 0,
    WMTSamplerMipFilterNearest      = 1,
    WMTSamplerMipFilterLinear       = 2,
};

enum WMTSamplerAddressMode {
    WMTSamplerAddressModeClampToEdge   = 0,
    WMTSamplerAddressModeRepeat        = 2,
    WMTSamplerAddressModeMirrorRepeat  = 3,
};

enum WMTCompareFunction {
    WMTCompareFunctionNever    = 0,
    WMTCompareFunctionLess     = 1,
    WMTCompareFunctionEqual    = 2,
    WMTCompareFunctionLEqual   = 3,
    WMTCompareFunctionGreater  = 4,
    WMTCompareFunctionNotEqual = 5,
    WMTCompareFunctionGEqual   = 6,
    WMTCompareFunctionAlways   = 7,
};

enum WMTRenderStages {
    WMTRenderStageVertex   = 1,
    WMTRenderStageFragment = 2,
};

/* ============================================================
 *  Phase 7D: 光栅化状态枚举
 * ============================================================ */

enum WMTCullMode {
    WMTCullModeNone  = 0,
    WMTCullModeFront = 1,
    WMTCullModeBack  = 2,
};

enum WMTWinding {
    WMTWindingClockwise        = 0,
    WMTWindingCounterClockwise = 1,
};

enum WMTDepthClipMode {
    WMTDepthClipModeClip   = 0,
    WMTDepthClipModeClamp  = 1,
};

enum WMTTriangleFillMode {
    WMTTriangleFillModeFill  = 0,
    WMTTriangleFillModeLines = 1,
};

/* ============================================================
 *  Phase 3 结构体
 * ============================================================ */

struct WMTTextureInfo {
    int pixel_format;      /* WMTPixelFormat */
    int texture_type;      /* WMTTextureType */
    uint64_t width;
    uint64_t height;
    uint64_t depth;
    int mipmap_levels;
    int sample_count;
    int usage;             /* WMTTextureUsage 位或 */
    int options;           /* WMTResourceOptions 位或 */
};

struct WMTSamplerInfo {
    int min_filter;        /* WMTSamplerMinMagFilter */
    int mag_filter;        /* WMTSamplerMinMagFilter */
    int mip_filter;        /* WMTSamplerMipFilter */
    int s_address_mode;    /* WMTSamplerAddressMode */
    int t_address_mode;    /* WMTSamplerAddressMode */
    int r_address_mode;    /* WMTSamplerAddressMode */
    int max_anisotropy;
    int compare_function;  /* WMTCompareFunction; -1 = disabled */
    float lod_min_clamp;
    float lod_max_clamp;
};

/* 三维原点（对应 MTLOrigin） */
struct WMTOrigin {
    uint64_t x;
    uint64_t y;
    uint64_t z;
};

/* ============================================================
 *  Phase 3: MTLTexture 创建与写入
 * ============================================================ */

mtl_handle_t MTLDevice_newTexture(mtl_handle_t device, const struct WMTTextureInfo *info);

/* 上传像素数据到纹理的指定区域和 mip 级别 */
void MTLTexture_replaceRegion(mtl_handle_t texture,
                               struct WMTOrigin origin, struct WMTSize size,
                               uint64_t mip_level, uint64_t slice,
                               const void *data, uint64_t bytes_per_row, uint64_t bytes_per_image);

/* ============================================================
 *  Phase 3: MTLSamplerState
 * ============================================================ */

mtl_handle_t MTLDevice_newSamplerState(mtl_handle_t device, const struct WMTSamplerInfo *info);

/* 获取 sampler 的 GPU 资源标识（MTLResourceID._impl，macOS 13+）。
 * sampler 必须在创建时设 supportArgumentBuffers=YES（见 MTLDevice_newSamplerState 实现），
 * 否则返回 0。用于将 sampler 写入 MSC 描述符堆条目。 */
uint64_t MTLSamplerState_gpuResourceID(mtl_handle_t sampler);

/* ============================================================
 *  Phase 3: MTLFence
 * ============================================================ */

mtl_handle_t MTLDevice_newFence(mtl_handle_t device);

/* ============================================================
 *  Phase 6: MTLSharedEvent + CPU fence（跨 CPU/GPU 同步 + 异步通知）
 *
 *  与 MTLFence 的区别：MTLFence 纯 GPU 侧、无 signaled value、CPU 无法等待；
 *  MTLSharedEvent 有单调 signaledValue，CPU 可阻塞等待或异步回调，
 *  支持 GPU encodeSignalEvent/encodeWaitForEvent 跨 command buffer 同步。
 *  参照 DXMT winemetal_unix.c:2471-2498 的 listener + CFRunLoop 模式。
 * ============================================================ */

/* 回调函数指针类型：GPU signal 到指定 value 时在 listener 后台线程触发。
 * user_data 由调用方透传（C# 端用 GCHandle 持有）。 */
typedef void (*shared_event_callback_t)(void *user_data, uint64_t value);

/* 创建 MTLSharedEvent（retained，初始 signaledValue=0） */
mtl_handle_t MTLDevice_newSharedEvent(mtl_handle_t device);

/* 读取当前已 signal 的值（CPU 侧，非阻塞） */
uint64_t MTLSharedEvent_signaledValue(mtl_handle_t event);

/* CPU 阻塞等待 event 达到 value（timeout_ms=0 表示无限等待）。
 * 返回 1=达到，0=超时 */
int MTLSharedEvent_waitUntilSignaledValue(mtl_handle_t event, uint64_t value, uint64_t timeout_ms);

/* GPU 命令缓冲：执行到此处时给 event 赋 value（GPU 侧 signal） */
void MTLCommandBuffer_encodeSignalEvent(mtl_handle_t cmdbuf, mtl_handle_t event, uint64_t value);

/* GPU 命令缓冲：执行到此处时阻塞直到 event.signaledValue >= value */
void MTLCommandBuffer_encodeWaitForEvent(mtl_handle_t cmdbuf, mtl_handle_t event, uint64_t value);

/* 创建 SharedEventListener：内部启动后台 CFRunLoop 线程承载 notifyListener 回调。
 * 返回的句柄持有 listener + 后台线程；释放时调 MTLSharedEventListener_release。 */
mtl_handle_t MTLSharedEventListener_create(void);

/* 释放 listener：停止后台 runloop 并 join 线程 */
void MTLSharedEventListener_release(mtl_handle_t listener);

/* 注册异步通知：当 GPU signal 到 value 时，在 listener 的后台线程调 callback。
 * callback/user_data 的生命周期由调用方管理（C# 端用 GCHandle 钉住）。 */
void MTLSharedEvent_notifyListener(mtl_handle_t event, mtl_handle_t listener,
                                    uint64_t value, shared_event_callback_t callback, void *user_data);

/* ============================================================
 *  Phase 3: MTLRenderCommandEncoder 扩展
 * ============================================================ */

void MTLRenderCommandEncoder_setVertexBytes(mtl_handle_t encoder, const void *bytes, uint64_t length, uint64_t index);
void MTLRenderCommandEncoder_setFragmentBuffer(mtl_handle_t encoder, mtl_handle_t buffer, uint64_t offset, uint64_t index);
void MTLRenderCommandEncoder_setFragmentBytes(mtl_handle_t encoder, const void *bytes, uint64_t length, uint64_t index);
void MTLRenderCommandEncoder_setFragmentTexture(mtl_handle_t encoder, mtl_handle_t texture, uint64_t index);
void MTLRenderCommandEncoder_setFragmentSamplerState(mtl_handle_t encoder, mtl_handle_t sampler, uint64_t index);
void MTLRenderCommandEncoder_useResource(mtl_handle_t encoder, mtl_handle_t resource, uint32_t usage, uint32_t stages);
void MTLRenderCommandEncoder_waitForFence(mtl_handle_t encoder, mtl_handle_t fence, uint32_t before_stages);
void MTLRenderCommandEncoder_updateFence(mtl_handle_t encoder, mtl_handle_t fence, uint32_t after_stages);

/* ============================================================
 *  Phase 7D: 光栅化状态 setters
 * ============================================================ */

void MTLRenderCommandEncoder_setCullMode(mtl_handle_t encoder, int cull_mode);
void MTLRenderCommandEncoder_setFrontFacingWinding(mtl_handle_t encoder, int winding);
void MTLRenderCommandEncoder_setDepthBias(mtl_handle_t encoder, float bias, float slope_scale, float clamp);
void MTLRenderCommandEncoder_setDepthClipMode(mtl_handle_t encoder, int clip_mode);
void MTLRenderCommandEncoder_setTriangleFillMode(mtl_handle_t encoder, int fill_mode);

/* ============================================================
 *  Phase 7E: 深度/模板状态 setters
 * ============================================================ */

void MTLRenderCommandEncoder_setDepthStencilState(mtl_handle_t encoder, mtl_handle_t state);
void MTLRenderCommandEncoder_setStencilReferenceValue(mtl_handle_t encoder, uint32_t front, uint32_t back);

/* ============================================================
 *  Phase 3.5: MTLArgumentEncoder
 *  MSC 4.0 把 texture/sampler 也放进 buffer(2) argument buffer，
 *  无法用 setFragmentTexture 直接绑定，必须用 ArgumentEncoder 编码。
 * ============================================================ */

/* 从 MTLFunction 的指定 buffer index 创建 argument encoder（retained） */
mtl_handle_t MTLFunction_newArgumentEncoder(mtl_handle_t function, uint64_t buffer_index);

/* argument encoder 编码后的总字节数 */
uint64_t MTLArgumentEncoder_encodedLength(mtl_handle_t encoder);

/* 将 texture[0] + sampler[0] 编码到 argument buffer 的指定 offset。
 * 调用前 arg_buffer 已分配；encoder 内部按 Metal argument buffer 规范布局。 */
void MTLArgumentEncoder_encodeTextureSampler(mtl_handle_t encoder,
                                              mtl_handle_t arg_buffer,
                                              uint64_t offset,
                                              mtl_handle_t texture,
                                              mtl_handle_t sampler);

/* ============================================================
 *  Phase 3.5: MTLRenderPassDescriptor (for Hexa.NET.ImGui Backends)
 * ============================================================ */

/* 为指定纹理创建一个 MTLRenderPassDescriptor（单 color attachment）。
 * loadAction=Load, storeAction=Store，用于 ImGui 叠加渲染。
 * 调用方负责用 MTLRenderPassDescriptor_release 释放。 */
mtl_handle_t MTLRenderPassDescriptor_createForTexture(mtl_handle_t texture);
void MTLRenderPassDescriptor_release(mtl_handle_t desc);

/* ============================================================
 *  Phase 6: 批量命令编码器（wmtcmd 链表回放）
 *
 *  把多个 encoder 命令打包成 POD 结构体单链表，一次 P/Invoke 回放，
 *  将 1000 draw/帧的 P/Invoke 从 ~1000 降到 1。设计参照 DXMT
 *  winemetal.h:876-1110 的 wmtcmd_base + 各 wmtcmd_* 结构体。
 *  每个命令结构体首部必须是 wmtcmd_base（type + next），bridge.m
 *  按 type switch 分发到 ObjC 调用。
 * ============================================================ */

/* 命令头：16 字节，所有命令结构体的公共前缀 */
struct wmtcmd_base {
    uint16_t type;          /* WMTComputeCmdType / WMTRenderCmdType */
    uint16_t reserved[3];   /* 填充到 8 字节，保证 next 指针 8 字节对齐 */
    const struct wmtcmd_base *next;  /* 单链表 next，NULL 表示链尾 */
};

/* Compute 命令类型 */
enum WMTComputeCmdType {
    WMTComputeCmdEndEncoding    = 0,
    WMTComputeCmdSetPipelineState,
    WMTComputeCmdUseResource,
    WMTComputeCmdSetBytes,
    WMTComputeCmdDispatch,
};

struct wmtcmd_compute_setpso {
    struct wmtcmd_base base;
    mtl_handle_t pso;
    struct WMTSize threadgroup_size;  /* 随 PSO 缓存，供后续 Dispatch 使用 */
};

struct wmtcmd_compute_useresource {
    struct wmtcmd_base base;
    mtl_handle_t resource;
    uint32_t usage;  /* MTLResourceUsage 位或 */
};

struct wmtcmd_compute_setbytes {
    struct wmtcmd_base base;
    const void *bytes;   /* 外挂 payload buffer（调用方保持存活到回放返回） */
    uint64_t length;
    uint64_t index;
};

struct wmtcmd_compute_dispatch {
    struct wmtcmd_base base;
    struct WMTSize threadgroups_per_grid;
    struct WMTSize threads_per_threadgroup;
};

struct wmtcmd_compute_endencoding {
    struct wmtcmd_base base;
};

/* Render 命令类型 */
enum WMTRenderCmdType {
    WMTRenderCmdEndEncoding     = 0,
    WMTRenderCmdSetPipelineState,
    WMTRenderCmdSetViewport,
    WMTRenderCmdSetVertexBytes,
    WMTRenderCmdSetFragmentBytes,
    WMTRenderCmdUseResource,
    WMTRenderCmdDrawPrimitives,
    /* Phase 7D: 光栅化状态 */
    WMTRenderCmdSetCullMode,
    WMTRenderCmdSetFrontFacing,
    WMTRenderCmdSetDepthBias,
    WMTRenderCmdSetDepthClipMode,
    WMTRenderCmdSetTriangleFillMode,
    /* Phase 7E: 深度/模板状态 */
    WMTRenderCmdSetDepthStencilState,
    WMTRenderCmdSetStencilReference,
    /* Phase 7G/7H: 绘制变体 */
    WMTRenderCmdDrawIndexedPrimitives,
    WMTRenderCmdDrawIndirectPrimitives,
    WMTRenderCmdDrawIndexedIndirectPrimitives,
};

struct wmtcmd_render_setpso {
    struct wmtcmd_base base;
    mtl_handle_t pso;
};

struct wmtcmd_render_setviewport {
    struct wmtcmd_base base;
    float x, y, w, h, znear, zfar;
};

struct wmtcmd_render_setbytes {
    struct wmtcmd_base base;
    const void *bytes;   /* 外挂 payload buffer */
    uint64_t length;
    uint64_t index;
};

struct wmtcmd_render_useresource {
    struct wmtcmd_base base;
    mtl_handle_t resource;
    uint32_t usage;    /* MTLResourceUsage 位或 */
    uint32_t stages;   /* MTLRenderStages 位或 */
};

struct wmtcmd_render_draw {
    struct wmtcmd_base base;
    int primitive_type;     /* 0=Triangle（与现有 drawPrimitives 约定一致） */
    uint64_t vertex_start;
    uint64_t vertex_count;
    uint64_t instance_count; /* Phase 7G: 0=非实例化(由 bridge 决定用普通 draw) */
};

struct wmtcmd_render_draw_indexed {
    struct wmtcmd_base base;
    int primitive_type;
    uint64_t index_count;
    int index_type;         /* 0=uint16, 1=uint32 */
    mtl_handle_t index_buffer;
    uint64_t index_buffer_offset;
    uint64_t instance_count;
};

struct wmtcmd_render_draw_indirect {
    struct wmtcmd_base base;
    int primitive_type;
    mtl_handle_t indirect_buffer;
    uint64_t indirect_buffer_offset;
};

struct wmtcmd_render_draw_indexed_indirect {
    struct wmtcmd_base base;
    int primitive_type;
    int index_type;
    mtl_handle_t index_buffer;
    mtl_handle_t indirect_buffer;
    uint64_t indirect_buffer_offset;
};

/* Phase 7D: 光栅化状态命令 */
struct wmtcmd_render_setcullmode {
    struct wmtcmd_base base;
    int cull_mode;          /* WMTCullMode */
};

struct wmtcmd_render_setfrontfacing {
    struct wmtcmd_base base;
    int winding;            /* WMTWinding */
};

struct wmtcmd_render_setdepthbias {
    struct wmtcmd_base base;
    float bias;
    float slope_scale;
    float clamp;
};

struct wmtcmd_render_setdepthclipmode {
    struct wmtcmd_base base;
    int clip_mode;          /* WMTDepthClipMode */
};

struct wmtcmd_render_settrianglefillmode {
    struct wmtcmd_base base;
    int fill_mode;          /* WMTTriangleFillMode */
};

/* Phase 7E: 深度/模板状态命令 */
struct wmtcmd_render_setdepthstencilstate {
    struct wmtcmd_base base;
    mtl_handle_t state;
};

struct wmtcmd_render_setstencilreference {
    struct wmtcmd_base base;
    uint32_t front;
    uint32_t back;
};

struct wmtcmd_render_endencoding {
    struct wmtcmd_base base;
};

/* 回放入口点：遍历链表，按 type switch 分发到 ObjC 调用。
 * encoder 句柄只 H2ID 一次；链表内存由调用方持有，回放返回后可释放。 */
void MTLComputeCommandEncoder_encodeCommands(mtl_handle_t encoder, const struct wmtcmd_base *head);
void MTLRenderCommandEncoder_encodeCommands(mtl_handle_t encoder, const struct wmtcmd_base *head);

#ifdef __cplusplus
}
#endif

#endif /* METAL_BRIDGE_H */
