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
        // 进程级未处理异常兜底（必须最先注册，早于任何可能抛异常的初始化）。
        // App.axaml.cs 只覆盖了 UI 线程调度异常（Dispatcher.UnhandledException）与
        // 未观察任务异常；工作线程——内核回调线程、Task.Run 里的 StepFramesAsync / 抓帧
        // 任务——抛出的未处理异常既不留痕也不落盘，进程直接静默终止，事后连崩溃点都看不到。
        // ⚠ 本处理器内**不得**再抛出任何异常：异常处理器里二次抛异常会让进程以更糟的方式
        // 退出并丢掉原始信息。故每一步都单独 try/catch 兜底。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                // 日志系统此时可能尚未初始化（AppLog.Initialize 在下面几行），
                // 也可能已经在跑——两种情况都要安全：前者 Enqueue 会自动丢弃。
                var text = e.ExceptionObject is Exception ex
                    ? $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                    : e.ExceptionObject?.ToString() ?? "(未知异常对象)";
                try { _3FCompare.Core.Diagnostics.AppLog.Error("Unhandled", $"IsTerminating={e.IsTerminating} {text}"); } catch { }
                try { Console.Error.WriteLine($"[UnhandledException] IsTerminating={e.IsTerminating} {text}"); Console.Error.Flush(); } catch { }
                // 进程马上就要没了，后台写线程来不及自然轮转 ⇒ 显式冲刷，
                // 否则最关键的"最后几行"永远留在内存队列里。
                try { _3FCompare.Core.Diagnostics.AppLog.Shutdown(); } catch { }
            }
            catch { }
        };

        // 进程退出钩子：卸载内核日志 sink + 冲刷日志。
        // 内核解码/播放线程可能活过托管侧——不注销回调，CLR 停机后它们反向 P/Invoke
        // 会触发 coreclr ceemain.cpp:1750 断言（"Attempt to execute managed code after the
        // .NET runtime thread state has been destroyed."）并让进程以 127 退出。
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { _3FCompare.Core.Diagnostics.KernelLogBridge.Uninstall(); } catch { }
            try { _3FCompare.Core.Diagnostics.AppLog.Shutdown(); } catch { }
            try { _3FCompare.Diagnostics.ComponentLog.Shutdown(); } catch { }
        };

        // F-LOG：落盘日志最先初始化（捕获从第一行起的全部内容）
        _3FCompare.Core.Diagnostics.AppLog.Initialize();
        // 组件生命周期日志（逐条 flush，硬崩也不丢最后几秒）：与 AppLog 并列，写 logs/component-*.log
        _3FCompare.Diagnostics.ComponentLog.Initialize();
        // 已知注入钩子在场检测（RTSS/MSI Afterburner / NVIDIA 覆盖层，docs/33 §八）：
        // 只记一条组件日志，不弹窗、不阻断启动。注入发生在进程创建期，故此刻即可探到。
        try { _3FCompare.Diagnostics.HookDetector.ProbeAndLog(); } catch { }
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

        // 内嵌 WGC 抓屏原生库自解压（3FC.WgcCapture.dll；docs/27 §九 阶段 3）
        try
        {
            ExtractEmbedded("3FC.WgcCapture.dll");
            _3FCompare.Core.Diagnostics.AppLog.Debug("Native", "3FC.WgcCapture.dll 自解压完成");
        }
        catch (Exception ex)
        {
            // 不崩：FrameCapture 探测到 DLL 缺失会 WARN 一次并走 GDI 兜底（docs/27 §三）
            _3FCompare.Core.Diagnostics.AppLog.Warn("Native", $"3FC.WgcCapture.dll 自解压跳过: {ex.Message}");
        }

        // 内嵌 Avalonia 原生 DLL 自解压（libSkiaSharp.dll / libHarfBuzzSharp.dll）
        try
        {
            ExtractEmbedded("libSkiaSharp.dll");
            ExtractEmbedded("libHarfBuzzSharp.dll");
        }
        catch { /* 忽略失败 */ }

        // ── 崩溃自愈（docs/43）：子进程标记 ──
        // 由守护进程拉起的这一代带 { --child, 父进程 PID }。必须先于其它分发处理：
        // ① 剥掉这两个参数，后面的模式分发看到的才是用户真正的命令行；
        // ② 装上"父进程存活"看门狗，避免守护被强杀后本进程变成孤儿。
        var isGuardChild = false;
        if (args.Length >= 2 && args[0] == _3FCompare.Diagnostics.CrashGuard.ChildArg)
        {
            isGuardChild = true;
            var ppid = int.TryParse(args[1], out var parsedPid) ? parsedPid : 0;
            _3FCompare.Diagnostics.CrashGuard.StartParentWatcher(ppid);
            var rest = args[2..];
            // 自检子进程体：前 N 次以访问违规退出，用来端到端验证守护循环
            if (rest.Length >= 2 && rest[0] == _3FCompare.Diagnostics.CrashGuard.SelfTestArg)
            {
                var counter = rest[1];
                var times = rest.Length >= 3 && int.TryParse(rest[2], out var ct)
                    ? ct : _3FCompare.Diagnostics.CrashGuard.MaxConsecutiveRestarts - 1;
                Environment.Exit(_3FCompare.Diagnostics.CrashGuard.RunSelfTestChild(counter, times));
            }
            args = rest;
        }

        // ── 崩溃自愈：守护自检（父侧）── 不需要真的制造原生崩溃，见 CrashGuard.RunSelfTest
        if (args.Length >= 2 && args[0] == _3FCompare.Diagnostics.CrashGuard.SelfTestArg)
        {
            var counter = args[1];
            var times = args.Length >= 3 && int.TryParse(args[2], out var ct)
                ? ct : _3FCompare.Diagnostics.CrashGuard.MaxConsecutiveRestarts - 1;
            Environment.Exit(_3FCompare.Diagnostics.CrashGuard.RunSelfTest(counter, times));
        }

        // ── 崩溃自愈：守护模式 ──
        // 只在"裸 GUI 启动"（命令行里没有任何 -- 前缀参数）时生效。所有自动化门禁
        // 都带模式位，因此不受影响；子进程带 --child，天然不递归。
        if (!isGuardChild && _3FCompare.Diagnostics.CrashGuard.ShouldGuard(args))
        {
            Environment.Exit(_3FCompare.Diagnostics.CrashGuard.RunGuard(args));
        }

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
        if (args.Length >= 3 && args[0] == _3FCompare.Diagnostics.CrashGuard.AutodemoArg)
        {
            // 过滤掉 `--` 前缀项：崩溃自愈重启时 CrashGuard 会把 `--crash-restore` 追加到
            // 命令行末尾（`--child` + PID 已在上面剥离），它不是素材路径。
            // 原实现直接 `args[1..]` ⇒ 重启后的这一代会把恢复标记当成一路素材去打开
            //（docs/45 P1-2），表现为路数虚增、failed+1。
            AutodemoFiles = Array.FindAll(args[1..],
                a => !a.StartsWith("--", StringComparison.Ordinal));
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

    /// <summary>将嵌入的资源 DLL 提取到应用目录（供 P/Invoke 加载）。
    /// <para><b>安全策略：目标文件已存在时绝不覆盖</b>（FFF.Native.dll / libSkiaSharp.dll /
    /// libHarfBuzzSharp.dll / 3FC.WgcCapture.dll 一视同仁）。理由：这些原生库都由本仓库的
    /// 构建脚本（构建全部.ps1 / native/wgc_capture/build.sh）单独产出，开发者磁盘上的版本
    /// 可能比当前发布批次里内嵌的那份**更新**；按内容覆盖会把新版降级成旧基线
    /// （2026-09-16 内核事故：API 15 被覆盖成 API 14，表现为会话全部创建失败）。
    /// 内嵌资源只作"磁盘上确实没有"时的兜底，所以写盘仅限缺失时。</para></summary>
    private static void ExtractEmbedded(string dllName)
    {
        var target = Path.Combine(AppContext.BaseDirectory, dllName);
        if (File.Exists(target)) return; // 已存在则跳过（见上方安全策略）
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(dllName);
        if (stream is null) return; // 未嵌入（开发运行或非内嵌发布）
        var data = new byte[stream.Length];
        stream.ReadExactly(data, 0, data.Length);
        File.WriteAllBytes(target, data);
    }
}
