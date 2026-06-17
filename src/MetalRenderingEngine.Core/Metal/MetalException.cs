using System.Text;
using MetalRenderingEngine.Metal.Interop;

namespace MetalRenderingEngine.Metal;

/// <summary>
/// 表示 bridge.m 透过 <c>err_out</c> 返回的 NSError* 句柄。
/// 使用 using 释放；<see cref="Description"/> 拷出 UTF-8 字符串。
/// </summary>
public sealed class MetalError : MetalObject
{
    /// <summary>从 retained 的 nuint 句柄接管所有权。</summary>
    internal MetalError(nuint handle) { SetNativeHandle(handle); }

    /// <summary>读取 NSError -localizedDescription。</summary>
    public string Description
    {
        get
        {
            if (IsInvalid) return string.Empty;
            unsafe
            {
                // 先探长度（含 \0），再分配相应大小读取。
                ulong need = MetalBridge.NSError_localizedDescription(Handle, null, 0);
                if (need <= 1) return string.Empty;
                Span<byte> buf = need <= 512 ? stackalloc byte[(int)need] : new byte[need];
                fixed (byte* p = buf)
                {
                    MetalBridge.NSError_localizedDescription(Handle, p, need);
                }
                // 去掉末尾 \0
                int len = buf.Length;
                while (len > 0 && buf[len - 1] == 0) len--;
                return Encoding.UTF8.GetString(buf[..len]);
            }
        }
    }
}

/// <summary>Metal 操作失败时抛出。</summary>
public sealed class MetalException : Exception
{
    public MetalException(string message) : base(message) { }
    public MetalException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// 用 <see cref="MetalError"/> 的描述字符串构造异常并主动释放底层 NSError。
    /// 调用方传入的 error 在此被消费，外部不应再访问。
    /// </summary>
    public static MetalException FromError(string action, MetalError error)
    {
        using (error)
        {
            string desc = error.IsInvalid ? "(no description)" : error.Description;
            return new MetalException($"{action} failed: {desc}");
        }
    }
}
