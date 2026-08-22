using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using _3FCompare.App;
using _3FCompare.Avalonia.Controls;
using _3FCompare.Avalonia.Services;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare.Avalonia;

/// <summary>主窗口（M2：核心播放面已接线）。
/// 打开/播放/步进/循环/缩放平移/网格布局/时间轴/状态栏全量；面板与对话框 M3 实装。</summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SyncController _sync = new();
    private readonly PlaybackCoordinator _coordinator;
    private readonly DispatcherTimer _pollTimer;
    private readonly IPlayerEngine _engine;
    private readonly bool _realMode;

    private readonly TransportBar _transport = new();
    private readonly TimelineView _timeline = new();

    private bool _isPlaying;
    private double _playbackSpeed = 1.0;
    private long _lastShownPos;
    private float _viewZoom = 1f, _viewPanX, _viewPanY;
    private bool _dragging;
    private PixelPoint _dragOriginScreen;
    private bool _fullscreen;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _engine = EngineFactory.Create();
        _realMode = _engine is Fff3FpEngine;
        _sync.StepProfile = new StepProfile { FrameStep = _settings.FrameStep, SecondsStep = _settings.SecondsStep };
        _coordinator = new PlaybackCoordinator(_engine, _sync, _settings, Grid.GetSurface);
        _coordinator.StateChanged += (_, _) => UpdateStatus();

        StatusEngine.Text = LanguageManager.T(_realMode ? "Status_EngineReal" : "Status_EngineDemo");

        TransportHost.Child = _transport;
        TimelineHost.Child = _timeline;
        WireTransport();
        WireTimeline();

        // 默认 2 路空网格（WinForms 初始形态）
        Grid.SetCount(2, _realMode);

        // 轮询：16ms 播放中 / 250ms 空闲（WinForms PollSnapshots 移植）
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pollTimer.Tick += (_, _) => PollSnapshots();
        _pollTimer.Start();

        RestoreWindowGeometry();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 自动化 selftest（走真实打开管线）
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 3 && args[1] == "--selftest")
            _ = RunSelftestAsync(args[2]);
    }

    // ══════════ 打开 / 拖放 ══════════

    private async void OnOpenVideos(object? sender, RoutedEventArgs e) => await OpenViaPickerAsync();

    private async System.Threading.Tasks.Task OpenViaPickerAsync()
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
        OpenPaths(files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList());
    }

    private void OpenPaths(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _coordinator.OpenFiles(paths, autoPlay: true);
        UpdateStatus();
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        var paths = e.Data.GetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Cast<string>()
            .ToList();
        if (paths is { Count: > 0 })
            OpenPaths(paths);
    }

    // ══════════ 传输栏 ══════════

    private void WireTransport()
    {
        _transport.StepProfileSecondsProvider = () => _sync.StepProfile.SecondsStep;
        _transport.PlayPauseClicked += (_, _) => TogglePlay();
        _transport.StopClicked += (_, _) => { _sync.Stop(); SetPlaying(false); };
        _transport.FrameStepClicked += (_, d) => _sync.StepFrames(d * _sync.StepProfile.FrameStep);
        _transport.SecondsStepClicked += (_, d) => _sync.StepSeconds(d);
        _transport.LoopToggled += (_, on) => ToggleLoop(on);
        _transport.AddClicked += (_, _) => AddSlotPlaceholder();
        _transport.RemoveClicked += (_, _) => RemoveLastSlot();
        _transport.SpeedChanged += (_, s) => _playbackSpeed = s;
        _transport.ColorModeChanged += OnColorModeChanged;
    }

    private void OnColorModeChanged(object? sender, int index)
    {
        var mode = index == 1 ? ColorMode.MapToHdr : ColorMode.MapToSdr;
        foreach (var slot in _sync.Slots)
        {
            try { slot.Session.SetColorMode(mode); } catch { /* 演示模式无操作 */ }
        }
    }

    private void ToggleLoop(bool on)
    {
        _sync.LoopEnabled = on;
        _transport.SetLoop(on);
    }

    private void TogglePlay()
    {
        if (_sync.Count == 0) return;
        var snap = _sync.ReadMasterSnapshot();
        var playing = snap is { State: PlayerState.Playing };
        if (playing) _sync.Pause();
        else _sync.Play();
        SetPlaying(!playing);
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        _transport.SetPlaying(playing);
    }

    private void AddSlotPlaceholder()
    {
        if (_sync.Count >= 9) return;
        Grid.SetCount(_sync.Count + 1, _realMode);
        UpdateStatus();
    }

    private void RemoveLastSlot()
    {
        if (_sync.Count <= 0) return;
        Grid.GetSurface(_sync.Count - 1)?.DetachSession();
        _sync.RemoveSlotAt(_sync.Count - 1);
        Grid.SetCount(_sync.Count, _realMode);
        UpdateStatus();
    }

    // ══════════ 时间轴 ══════════

    private void WireTimeline()
    {
        _timeline.SeekRequested += pos => _sync.SeekTo(pos);
        _timeline.AbPointSet += (pos, isA) => SetLoopPoint(pos, isA);
        _timeline.ScrubPreview += pos => { /* M4：缩略图预览管线 */ };
    }

    /// <summary>设置 A/B 循环点（自动补全另一点：设 A 时若 B 未设则 B=结尾，反之亦然）。</summary>
    private void SetLoopPoint(long pos, bool isA)
    {
        var dur = _sync.GetMasterDuration100ns();
        if (isA)
        {
            _sync.LoopStart100ns = pos;
            if (_sync.LoopEnd100ns <= pos) _sync.LoopEnd100ns = dur;
        }
        else
        {
            _sync.LoopEnd100ns = pos;
            if (_sync.LoopStart100ns >= pos) _sync.LoopStart100ns = 0;
        }
        _sync.LoopEnabled = true;
        _transport.SetLoop(true);
        _timeline.SetLoopRange(_sync.LoopStart100ns, _sync.LoopEnd100ns, true);
    }

    // ══════════ 轮询（WinForms PollSnapshots 移植） ══════════

    private void PollSnapshots()
    {
        if (_sync.Count == 0) return;

        var snaps = _sync.ReadAllSnapshots();
        for (var i = 0; i < snaps.Count && i < Grid.Count; i++)
            Grid.GetSurface(i)?.UpdateSnapshot(snaps[i]);

        var master = snaps.Count > 0 ? snaps[0] : null;
        if (master is not null)
        {
            _timeline.SetDuration(master.Duration100ns);
            if (!_timeline.IsScrubbing)
                _timeline.SetPosition(master.Position100ns);
            _transport.SetTime(
                TimeSpan.FromTicks(master.Position100ns),
                TimeSpan.FromTicks(master.Duration100ns),
                FrameInSecond(master));
        }

        // 播放状态回显（若被原生事件改变）
        if (master is { State: PlayerState.Playing } && !_isPlaying) SetPlaying(true);
        else if (master is not null and { State: not PlayerState.Playing } && _isPlaying) SetPlaying(false);

        // 循环回绕
        if (_sync.LoopEnabled) _sync.TickLoop();

        // 伪变速：真实模式下按速度节流 Seek（A2 落地前）
        if (_isPlaying && _realMode && Math.Abs(_playbackSpeed - 1.0) > 0.01 && master is not null)
        {
            var now = master.Position100ns;
            if (_lastShownPos == 0) _lastShownPos = now;
            var elapsed = now - _lastShownPos;
            if (elapsed > 0 && _playbackSpeed > 1.0)
                _sync.SeekTo(now + (long)(elapsed * (_playbackSpeed - 1.0)));
            _lastShownPos = now;
        }
        else if (_lastShownPos != 0 && master is not null)
        {
            _lastShownPos = master.Position100ns;
        }

        // 自适应频率
        var target = _isPlaying && _sync.Count > 0 ? 16 : 250;
        if (_pollTimer.Interval.TotalMilliseconds != target)
            _pollTimer.Interval = TimeSpan.FromMilliseconds(target);
    }

    /// <summary>PR 时间码的秒内帧号（1 起；帧率由快照时间基估算，缺省 24）。</summary>
    private static int FrameInSecond(EngineSnapshot snap)
    {
        var fps = SyncController.EstimateFps(snap);
        if (fps <= 0) return 0;
        var sec = TimeSpan.TicksPerSecond;
        var frac = (double)(snap.Position100ns % sec) / sec;
        return Math.Clamp((int)Math.Floor(frac * fps) + 1, 1, (int)Math.Round(fps));
    }

    private void UpdateStatus()
    {
        if (_sync.Count == 0)
        {
            StatusInfo.Text = LanguageManager.T(_realMode ? "Status_Ready" : "Status_DemoHint");
            return;
        }
        var mode = Grid.SingleView ? LanguageManager.T("Status_SingleMode") : LanguageManager.T("Status_GridMode");
        var failed = _sync.Slots.Count(s => s.Failed);
        var runtimeError = _sync.LastRuntimeError;
        StatusInfo.Text =
            $"{mode}模式 | 路数 {_sync.Count}/9 | {LanguageManager.T("Status_Steps")}: {_sync.StepProfile.FrameStep}帧/{_sync.StepProfile.SecondsStep:0.#}秒" +
            (failed > 0 ? $" | {failed} 路失败" : string.Empty) +
            (runtimeError is not null ? $" | ⚠ {runtimeError}" : string.Empty);
    }

    // ══════════ 视图变换：滚轮缩放 / 拖拽平移（所有表面共享，广播到全部会话） ══════════

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Avalonia 滚轮直达悬停控件（无需 WinForms 的 IMessageFilter hack）
        if (HitSurface(e.Source as Visual) is { } _)
        {
            var factor = e.Delta.Y > 0 ? 1.15f : 1f / 1.15f;
            _viewZoom = Math.Clamp(_viewZoom * factor, 1f, 32f);
            ApplyViewTransform();
            e.Handled = true;
        }
    }

    private PlayerSurface? HitSurface(Visual? v)
    {
        while (v is not null)
        {
            if (v is PlayerSurface s) return s;
            v = v.GetVisualParent();
        }
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging && _viewZoom > 1.001f && e.Source is Visual src && HitSurface(src) is { } surface)
        {
            // 屏幕坐标累积 delta：跨过相邻表面边界时平滑衔接
            var cur = surface.PointToScreen(e.GetPosition(surface));
            var dx = cur.X - _dragOriginScreen.X;
            var dy = cur.Y - _dragOriginScreen.Y;
            _dragOriginScreen = cur;
            var scale = 2.0f / Math.Max((float)surface.Bounds.Width, (float)surface.Bounds.Height);
            _viewPanX = Math.Clamp(_viewPanX + dx * scale, -1f, 1f);
            _viewPanY = Math.Clamp(_viewPanY + dy * scale, -1f, 1f);
            ApplyViewTransform();
            e.Handled = true;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && _viewZoom > 1.001f
            && e.Source is Visual src && HitSurface(src) is { } surface)
        {
            _dragging = true;
            _dragOriginScreen = surface.PointToScreen(e.GetPosition(surface));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
    }

    private void ApplyViewTransform()
    {
        try { _sync.SetViewTransform(_viewZoom, _viewPanX, _viewPanY); }
        catch (Exception ex) { Console.WriteLine($"ApplyViewTransform: {ex.Message}"); }
    }

    private void ResetViewTransform()
    {
        _viewZoom = 1f;
        _viewPanX = _viewPanY = 0f;
        _dragging = false;
        ApplyViewTransform();
    }

    // ══════════ 快捷键（WinForms ProcessCmdKey 全表） ══════════

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Space when mods == KeyModifiers.None: TogglePlay(); break;
            case Key.S when mods.HasFlag(KeyModifiers.Control): OnExportFrame(this, e); break;
            case Key.Left when mods == KeyModifiers.None: _sync.StepFrames(-_sync.StepProfile.FrameStep); break;
            case Key.Right when mods == KeyModifiers.None: _sync.StepFrames(_sync.StepProfile.FrameStep); break;
            case Key.Left when mods.HasFlag(KeyModifiers.Shift): _sync.StepSeconds(-_sync.StepProfile.SecondsStep); break;
            case Key.Right when mods.HasFlag(KeyModifiers.Shift): _sync.StepSeconds(_sync.StepProfile.SecondsStep); break;
            case Key.Up: _sync.StepSeconds(10); break;
            case Key.Down: _sync.StepSeconds(-10); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.O when mods == KeyModifiers.None: _ = OpenViaPickerAsync(); break;
            case Key.B when mods == KeyModifiers.None: OnToggleAbSlider(this, e); break;
            case Key.P when mods == KeyModifiers.None: OnToggleProbe(this, e); break;
            case Key.F6: OnToggleOffset(this, e); break;
            case Key.R when mods == KeyModifiers.None: ResetViewTransform(); break;
            case Key.Delete: Pending("删除书签", "M3"); break;
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

    private void GrowLanes(int upTo)
    {
        // D1..D9 只加不减（WinForms 语义）
        if (_sync.Count >= upTo) return;
        Grid.SetCount(upTo, _realMode);
        UpdateStatus();
    }

    // ══════════ 菜单 ══════════

    private void OnSaveSession(object? sender, RoutedEventArgs e) => Pending("保存会话", "M3");
    private void OnLoadSession(object? sender, RoutedEventArgs e) => Pending("加载会话", "M3");
    private void OnExportFrame(object? sender, RoutedEventArgs e) => Pending("导出当前帧", "M4");

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleSingleMulti(object? sender, RoutedEventArgs e)
    {
        Grid.SingleView = !Grid.SingleView;
        UpdateStatus();
    }

    private void OnToggleAbSlider(object? sender, RoutedEventArgs e) => Pending("A-B 滑块", "M3");
    private void OnToggleProbe(object? sender, RoutedEventArgs e) => Pending("像素探针", "M3");
    private void OnToggleBookmarks(object? sender, RoutedEventArgs e) => Pending("书签", "M3");
    private void OnToggleOffset(object? sender, RoutedEventArgs e) => Pending("偏移校准", "M3");
    private void OnToggleMediaInfo(object? sender, RoutedEventArgs e) => Pending("媒体信息", "M3");
    private void OnToggleDiff(object? sender, RoutedEventArgs e) => Pending("差异叠加", "M3");
    private void OnToggleAudio(object? sender, RoutedEventArgs e) => Pending("音频", "M3");

    private void OnGridPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string preset })
            Grid.SetGridLayout(preset);
    }

    private void OnShowGridOnly(object? sender, RoutedEventArgs e) =>
        SidebarHost.IsVisible = !SidebarHost.IsVisible;

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        WindowState = _fullscreen ? WindowState.FullScreen : WindowState.Normal;
        var hideChrome = _fullscreen && _settings.HideChromeInFullscreen;
        MenuMain.IsVisible = !hideChrome;
        TransportHost.IsVisible = !hideChrome;
        TimelineHost.IsVisible = !hideChrome;
    }

    private void OnOpenSettings(object? sender, RoutedEventArgs e) => Pending("设置", "M3");

    private void Pending(string what, string milestone) =>
        StatusInfo.Text = $"{what} —— {milestone} 实装";

    // ══════════ 窗口几何记忆 ══════════

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
        _pollTimer.Stop();
        _coordinator.Close();
        _sync.Clear();
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

    // ══════════ 自动化 selftest（走真实打开管线；WinForms RunSelfTest 断言移植） ══════════

    private static volatile string _step = "启动";

    private static void Log(string msg)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} selftest[{_step}]: {msg}");
        Console.Out.Flush();
    }

    private async System.Threading.Tasks.Task RunSelftestAsync(string videoPath)
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var last = _step; var stable = 0;
            while (true)
            {
                await System.Threading.Tasks.Task.Delay(1000);
                stable = _step == last ? stable + 1 : 0;
                last = _step;
                if (stable >= 40)
                {
                    Console.Error.WriteLine($"selftest: 看门狗触发 ✗ 卡在步骤 [{_step}] 超过 40s");
                    Console.Error.Flush();
                    Environment.Exit(3);
                }
            }
        });
        var code = 2;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"selftest: 文件不存在 {videoPath}");
                Environment.Exit(2);
            }

            _step = "打开";
            Grid.SetCount(1, _realMode);
            Log($"打开 {videoPath} ({(_realMode ? "真实" : "演示")}模式)");
            _coordinator.OpenFiles(new[] { videoPath }, autoPlay: true);

            // 等待就绪（≤15s）
            _step = "等就绪";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _sync.ReadMasterSnapshot();
                if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            var ready = _sync.ReadMasterSnapshot();
            if (ready is null || !PlaybackCoordinator.IsReadyState(ready.State))
                throw new InvalidOperationException($"未就绪（状态={ready?.State}）");
            Log($"就绪 ✓ 时长={TimeSpan.FromTicks(ready.Duration100ns):hh\\:mm\\:ss}");

            // 帧步进 +1：位置不得后退（真实模式）
            _step = "帧步进";
            if (_realMode)
            {
                var before = _sync.GetMasterPosition100ns();
                _sync.StepFrames(_sync.StepProfile.FrameStep);
                await System.Threading.Tasks.Task.Delay(300);
                var after = _sync.GetMasterPosition100ns();
                Log($"帧步进 {TimeSpan.FromTicks(before):g} → {TimeSpan.FromTicks(after):g}");
                if (after < before)
                    throw new InvalidOperationException($"帧步进位置后退 {before} → {after}");
            }

            // 秒步进 +1：同上断言
            _step = "秒步进";
            if (_realMode)
            {
                var before = _sync.GetMasterPosition100ns();
                _sync.StepSeconds(_sync.StepProfile.SecondsStep);
                await System.Threading.Tasks.Task.Delay(300);
                var after = _sync.GetMasterPosition100ns();
                Log($"秒步进 {TimeSpan.FromTicks(before):g} → {TimeSpan.FromTicks(after):g}");
                if (after < before)
                    throw new InvalidOperationException($"秒步进位置后退 {before} → {after}");
            }

            // 媒体信息
            _step = "媒体信息";
            var media = _sync.Slots[0].Session.ReadMediaInfo();
            if (media is not null)
                Log($"媒体 {media.VideoWidth}x{media.VideoHeight} @{SyncController.EstimateFps(ready):0.##}fps {media.Codec} HDR={media.IsHdr}");

            _step = "自动播放断言";
            var deadline2 = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < deadline2)
            {
                var s = _sync.ReadMasterSnapshot();
                if (s is { State: PlayerState.Playing }) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            var final = _sync.ReadMasterSnapshot();
            Log($"状态={final?.State}");
            if (_realMode && final is not { State: PlayerState.Playing })
                throw new InvalidOperationException($"自动播放未启动（状态={final?.State}）");

            Log("全部通过 ✓");
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"selftest[步骤{_step}]: 失败 ✗ {ex.Message}");
            Console.Error.Flush();
            code = 1;
        }
        finally
        {
            Console.Out.Flush();
            Environment.Exit(code);
        }
    }
}
