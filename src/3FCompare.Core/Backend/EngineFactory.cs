using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Backend;

/// <summary>引擎工厂：自动探测 FFF.Native 是否可用，选择真实 3FP 后端或演示后端。
/// 可通过 <see cref="NativeRuntime.SetFfmpegDirectory"/> 指定 FFmpeg DLL 搜索目录。</summary>
public static class EngineFactory
{
    /// <summary>探测真实 3FP 后端是否可用。
    /// 需同时满足：① FFF.Native.dll 可加载（应用目录/PATH）；② FFmpeg 核心 DLL 已在应用目录。
    /// 缺 FFmpeg 时 FFF.Native 虽能加载，但打开视频时 Delay-Load FFmpeg 会原生崩溃，
    /// 故此时应回退演示模式（见 NativeRuntime.IsFfmpegAvailable）。</summary>
    public static bool IsNativeAvailable()
    {
        // 先低成本检查 FFmpeg（避免加载 FFF.Native 后才发现不可用）
        if (!NativeRuntime.IsFfmpegAvailable())
        {
            Console.Error.WriteLine("[EngineFactory] IsNativeAvailable: IsFfmpegAvailable=false");
            return false;
        }
        try
        {
            var ver = Fff3FpNativeProbe.FFF3FP_GetApiVersion();
            Console.Error.WriteLine($"[EngineFactory] IsNativeAvailable: GetApiVersion={ver}");
            return ver >= 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Console.Error.WriteLine($"[EngineFactory] IsNativeAvailable: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>当前引擎模式名称（用于 UI 显示）。</summary>
    public static string CurrentModeName
        => IsNativeAvailable() ? "FFF.Native (3FP)" : SimulatedEngine.ModeName;
}