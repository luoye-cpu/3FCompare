using Avalonia;
using System;

namespace _3FCompare.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // --selftest <video>：自动化验证（打开→等就绪→Play→截图→退出码），与 WinForms 版语义一致
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            RunSelftest(args[1]);
            return;
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

    /// <summary>selftest 结果（由 MainWindow 在自动化流程结束时写入）。</summary>
    public static (int Code, string Message) SelftestResult;

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
