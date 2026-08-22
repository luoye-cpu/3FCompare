using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using _3FCompare.Avalonia.Platform;
using _3FCompare.Avalonia.ViewModels;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.App;

namespace _3FCompare.Avalonia;

/// <summary>主窗口（M1 骨架）：菜单/快捷键/窗口几何记忆/状态栏。
/// 中央网格、传输栏、时间轴、侧栏为占位，M2/M3 实装。</summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _vm = new();

    private HostSurface? _host;
    private IPlayerEngine? _engine;
    private IPlayerSession? _session;
    private bool _realMode;
    private bool _fullscreen;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        DataContext = _vm;
        _vm.IsRealMode = _realMode = EngineFactory.IsNativeAvailable();
        _vm.EngineLabel = LanguageManager.T(_realMode ? "Status_EngineReal" : "Status_EngineDemo");
        StatusEngine.Text = _vm.EngineLabel;
        StatusInfo.Text = LanguageManager.T(_realMode ? "Status_Ready" : "Status_DemoHint");

        RestoreWindowGeometry();

        // 拖放打开（M2 接入完整打开管线，此处先注册目标）
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 自动化 selftest 模式：进程命令行 --selftest <video>
        // （GetCommandLineArgs 返回进程原始参数，不是 StartWithClassicDesktopLifetime 转发的数组）
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 3 && args[1] == "--selftest")
            _ = RunSelftestAsync(args[2]);
    }

    // ══════════ 窗口几何记忆（WinForms OnFormClosing 等价） ══════════

    /// <summary>最近一次普通（非最大化/全屏）态的位置与尺寸。</summary>
    private (PixelPoint Pos, Size Size)? _lastNormal;

    private void RestoreWindowGeometry()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (_settings.WindowWidth > 0 && _settings.WindowHeight > 0)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
            if (_settings.WindowX >= 0 && _settings.WindowY >= 0 && screen is not null)
            {
                // 钳制到工作区，避免恢复到已拔掉的显示器
                var wa = screen.WorkingArea;
                var x = Math.Clamp(_settings.WindowX, wa.X, wa.Right - 200);
                var y = Math.Clamp(_settings.WindowY, wa.Y, wa.Bottom - 200);
                Position = new PixelPoint(x, y);
            }
        }
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnOpened(EventArgs e)
    {
        if (WindowState == WindowState.Normal)
            _lastNormal = (Position, new Size(Width, Height));
        base.OnOpened(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState == WindowState.Normal)
            _lastNormal = (Position, Bounds.Size);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var normal = _lastNormal ?? (Position, Bounds.Size);
        if (normal.Size.Width > 0)
        {
            _settings.WindowX = normal.Pos.X;
            _settings.WindowY = normal.Pos.Y;
            _settings.WindowWidth = (int)normal.Size.Width;
            _settings.WindowHeight = (int)normal.Size.Height;
        }
        SettingsStore.Save(_settings);
        base.OnClosing(e);
    }

    // ══════════ 快捷键（WinForms ProcessCmdKey 全表复刻） ══════════

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Space when mods == KeyModifiers.None:
                TogglePlayPause(); break;
            case Key.S when mods.HasFlag(KeyModifiers.Control):
                OnExportFrame(this, e); break;
            case Key.Left when mods == KeyModifiers.None:
                StepFrames(-1); break;
            case Key.Right when mods == KeyModifiers.None:
                StepFrames(1); break;
            case Key.Left when mods.HasFlag(KeyModifiers.Shift):
                StepSeconds(-1); break;
            case Key.Right when mods.HasFlag(KeyModifiers.Shift):
                StepSeconds(1); break;
            case Key.Up:
                StepSeconds(10); break;
            case Key.Down:
                StepSeconds(-10); break;
            case Key.F11:
                ToggleFullscreen(); break;
            case Key.Escape when _fullscreen:
                ToggleFullscreen(); break;
            case Key.O when mods == KeyModifiers.None:
                OnOpenVideos(this, e); break;
            case Key.B when mods == KeyModifiers.None:
                OnToggleAbSlider(this, e); break;
            case Key.P when mods == KeyModifiers.None:
                OnToggleProbe(this, e); break;
            case Key.F6:
                OnToggleOffset(this, e); break;
            case Key.R when mods == KeyModifiers.None:
                ResetViewTransform(); break;
            case Key.Delete:
                RemoveSelectedBookmark(); break;
            case Key.D1: GrowLanes(1); break;
            case Key.D2: GrowLanes(2); break;
            case Key.D3: GrowLanes(3); break;
            case Key.D4: GrowLanes(4); break;
            case Key.D5: GrowLanes(5); break;
            case Key.D6: GrowLanes(6); break;
            case Key.D7: GrowLanes(7); break;
            case Key.D8: GrowLanes(8); break;
            case Key.D9: GrowLanes(9); break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    // ══════════ 播放操作（M2 接 SyncController；M1 安全占位） ══════════

    private void TogglePlayPause() => Pending("播放/暂停", "M2");
    private void StepFrames(int delta) => Pending($"帧步进 {delta:+0;-0}", "M2");
    private void StepSeconds(double delta) => Pending($"秒步进 {delta:+0.#;-0.#}", "M2");
    private void ResetViewTransform() => Pending("视图重置", "M2");
    private void GrowLanes(int upTo) => Pending($"路数 → {upTo}", "M2");
    private void RemoveSelectedBookmark() => Pending("删除书签", "M3");

    private void Pending(string what, string milestone) =>
        StatusInfo.Text = $"{what} —— {milestone} 实装";

    // ══════════ 菜单：文件 ══════════

    private async void OnOpenVideos(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageManager.T("Menu_Open"),
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Media")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.webm", "*.avi", "*.ts", "*.m2ts", "*.flv", "*.wmv" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (files is not { Count: > 0 }) return;
        // M2：接入多路打开管线（PlaybackCoordinator.OpenFiles）
        var paths = string.Join(", ", files.Select(f => Path.GetFileName(f.TryGetLocalPath() ?? f.Name)));
        StatusInfo.Text = $"已选择（M2 打开管线实装）：{paths}";
    }

    private void OnSaveSession(object? sender, RoutedEventArgs e) => Pending("保存会话", "M3");
    private void OnLoadSession(object? sender, RoutedEventArgs e) => Pending("加载会话", "M3");
    private void OnExportFrame(object? sender, RoutedEventArgs e) => Pending("导出当前帧", "M4");

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    // ══════════ 菜单：视图 ══════════

    private void OnToggleSingleMulti(object? sender, RoutedEventArgs e) => Pending("单屏/多屏", "M2");
    private void OnToggleAbSlider(object? sender, RoutedEventArgs e) => Pending("A-B 滑块", "M3");
    private void OnToggleProbe(object? sender, RoutedEventArgs e) => Pending("像素探针", "M3");
    private void OnToggleBookmarks(object? sender, RoutedEventArgs e) => Pending("书签", "M3");
    private void OnToggleOffset(object? sender, RoutedEventArgs e) => Pending("偏移校准", "M3");
    private void OnToggleMediaInfo(object? sender, RoutedEventArgs e) => Pending("媒体信息", "M3");
    private void OnToggleDiff(object? sender, RoutedEventArgs e) => Pending("差异叠加", "M3");
    private void OnToggleAudio(object? sender, RoutedEventArgs e) => Pending("音频", "M3");
    private void OnGridPreset(object? sender, RoutedEventArgs e) => Pending("网格布局", "M2");

    private void OnShowGridOnly(object? sender, RoutedEventArgs e)
    {
        // 复刻 WinForms「仅显示对比网格」：隐藏右侧工具栏（M3 起为完整侧栏切换）
        SidebarHost.IsVisible = !SidebarHost.IsVisible;
    }

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        WindowState = _fullscreen ? WindowState.FullScreen : WindowState.Normal;
        // HideChromeInFullscreen：全屏时隐藏菜单/传输/时间轴（M2 接 settings 开关）
    }

    // ══════════ 菜单：设置 ══════════

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => Pending("设置", "M3");

    // ══════════ 拖放（M2 接完整打开管线） ══════════

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        var files = e.Data.GetFiles();
        // M2：OpenFiles(files, autoPlay: true)
    }

    // ══════════ 自动化 selftest（M0 通路保持；M4 移植 --screentest/--autodemo） ══════════

    /// <summary>当前执行到的步骤名（自动化卡死诊断用）。</summary>
    private static volatile string _step = "启动";

    private static void Log(string msg)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} selftest[{_step}]: {msg}");
        Console.Out.Flush();
    }

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
            _step = "等HWND";
            _host = new HostSurface();
            // 折叠的 ContentControl 不会实例化子内容，自动化模式先置可见
            HostContainer.IsVisible = true;
            HostContainer.Content = _host;
            for (var i = 0; i < 50 && (_host?.Hwnd ?? 0) == 0; i++)
            {
                if (i % 10 == 0)
                    Log($"等待中 i={i} HostVisible={HostContainer.IsVisible} Root={_host.GetVisualRoot() != null}");
                await System.Threading.Tasks.Task.Delay(100);
            }
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
            Log("会话已创建 ✓");

            _step = "OpenAsync";
            await _session.OpenAsync(videoPath);
            Log("Open 调用返回");

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
            // Close() 在窗口未完全显示时不会终止 ClassicDesktopLifetime，自动化模式直接强制退出
            Console.Out.Flush();
            Environment.Exit(Program.SelftestResult.Code);
        }
    }
}
