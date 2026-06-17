using System.Runtime.InteropServices;

namespace MetalRenderingEngine.Metal.Interop;

/// <summary>
/// Phase 1 阶段的 P/Invoke 集中点，所有 DllImport 必须放在本文件
/// （遵循 AGENTS.md §4.1）。
///
/// 命名与签名严格对应 native/bridge.h；签名变动时请同步两端。
/// </summary>
internal static partial class MetalBridge
{
    /// <summary>桥接动态库的基名；运行时按平台规则解析（macOS: lib*.dylib）。</summary>
    public const string LibraryName = "libmetal_bridge";

    // ============================================================
    //  引用计数
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "NSObject_retain")]
    public static partial void NSObject_retain(nuint obj);

    [LibraryImport(LibraryName, EntryPoint = "NSObject_release")]
    public static partial void NSObject_release(nuint obj);

    // ============================================================
    //  NSError
    // ============================================================

    /// <summary>拷贝 localizedDescription 到 buffer（UTF-8）；返回所需字节数（含 \0）。</summary>
    [LibraryImport(LibraryName, EntryPoint = "NSError_localizedDescription")]
    public static unsafe partial ulong NSError_localizedDescription(nuint error, byte* buffer, ulong max_length);

    // ============================================================
    //  MTLDevice
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_createSystemDefault")]
    public static partial nuint MTLDevice_createSystemDefault();

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_name")]
    public static unsafe partial ulong MTLDevice_name(nuint device, byte* buffer, ulong max_length);

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_hasUnifiedMemory")]
    [return: MarshalAs(UnmanagedType.I4)]
    public static partial int MTLDevice_hasUnifiedMemory(nuint device);

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_recommendedMaxWorkingSetSize")]
    public static partial ulong MTLDevice_recommendedMaxWorkingSetSize(nuint device);

    // ============================================================
    //  MTLLibrary / MTLFunction
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newLibrary")]
    public static unsafe partial nuint MTLDevice_newLibrary(nuint device, void* data, ulong length, nuint* err_out);

    [LibraryImport(LibraryName, EntryPoint = "MTLLibrary_newFunctionWithName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint MTLLibrary_newFunctionWithName(nuint library, string name);

    // ============================================================
    //  MTLComputePipelineState
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newComputePipelineState")]
    public static unsafe partial nuint MTLDevice_newComputePipelineState(nuint device, nuint function, nuint* err_out);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputePipelineState_maxTotalThreadsPerThreadgroup")]
    public static partial ulong MTLComputePipelineState_maxTotalThreadsPerThreadgroup(nuint pso);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputePipelineState_threadExecutionWidth")]
    public static partial ulong MTLComputePipelineState_threadExecutionWidth(nuint pso);

    // ============================================================
    //  MTLBuffer
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newBuffer")]
    public static unsafe partial nuint MTLDevice_newBuffer(nuint device, WMTBufferInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "MTLBuffer_contents")]
    public static partial nint MTLBuffer_contents(nuint buffer);

    [LibraryImport(LibraryName, EntryPoint = "MTLBuffer_gpuAddress")]
    public static partial ulong MTLBuffer_gpuAddress(nuint buffer);

    [LibraryImport(LibraryName, EntryPoint = "MTLBuffer_didModifyRange")]
    public static partial void MTLBuffer_didModifyRange(nuint buffer, ulong offset, ulong length);

    // ============================================================
    //  MTLCommandQueue / MTLCommandBuffer
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newCommandQueue")]
    public static partial nuint MTLDevice_newCommandQueue(nuint device);

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandQueue_commandBuffer")]
    public static partial nuint MTLCommandQueue_commandBuffer(nuint queue);

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandBuffer_commit")]
    public static partial void MTLCommandBuffer_commit(nuint cmdbuf);

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandBuffer_waitUntilCompleted")]
    public static partial void MTLCommandBuffer_waitUntilCompleted(nuint cmdbuf);

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandBuffer_status")]
    public static partial int MTLCommandBuffer_status(nuint cmdbuf);

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandBuffer_error")]
    public static partial nuint MTLCommandBuffer_error(nuint cmdbuf);

    // ============================================================
    //  MTLComputeCommandEncoder
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandBuffer_computeCommandEncoder")]
    public static partial nuint MTLCommandBuffer_computeCommandEncoder(nuint cmdbuf);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputeCommandEncoder_setComputePipelineState")]
    public static partial void MTLComputeCommandEncoder_setComputePipelineState(nuint encoder, nuint pso);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputeCommandEncoder_setBuffer")]
    public static partial void MTLComputeCommandEncoder_setBuffer(nuint encoder, nuint buffer, ulong offset, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputeCommandEncoder_setBytes")]
    public static unsafe partial void MTLComputeCommandEncoder_setBytes(nuint encoder, void* bytes, ulong length, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputeCommandEncoder_setTexture")]
    public static partial void MTLComputeCommandEncoder_setTexture(nuint encoder, nuint texture, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputeCommandEncoder_useResource")]
    public static partial void MTLComputeCommandEncoder_useResource(nuint encoder, nuint resource, uint usage);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputeCommandEncoder_dispatchThreadgroups")]
    public static partial void MTLComputeCommandEncoder_dispatchThreadgroups(nuint encoder, WMTSize threadgroups_per_grid, WMTSize threads_per_threadgroup);

    [LibraryImport(LibraryName, EntryPoint = "MTLComputeCommandEncoder_endEncoding")]
    public static partial void MTLComputeCommandEncoder_endEncoding(nuint encoder);

    // ============================================================
    //  MTLRenderPipelineState
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newRenderPipelineState")]
    public static unsafe partial nuint MTLDevice_newRenderPipelineState(nuint device, nuint vertex_func, nuint fragment_func, WMTRenderPipelineDesc* desc, nuint* err_out);

    // ============================================================
    //  MTLRenderCommandEncoder
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandBuffer_renderCommandEncoder")]
    public static unsafe partial nuint MTLCommandBuffer_renderCommandEncoder(nuint cmdbuf, WMTRenderPassDesc* desc);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setRenderPipelineState")]
    public static partial void MTLRenderCommandEncoder_setRenderPipelineState(nuint encoder, nuint pso);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setVertexBuffer")]
    public static partial void MTLRenderCommandEncoder_setVertexBuffer(nuint encoder, nuint buffer, ulong offset, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setViewport")]
    public static partial void MTLRenderCommandEncoder_setViewport(nuint encoder, float x, float y, float w, float h, float znear, float zfar);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setScissorRect")]
    public static partial void MTLRenderCommandEncoder_setScissorRect(nuint encoder, int x, int y, int w, int h);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_drawPrimitives")]
    public static partial void MTLRenderCommandEncoder_drawPrimitives(nuint encoder, int primitive_type, ulong vertex_start, ulong vertex_count);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_drawIndexedPrimitives")]
    public static partial void MTLRenderCommandEncoder_drawIndexedPrimitives(nuint encoder, int primitive_type, ulong index_count, int index_type, nuint index_buffer, ulong index_buffer_offset);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_endEncoding")]
    public static partial void MTLRenderCommandEncoder_endEncoding(nuint encoder);

    // ============================================================
    //  CAMetalLayer / CAMetalDrawable
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "Cocoa_CreateMetalWindow", StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial nuint Cocoa_CreateMetalWindow(string title, float width, float height, nuint* out_layer);

    [LibraryImport(LibraryName, EntryPoint = "CAMetalLayer_setDevice")]
    public static partial void CAMetalLayer_setDevice(nuint layer, nuint device);

    [LibraryImport(LibraryName, EntryPoint = "CAMetalLayer_setPixelFormat")]
    public static partial void CAMetalLayer_setPixelFormat(nuint layer, int pixel_format);

    [LibraryImport(LibraryName, EntryPoint = "CAMetalLayer_setDrawableSize")]
    public static partial void CAMetalLayer_setDrawableSize(nuint layer, float width, float height);

    [LibraryImport(LibraryName, EntryPoint = "CAMetalLayer_nextDrawable")]
    public static partial nuint CAMetalLayer_nextDrawable(nuint layer);

    [LibraryImport(LibraryName, EntryPoint = "CAMetalDrawable_texture")]
    public static partial nuint CAMetalDrawable_texture(nuint drawable);

    [LibraryImport(LibraryName, EntryPoint = "MTLCommandBuffer_presentDrawable")]
    public static partial void MTLCommandBuffer_presentDrawable(nuint cmdbuf, nuint drawable);

    [LibraryImport(LibraryName, EntryPoint = "Cocoa_PollEvents")]
    public static partial int Cocoa_PollEvents();

    /// <summary>获取 window 的 contentView（NSView*），用于传给 ImGuiImplOSX.Init。
    /// 返回的句柄不增加引用计数（view 由 window 持有）。</summary>
    [LibraryImport(LibraryName, EntryPoint = "Cocoa_WindowContentView")]
    public static partial nuint Cocoa_WindowContentView(nuint window);

    /// <summary>获取 view 的 drawable 尺寸（像素，已考虑 HiDPI）。</summary>
    [LibraryImport(LibraryName, EntryPoint = "Cocoa_ViewDrawableSize")]
    public static unsafe partial void Cocoa_ViewDrawableSize(nuint view, float* out_width, float* out_height);

    // ============================================================
    //  MTLTexture（只读回读）
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLTexture_width")]
    public static partial ulong MTLTexture_width(nuint texture);

    [LibraryImport(LibraryName, EntryPoint = "MTLTexture_height")]
    public static partial ulong MTLTexture_height(nuint texture);

    [LibraryImport(LibraryName, EntryPoint = "MTLTexture_bytesPerRow")]
    public static partial ulong MTLTexture_bytesPerRow(nuint texture, ulong mip_level);

    [LibraryImport(LibraryName, EntryPoint = "MTLTexture_getBytes")]
    public static unsafe partial ulong MTLTexture_getBytes(nuint texture, void* dst, ulong dst_size, ulong mip_level);

    // ============================================================
    //  Phase 3: MTLTexture 创建与写入
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newTexture")]
    public static unsafe partial nuint MTLDevice_newTexture(nuint device, WMTTextureInfo* info);

    [LibraryImport(LibraryName, EntryPoint = "MTLTexture_replaceRegion")]
    public static unsafe partial void MTLTexture_replaceRegion(nuint texture, WMTOrigin origin, WMTSize size, ulong mip_level, ulong slice, void* data, ulong bytes_per_row, ulong bytes_per_image);

    // ============================================================
    //  Phase 3: MTLSamplerState
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newSamplerState")]
    public static unsafe partial nuint MTLDevice_newSamplerState(nuint device, WMTSamplerInfo* info);

    // ============================================================
    //  Phase 3: MTLFence
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLDevice_newFence")]
    public static partial nuint MTLDevice_newFence(nuint device);

    // ============================================================
    //  Phase 3: MTLRenderCommandEncoder 扩展
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setVertexBytes")]
    public static unsafe partial void MTLRenderCommandEncoder_setVertexBytes(nuint encoder, void* bytes, ulong length, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setFragmentBuffer")]
    public static partial void MTLRenderCommandEncoder_setFragmentBuffer(nuint encoder, nuint buffer, ulong offset, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setFragmentBytes")]
    public static unsafe partial void MTLRenderCommandEncoder_setFragmentBytes(nuint encoder, void* bytes, ulong length, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setFragmentTexture")]
    public static partial void MTLRenderCommandEncoder_setFragmentTexture(nuint encoder, nuint texture, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_setFragmentSamplerState")]
    public static partial void MTLRenderCommandEncoder_setFragmentSamplerState(nuint encoder, nuint sampler, ulong index);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_useResource")]
    public static partial void MTLRenderCommandEncoder_useResource(nuint encoder, nuint resource, uint usage, uint stages);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_waitForFence")]
    public static partial void MTLRenderCommandEncoder_waitForFence(nuint encoder, nuint fence, uint before_stages);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderCommandEncoder_updateFence")]
    public static partial void MTLRenderCommandEncoder_updateFence(nuint encoder, nuint fence, uint after_stages);

    // ============================================================
    //  Phase 3.5: MTLRenderPassDescriptor (for Hexa.NET.ImGui Backends)
    // ============================================================

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderPassDescriptor_createForTexture")]
    public static partial nuint MTLRenderPassDescriptor_createForTexture(nuint texture);

    [LibraryImport(LibraryName, EntryPoint = "MTLRenderPassDescriptor_release")]
    public static partial void MTLRenderPassDescriptor_release(nuint desc);
}
