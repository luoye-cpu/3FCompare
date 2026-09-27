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

    /// <summary>清除原生可用性探测缓存。
    /// 探测结果此前一旦算出就永久缓存，而全仓库没有任何重置点 ⇒ 用户改完 FFmpeg 目录后
    /// （<see cref="NativeRuntime.SetFfmpegDirectory"/> 成功）拿到的仍是进程启动那次探测的
    /// 结论，表现为"配好了却还是演示模式，重启才生效"。凡是改变了 DLL 加载条件的入口
    /// （设置 FFmpeg 目录、释放内嵌 DLL）都必须调用本方法。</summary>
    public static void ResetNativeProbe()
    {
        lock (_modeLock)
        {
            _nativeAvailable = null;
            _lastUnavailableReason = null;
        }
    }

    /// <summary>非缓存探测：每次都真正走一遍加载流程。
    /// 用于设置页"测试"这类需要反映<b>当前</b>目录真实可用性的场合
    /// （<see cref="IsNativeAvailable"/> 走缓存，改完目录后不重置就永远是旧结论）。</summary>
    public static bool ProbeNow()
    {
        lock (_modeLock)
        {
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
            // 内核 FFF3FP_Create 对 version 是**严格相等**（PlayerApi.cpp），不是"不低于"。
            // 故这里也必须判相等：旧判据 `ver < 1` 会让残留的旧内核（如 API 14）被判为可用，
            // 表现为状态栏"已就绪"而每次 CreateSession 抛 InvalidArgument ⇒ **全部会话创建失败
            // 且看不出原因**（docs/45 P1-1，历史上真实发生过）。
            if (ver != Fff3FpEngine.ConfigVersion)
            {
                reason = $"内核 API 版本不匹配（内核 {ver} ≠ 托管 {Fff3FpEngine.ConfigVersion}）";
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
        // 降级时把模式名（含原因）写日志：状态栏只显示一次、且用户可能不看，
        // 日志是排查"为什么是演示模式"的唯一留痕（可用性 P1-3 的配套）。
        AppLog.Warn("EngineFactory", $"Create: {CurrentModeName}");
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
