using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using _3FCompare.Core.Backend;

namespace _3FCompare.Avalonia;

/// <summary>M0 PoC 主窗体：
/// PoC-A: NativeControlHost 承载 Win32 子窗口 + GDI 色块绘制 → 验证承载/焦点/DPI
/// PoC-B: FFF.Native 真实会话输出到该子窗口 → selftest 等价验证</summary>
public partial class MainWindow : Window
{
    private HostSurface? _host;
    private IPlayerEngine? _engine;
    private IPlayerSession? _session;
    private bool _realMode;

    public MainWindow()
    {
        InitializeComponent();
        AttachHost();

        // 自动化 selftest 模式：进程命令行 --selftest <video>
        // （注意：GetCommandLineArgs 返回进程原始参数，不是 StartWithClassicDesktopLifetime 转发的数组）
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 3 && args[1] == "--selftest")
            _ = RunSelftestAsync(args[2]);
    }

    /// <summary>当前执行到的步骤名（自动化卡死诊断用）。</summary>
    private static volatile string _step = "启动";

    private static void Log(string msg)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} selftest[{_step}]: {msg}");
        Console.Out.Flush();
    }

    /// <summary>PoC-B 自动化流程：等承载就绪→打开视频→等就绪→Play→验证状态→退出。</summary>
    private async System.Threading.Tasks.Task RunSelftestAsync(string videoPath)
    {
        // 看门狗：任一步骤卡死 25s 则带诊断信息退出（不无限挂起）
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var last = _step; var stable = 0;
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(1000);
                stable = _step == last ? stable + 1 : 0;
                last = _step;
                if (stable >= 25)
                {
                    Console.Error.WriteLine($"selftest: 看门狗触发 ✗ 卡在步骤 [{_step}] 超过 25s");
                    Console.Error.Flush();
                    Environment.Exit(3);
                }
            }
        });
        try
        {
            // 等 NativeControlHost 附件完成（AttachedToVisualTree 触发后 HWND 才有效）
            _step = "等HWND";
            for (var i = 0; i < 50 && (_host?.Hwnd ?? 0) == 0; i++)
                await System.Threading.Tasks.Task.Delay(100);
            if ((_host?.Hwnd ?? 0) == 0)
                throw new InvalidOperationException("子窗口 HWND 未创建（NativeControlHost 附件失败）");

            Log($"子窗口 HWND={_host.Hwnd} ✓");
            Log($"打开 {videoPath}");

            _step = "EngineFactory";
            _engine = EngineFactory.Create();
            _realMode = _engine is Fff3FpEngine;
            Log($"模式={( _realMode ? "真实(FFF.Native)" : "演示(Simulated)" )}");

            _step = "CreateSession";
            _session = _engine.CreateSession(new EngineSessionOptions
            {
                OutputWindow = _host.Hwnd,
                HardwareDecode = false,
            });
            Log("会话已创建 ✓（含 DXGI 显示器探测）");

            _step = "OpenAsync";
            await _session.OpenAsync(videoPath);
            Log("Open 调用返回");

            // 等待后端真正就绪（与 WinForms 版相同的 3FP 异步 Open 时序处理）
            _step = "等就绪";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _session.ReadSnapshot();
                Log($"轮询 State={snap.State}");
                if (snap.State is PlayerState.Ready or PlayerState.Playing or PlayerState.Paused) break;
                if (snap.State == PlayerState.Failed)
                    throw new IOException("内核打开失败");
                await System.Threading.Tasks.Task.Delay(100);
            }

            _step = "Play";
            _session.Play();
            _step = "Play后等500ms";
            await System.Threading.Tasks.Task.Delay(500);
            var final = _session.ReadSnapshot();
            var dur = TimeSpan.FromTicks(final.Duration100ns);
            Log($"状态={final.State} 时长={dur:hh\\:mm\\:ss} 帧号={final.FrameIndex}");

            if (final.State != PlayerState.Playing)
                throw new InvalidOperationException($"播放未启动（状态={final.State}）");

            Program.SelftestResult = (0, "全部通过");
            Log("全部通过 ✓");
        }
        catch (Exception ex)
        {
            Program.SelftestResult = (2, ex.Message);
            Console.Error.WriteLine($"selftest[步骤{_step}]: 失败 ✗ {ex.Message}");
            Console.Error.Flush();
        }
        finally
        {
            // 注意：Close() 在窗口未完全显示时不会终止 ClassicDesktopLifetime，
            // 自动化模式直接强制退出，保证退出码与不挂起
            Console.Out.Flush();
            Environment.Exit(Program.SelftestResult.Code);
        }
    }

    // ---------- PoC-A: 承载子窗口 ----------

    private void AttachHost()
    {
        _host = new HostSurface();
        HostContainer.Content = _host;
        StatusText.Text = $"PoC-A: 子窗口 HWND={_host.Hwnd} 已创建";
    }

    private void OnGdiTestClick(object? sender, RoutedEventArgs e)
    {
        if (_host is null || _host.Hwnd == 0) { StatusText.Text = "PoC-A 失败：无有效 HWND"; return; }
        _host.DrawTestPattern();
        StatusText.Text = "PoC-A ✓ GDI 色块已绘制到子窗口（若可见则承载成功）";
    }

    // ---------- PoC-B: 真实引擎接入 ----------

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择测试视频",
            AllowMultiple = false,
        });
        if (files is { Count: > 0 })
            OpenVideo(files[0].TryGetLocalPath() ?? "");
    }

    private async void OpenVideo(string path)
    {
        try
        {
            BtnOpen.IsEnabled = false;
            _engine ??= EngineFactory.Create();
            _realMode = _engine is Fff3FpEngine;
            StatusText.Text = $"打开中… (模式={( _realMode ? "真实" : "演示" )})";

            var hwnd = _host?.Hwnd ?? 0;
            _session?.Dispose();
            _session = _engine.CreateSession(new EngineSessionOptions
            {
                OutputWindow = hwnd,
                HardwareDecode = false,
            });

            await _session.OpenAsync(path);

            // 等待后端真正就绪（3FP 的 Open 是异步入队，与 WinForms 版相同的时序问题）
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _session.ReadSnapshot();
                if (snap.State is PlayerState.Ready or PlayerState.Playing or PlayerState.Paused) break;
                if (snap.State == PlayerState.Failed) throw new IOException("内核打开失败");
                await Task.Delay(100);
            }

            var snap2 = _session.ReadSnapshot();
            StatusText.Text = $"PoC-B: 就绪 ✓ 状态={snap2.State} 时长={TimeSpan.FromTicks(snap2.Duration100ns):hh\\:mm\\:ss}";
            BtnPlay.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"PoC-B ✗ {ex.Message}";
        }
        finally
        {
            BtnOpen.IsEnabled = true;
        }
    }

    private void OnPlayClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            _session?.Play();
            var snap = _session!.ReadSnapshot();
            StatusText.Text = $"播放指令已发 → 状态={snap.State}（Playing 即 PoC-B 全过）";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"播放失败: {ex.Message}";
        }
    }
}

/// <summary>Win32 子窗口承载器：NativeControlHost 创建真实 HWND，
/// 供原生 D3D 输出或 GDI 直接绘制。M0 核心验证对象。</summary>
public sealed class HostSurface : NativeControlHost
{
    private IntPtr _hwnd;

    public nint Hwnd => _hwnd;

    public HostSurface()
    {
        // AttachedToLogicalTree 后才有顶层窗口，此时再创建子 HWND
        AttachedToVisualTree += (_, _) =>
        {
            _hwnd = CreateChildWindow();
            // NativeControlHost 的子控件定位由 QueryContinueChild/平台实现处理；
            // PoC 阶段子窗口尺寸固定，后续 M2 再做跟随布局。
        };
        DetachedFromVisualTree += (_, _) => DestroyWindow(_hwnd);
    }

    /// <summary>创建子窗口并返回其 HWND（父 = Avalonia 顶层窗口）。</summary>
    private IntPtr CreateChildWindow()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var parent = topLevel?.TryGetPlatformHandle()?.Handle ?? GetActiveWindow();
        if (parent == IntPtr.Zero)
            throw new InvalidOperationException("无法获取 Avalonia 顶层 HWND");

        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        var hwnd = CreateWindowExW(
            0, "STATIC", null, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 800, 450, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
        return hwnd;
    }

    /// <summary>PoC-A：GDI 绘制测试色块（渐变 + 文字），验证子窗口可见性与 DPI 缩放。</summary>
    public void DrawTestPattern()
    {
        if (_hwnd == IntPtr.Zero) return;
        var hdc = GetDC(_hwnd);
        try
        {
            if (!GetClientRect(_hwnd, out var rc)) return;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;

            using var g = System.Drawing.Graphics.FromHdc(hdc);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Color.FromArgb(30, 90, 200),
                System.Drawing.Color.FromArgb(15, 15, 20), 45f);
            g.FillRectangle(brush, 0, 0, w, h);
            g.DrawString($"HWND 承载成功  {w}x{h}",
                new System.Drawing.Font("Segoe UI", 16),
                System.Drawing.Brushes.White, 24, 24);
        }
        finally
        {
            ReleaseDC(_hwnd, hdc);
        }
    }

    // ---- Win32 P/Invoke（自声明，避免依赖 Avalonia internal API）----

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
