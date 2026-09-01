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

    [LibraryImport(DllName)]
    internal static partial void FFF3FP_SetLogCallback(FFF3FPLogCallback callback, nint context);
}