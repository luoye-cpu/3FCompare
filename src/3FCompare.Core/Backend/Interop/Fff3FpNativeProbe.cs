using System.Runtime.InteropServices;

namespace _3FCompare.Core.Backend.Interop;

/// <summary>引擎可用性探测：加载 FFF.Native 并调用无副作用函数。</summary>
internal static partial class Fff3FpNativeProbe
{
    private const string DllName = "FFF.Native";

    [LibraryImport(DllName)]
    internal static partial uint FFF3FP_GetApiVersion();

    /// <summary>内核日志回调（F-LOG）：UTF-8 行文本，任意内核线程触发。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void FFF3FPLogCallback(nint context, nint utf8LinePtr);

    /// <summary>注册/注销日志回调。callbackFnPtr 传 nint.Zero 表示注销（原生侧停止回调）。
    /// 用 nint 而非委托类型声明：注销时需要传空函数指针，委托类型形参传 null 会抛。</summary>
    [LibraryImport(DllName)]
    internal static partial void FFF3FP_SetLogCallback(nint callbackFnPtr, nint context);
}