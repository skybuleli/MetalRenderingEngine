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
 *  MTLRenderPipelineState
 * ============================================================ */

/* 关键像素格式（与 MTLPixelFormat 原始值对齐） */
enum WMTPixelFormat {
    WMTPixelFormatInvalid      = 0,
    WMTPixelFormatBGRA8Unorm   = 80,
    WMTPixelFormatRGBA8Unorm   = 70,
    WMTPixelFormatRGBA32Float  = 125,
    WMTPixelFormatDepth32Float = 252,
};

/* 颜色附件描述（简化版，Phase 2 只需 pixelFormat） */
struct WMTColorAttachment {
    int pixel_format;    /* WMTPixelFormat */
    int write_mask;      /* 默认 0xF（RGBA 全写） */
    int blending_enabled;
};

struct WMTRenderPipelineDesc {
    struct WMTColorAttachment colors[8];
    int color_count;              /* 实际使用的颜色附件数 */
    int depth_pixel_format;       /* 0 = None */
    int stencil_pixel_format;     /* 0 = None */
    int sample_count;             /* 默认 1 */
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

/* ============================================================
 *  Phase 3: MTLFence
 * ============================================================ */

mtl_handle_t MTLDevice_newFence(mtl_handle_t device);

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

#ifdef __cplusplus
}
#endif

#endif /* METAL_BRIDGE_H */
