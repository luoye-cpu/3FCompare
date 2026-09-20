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
            // DXGI 1.6 读取显示器亮度参数（Min/Max/FullFrame 亮度单位均为 nits）。
            //
            // ⚠ 关于 Supported（hdrCapable）的语义，别被字段名误导：
            // 判据是 ColorSpace >= 12（DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020，
            // 见 dxgicommon.h），不是 >= 3 —— 0..11 里还夹着 G22/BT709 等一大票 SDR
            // 色彩空间，用 3 会把普通 SDR 显示器误判成 HDR（常量与比较在 DxgiOutputInfo 内）。
            // 而 DXGI 的 ColorSpace 描述的是**当前输出**的色彩空间，不是显示器硬件上限：
            // 用户在系统设置里关掉 HDR 后它会回落到 SDR 值，此时即使 MaxLuminance 仍上报
            // 几百 nits，Supported 也会是 false。
            // 这**正是本项目想要的行为**：本结果用于 ColorModeHelper.Resolve 自动选择
            // HDR/SDR 输出，理应跟随系统当前的 HDR 开关状态 —— 系统没开 HDR 时，
            // 应用按 HDR 输出也不会被正确呈现。
            // 若将来需要"显示器硬件是否支持 HDR"这一独立语义，应改为用
            // MaxLuminance（如 > 400 nits）或 BitsPerColor >= 10 判定，另开字段，不要复用本值。
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
