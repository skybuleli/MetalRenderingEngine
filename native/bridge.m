/*
 * bridge.m — Metal 桥接层 ObjC 实现（Phase 1：compute 子集）
 *
 * 实现约束（详见 AGENTS.md §4.2）：
 *   - 每个 metal_xxx 函数仅做"类型转换 + ObjC 方法调用 + 句柄返回"
 *   - 每个函数 ≤ 20 行
 *   - newXxx 用 __bridge_retained 转移所有权给 C 层（计数 +1）
 *   - 引用计数走 CFRetain / CFRelease（Metal id 与 CFTypeRef 互通）
 *
 * 不允许：
 *   - 在本文件之外编写 ObjC（AGENTS.md §5.1）
 *   - 复杂业务逻辑（缓存、池化、状态机），由 C# 端承担
 */

#import <Metal/Metal.h>
#import <QuartzCore/QuartzCore.h>
#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>
#import <CoreFoundation/CoreFoundation.h>
#import <objc/message.h>
#include <pthread.h>
#include <unistd.h>
#include "bridge.h"

/* 把 mtl_handle_t 转回 ObjC id（不改变引用计数） */
#define H2ID(h)  ((__bridge id)(void *)(uintptr_t)(h))
/* 把新建的 ObjC 对象转交给 C 层（计数 +1） */
#define ID2H(o)  ((mtl_handle_t)(uintptr_t)(__bridge_retained void *)(o))

/* 工具：拷贝 NSString 到 UTF-8 缓冲，返回所需字节数（含 \0）。
 * buffer == NULL 或 max_length == 0 时仅返回需要的字节数。 */
static uint64_t copy_nsstring_utf8(NSString *str, char *buffer, uint64_t max_length) {
    if (str == nil) {
        if (buffer && max_length > 0) buffer[0] = '\0';
        return 1;
    }
    const char *utf8 = [str UTF8String];
    size_t need = strlen(utf8) + 1;
    if (buffer && max_length > 0) {
        size_t n = (need <= max_length) ? need : max_length;
        memcpy(buffer, utf8, n);
        buffer[n - 1] = '\0';
    }
    return (uint64_t)need;
}

/* 仅当调用方显式配置了 stencil 字段时，才创建 MTLStencilDescriptor。
 * 否则保持 nil，避免把“未配置 stencil”误解释成 CompareFunction=Never。 */
static BOOL has_stencil_descriptor(const struct WMTStencilDescriptor *desc) {
    return desc->stencilFailureOperation != 0 ||
           desc->depthFailureOperation != 0 ||
           desc->depthStencilPassOperation != 0 ||
           desc->stencilCompareFunction != 0 ||
           desc->readMask != 0 ||
           desc->writeMask != 0;
}

/* ============================================================
 *  引用计数
 * ============================================================ */

void NSObject_retain(mtl_handle_t obj) {
    if (obj != MTL_NULL_HANDLE) CFRetain((CFTypeRef)(void *)(uintptr_t)obj);
}

void NSObject_release(mtl_handle_t obj) {
    if (obj != MTL_NULL_HANDLE) CFRelease((CFTypeRef)(void *)(uintptr_t)obj);
}

/* ============================================================
 *  NSError
 * ============================================================ */

uint64_t NSError_localizedDescription(mtl_handle_t error, char *buffer, uint64_t max_length) {
    if (error == MTL_NULL_HANDLE) {
        if (buffer && max_length > 0) buffer[0] = '\0';
        return 1;
    }
    NSError *err = H2ID(error);
    return copy_nsstring_utf8([err localizedDescription], buffer, max_length);
}

/* ============================================================
 *  MTLDevice
 * ============================================================ */

mtl_handle_t MTLDevice_createSystemDefault(void) {
    id<MTLDevice> device = MTLCreateSystemDefaultDevice();
    return device ? ID2H(device) : MTL_NULL_HANDLE;
}

uint64_t MTLDevice_name(mtl_handle_t device, char *buffer, uint64_t max_length) {
    if (device == MTL_NULL_HANDLE) return 1;
    id<MTLDevice> dev = H2ID(device);
    return copy_nsstring_utf8([dev name], buffer, max_length);
}

int MTLDevice_hasUnifiedMemory(mtl_handle_t device) {
    if (device == MTL_NULL_HANDLE) return 0;
    id<MTLDevice> dev = H2ID(device);
    return [dev hasUnifiedMemory] ? 1 : 0;
}

uint64_t MTLDevice_recommendedMaxWorkingSetSize(mtl_handle_t device) {
    if (device == MTL_NULL_HANDLE) return 0;
    id<MTLDevice> dev = H2ID(device);
    return (uint64_t)[dev recommendedMaxWorkingSetSize];
}

/* ============================================================
 *  MTLLibrary / MTLFunction
 * ============================================================ */

mtl_handle_t MTLDevice_newLibrary(mtl_handle_t device,
                                  const void *data,
                                  uint64_t length,
                                  mtl_handle_t *err_out) {
    if (err_out) *err_out = MTL_NULL_HANDLE;
    if (device == MTL_NULL_HANDLE || data == NULL || length == 0) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    /* dispatch_data 用 GLOBAL 队列承载销毁回调即可；data 由调用方保活到此处。 */
    dispatch_data_t dd = dispatch_data_create(data, (size_t)length,
        dispatch_get_global_queue(QOS_CLASS_DEFAULT, 0), DISPATCH_DATA_DESTRUCTOR_DEFAULT);
    NSError *err = nil;
    id<MTLLibrary> lib = [dev newLibraryWithData:dd error:&err];
    if (!lib && err_out) *err_out = ID2H(err);
    return lib ? ID2H(lib) : MTL_NULL_HANDLE;
}

mtl_handle_t MTLLibrary_newFunctionWithName(mtl_handle_t library, const char *name) {
    if (library == MTL_NULL_HANDLE || name == NULL) return MTL_NULL_HANDLE;
    id<MTLLibrary> lib = H2ID(library);
    NSString *ns = [[NSString alloc] initWithUTF8String:name];
    id<MTLFunction> fn = [lib newFunctionWithName:ns];
    return fn ? ID2H(fn) : MTL_NULL_HANDLE;
}

/* Phase 9E: newLibraryWithSource —— 仅 SpirvCross 路径使用（AGENTS.md §3.3 例外） */
mtl_handle_t MTLDevice_newLibraryWithSource(mtl_handle_t device,
                                             const char *source,
                                             mtl_handle_t *err_out) {
    if (err_out) *err_out = MTL_NULL_HANDLE;
    if (device == MTL_NULL_HANDLE || source == NULL) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    NSString *src = [[NSString alloc] initWithUTF8String:source];
    MTLCompileOptions *opts = [[MTLCompileOptions alloc] init];
    opts.languageVersion = MTLLanguageVersion3_1;
    NSError *err = nil;
    id<MTLLibrary> lib = [dev newLibraryWithSource:src options:opts error:&err];
    if (!lib && err_out) *err_out = ID2H(err);
    return lib ? ID2H(lib) : MTL_NULL_HANDLE;
}

/* ============================================================
 *  MTLComputePipelineState
 * ============================================================ */

mtl_handle_t MTLDevice_newComputePipelineState(mtl_handle_t device,
                                               mtl_handle_t function,
                                               mtl_handle_t *err_out) {
    if (err_out) *err_out = MTL_NULL_HANDLE;
    if (device == MTL_NULL_HANDLE || function == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    id<MTLFunction> fn = H2ID(function);
    NSError *err = nil;
    id<MTLComputePipelineState> pso = [dev newComputePipelineStateWithFunction:fn error:&err];
    if (!pso && err_out) *err_out = ID2H(err);
    return pso ? ID2H(pso) : MTL_NULL_HANDLE;
}

uint64_t MTLComputePipelineState_maxTotalThreadsPerThreadgroup(mtl_handle_t pso) {
    if (pso == MTL_NULL_HANDLE) return 0;
    id<MTLComputePipelineState> p = H2ID(pso);
    return (uint64_t)[p maxTotalThreadsPerThreadgroup];
}

uint64_t MTLComputePipelineState_threadExecutionWidth(mtl_handle_t pso) {
    if (pso == MTL_NULL_HANDLE) return 0;
    id<MTLComputePipelineState> p = H2ID(pso);
    return (uint64_t)[p threadExecutionWidth];
}

/* ============================================================
 *  MTLBuffer
 * ============================================================ */

mtl_handle_t MTLDevice_newBuffer(mtl_handle_t device, const struct WMTBufferInfo *info) {
    if (device == MTL_NULL_HANDLE || info == NULL || info->length == 0) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    id<MTLBuffer> buf = [dev newBufferWithLength:(NSUInteger)info->length
                                         options:(MTLResourceOptions)info->options];
    return buf ? ID2H(buf) : MTL_NULL_HANDLE;
}

void *MTLBuffer_contents(mtl_handle_t buffer) {
    if (buffer == MTL_NULL_HANDLE) return NULL;
    id<MTLBuffer> b = H2ID(buffer);
    return [b contents];
}

uint64_t MTLBuffer_gpuAddress(mtl_handle_t buffer) {
    if (buffer == MTL_NULL_HANDLE) return 0;
    id<MTLBuffer> b = H2ID(buffer);
    return (uint64_t)[b gpuAddress];
}

void MTLBuffer_didModifyRange(mtl_handle_t buffer, uint64_t offset, uint64_t length) {
    if (buffer == MTL_NULL_HANDLE) return;
    id<MTLBuffer> b = H2ID(buffer);
    /* Shared / Private 模式下该方法仍可调用但是 no-op；显式检查避免 Metal 验证层告警 */
    if (([b storageMode] == MTLStorageModeManaged) && length > 0) {
        [b didModifyRange:NSMakeRange((NSUInteger)offset, (NSUInteger)length)];
    }
}

/* ============================================================
 *  MTLCommandQueue / MTLCommandBuffer
 * ============================================================ */

mtl_handle_t MTLDevice_newCommandQueue(mtl_handle_t device) {
    if (device == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    id<MTLCommandQueue> q = [dev newCommandQueue];
    return q ? ID2H(q) : MTL_NULL_HANDLE;
}

mtl_handle_t MTLCommandQueue_commandBuffer(mtl_handle_t queue) {
    if (queue == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLCommandQueue> q = H2ID(queue);
    /* commandBuffer 返回 autoreleased，CFRetain 一份给 C 层 */
    id<MTLCommandBuffer> cb = [q commandBuffer];
    return cb ? ID2H(cb) : MTL_NULL_HANDLE;
}

void MTLCommandBuffer_commit(mtl_handle_t cmdbuf) {
    if (cmdbuf == MTL_NULL_HANDLE) return;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    [cb commit];
}

void MTLCommandBuffer_waitUntilCompleted(mtl_handle_t cmdbuf) {
    if (cmdbuf == MTL_NULL_HANDLE) return;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    [cb waitUntilCompleted];
}

int MTLCommandBuffer_status(mtl_handle_t cmdbuf) {
    if (cmdbuf == MTL_NULL_HANDLE) return WMTCommandBufferStatusNotEnqueued;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    return (int)[cb status];
}

mtl_handle_t MTLCommandBuffer_error(mtl_handle_t cmdbuf) {
    if (cmdbuf == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    NSError *err = [cb error];
    return err ? ID2H(err) : MTL_NULL_HANDLE;
}

/* ============================================================
 *  MTLComputeCommandEncoder
 * ============================================================ */

mtl_handle_t MTLCommandBuffer_computeCommandEncoder(mtl_handle_t cmdbuf) {
    if (cmdbuf == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
    return enc ? ID2H(enc) : MTL_NULL_HANDLE;
}

void MTLComputeCommandEncoder_setComputePipelineState(mtl_handle_t encoder, mtl_handle_t pso) {
    if (encoder == MTL_NULL_HANDLE || pso == MTL_NULL_HANDLE) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    [e setComputePipelineState:H2ID(pso)];
}

void MTLComputeCommandEncoder_setBuffer(mtl_handle_t encoder, mtl_handle_t buffer, uint64_t offset, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    [e setBuffer:H2ID(buffer) offset:(NSUInteger)offset atIndex:(NSUInteger)index];
}

void MTLComputeCommandEncoder_setBytes(mtl_handle_t encoder, const void *bytes, uint64_t length, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE || bytes == NULL) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    [e setBytes:bytes length:(NSUInteger)length atIndex:(NSUInteger)index];
}

void MTLComputeCommandEncoder_setTexture(mtl_handle_t encoder, mtl_handle_t texture, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    [e setTexture:H2ID(texture) atIndex:(NSUInteger)index];
}

void MTLComputeCommandEncoder_useResource(mtl_handle_t encoder, mtl_handle_t resource, uint32_t usage) {
    if (encoder == MTL_NULL_HANDLE || resource == MTL_NULL_HANDLE) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    [e useResource:(id<MTLResource>)H2ID(resource) usage:(MTLResourceUsage)usage];
}

void MTLComputeCommandEncoder_dispatchThreadgroups(mtl_handle_t encoder,
                                                   struct WMTSize groups,
                                                   struct WMTSize threads) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    MTLSize g = MTLSizeMake((NSUInteger)groups.width, (NSUInteger)groups.height, (NSUInteger)groups.depth);
    MTLSize t = MTLSizeMake((NSUInteger)threads.width, (NSUInteger)threads.height, (NSUInteger)threads.depth);
    [e dispatchThreadgroups:g threadsPerThreadgroup:t];
}

void MTLComputeCommandEncoder_endEncoding(mtl_handle_t encoder) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    [e endEncoding];
}

/* ============================================================
 *  Phase 7A: MTLDepthStencilState
 * ============================================================ */

mtl_handle_t MTLDevice_newDepthStencilState(mtl_handle_t device,
                                             const struct WMTDepthStencilDesc *desc) {
    if (device == MTL_NULL_HANDLE || desc == NULL) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);

    MTLDepthStencilDescriptor *dsd = [[MTLDepthStencilDescriptor alloc] init];
    dsd.depthCompareFunction = (MTLCompareFunction)desc->depthCompareFunction;
    dsd.depthWriteEnabled = desc->depthWriteEnabled ? YES : NO;

    if (has_stencil_descriptor(&desc->frontFaceStencil)) {
        MTLStencilDescriptor *front = [[MTLStencilDescriptor alloc] init];
        front.stencilFailureOperation =
            (MTLStencilOperation)desc->frontFaceStencil.stencilFailureOperation;
        front.depthFailureOperation =
            (MTLStencilOperation)desc->frontFaceStencil.depthFailureOperation;
        front.depthStencilPassOperation =
            (MTLStencilOperation)desc->frontFaceStencil.depthStencilPassOperation;
        front.stencilCompareFunction =
            (MTLCompareFunction)desc->frontFaceStencil.stencilCompareFunction;
        front.readMask = desc->frontFaceStencil.readMask;
        front.writeMask = desc->frontFaceStencil.writeMask;
        dsd.frontFaceStencil = front;
    }

    if (has_stencil_descriptor(&desc->backFaceStencil)) {
        MTLStencilDescriptor *back = [[MTLStencilDescriptor alloc] init];
        back.stencilFailureOperation =
            (MTLStencilOperation)desc->backFaceStencil.stencilFailureOperation;
        back.depthFailureOperation =
            (MTLStencilOperation)desc->backFaceStencil.depthFailureOperation;
        back.depthStencilPassOperation =
            (MTLStencilOperation)desc->backFaceStencil.depthStencilPassOperation;
        back.stencilCompareFunction =
            (MTLCompareFunction)desc->backFaceStencil.stencilCompareFunction;
        back.readMask = desc->backFaceStencil.readMask;
        back.writeMask = desc->backFaceStencil.writeMask;
        dsd.backFaceStencil = back;
    }

    id<MTLDepthStencilState> state = [dev newDepthStencilStateWithDescriptor:dsd];
    return state ? ID2H(state) : MTL_NULL_HANDLE;
}

/* ============================================================
 *  MTLRenderPipelineState
 * ============================================================ */

mtl_handle_t MTLDevice_newRenderPipelineState(mtl_handle_t device,
                                              mtl_handle_t vertex_func,
                                              mtl_handle_t fragment_func,
                                              const struct WMTRenderPipelineDesc *desc,
                                              mtl_handle_t *err_out) {
    if (err_out) *err_out = MTL_NULL_HANDLE;
    if (device == MTL_NULL_HANDLE || vertex_func == MTL_NULL_HANDLE || fragment_func == MTL_NULL_HANDLE || desc == NULL)
        return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    id<MTLFunction> vert = H2ID(vertex_func);
    id<MTLFunction> frag = H2ID(fragment_func);

    MTLRenderPipelineDescriptor *pd = [[MTLRenderPipelineDescriptor alloc] init];
    pd.vertexFunction = vert;
    pd.fragmentFunction = frag;

    for (int i = 0; i < desc->color_count && i < 8; i++) {
        pd.colorAttachments[i].pixelFormat = (MTLPixelFormat)desc->colors[i].pixel_format;
        pd.colorAttachments[i].writeMask = (MTLColorWriteMask)desc->colors[i].write_mask;
        if (desc->colors[i].blending_enabled) {
            pd.colorAttachments[i].blendingEnabled = YES;
            pd.colorAttachments[i].sourceRGBBlendFactor = (MTLBlendFactor)desc->colors[i].src_rgb_blend_factor;
            pd.colorAttachments[i].destinationRGBBlendFactor = (MTLBlendFactor)desc->colors[i].dst_rgb_blend_factor;
            pd.colorAttachments[i].sourceAlphaBlendFactor = (MTLBlendFactor)desc->colors[i].src_alpha_blend_factor;
            pd.colorAttachments[i].destinationAlphaBlendFactor = (MTLBlendFactor)desc->colors[i].dst_alpha_blend_factor;
            pd.colorAttachments[i].rgbBlendOperation = (MTLBlendOperation)desc->colors[i].rgb_blend_op;
            pd.colorAttachments[i].alphaBlendOperation = (MTLBlendOperation)desc->colors[i].alpha_blend_op;
        }
    }
    pd.depthAttachmentPixelFormat = (MTLPixelFormat)desc->depth_pixel_format;
    pd.stencilAttachmentPixelFormat = (MTLPixelFormat)desc->stencil_pixel_format;
    pd.rasterSampleCount = (NSUInteger)desc->sample_count;

    /* Phase 7F: 消费 vertexDescriptor */
    const struct WMTVertexDescriptor *vd = &desc->vertex_descriptor;
    if (vd->attributeCount > 0 || vd->layoutCount > 0) {
        MTLVertexDescriptor *mtlVD = [[MTLVertexDescriptor alloc] init];
        for (uint32_t i = 0; i < vd->attributeCount && i < 8; i++) {
            mtlVD.attributes[i].format = (MTLVertexFormat)vd->attributes[i].format;
            mtlVD.attributes[i].offset = (NSUInteger)vd->attributes[i].offset;
            mtlVD.attributes[i].bufferIndex = (NSUInteger)vd->attributes[i].bufferIndex;
        }
        for (uint32_t i = 0; i < vd->layoutCount && i < 8; i++) {
            mtlVD.layouts[i].stride = (NSUInteger)vd->layouts[i].stride;
            mtlVD.layouts[i].stepFunction = (MTLVertexStepFunction)vd->layouts[i].stepFunction;
            mtlVD.layouts[i].stepRate = (NSUInteger)vd->layouts[i].stepRate;
        }
        pd.vertexDescriptor = mtlVD;
    }

    NSError *err = nil;
    id<MTLRenderPipelineState> pso = [dev newRenderPipelineStateWithDescriptor:pd error:&err];
    if (!pso && err_out) *err_out = ID2H(err);
    return pso ? ID2H(pso) : MTL_NULL_HANDLE;
}

/* ============================================================
 *  MTLRenderCommandEncoder
 * ============================================================ */

mtl_handle_t MTLCommandBuffer_renderCommandEncoder(mtl_handle_t cmdbuf,
                                                    const struct WMTRenderPassDesc *desc) {
    if (cmdbuf == MTL_NULL_HANDLE || desc == NULL) return MTL_NULL_HANDLE;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);

    MTLRenderPassDescriptor *rpd = [MTLRenderPassDescriptor renderPassDescriptor];
    for (int i = 0; i < 8; i++) {
        if (desc->colors[i].texture != MTL_NULL_HANDLE) {
            rpd.colorAttachments[i].texture = H2ID(desc->colors[i].texture);
            rpd.colorAttachments[i].loadAction  = (MTLLoadAction)desc->colors[i].load_action;
            rpd.colorAttachments[i].storeAction = (MTLStoreAction)desc->colors[i].store_action;
            rpd.colorAttachments[i].clearColor = MTLClearColorMake(
                desc->colors[i].clear_color.r,
                desc->colors[i].clear_color.g,
                desc->colors[i].clear_color.b,
                desc->colors[i].clear_color.a);
            /* Phase 7K: MSAA resolve */
            if (desc->colors[i].resolve_texture != MTL_NULL_HANDLE) {
                rpd.colorAttachments[i].resolveTexture = H2ID(desc->colors[i].resolve_texture);
            }
        }
    }
    if (desc->depth.texture != MTL_NULL_HANDLE) {
        rpd.depthAttachment.texture = H2ID(desc->depth.texture);
        rpd.depthAttachment.loadAction  = (MTLLoadAction)desc->depth.load_action;
        rpd.depthAttachment.storeAction = (MTLStoreAction)desc->depth.store_action;
        rpd.depthAttachment.clearDepth = desc->depth.clear_depth;
    }
    if (desc->stencil.texture != MTL_NULL_HANDLE) {
        rpd.stencilAttachment.texture = H2ID(desc->stencil.texture);
        rpd.stencilAttachment.loadAction  = (MTLLoadAction)desc->stencil.load_action;
        rpd.stencilAttachment.storeAction = (MTLStoreAction)desc->stencil.store_action;
        rpd.stencilAttachment.clearStencil = desc->stencil.clear_stencil;
    }

    id<MTLRenderCommandEncoder> enc = [cb renderCommandEncoderWithDescriptor:rpd];
    return enc ? ID2H(enc) : MTL_NULL_HANDLE;
}

void MTLRenderCommandEncoder_setRenderPipelineState(mtl_handle_t encoder, mtl_handle_t pso) {
    if (encoder == MTL_NULL_HANDLE || pso == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e setRenderPipelineState:H2ID(pso)];
}

void MTLRenderCommandEncoder_setVertexBuffer(mtl_handle_t encoder, mtl_handle_t buffer,
                                              uint64_t offset, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e setVertexBuffer:H2ID(buffer) offset:(NSUInteger)offset atIndex:(NSUInteger)index];
}

void MTLRenderCommandEncoder_setViewport(mtl_handle_t encoder,
                                          float x, float y, float w, float h,
                                          float znear, float zfar) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLViewport vp = { x, y, w, h, znear, zfar };
    [e setViewport:vp];
}

void MTLRenderCommandEncoder_setScissorRect(mtl_handle_t encoder,
                                             int x, int y, int w, int h) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLScissorRect scissor = { (NSUInteger)x, (NSUInteger)y, (NSUInteger)w, (NSUInteger)h };
    [e setScissorRect:scissor];
}

void MTLRenderCommandEncoder_drawPrimitives(mtl_handle_t encoder,
                                             int primitive_type,
                                             uint64_t vertex_start,
                                             uint64_t vertex_count) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLPrimitiveType pt = (primitive_type == 0) ? MTLPrimitiveTypeTriangle : MTLPrimitiveTypeTriangle;
    [e drawPrimitives:pt vertexStart:(NSUInteger)vertex_start vertexCount:(NSUInteger)vertex_count];
}

void MTLRenderCommandEncoder_drawPrimitivesInstanced(mtl_handle_t encoder,
                                                      int primitive_type,
                                                      uint64_t vertex_start,
                                                      uint64_t vertex_count,
                                                      uint64_t instance_count) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLPrimitiveType pt = (primitive_type == 0) ? MTLPrimitiveTypeTriangle : MTLPrimitiveTypeTriangle;
    [e drawPrimitives:pt
          vertexStart:(NSUInteger)vertex_start
          vertexCount:(NSUInteger)vertex_count
        instanceCount:(NSUInteger)instance_count];
}

void MTLRenderCommandEncoder_drawIndexedPrimitives(mtl_handle_t encoder,
                                                    int primitive_type,
                                                    uint64_t index_count,
                                                    int index_type,
                                                    mtl_handle_t index_buffer,
                                                    uint64_t index_buffer_offset) {
    if (encoder == MTL_NULL_HANDLE || index_buffer == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLPrimitiveType pt = (primitive_type == 0) ? MTLPrimitiveTypeTriangle : MTLPrimitiveTypeTriangle;
    MTLIndexType it = (index_type == 0) ? MTLIndexTypeUInt16 : MTLIndexTypeUInt32;
    [e drawIndexedPrimitives:pt
                  indexCount:(NSUInteger)index_count
                   indexType:it
                 indexBuffer:H2ID(index_buffer)
           indexBufferOffset:(NSUInteger)index_buffer_offset];
}

void MTLRenderCommandEncoder_drawIndexedPrimitivesInstanced(mtl_handle_t encoder,
                                                             int primitive_type,
                                                             uint64_t index_count,
                                                             int index_type,
                                                             mtl_handle_t index_buffer,
                                                             uint64_t index_buffer_offset,
                                                             uint64_t instance_count) {
    if (encoder == MTL_NULL_HANDLE || index_buffer == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLPrimitiveType pt = (primitive_type == 0) ? MTLPrimitiveTypeTriangle : MTLPrimitiveTypeTriangle;
    MTLIndexType it = (index_type == 0) ? MTLIndexTypeUInt16 : MTLIndexTypeUInt32;
    [e drawIndexedPrimitives:pt
                  indexCount:(NSUInteger)index_count
                   indexType:it
                 indexBuffer:H2ID(index_buffer)
           indexBufferOffset:(NSUInteger)index_buffer_offset
               instanceCount:(NSUInteger)instance_count];
}

void MTLRenderCommandEncoder_drawPrimitivesIndirect(mtl_handle_t encoder,
                                                     int primitive_type,
                                                     mtl_handle_t indirect_buffer,
                                                     uint64_t indirect_buffer_offset) {
    if (encoder == MTL_NULL_HANDLE || indirect_buffer == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLPrimitiveType pt = (primitive_type == 0) ? MTLPrimitiveTypeTriangle : MTLPrimitiveTypeTriangle;
    [e drawPrimitives:pt
       indirectBuffer:H2ID(indirect_buffer)
 indirectBufferOffset:(NSUInteger)indirect_buffer_offset];
}

void MTLRenderCommandEncoder_drawIndexedPrimitivesIndirect(mtl_handle_t encoder,
                                                            int primitive_type,
                                                            int index_type,
                                                            mtl_handle_t index_buffer,
                                                            mtl_handle_t indirect_buffer,
                                                            uint64_t indirect_buffer_offset) {
    if (encoder == MTL_NULL_HANDLE || index_buffer == MTL_NULL_HANDLE || indirect_buffer == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    MTLPrimitiveType pt = (primitive_type == 0) ? MTLPrimitiveTypeTriangle : MTLPrimitiveTypeTriangle;
    MTLIndexType it = (index_type == 0) ? MTLIndexTypeUInt16 : MTLIndexTypeUInt32;
    [e drawIndexedPrimitives:pt
                   indexType:it
                 indexBuffer:H2ID(index_buffer)
           indexBufferOffset:0
              indirectBuffer:H2ID(indirect_buffer)
    indirectBufferOffset:(NSUInteger)indirect_buffer_offset];
}

void MTLRenderCommandEncoder_endEncoding(mtl_handle_t encoder) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e endEncoding];
}

/* ============================================================
 *  CAMetalLayer / CAMetalDrawable
 * ============================================================ */

/* CAMetalLayer_fromView removed — dead code.
 * NSWindow + CAMetalLayer 创建统一由 Cocoa_CreateMetalWindow 处理。
 * 参见 src/MetalRenderingEngine.Core/Platform/CocoaWindow.cs。
 */

void CAMetalLayer_setDevice(mtl_handle_t layer, mtl_handle_t device) {
    if (layer == MTL_NULL_HANDLE || device == MTL_NULL_HANDLE) return;
    CAMetalLayer *ml = H2ID(layer);
    ml.device = H2ID(device);
}

void CAMetalLayer_setPixelFormat(mtl_handle_t layer, int pixel_format) {
    if (layer == MTL_NULL_HANDLE) return;
    CAMetalLayer *ml = H2ID(layer);
    ml.pixelFormat = (MTLPixelFormat)pixel_format;
}

void CAMetalLayer_setDrawableSize(mtl_handle_t layer, float width, float height) {
    if (layer == MTL_NULL_HANDLE) return;
    CAMetalLayer *ml = H2ID(layer);
    ml.drawableSize = CGSizeMake(width, height);
}

void CAMetalLayer_setDisplaySyncEnabled(mtl_handle_t layer, int enabled) {
    if (layer == MTL_NULL_HANDLE) return;
    CAMetalLayer *ml = H2ID(layer);
    if (@available(macOS 14.0, *)) {
        ml.displaySyncEnabled = (enabled != 0);
    }
}

void CAMetalLayer_setMaximumDrawableCount(mtl_handle_t layer, int count) {
    if (layer == MTL_NULL_HANDLE) return;
    CAMetalLayer *ml = H2ID(layer);
    ml.maximumDrawableCount = (NSUInteger)count;
}

mtl_handle_t CAMetalLayer_nextDrawable(mtl_handle_t layer) {
    if (layer == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    CAMetalLayer *ml = H2ID(layer);
    id<CAMetalDrawable> drawable = [ml nextDrawable];
    return drawable ? ID2H(drawable) : MTL_NULL_HANDLE;
}

mtl_handle_t CAMetalDrawable_texture(mtl_handle_t drawable) {
    if (drawable == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<CAMetalDrawable> d = H2ID(drawable);
    id<MTLTexture> tex = d.texture;
    return tex ? ID2H(tex) : MTL_NULL_HANDLE;
}

void MTLCommandBuffer_presentDrawable(mtl_handle_t cmdbuf, mtl_handle_t drawable) {
    if (cmdbuf == MTL_NULL_HANDLE || drawable == MTL_NULL_HANDLE) return;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    id<CAMetalDrawable> d = H2ID(drawable);
    [cb presentDrawable:d];
}

mtl_handle_t Cocoa_CreateMetalWindow(const char *title, float width, float height, mtl_handle_t *out_layer) {
    if (out_layer) *out_layer = MTL_NULL_HANDLE;
    NSRect frame = NSMakeRect(0, 0, width, height);
    NSWindow *window = [[NSWindow alloc] initWithContentRect:frame
                                                   styleMask:(NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable)
                                                     backing:NSBackingStoreBuffered
                                                       defer:NO];
    [window setTitle:[[NSString alloc] initWithUTF8String:title ? title : "Metal"]];
    [window center];
    [window makeKeyAndOrderFront:nil];

    NSView *view = [window contentView];
    view.wantsLayer = YES;
    CAMetalLayer *layer = [CAMetalLayer layer];
    view.layer = layer;

    if (out_layer) *out_layer = ID2H(layer);
    return ID2H(window);
}

/* 轮询一次 Cocoa 事件队列；返回 0 表示窗口仍打开，1 表示用户请求关闭（按 ESC 或关窗） */
int Cocoa_PollEvents(void) {
    NSEvent *event = [NSApp nextEventMatchingMask:NSEventMaskAny
                                        untilDate:[NSDate distantPast]
                                           inMode:NSDefaultRunLoopMode
                                          dequeue:YES];
    if (event) {
        if ([event type] == NSEventTypeKeyDown) {
            NSString *chars = [event characters];
            if ([chars length] > 0 && [chars characterAtIndex:0] == 27) { // ESC
                return 1;
            }
        }
        [NSApp sendEvent:event];
    }
    /* 检测用户点击关闭按钮后窗口已不可见的情况 */
    NSWindow *keyWin = [NSApp keyWindow];
    if (keyWin && ![keyWin isVisible]) return 1;
    return 0;
}

mtl_handle_t Cocoa_WindowContentView(mtl_handle_t window) {
    if (window == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    NSWindow *win = H2ID(window);
    NSView *view = [win contentView];
    /* 不增加引用计数：view 由 window 持有，C# 端仅在窗口存活期间使用 */
    return (mtl_handle_t)(uintptr_t)(__bridge void *)view;
}

void Cocoa_ViewDrawableSize(mtl_handle_t view, float *out_width, float *out_height) {
    if (view == MTL_NULL_HANDLE || !out_width || !out_height) return;
    NSView *nsView = H2ID(view);
    /* convertSizeToBacking 将逻辑坐标转换为像素坐标（HiDPI） */
    NSSize pixelSize = [nsView convertSizeToBacking:nsView.bounds.size];
    *out_width  = (float)pixelSize.width;
    *out_height = (float)pixelSize.height;
}

/* ============================================================
 *  MTLTexture（只读回读）
 * ============================================================ */

uint64_t MTLTexture_width(mtl_handle_t texture) {
    if (texture == MTL_NULL_HANDLE) return 0;
    id<MTLTexture> tex = H2ID(texture);
    return (uint64_t)tex.width;
}

uint64_t MTLTexture_height(mtl_handle_t texture) {
    if (texture == MTL_NULL_HANDLE) return 0;
    id<MTLTexture> tex = H2ID(texture);
    return (uint64_t)tex.height;
}

/* 查 MTLPixelFormat 每像素字节数（仅覆盖本项目用到的格式）。
 * MTLTexture 协议无 bytesPerRow 属性，需根据 pixelFormat + width 自算。 */
static NSUInteger pixel_format_bytes_per_pixel(MTLPixelFormat pf) {
    switch (pf) {
    case MTLPixelFormatR8Unorm:        return 1;
    case MTLPixelFormatRGBA8Unorm:
    case MTLPixelFormatBGRA8Unorm:     return 4;
    case MTLPixelFormatRGBA16Float:    return 8;
    case MTLPixelFormatRGBA32Float:    return 16;
    case MTLPixelFormatDepth32Float:   return 4;
    case MTLPixelFormatDepth32Float_Stencil8: return 5;  /* packed: 32-bit depth + 8-bit stencil (按 8 字节对齐) */
    default:                           return 4;  /* 保守默认 */
    }
}

uint64_t MTLTexture_bytesPerRow(mtl_handle_t texture, uint64_t mip_level) {
    if (texture == MTL_NULL_HANDLE) return 0;
    id<MTLTexture> tex = H2ID(texture);
    NSUInteger width = [tex width];
    NSUInteger bpp = pixel_format_bytes_per_pixel([tex pixelFormat]);
    /* mip_level > 0 时宽度右移，此处仅 mipmap level 0 准确；高 level 需调用方注意 */
    for (uint64_t i = 0; i < mip_level && width > 1; i++) width = (width + 1) / 2;
    return (uint64_t)(width * bpp);
}

/* 获取 texture 的 GPU 资源标识（MTLResourceID._impl）。
 * 对应 Metal: [MTLTexture gpuResourceID]。
 * 用于 Phase 10 描述符堆绑定：把 texture 写入 MSC 自定义描述符堆条目。
 * texture 需具备 ShaderRead usage（创建时 usage 位含 1）。 */
uint64_t MTLTexture_gpuResourceID(mtl_handle_t texture) {
    if (texture == MTL_NULL_HANDLE) return 0;
    id<MTLTexture> tex = H2ID(texture);
    return (uint64_t)[tex gpuResourceID]._impl;
}

uint64_t MTLTexture_getBytes(mtl_handle_t texture, void *dst, uint64_t dst_size, uint64_t mip_level) {
    if (texture == MTL_NULL_HANDLE || dst == NULL || dst_size == 0) return 0;
    id<MTLTexture> tex = H2ID(texture);
    NSUInteger width = [tex width];
    NSUInteger height = [tex height];
    NSUInteger bpp = pixel_format_bytes_per_pixel([tex pixelFormat]);
    /* 简化：仅在 mip level 0 准确；高 mipmap 需按 (w+1)/2 递减 */
    NSUInteger level = (NSUInteger)mip_level;
    NSUInteger w = width, h = height;
    for (NSUInteger i = 0; i < level; i++) { if (w > 1) w = (w + 1) / 2; if (h > 1) h = (h + 1) / 2; }
    NSUInteger bytesPerRow = w * bpp;
    NSUInteger requiredSize = bytesPerRow * h;
    if (dst_size < requiredSize) return 0;
    MTLRegion region = MTLRegionMake2D(0, 0, w, h);
    [tex getBytes:dst bytesPerRow:bytesPerRow fromRegion:region mipmapLevel:(NSUInteger)mip_level];
    return (uint64_t)requiredSize;
}

/* ============================================================
 *  Phase 3: MTLTexture 创建与写入
 * ============================================================ */

mtl_handle_t MTLDevice_newTexture(mtl_handle_t device, const struct WMTTextureInfo *info) {
    if (device == MTL_NULL_HANDLE || info == NULL) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    MTLTextureDescriptor *td = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:(MTLPixelFormat)info->pixel_format
                                                                                   width:(NSUInteger)info->width
                                                                                  height:(NSUInteger)info->height
                                                                              mipmapped:(info->mipmap_levels > 1)];
    td.textureType = (info->texture_type == 5) ? MTLTextureType2DMultisample : MTLTextureType2D;
    td.storageMode = (MTLStorageMode)((info->options >> 4) & 0xF);
    td.usage = (MTLTextureUsage)info->usage;
    td.mipmapLevelCount = (NSUInteger)(info->mipmap_levels > 0 ? info->mipmap_levels : 1);
    td.sampleCount = (NSUInteger)(info->sample_count > 0 ? info->sample_count : 1);
    id<MTLTexture> tex = [dev newTextureWithDescriptor:td];
    return tex ? ID2H(tex) : MTL_NULL_HANDLE;
}

void MTLTexture_replaceRegion(mtl_handle_t texture, struct WMTOrigin origin, struct WMTSize size,
                               uint64_t mip_level, uint64_t slice,
                               const void *data, uint64_t bytes_per_row, uint64_t bytes_per_image) {
    if (texture == MTL_NULL_HANDLE || data == NULL) return;
    id<MTLTexture> tex = H2ID(texture);
    MTLRegion region = { {(NSUInteger)origin.x, (NSUInteger)origin.y, (NSUInteger)origin.z},
                         {(NSUInteger)size.width, (NSUInteger)size.height, (NSUInteger)size.depth} };
    [tex replaceRegion:region mipmapLevel:(NSUInteger)mip_level slice:(NSUInteger)slice
             withBytes:data bytesPerRow:(NSUInteger)bytes_per_row bytesPerImage:(NSUInteger)bytes_per_image];
}

/* ============================================================
 *  Phase 3: MTLSamplerState
 * ============================================================ */

mtl_handle_t MTLDevice_newSamplerState(mtl_handle_t device, const struct WMTSamplerInfo *info) {
    if (device == MTL_NULL_HANDLE || info == NULL) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    MTLSamplerDescriptor *sd = [[MTLSamplerDescriptor alloc] init];
    sd.minFilter = (MTLSamplerMinMagFilter)info->min_filter;
    sd.magFilter = (MTLSamplerMinMagFilter)info->mag_filter;
    sd.mipFilter = (MTLSamplerMipFilter)info->mip_filter;
    sd.sAddressMode = (MTLSamplerAddressMode)info->s_address_mode;
    sd.tAddressMode = (MTLSamplerAddressMode)info->t_address_mode;
    sd.rAddressMode = (MTLSamplerAddressMode)info->r_address_mode;
    sd.maxAnisotropy = (NSUInteger)info->max_anisotropy;
    sd.lodMinClamp = info->lod_min_clamp;
    sd.lodMaxClamp = info->lod_max_clamp;
    if (info->compare_function >= 0) sd.compareFunction = (MTLCompareFunction)info->compare_function;
    /* Phase 10: sampler 进 MSC 描述符堆必需 supportArgumentBuffers=YES，
     * 否则 [sampler gpuResourceID] 返回 0（DXMT winemetal_unix.c:195-198 验证）。 */
    sd.supportArgumentBuffers = YES;
    id<MTLSamplerState> sampler = [dev newSamplerStateWithDescriptor:sd];
    return sampler ? ID2H(sampler) : MTL_NULL_HANDLE;
}

/* 获取 sampler 的 GPU 资源标识（MTLResourceID._impl）。
 * 对应 Metal: [MTLSamplerState gpuResourceID]。
 * 必须在创建 sampler 时设 supportArgumentBuffers=YES（见上函数），否则返回 0。
 * 用于 Phase 10 描述符堆绑定：把 sampler 写入 MSC 自定义描述符堆条目。 */
uint64_t MTLSamplerState_gpuResourceID(mtl_handle_t sampler) {
    if (sampler == MTL_NULL_HANDLE) return 0;
    id<MTLSamplerState> s = H2ID(sampler);
    return (uint64_t)[s gpuResourceID]._impl;
}

/* ============================================================
 *  Phase 3: MTLFence
 * ============================================================ */

mtl_handle_t MTLDevice_newFence(mtl_handle_t device) {
    if (device == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    id<MTLFence> fence = [dev newFence];
    return fence ? ID2H(fence) : MTL_NULL_HANDLE;
}

/* ============================================================
 *  Phase 6: MTLSharedEvent + SharedEventListener
 *  参照 DXMT winemetal_unix.c:2471-2498 的 listener + CFRunLoop 模式。
 * ============================================================ */

mtl_handle_t MTLDevice_newSharedEvent(mtl_handle_t device) {
    if (device == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLDevice> dev = H2ID(device);
    id<MTLSharedEvent> evt = [dev newSharedEvent];
    return evt ? ID2H(evt) : MTL_NULL_HANDLE;
}

uint64_t MTLSharedEvent_signaledValue(mtl_handle_t event) {
    if (event == MTL_NULL_HANDLE) return 0;
    id<MTLSharedEvent> e = H2ID(event);
    return (uint64_t)[e signaledValue];
}

int MTLSharedEvent_waitUntilSignaledValue(mtl_handle_t event, uint64_t value, uint64_t timeout_ms) {
    if (event == MTL_NULL_HANDLE) return 0;
    id<MTLSharedEvent> e = H2ID(event);
    /* timeout_ms=0 在本封装里表示无限等待（Metal API 用 NSUInteger max） */
    NSUInteger timeout = (timeout_ms == 0) ? NSUIntegerMax : (NSUInteger)timeout_ms;
    BOOL ok = [e waitUntilSignaledValue:value timeoutMS:timeout];
    return ok ? 1 : 0;
}

void MTLCommandBuffer_encodeSignalEvent(mtl_handle_t cmdbuf, mtl_handle_t event, uint64_t value) {
    if (cmdbuf == MTL_NULL_HANDLE || event == MTL_NULL_HANDLE) return;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    [cb encodeSignalEvent:H2ID(event) value:value];
}

void MTLCommandBuffer_encodeWaitForEvent(mtl_handle_t cmdbuf, mtl_handle_t event, uint64_t value) {
    if (cmdbuf == MTL_NULL_HANDLE || event == MTL_NULL_HANDLE) return;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    [cb encodeWaitForEvent:H2ID(event) value:value];
}

/* SharedEventListener：内部持有 ObjC listener + 后台 CFRunLoop 线程。
 * 用结构体打包，release 时停止 runloop 并 join 线程。 */
typedef struct {
    MTLSharedEventListener *listener;
    CFRunLoopRef runloop;
    pthread_t thread;
    _Bool stopped;
} shared_event_listener_state;

static void *listener_thread_func(void *arg) {
    shared_event_listener_state *st = (shared_event_listener_state *)arg;
    st->runloop = CFRunLoopGetCurrent();
    CFRetain(st->runloop);
    /* 加一个 dummy source 保活 runloop（参照 DXMT winemetal_unix.c:2480-2483） */
    CFRunLoopSourceContext ctx = {0};
    CFRunLoopSourceRef src = CFRunLoopSourceCreate(NULL, 0, &ctx);
    CFRunLoopAddSource(st->runloop, src, kCFRunLoopCommonModes);
    CFRunLoopRun();
    CFRelease(src);
    CFRelease(st->runloop);
    st->runloop = NULL;
    return NULL;
}

mtl_handle_t MTLSharedEventListener_create(void) {
    shared_event_listener_state *st = calloc(1, sizeof(*st));
    if (!st) return MTL_NULL_HANDLE;
    st->listener = [[MTLSharedEventListener alloc] init];
    st->stopped = 0;
    /* 启动后台线程跑 CFRunLoop（承载 notifyListener 回调） */
    pthread_create(&st->thread, NULL, listener_thread_func, st);
    /* 等线程设好 runloop（notifyListener 需要它就绪） */
    while (st->runloop == NULL) { usleep(100); }
    return (mtl_handle_t)(uintptr_t)st;
}

void MTLSharedEventListener_release(mtl_handle_t listener) {
    if (listener == MTL_NULL_HANDLE) return;
    shared_event_listener_state *st = (shared_event_listener_state *)(void *)(uintptr_t)listener;
    st->stopped = 1;
    CFRunLoopStop(st->runloop);
    pthread_join(st->thread, NULL);
    /* ARC 下用 CFRelease 释放 ObjC 对象（toll-free bridged） */
    if (st->listener) CFRelease((__bridge CFTypeRef)st->listener);
    free(st);
}

void MTLSharedEvent_notifyListener(mtl_handle_t event, mtl_handle_t listener,
                                    uint64_t value, shared_event_callback_t callback, void *user_data) {
    if (event == MTL_NULL_HANDLE || listener == MTL_NULL_HANDLE || callback == NULL) return;
    id<MTLSharedEvent> e = H2ID(event);
    shared_event_listener_state *st = (shared_event_listener_state *)(void *)(uintptr_t)listener;
    /* ObjC block 桥接 C 回调：block 捕获 callback + user_data，在 listener 线程触发 */
    [e notifyListener:st->listener
              atValue:value
                block:^(id<MTLSharedEvent> _evt, uint64_t _value) {
        callback(user_data, _value);
    }];
}

/* ============================================================
 *  Phase 3: MTLRenderCommandEncoder 扩展
 * ============================================================ */

void MTLRenderCommandEncoder_setVertexBytes(mtl_handle_t encoder, const void *bytes, uint64_t length, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE || bytes == NULL) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e setVertexBytes:bytes length:(NSUInteger)length atIndex:(NSUInteger)index];
}

void MTLRenderCommandEncoder_setFragmentBuffer(mtl_handle_t encoder, mtl_handle_t buffer, uint64_t offset, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e setFragmentBuffer:H2ID(buffer) offset:(NSUInteger)offset atIndex:(NSUInteger)index];
}

void MTLRenderCommandEncoder_setFragmentBytes(mtl_handle_t encoder, const void *bytes, uint64_t length, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE || bytes == NULL) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e setFragmentBytes:bytes length:(NSUInteger)length atIndex:(NSUInteger)index];
}

void MTLRenderCommandEncoder_setFragmentTexture(mtl_handle_t encoder, mtl_handle_t texture, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e setFragmentTexture:H2ID(texture) atIndex:(NSUInteger)index];
}

void MTLRenderCommandEncoder_setFragmentSamplerState(mtl_handle_t encoder, mtl_handle_t sampler, uint64_t index) {
    if (encoder == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e setFragmentSamplerState:H2ID(sampler) atIndex:(NSUInteger)index];
}

void MTLRenderCommandEncoder_useResource(mtl_handle_t encoder, mtl_handle_t resource, uint32_t usage, uint32_t stages) {
    if (encoder == MTL_NULL_HANDLE || resource == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e useResource:(id<MTLResource>)H2ID(resource) usage:(MTLResourceUsage)usage stages:(MTLRenderStages)stages];
}

void MTLRenderCommandEncoder_waitForFence(mtl_handle_t encoder, mtl_handle_t fence, uint32_t before_stages) {
    if (encoder == MTL_NULL_HANDLE || fence == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e waitForFence:H2ID(fence) beforeStages:(MTLRenderStages)before_stages];
}

void MTLRenderCommandEncoder_updateFence(mtl_handle_t encoder, mtl_handle_t fence, uint32_t after_stages) {
    if (encoder == MTL_NULL_HANDLE || fence == MTL_NULL_HANDLE) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    [e updateFence:H2ID(fence) afterStages:(MTLRenderStages)after_stages];
}

/* ============================================================
 *  Phase 7D: 光栅化状态 setters
 * ============================================================ */

void MTLRenderCommandEncoder_setCullMode(mtl_handle_t encoder, int cull_mode) {
    if (encoder == MTL_NULL_HANDLE) return;
    [H2ID(encoder) setCullMode:(MTLCullMode)cull_mode];
}

void MTLRenderCommandEncoder_setFrontFacingWinding(mtl_handle_t encoder, int winding) {
    if (encoder == MTL_NULL_HANDLE) return;
    [H2ID(encoder) setFrontFacingWinding:(MTLWinding)winding];
}

void MTLRenderCommandEncoder_setDepthBias(mtl_handle_t encoder, float bias, float slope_scale, float clamp) {
    if (encoder == MTL_NULL_HANDLE) return;
    [H2ID(encoder) setDepthBias:bias slopeScale:slope_scale clamp:clamp];
}

void MTLRenderCommandEncoder_setDepthClipMode(mtl_handle_t encoder, int clip_mode) {
    if (encoder == MTL_NULL_HANDLE) return;
    [H2ID(encoder) setDepthClipMode:(MTLDepthClipMode)clip_mode];
}

void MTLRenderCommandEncoder_setTriangleFillMode(mtl_handle_t encoder, int fill_mode) {
    if (encoder == MTL_NULL_HANDLE) return;
    [H2ID(encoder) setTriangleFillMode:(MTLTriangleFillMode)fill_mode];
}

/* ============================================================
 *  Phase 7E: 深度/模板状态 setters
 * ============================================================ */

void MTLRenderCommandEncoder_setDepthStencilState(mtl_handle_t encoder, mtl_handle_t state) {
    if (encoder == MTL_NULL_HANDLE || state == MTL_NULL_HANDLE) return;
    [H2ID(encoder) setDepthStencilState:H2ID(state)];
}

void MTLRenderCommandEncoder_setStencilReferenceValue(mtl_handle_t encoder, uint32_t front, uint32_t back) {
    if (encoder == MTL_NULL_HANDLE) return;
    [H2ID(encoder) setStencilFrontReferenceValue:front backReferenceValue:back];
}

/* ============================================================
 *  Phase 3.5: MTLArgumentEncoder
 * ============================================================ */

mtl_handle_t MTLFunction_newArgumentEncoder(mtl_handle_t function, uint64_t buffer_index) {
    if (function == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLFunction> fn = H2ID(function);
    id<MTLArgumentEncoder> enc = [fn newArgumentEncoderWithBufferIndex:(NSUInteger)buffer_index];
    return enc ? ID2H(enc) : MTL_NULL_HANDLE;
}

uint64_t MTLArgumentEncoder_encodedLength(mtl_handle_t encoder) {
    if (encoder == MTL_NULL_HANDLE) return 0;
    id<MTLArgumentEncoder> enc = H2ID(encoder);
    return (uint64_t)[enc encodedLength];
}

void MTLArgumentEncoder_encodeTextureSampler(mtl_handle_t encoder,
                                              mtl_handle_t arg_buffer,
                                              uint64_t offset,
                                              mtl_handle_t texture,
                                              mtl_handle_t sampler) {
    if (encoder == MTL_NULL_HANDLE || arg_buffer == MTL_NULL_HANDLE) return;
    id<MTLArgumentEncoder> enc = H2ID(encoder);
    id<MTLBuffer> buf = H2ID(arg_buffer);
    /* setArgumentBuffer:offset: 将 encoder 关联到 buffer 的指定偏移 */
    [enc setArgumentBuffer:buf offset:(NSUInteger)offset];
    if (texture != MTL_NULL_HANDLE) [enc setTexture:H2ID(texture) atIndex:0];
    if (sampler != MTL_NULL_HANDLE) [enc setSamplerState:H2ID(sampler) atIndex:0];
}

/* ============================================================
 *  Phase 3.5: MTLRenderPassDescriptor
 * ============================================================ */

mtl_handle_t MTLRenderPassDescriptor_createForTexture(mtl_handle_t texture) {
    if (texture == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    MTLRenderPassDescriptor *desc = [MTLRenderPassDescriptor renderPassDescriptor];
    desc.colorAttachments[0].texture = H2ID(texture);
    desc.colorAttachments[0].loadAction = MTLLoadActionLoad;
    desc.colorAttachments[0].storeAction = MTLStoreActionStore;
    return ID2H(desc);
}

void MTLRenderPassDescriptor_release(mtl_handle_t desc) {
    if (desc != MTL_NULL_HANDLE) CFRelease((CFTypeRef)(void*)desc);
}

/* ============================================================
 *  Phase 6: 批量命令编码器回放
 *  参照 DXMT winemetal_unix.c:786-869 的 while+switch 模式。
 *  每个回放函数只 H2ID(encoder) 一次，遍历链表按 type 分发。
 * ============================================================ */

/* Compute 回放：拆成 helper 以满足 ≤20 行约束（AGENTS.md §4.2）。
 * threadgroup_size 随 SetPipelineState 缓存，供后续 Dispatch 消费
 * —— 与 DXMT 行为一致（链表内 SetPSO 必须先于 Dispatch）。 */
static void replay_compute_cmd(id<MTLComputeCommandEncoder> e,
                               const struct wmtcmd_base *cmd,
                               MTLSize *threadgroup_size) {
    switch ((enum WMTComputeCmdType)cmd->type) {
    case WMTComputeCmdSetPipelineState: {
        const struct wmtcmd_compute_setpso *b = (const void*)cmd;
        [e setComputePipelineState:H2ID(b->pso)];
        threadgroup_size->width  = (NSUInteger)b->threadgroup_size.width;
        threadgroup_size->height = (NSUInteger)b->threadgroup_size.height;
        threadgroup_size->depth  = (NSUInteger)b->threadgroup_size.depth;
        break;
    }
    case WMTComputeCmdUseResource: {
        const struct wmtcmd_compute_useresource *b = (const void*)cmd;
        [e useResource:(id<MTLResource>)H2ID(b->resource) usage:(MTLResourceUsage)b->usage];
        break;
    }
    case WMTComputeCmdSetBytes: {
        const struct wmtcmd_compute_setbytes *b = (const void*)cmd;
        [e setBytes:b->bytes length:(NSUInteger)b->length atIndex:(NSUInteger)b->index];
        break;
    }
    case WMTComputeCmdDispatch: {
        const struct wmtcmd_compute_dispatch *b = (const void*)cmd;
        MTLSize g = MTLSizeMake((NSUInteger)b->threadgroups_per_grid.width,
                                (NSUInteger)b->threadgroups_per_grid.height,
                                (NSUInteger)b->threadgroups_per_grid.depth);
        [e dispatchThreadgroups:g threadsPerThreadgroup:*threadgroup_size];
        break;
    }
    case WMTComputeCmdEndEncoding:
        [e endEncoding];
        break;
    default:
        break;  /* 未知类型静默跳过（release 保守策略） */
    }
}

void MTLComputeCommandEncoder_encodeCommands(mtl_handle_t encoder, const struct wmtcmd_base *head) {
    if (encoder == MTL_NULL_HANDLE || head == NULL) return;
    id<MTLComputeCommandEncoder> e = H2ID(encoder);
    MTLSize threadgroup_size = {0, 0, 0};
    while (head) {
        replay_compute_cmd(e, head, &threadgroup_size);
        head = head->next;
    }
}

/* Render 回放：同理拆 helper。 */
static void replay_render_cmd(id<MTLRenderCommandEncoder> e,
                              const struct wmtcmd_base *cmd) {
    switch ((enum WMTRenderCmdType)cmd->type) {
    case WMTRenderCmdSetPipelineState: {
        const struct wmtcmd_render_setpso *b = (const void*)cmd;
        [e setRenderPipelineState:H2ID(b->pso)];
        break;
    }
    case WMTRenderCmdSetViewport: {
        const struct wmtcmd_render_setviewport *b = (const void*)cmd;
        MTLViewport vp = { b->x, b->y, b->w, b->h, b->znear, b->zfar };
        [e setViewport:vp];
        break;
    }
    case WMTRenderCmdSetVertexBytes: {
        const struct wmtcmd_render_setbytes *b = (const void*)cmd;
        [e setVertexBytes:b->bytes length:(NSUInteger)b->length atIndex:(NSUInteger)b->index];
        break;
    }
    case WMTRenderCmdSetFragmentBytes: {
        const struct wmtcmd_render_setbytes *b = (const void*)cmd;
        [e setFragmentBytes:b->bytes length:(NSUInteger)b->length atIndex:(NSUInteger)b->index];
        break;
    }
    case WMTRenderCmdUseResource: {
        const struct wmtcmd_render_useresource *b = (const void*)cmd;
        [e useResource:(id<MTLResource>)H2ID(b->resource)
                 usage:(MTLResourceUsage)b->usage
                stages:(MTLRenderStages)b->stages];
        break;
    }
    case WMTRenderCmdDrawPrimitives: {
        const struct wmtcmd_render_draw *b = (const void*)cmd;
        /* primitive_type=0 → Triangle（与现有 drawPrimitives 约定一致） */
        if (b->instance_count > 1) {
            [e drawPrimitives:MTLPrimitiveTypeTriangle
                  vertexStart:(NSUInteger)b->vertex_start
                  vertexCount:(NSUInteger)b->vertex_count
                instanceCount:(NSUInteger)b->instance_count];
        } else {
            [e drawPrimitives:MTLPrimitiveTypeTriangle
                  vertexStart:(NSUInteger)b->vertex_start
                  vertexCount:(NSUInteger)b->vertex_count];
        }
        break;
    }
    case WMTRenderCmdDrawIndexedPrimitives: {
        const struct wmtcmd_render_draw_indexed *b = (const void*)cmd;
        MTLIndexType it = (b->index_type == 0) ? MTLIndexTypeUInt16 : MTLIndexTypeUInt32;
        if (b->instance_count > 1) {
            [e drawIndexedPrimitives:MTLPrimitiveTypeTriangle
                          indexCount:(NSUInteger)b->index_count
                           indexType:it
                         indexBuffer:H2ID(b->index_buffer)
                   indexBufferOffset:(NSUInteger)b->index_buffer_offset
                       instanceCount:(NSUInteger)b->instance_count];
        } else {
            [e drawIndexedPrimitives:MTLPrimitiveTypeTriangle
                          indexCount:(NSUInteger)b->index_count
                           indexType:it
                         indexBuffer:H2ID(b->index_buffer)
                   indexBufferOffset:(NSUInteger)b->index_buffer_offset];
        }
        break;
    }
    case WMTRenderCmdDrawIndirectPrimitives: {
        const struct wmtcmd_render_draw_indirect *b = (const void*)cmd;
        [e drawPrimitives:MTLPrimitiveTypeTriangle
           indirectBuffer:H2ID(b->indirect_buffer)
     indirectBufferOffset:(NSUInteger)b->indirect_buffer_offset];
        break;
    }
    case WMTRenderCmdDrawIndexedIndirectPrimitives: {
        const struct wmtcmd_render_draw_indexed_indirect *b = (const void*)cmd;
        MTLIndexType it = (b->index_type == 0) ? MTLIndexTypeUInt16 : MTLIndexTypeUInt32;
        [e drawIndexedPrimitives:MTLPrimitiveTypeTriangle
                       indexType:it
                     indexBuffer:H2ID(b->index_buffer)
               indexBufferOffset:0
                  indirectBuffer:H2ID(b->indirect_buffer)
        indirectBufferOffset:(NSUInteger)b->indirect_buffer_offset];
        break;
    }
    case WMTRenderCmdEndEncoding:
        [e endEncoding];
        break;
    case WMTRenderCmdSetCullMode: {
        const struct wmtcmd_render_setcullmode *b = (const void*)cmd;
        [e setCullMode:(MTLCullMode)b->cull_mode];
        break;
    }
    case WMTRenderCmdSetFrontFacing: {
        const struct wmtcmd_render_setfrontfacing *b = (const void*)cmd;
        [e setFrontFacingWinding:(MTLWinding)b->winding];
        break;
    }
    case WMTRenderCmdSetDepthBias: {
        const struct wmtcmd_render_setdepthbias *b = (const void*)cmd;
        [e setDepthBias:b->bias slopeScale:b->slope_scale clamp:b->clamp];
        break;
    }
    case WMTRenderCmdSetDepthClipMode: {
        const struct wmtcmd_render_setdepthclipmode *b = (const void*)cmd;
        [e setDepthClipMode:(MTLDepthClipMode)b->clip_mode];
        break;
    }
    case WMTRenderCmdSetTriangleFillMode: {
        const struct wmtcmd_render_settrianglefillmode *b = (const void*)cmd;
        [e setTriangleFillMode:(MTLTriangleFillMode)b->fill_mode];
        break;
    }
    case WMTRenderCmdSetDepthStencilState: {
        const struct wmtcmd_render_setdepthstencilstate *b = (const void*)cmd;
        [e setDepthStencilState:H2ID(b->state)];
        break;
    }
    case WMTRenderCmdSetStencilReference: {
        const struct wmtcmd_render_setstencilreference *b = (const void*)cmd;
        [e setStencilFrontReferenceValue:b->front backReferenceValue:b->back];
        break;
    }
    default:
        break;
    }
}

void MTLRenderCommandEncoder_encodeCommands(mtl_handle_t encoder, const struct wmtcmd_base *head) {
    if (encoder == MTL_NULL_HANDLE || head == NULL) return;
    id<MTLRenderCommandEncoder> e = H2ID(encoder);
    while (head) {
        replay_render_cmd(e, head);
        head = head->next;
    }
}
