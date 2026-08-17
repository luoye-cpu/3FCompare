using System.Diagnostics;
using System.Runtime.InteropServices;

namespace _3FCompare.Core.Backend;

/// <summary>原生运行时路径管理：手动指定 FFmpeg DLL 目录、自释放嵌入的 FFF.Native.dll。
/// 原理：将用户目录中的 FFmpeg DLL 复制到应用目录（与 FFF.Native 同级），
/// 使内核的 Delay-Load 在应用目录中直接命中。最可靠的方式。
/// 兼容 NativeAOT：仅 P/Invoke + P/Invoke Source Generator。</summary>
public static partial class NativeRuntime
{
    /// <summary>当前生效的 FFmpeg 目录（null = 未手动指定，走自动检测）。</summary>
    public static string? FfmpegDirectory { get; private set; }

    /// <summary>从嵌入资源释放 FFF.Native.dll 到应用目录（如果尚未存在或版本不同）。
    /// 应当只在 MainForm 启动时调用一次，在 EngineFactory 探测之前。</summary>
    public static void ExtractEmbeddedDll(Func<string, byte[]?> resourceLoader)
    {
        var targetDir = AppContext.BaseDirectory;
        if (!Directory.Exists(targetDir)) return;

        var targetPath = Path.Combine(targetDir, "FFF.Native.dll");
        try
        {
            if (File.Exists(targetPath))
            {
                // 如果已有且版本相同则跳过
                var existing = FileVersionInfo.GetVersionInfo(targetPath);
                var embedded = resourceLoader("FFF.Native.dll");
                if (embedded is not null)
                {
                    // 用临时文件取版本比较
                    var tmp = Path.GetTempFileName();
                    try
                    {
                        File.WriteAllBytes(tmp, embedded);
                        var embeddedVer = FileVersionInfo.GetVersionInfo(tmp);
                        if (embeddedVer.FileVersion == existing.FileVersion)
                            return; // 版本一致，无需覆盖
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                else
                {
                    return; // 无可嵌入资源，保持现有
                }
            }

            var data = resourceLoader("FFF.Native.dll");
            if (data is not null)
            {
                File.WriteAllBytes(targetPath, data);
                Console.Error.WriteLine($"[NativeRuntime] 已释放 FFF.Native.dll ({data.Length / 1024} KB)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NativeRuntime] 释放 FFF.Native.dll 失败: {ex.Message}");
        }
    }

    /// <summary>设置 FFmpeg DLL 搜索目录（null/空白 = 清除手动设置，恢复自动检测）。
    /// 将用户目录中的 FFmpeg DLL 复制到应用目录，确保内核 Delay-Load 可命中。</summary>
    public static void SetFfmpegDirectory(string? directory)
    {
        FfmpegDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();
        if (FfmpegDirectory is not null)
            CopyDlls(FfmpegDirectory);
    }

    /// <summary>验证目录是否包含 FFmpeg 核心 DLL（avcodec-*.dll）。</summary>
    public static string? ValidateFfmpegDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "目录为空";
        var dir = directory.Trim();
        if (!Directory.Exists(dir)) return "目录不存在";
        var match = Directory.GetFiles(dir, "avcodec-*.dll").FirstOrDefault()
            ?? Directory.GetFiles(dir, "avcodec*.dll").FirstOrDefault();
        if (match is null)
            return "目录中未找到 avcodec DLL（请选择包含 bin 的 FFmpeg 目录）";
        return null; // 有效
    }

    /// <summary>探测：设置目录后是否能让 FFF.Native 加载成功。</summary>
    public static bool IsNativeAvailableWithDirectory(string? directory)
    {
        var existing = FfmpegDirectory;
        try
        {
            SetFfmpegDirectory(directory);
            return EngineFactory.IsNativeAvailable();
        }
        finally
        {
            SetFfmpegDirectory(existing);
        }
    }

    /// <summary>将用户目录的 FFmpeg / ass DLL 复制到应用目录（用户手动设置优先，无条件覆盖）。</summary>
    private static void CopyDlls(string sourceDir)
    {
        if (!Directory.Exists(sourceDir)) return;

        var targetDir = AppContext.BaseDirectory;
        if (!Directory.Exists(targetDir)) return;

        var copied = 0;
        foreach (var dll in Directory.GetFiles(sourceDir, "*.dll"))
        {
            var name = Path.GetFileName(dll);
            if (!(name.StartsWith("av", StringComparison.OrdinalIgnoreCase) ||
                  name.StartsWith("sw", StringComparison.OrdinalIgnoreCase) ||
                  name.StartsWith("ass", StringComparison.OrdinalIgnoreCase) ||
                  name.StartsWith("postproc", StringComparison.OrdinalIgnoreCase)))
                continue;

            var target = Path.Combine(targetDir, name);
            try
            {
                File.Copy(dll, target, overwrite: true);
                copied++;
            }
            catch { /* 个别文件复制失败不影响整体 */ }
        }
        if (copied > 0)
            Console.Error.WriteLine($"[NativeRuntime] 已复制 {copied} 个 DLL 从 {sourceDir} 到 {targetDir}");
    }
}