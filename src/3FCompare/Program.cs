using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace _3FCompare;

internal static class Program
{
    // 将子目录加入 DLL 搜索路径，使 ffmpeg 等 DLL 可从 exe 同级子目录加载
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    [STAThread]
    public static void Main(string[] args)
    {
        // 添加 ffmpeg 子目录到 DLL 搜索路径（发布包结构：ffmpeg/*.dll 与 exe 分开放置）
        try
        {
            var ffmpegDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (Directory.Exists(ffmpegDir))
                SetDllDirectory(ffmpegDir);
        }
        catch { /* 忽略失败（非核心功能）*/ }

        // 内嵌 FFF.Native.dll 自解压（EmbedFffNative 发布形态；已存在则跳过）
        try
        {
            _3FCompare.Core.Backend.NativeRuntime.ExtractEmbeddedDll(
                name => System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name) is { } s
                    ? ReadAll(s)
                    : null);
        }
        catch { /* 非内嵌形态（精简版/开发运行）：忽略 */ }

        // --selftest <video>：打开→等就绪→步进断言→自动播放断言（WinForms 版语义一致）
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            RunSelftest(args[1]);
            return;
        }
        // --screentest <input> <png>：打开→就绪+500ms→抓表面0→存 PNG（>1000B 判过）
        if (args.Length >= 3 && args[0] == "--screentest")
        {
            RunScreentest(args[1], args[2]);
            return;
        }
        // --autodemo <files...>：自动打开并播放（演示/巡检模式）
        if (args.Length >= 3 && args[0] == "--autodemo")
        {
            AutodemoFiles = args[1..];
            var exitCode = 1;
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(new[] { "--autodemo-internal" });
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"autodemo: 异常 {ex}");
                exitCode = 2;
            }
            Environment.Exit(exitCode);
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void RunSelftest(string videoPath)
    {
        var exitCode = 1;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(new[] { "--selftest-internal", videoPath });
            exitCode = SelftestResult.Code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"selftest: 异常 {ex}");
            exitCode = 2;
        }
        Environment.Exit(exitCode);
    }

    private static void RunScreentest(string input, string outputPng)
    {
        var exitCode = 1;
        try
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"screentest: 文件不存在 {input}");
                Environment.Exit(2);
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                new[] { "--screentest-internal", input, outputPng });
            exitCode = ScreentestResult;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"screentest: 异常 {ex}");
            exitCode = 2;
        }
        Environment.Exit(exitCode);
    }

    /// <summary>selftest 结果（由 MainWindow 自动化流程写入）。</summary>
    public static (int Code, string Message) SelftestResult;

    /// <summary>autodemo 待打开文件（App 创建主窗时消费）。</summary>
    public static string[]? AutodemoFiles;

    /// <summary>screentest 结果（0=成功 >1000B；1=失败；由 MainWindow 写入）。</summary>
    public static int ScreentestResult = 1;

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<AppEntry>()
            .UsePlatformDetect()
            .LogToTrace();

    private static byte[] ReadAll(System.IO.Stream s)
    {
        using var ms = new System.IO.MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
