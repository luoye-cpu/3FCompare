using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace _3FCompare.Core.Display;

/// <summary>显示器 HDR 能力枚举（基于 DXGI/DXGI_OUTPUT_DESC1 + Advanced Color Info）。
/// NativeAOT 安全的纯 P/Invoke；零 COM 引用。</summary>
public static class DisplayCapabilities
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // ---- HMONITOR 级缓存：避免高频重复枚举 DXGI 适配器/输出链 ----
    private static readonly ConcurrentDictionary<nint, CachedEntry> _capsCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private sealed record CachedEntry(DisplayLuminanceCapabilities? Caps, DateTime CachedAt);

    /// <summary>读取指定窗口所在显示器的 HDR 能力。
    /// 返回 null 表示无法读取（旧系统或不支持 DXGI 1.6）。
    /// 结果按 HMONITOR 缓存 5 秒，避免高频 DXGI 枚举。</summary>
    public static DisplayLuminanceCapabilities? ReadForWindow(nint hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == 0) return null;

            // 缓存命中且未过期
            if (_capsCache.TryGetValue(monitor, out var cached) &&
                (DateTime.UtcNow - cached.CachedAt) < CacheTtl)
                return cached.Caps;

            var caps = ReadForMonitor(monitor);
            _capsCache[monitor] = new CachedEntry(caps, DateTime.UtcNow);
            return caps;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清空显示器能力缓存（显示器变更时由外部调用）。</summary>
    public static void InvalidateCache() => _capsCache.Clear();

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
