using System.Runtime.InteropServices;

namespace _3FCompare.Core.Display;

/// <summary>窗口所在显示器的当前刷新率（GDI <c>GetDeviceCaps(VREFRESH)</c>）。
///
/// <para>只用于 <c>BluetoothLatencyPolicy.EstimateDisplayLatencyMs</c> 的上界估计，
/// 不参与呈现节律/计时。拿不到（远程桌面、虚拟显示器、驱动返回 0/1）时回落 60Hz ——
/// 这会让 <c>L_disp</c> 取 38.3ms 的保守值，不会把音频补成负延迟之外的错误方向。</para></summary>
public static partial class DisplayRefreshRate
{
    private const int VRefresh = 116;

    /// <param name="hwnd">窗口句柄；0 = 主屏 DC。设置窗口构造期还没有 HWND，可传 0。</param>
    public static int Read(nint hwnd)
    {
        var dc = GetDC(hwnd);
        if (dc == nint.Zero) return 60;
        try
        {
            var hz = GetDeviceCaps(dc, VRefresh);
            return hz is >= 24 and <= 500 ? hz : 60;
        }
        finally
        {
            ReleaseDC(hwnd, dc);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDeviceCaps(nint hdc, int index);
}
