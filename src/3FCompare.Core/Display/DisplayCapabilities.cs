using System.Runtime.InteropServices;

namespace _3FCompare.Core.Display;

/// <summary>显示器 HDR 能力枚举（基于 DXGI/DXGI_OUTPUT_DESC1 + Advanced Color Info）。
/// NativeAOT 安全的纯 P/Invoke；零 COM 引用。</summary>
public static class DisplayCapabilities
{
    private const uint DXGI_ENUM_REGISTRY_SETTINGS = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFOEXW
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEXW lpmi);

    /// <summary>读取指定窗口所在显示器的 HDR 能力。
    /// 返回 null 表示无法读取（旧系统或不支持 DXGI 1.6）。</summary>
    public static DisplayLuminanceCapabilities? ReadForWindow(nint hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == 0) return null;
            return ReadForMonitor(monitor);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取指定 HMONITOR 的 HDR 能力（DXGI 1.6 GetDesc1）。
    /// 失败或被枚举到匹配输出时返回 null（调用方使用默认参数）。</summary>
    public static DisplayLuminanceCapabilities? ReadForMonitor(nint monitor)
    {
        try
        {
            // DXGI 1.6 读取显示器真实 HDR 能力
            // （Min/Max/FullFrame 亮度单位均为 nits；ColorSpace>=3 表示 HDR 输出）。
            if (DxgiOutputInfo.TryReadLuminance(
                    monitor,
                    out var minNits,
                    out var maxNits,
                    out var fullFrameNits,
                    out var hdrCapable))
            {
                return new DisplayLuminanceCapabilities
                {
                    Supported = hdrCapable,
                    MaximumNits = maxNits,
                    MinimumNits = minNits,
                    FullFrameNits = fullFrameNits,
                };
            }

            // 读取失败（旧驱动/DXGI<1.6/无匹配输出）：返回 null 让调用方回退默认。
            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>显示器亮度能力快照。</summary>
public sealed class DisplayLuminanceCapabilities
{
    public bool Supported { get; init; }
    public float MaximumNits { get; init; }
    public float MinimumNits { get; init; }
    public float FullFrameNits { get; init; }
}
