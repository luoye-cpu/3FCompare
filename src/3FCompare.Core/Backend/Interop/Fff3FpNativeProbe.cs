using System.Runtime.InteropServices;

namespace _3FCompare.Core.Backend.Interop;

/// <summary>引擎可用性探测：加载 FFF.Native 并调用无副作用函数。</summary>
internal static partial class Fff3FpNativeProbe
{
    private const string DllName = "FFF.Native";

    [LibraryImport(DllName)]
    internal static partial uint FFF3FP_GetApiVersion();
}