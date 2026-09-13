using _3FCompare.Core.Backend.Interop;
using _3FCompare.Core.Diagnostics;

namespace _3FCompare.Core.Backend;

/// <summary>引擎工厂：自动探测 FFF.Native 是否可用，选择真实 3FP 后端或演示后端。
/// 可通过 <see cref="NativeRuntime.SetFfmpegDirectory"/> 指定 FFmpeg DLL 搜索目录。</summary>
public static class EngineFactory
{
    private static readonly object _modeLock = new();
    private static bool? _nativeAvailable;
    private static string? _lastUnavailableReason;

    /// <summary>探测真实 3FP 后端是否可用。
    /// 需同时满足：① FFF.Native.dll 可加载（应用目录/PATH）；② FFmpeg 核心 DLL 已在应用目录。
    /// 缺 FFmpeg 时 FFF.Native 虽能加载，但打开视频时 Delay-Load FFmpeg 会原生崩溃，
    /// 故此时应回退演示模式（见 NativeRuntime.IsFfmpegAvailable）。
    /// 结果按进程缓存：引擎类型在进程生命周期内不变，避免每次读取都重新探测。</summary>
    public static bool IsNativeAvailable()
    {
        lock (_modeLock)
        {
            if (_nativeAvailable.HasValue) return _nativeAvailable.Value;
            _nativeAvailable = ProbeNativeAvailable(out _lastUnavailableReason);
            return _nativeAvailable.Value;
        }
    }

    /// <summary>上次探测为不可用时的降级原因（可用于 UI 透出；可用时为 null）。</summary>
    public static string? LastUnavailableReason
    {
        get { lock (_modeLock) { IsNativeAvailable(); return _lastUnavailableReason; } }
    }

    private static bool ProbeNativeAvailable(out string? reason)
    {
        reason = null;
        // 先低成本检查 FFmpeg（避免加载 FFF.Native 后才发现不可用）
        if (!NativeRuntime.IsFfmpegAvailable())
        {
            AppLog.Warn("EngineFactory", "IsNativeAvailable: FFmpeg 核心库不可用（avcodec/avformat 未找到）");
            reason = "缺少 FFmpeg 核心库（avcodec-*.dll）";
            return false;
        }
        try
        {
            var ver = Fff3FpNativeProbe.FFF3FP_GetApiVersion();
            AppLog.Info("EngineFactory", $"IsNativeAvailable: GetApiVersion={ver}");
            if (ver < 1)
            {
                reason = $"内核 API 版本异常（{ver}）";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLog.Warn("EngineFactory", $"IsNativeAvailable: {ex.GetType().Name}: {ex.Message}");
            reason = $"{ex.GetType().Name}：FFF.Native.dll 加载失败";
            return false;
        }
    }

    /// <summary>创建引擎：优先真实 3FP，缺失时演示模式。</summary>
    public static IPlayerEngine Create()
    {
        if (IsNativeAvailable())
        {
            return new Fff3FpEngine();
        }
        return new SimulatedEngine();
    }

    /// <summary>当前引擎模式名称（用于 UI 显示；演示模式附降级原因）。</summary>
    public static string CurrentModeName
    {
        get
        {
            if (IsNativeAvailable()) return "FFF.Native (3FP)";
            var reason = LastUnavailableReason;
            return reason is null ? SimulatedEngine.ModeName : $"{SimulatedEngine.ModeName}：{reason}";
        }
    }
}
