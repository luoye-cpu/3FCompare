using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Backend;

/// <summary>引擎工厂：自动探测 FFF.Native 是否可用，选择真实 3FP 后端或演示后端。
/// 可通过 <see cref="NativeRuntime.SetFfmpegDirectory"/> 指定 FFmpeg DLL 搜索目录。</summary>
public static class EngineFactory
{
    /// <summary>探测 FFF.Native.dll 是否存在于应用目录 / 当前目录 / PATH。</summary>
    public static bool IsNativeAvailable()
    {
        try
        {
            // 直接尝试加载并调用无副作用函数，最可靠。
            return Fff3FpNativeProbe.FFF3FP_GetApiVersion() >= 1;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
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