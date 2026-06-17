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
#import <Foundation/Foundation.h>
#import <CoreFoundation/CoreFoundation.h>
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
    return cb ? (mtl_handle_t)(uintptr_t)CFBridgingRetain(cb) : MTL_NULL_HANDLE;
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
    return err ? (mtl_handle_t)(uintptr_t)CFBridgingRetain(err) : MTL_NULL_HANDLE;
}

/* ============================================================
 *  MTLComputeCommandEncoder
 * ============================================================ */

mtl_handle_t MTLCommandBuffer_computeCommandEncoder(mtl_handle_t cmdbuf) {
    if (cmdbuf == MTL_NULL_HANDLE) return MTL_NULL_HANDLE;
    id<MTLCommandBuffer> cb = H2ID(cmdbuf);
    id<MTLComputeCommandEncoder> enc = [cb computeCommandEncoder];
    return enc ? (mtl_handle_t)(uintptr_t)CFBridgingRetain(enc) : MTL_NULL_HANDLE;
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
