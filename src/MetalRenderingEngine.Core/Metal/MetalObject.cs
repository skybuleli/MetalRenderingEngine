using System.Runtime.InteropServices;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// 所有 Metal 对象的 SafeHandle 基类。
///
/// 设计要点（对应 references/dxmt/src/winemetal/Metal.hpp:62-122 的 Reference&lt;T&gt;）：
/// <list type="bullet">
/// <item>持有由 bridge.m 端 <c>__bridge_retained</c> 转交而来的 retained 句柄（+1）。</item>
/// <item>Dispose / 终结器一次调用 <c>NSObject_release</c>（-1），保证不泄漏也不重释。</item>
/// <item><see cref="Retain"/> 用于跨作用域共享时显式增计数，必须与一次额外的 Dispose 配对。</item>
/// </list>
/// </summary>
public abstract class MetalObject : SafeHandle
{
    /// <summary>所有 Metal SafeHandle 共用的释放逻辑（CFRelease 等价）。</summary>
    protected MetalObject() : base(invalidHandleValue: IntPtr.Zero, ownsHandle: true) { }

    /// <summary>0 即为 MTL_NULL_HANDLE。</summary>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>转回 P/Invoke 用的 nuint 句柄。</summary>
    public nuint Handle => (nuint)(nint)handle;

    /// <summary>
    /// 由派生类构造时调用：设置由 native 层 retained 后转交过来的句柄。
    /// </summary>
    protected void SetNativeHandle(nuint h) => SetHandle((IntPtr)(nint)h);

    /// <summary>
    /// 显式增加引用计数。用于把同一个对象交给多个共享所有者的场景；
    /// 之后需要对应一次额外的 <see cref="SafeHandle.Dispose()"/>。
    /// </summary>
    public void Retain()
    {
        if (!IsInvalid) MetalBridge.NSObject_retain(Handle);
    }

    /// <summary>SafeHandle 释放钩子，由 .NET 在 Dispose / 终结时调用一次。</summary>
    protected override bool ReleaseHandle()
    {
        if (handle != IntPtr.Zero)
        {
            MetalBridge.NSObject_release((nuint)(nint)handle);
            SetHandle(IntPtr.Zero);
        }
        return true;
    }
}
