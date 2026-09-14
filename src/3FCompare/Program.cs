using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace _3FCompare;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 进程退出钩子：卸载内核日志 sink + 冲刷日志。
        // 内核解码/播放线程可能活过托管侧——不注销回调，CLR 停机后它们反向 P/Invoke
        // 会触发 coreclr ceemain.cpp:1750 断言（"Attempt to execute managed code after the
        // .NET runtime thread state has been destroyed."）并让进程以 127 退出。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _3FCompare.Core.Diagnostics.KernelLogBridge.Uninstall(); } catch { }
            try { _3FCompare.Core.Diagnostics.AppLog.Shutdown(); } catch { }
        };

        // F-LOG：落盘日志最先初始化（捕获从第一行起的全部内容）
        _3FCompare.Core.Diagnostics.AppLog.Initialize();
        // 双写器：全代码库 Console.Error.WriteLine 自动同步落盘（55 处调用点零改动）
        _3FCompare.Core.Diagnostics.ConsoleErrorRerouter.Install();
        _3FCompare.Core.Diagnostics.AppLog.Info("App",
            $"启动 args=[{string.Join(' ', args)}]");

        // 内嵌 FFF.Native.dll 自解压（3FP 播放器内核）
        try
        {
            _3FCompare.Core.Backend.NativeRuntime.ExtractEmbeddedDll(
                name => System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name) is { } s
                    ? ReadAll(s)
                    : null);
            _3FCompare.Core.Diagnostics.AppLog.Debug("Native", "FFF.Native.dll 自解压完成");
        }
        catch (Exception ex)
        {
            _3FCompare.Core.Diagnostics.AppLog.Warn("Native", $"自解压跳过: {ex.Message}");
        }

        // F-LOG：安装内核日志 sink（内核线程的日志汇入同一落盘通道）
        // FFF_NO_KERNEL_LOG=1 可禁用，用于定位退出期 CLR 反向 P/Invoke 断言的来源
        try
        {
            if (Environment.GetEnvironmentVariable("FFF_NO_KERNEL_LOG") != "1")
                _3FCompare.Core.Diagnostics.KernelLogBridge.Install();
        }
        catch (Exception ex)
        {
            _3FCompare.Core.Diagnostics.AppLog.Warn("Kernel", $"sink 安装失败: {ex.Message}");
        }

        // 内嵌 Avalonia 原生 DLL 自解压（libSkiaSharp.dll / libHarfBuzzSharp.dll）
        try
        {
            ExtractEmbedded("libSkiaSharp.dll");
            ExtractEmbedded("libHarfBuzzSharp.dll");
        }
        catch { /* 忽略失败 */ }

        // --selftest <video> [video2]：video2 用于嵌入式 UI 消息注入拖入测试（可选）
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            RunSelftest(args[1], args.Length >= 3 ? args[2] : null);
            return;
        }
        // --screentest <input> <png>：打开→就绪+500ms→抓表面0→存 PNG（>1000B 判过）
        if (args.Length >= 3 && args[0] == "--screentest")
        {
            RunScreentest(args[1], args[2]);
            return;
        }
        // --sessiontest <video> [video2] [video3]：会话保存→清空→重载，断言路数/位置/自动播放
        if (args.Length >= 2 && args[0] == "--sessiontest")
        {
            RunSessiontest(args[1..]);
            return;
        }
        // --autodemo <files...>：自动打开并播放（演示/巡检模式）
        if (args.Length >= 3 && args[0] == "--autodemo")
        {
            AutodemoFiles = args[1..];
            var exitCode = 1;
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(OriginalArgs());
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

    /// <summary>进程原始命令行参数（去掉 argv[0] 的可执行文件路径）。
    /// <para><b>为什么必须透传原始参数</b>：MainWindow 的模式分发读的是
    /// <see cref="Environment.GetCommandLineArgs"/>（进程真实命令行），
    /// <b>不是</b>传给 <c>StartWithClassicDesktopLifetime</c> 的这份数组。
    /// 历史实现在这里手工拼了 "--selftest-internal" / "--sessiontest-internal" /
    /// "--screentest-internal" / "--autodemo-internal" 之类的标记，
    /// 它们从未被任何代码读到过，只是让后来者误以为分发靠的是这个数组。</para></summary>
    private static string[] OriginalArgs() => Environment.GetCommandLineArgs()[1..];

    private static void RunSelftest(string videoPath, string? dropVideoPath = null)
    {
        var exitCode = 1;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(OriginalArgs());
            exitCode = SelftestResult.Code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"selftest: 异常 {ex}");
            exitCode = 2;
        }
        Environment.Exit(exitCode);
    }

    private static void RunSessiontest(string[] videos)
    {
        var exitCode = 1;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(OriginalArgs());
            exitCode = SelftestResult.Code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sessiontest: 异常 {ex}");
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
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(OriginalArgs());
            exitCode = ScreentestResult;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"screentest: 异常 {ex}");
            exitCode = 2;
        }
        Environment.Exit(exitCode);
    }

    /// <summary>selftest 结果（由 MainWindow 自动化流程写入）。默认 = 失败，
    /// 防止 MainWindow 未写入时（启动即崩）误报成功。</summary>
    public static (int Code, string Message) SelftestResult = (1, "selftest 未完成");

    /// <summary>autodemo 待打开文件（App 创建主窗时消费）。</summary>
    public static string[]? AutodemoFiles;

    /// <summary>screentest 结果（0=成功 >1000B；1=失败；由 MainWindow 写入）。</summary>
    public static int ScreentestResult = 1;

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<AppEntry>()
            .UseWin32()
            .With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Wgl, Win32RenderingMode.Software },
            })
            .UseSkia()
            .UseHarfBuzz()
            .LogToTrace();

    private static byte[] ReadAll(System.IO.Stream s)
    {
        using var ms = new System.IO.MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>将嵌入的资源 DLL 提取到应用目录（供 P/Invoke 加载）。</summary>
    private static void ExtractEmbedded(string dllName)
    {
        var target = Path.Combine(AppContext.BaseDirectory, dllName);
        if (File.Exists(target)) return; // 已存在则跳过
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(dllName);
        if (stream is null) return; // 未嵌入（开发运行或非内嵌发布）
        var data = new byte[stream.Length];
        stream.ReadExactly(data, 0, data.Length);
        File.WriteAllBytes(target, data);
    }
}
