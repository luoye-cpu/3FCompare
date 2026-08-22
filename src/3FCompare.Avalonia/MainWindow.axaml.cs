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
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Avalonia.Controls;
using _3FCompare.Avalonia.Panels;
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

    // M3：侧栏与面板
    private readonly ToolsSidebar _sidebar;
    private readonly ProbePanel _probe;
    private readonly BookmarkPanel _bookmarks;
    private readonly OffsetPanel _offsetPanel;
    private readonly MediaInfoPanel _mediaPanel;
    private readonly AudioPanel _audioPanel;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _engine = EngineFactory.Create();
        _realMode = _engine is Fff3FpEngine;
        _sync.StepProfile = new StepProfile { FrameStep = _settings.FrameStep, SecondsStep = _settings.SecondsStep };
        _coordinator = new PlaybackCoordinator(_engine, _sync, _settings, Grid.GetSurface);
        _coordinator.StateChanged += (_, _) => { UpdateStatus(); UpdatePanelsForSelection(); };

        StatusEngine.Text = LanguageManager.T(_realMode ? "Status_EngineReal" : "Status_EngineDemo");

        TransportHost.Child = _transport;
        TimelineHost.Child = _timeline;
        WireTransport();
        WireTimeline();

        // 默认 2 路空网格（WinForms 初始形态）
        Grid.SetCount(2, _realMode);

        // ---- M3：侧栏与五面板 ----
        _bookmarks = new BookmarkPanel(() =>
        {
            var master = _sync.ReadMasterSnapshot();
            return (master?.Position100ns ?? _sync.GetMasterPosition100ns(), master?.FrameIndex ?? 0);
        });
        _probe = new ProbePanel();
        _offsetPanel = new OffsetPanel();
        _mediaPanel = new MediaInfoPanel();
        _audioPanel = new AudioPanel();

        _bookmarks.JumpRequested += pos => _sync.SeekTo(pos);
        _offsetPanel.AlignRequested += (_, _) => OnOffsetAlign();
        _offsetPanel.OffsetNudge += delta => OnOffsetNudge(delta);
        _offsetPanel.OffsetReset += (_, _) => OnOffsetReset();

        _sidebar = new ToolsSidebar(_probe, _bookmarks, _offsetPanel, _mediaPanel, _audioPanel);
        _sidebar.MagnifierToggled += (_, _) =>
        {
            if (!_sidebar.MagnifierOn) Magnifier.HideOverlay();
        };
        _sidebar.CollapsedChanged += (_, _) =>
            MainArea.ColumnDefinitions[0].Width =
                new GridLength(_sidebar.Collapsed ? 24 : SidebarHost.Bounds.Width, GridUnitType.Pixel);
        SidebarHost.Content = _sidebar;
        Grid.SelectionChanged += (_, _) => UpdatePanelsForSelection();
        UpdatePanelsForSelection();

        // 探针/放大镜：隧道指针移动定位命中表面
        CenterPanel.AddHandler(InputElement.PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);

        AbSlider.SliderChanged += _ => { /* 视觉滑块（WinForms 同语义） */ };

        // 轮询：16ms 播放中 / 250ms 空闲（WinForms PollSnapshots 移植）
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pollTimer.Tick += (_, _) => PollSnapshots();
        _pollTimer.Start();

        RestoreWindowGeometry();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // 自动化 selftest / screentest 模式（GetCommandLineArgs 返回进程原始参数）
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 3 && args[1] == "--selftest")
            _ = RunSelftestAsync(args[2]);
        else if (args.Length >= 4 && args[1] == "--screentest")
            _ = RunScreentestAsync(args[2], args[3]);
    }

    /// <summary>--autodemo：窗口显示后自动打开并播放。</summary>
    public void AutoOpenFiles(string[] files)
    {
        Opened += (_, _) =>
        {
            Grid.SetCount(Math.Max(1, Math.Min(9, files.Length)), _realMode);
            _coordinator.OpenFiles(files, autoPlay: true);
        };
    }

    /// <summary>--screentest：打开→就绪+500ms 渲染→抓表面 0→存 PNG（>1000B 判过）。</summary>
    private async System.Threading.Tasks.Task RunScreentestAsync(string input, string outputPng)
    {
        var code = 1;
        try
        {
            _step = "screentest 打开";
            Grid.SetCount(1, _realMode);
            Console.WriteLine($"screentest: 打开 {input} ({(_realMode ? "真实" : "演示")})");
            _coordinator.OpenFiles(new[] { input }, autoPlay: true);

            _step = "screentest 等就绪";
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var snap = _sync.ReadMasterSnapshot();
                if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            _step = "screentest 渲染等待";
            await System.Threading.Tasks.Task.Delay(500);

            _step = "screentest 抓帧";
            var surface = Grid.GetSurface(0);
            System.Drawing.Bitmap? bmp = null;
            if (surface is not null && surface.Hwnd != 0)
                bmp = _3FCompare.App.Capture.WgcFrameCapture.CaptureWindowFrame(surface.Hwnd);
            bmp ??= CapturePixelSampled(_sync.Slots.FirstOrDefault()?.Session);

            if (bmp is not null)
            {
                using (bmp)
                    bmp.Save(outputPng, System.Drawing.Imaging.ImageFormat.Png);
                var size = new FileInfo(outputPng).Length;
                Console.WriteLine($"screentest: PNG {size} bytes");
                code = size > 1000 ? 0 : 1;
            }
            else
            {
                Console.Error.WriteLine("screentest: 抓帧失败");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"screentest: 失败 {ex.Message}");
        }
        finally
        {
            Console.Out.Flush();
            Environment.Exit(code);
        }
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
        ColorMode mode;
        switch (index)
        {
            case 0: // Auto
                var surface = Grid.GetSurface(0);
                var caps = surface?.Hwnd != 0
                    ? _3FCompare.Core.Display.DisplayCapabilities.ReadForWindow(surface.Hwnd) : null;
                mode = _3FCompare.Core.Settings.ColorModeHelper.Resolve(
                    _3FCompare.Core.Settings.ColorModeSetting.Auto, caps);
                break;
            case 2: // HDR
                mode = ColorMode.MapToHdr;
                break;
            default: // SDR
                mode = ColorMode.MapToSdr;
                break;
        }
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
        _timeline.SeekRequested += pos => SafeSeek(pos);
        _timeline.AbPointSet += (pos, isA) => SetLoopPoint(pos, isA);
        _timeline.ScrubPreview += OnScrubPreview;
        _timeline.PointerReleased += (_, _) => EndScrubPreview();
        _timeline.PointerCaptureLost += (_, _) => EndScrubPreview();
        _scrubTimer.Tick += OnScrubTimerTick;
    }

    /// <summary>安全的 Seek：捕获异常避免反复拖动导致崩溃。</summary>
    private void SafeSeek(long pos)
    {
        try { _sync.SeekTo(pos); }
        catch (Exception ex) { Console.Error.WriteLine($"Seek 异常: {ex.Message}"); }
    }

    // ---- 时间轴拖动缩略图预览 ----

    private ThumbnailPopup? _thumbnail;
    private readonly DispatcherTimer _scrubTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private long _scrubTarget, _preScrubPos;
    private bool _scrubbing;

    private void OnScrubPreview(long pos)
    {
        if (!_scrubbing)
        {
            _scrubbing = true;
            _preScrubPos = _sync.GetMasterPosition100ns();
            _thumbnail ??= new ThumbnailPopup();
        }
        _scrubTarget = pos;
        if (!_scrubTimer.IsEnabled) _scrubTimer.Start();
    }

    private void OnScrubTimerTick(object? sender, EventArgs e)
    {
        if (!_scrubbing) { _scrubTimer.Stop(); return; }
        SafeSeek(_scrubTarget);
        global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(50);
                if (!_scrubbing || _thumbnail is null) return;
                var surface = Grid.GetSurface(0);
                if (surface is null || surface.Hwnd == 0) return;
                using var bmp = _3FCompare.App.Capture.WgcFrameCapture.CaptureWindowFrame(surface.Hwnd);
                if (bmp is null) return;
                var dur = _sync.GetMasterDuration100ns();
                var ratio = dur > 0 ? (double)_scrubTarget / dur : 0;
                var screen = _timeline.PointToScreen(new Point(ratio * _timeline.Bounds.Width, 0));
                _thumbnail.ShowAt(screen, bmp);
            }
            catch (Exception ex) { Console.Error.WriteLine($"Scrub capture 异常: {ex.Message}"); }
        });
    }

    private void EndScrubPreview()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        _scrubTimer.Stop();
        _thumbnail?.Hide();
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
            case Key.Delete: _bookmarks.RemoveSelected(); break;
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

    // ══════════ 菜单：文件（会话存取） ══════════

    private async void OnSaveSession(object? sender, RoutedEventArgs e)
    {
        if (_sync.Count == 0) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageManager.T("Menu_SaveSession"),
            DefaultExtension = "3fcs",
            SuggestedFileName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.3fcs",
            FileTypeChoices = new[] { new FilePickerFileType("3FCompare Session") { Patterns = new[] { "*.3fcs", "*.json" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        var snapshot = new SessionSnapshot
        {
            GridLayout = Grid.SingleView ? 1 : (_sync.Count <= 4 ? 2 : 3),
            Position100ns = _sync.GetMasterPosition100ns(),
            LoopEnabled = _sync.LoopEnabled,
            LoopStart100ns = _sync.LoopStart100ns,
            LoopEnd100ns = _sync.LoopEnd100ns,
            Items = _sync.Slots.Select(s => new SessionSnapshot.SessionItem
            {
                Path = s.Path,
                Offset100ns = s.Offset100ns,
                HardwareDecode = _settings.HardwareDecode,
                AdapterIndex = _settings.PreferredAdapterIndex,
            }).ToList(),
        };
        SessionSnapshot.SaveToFile(path, snapshot);
        StatusInfo.Text = $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)}";
    }

    private async void OnLoadSession(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LanguageManager.T("Menu_LoadSession"),
            FileTypeFilter = new[] { new FilePickerFileType("3FCompare Session") { Patterns = new[] { "*.3fcs", "*.json" } } },
        });
        var path = files?.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;

        var snapshot = SessionSnapshot.LoadFromFile(path);
        if (snapshot is not { Items.Count: > 0 })
        {
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                LanguageManager.T("Msg_SessionInvalid"), LanguageManager.T("Settings_Ok"));
            return;
        }

        // 清空后按会话文件重开；全部打开后 Seek 到保存位置并恢复循环区间
        foreach (var s in Grid.Surfaces) s.DetachSession();
        _sync.Clear();
        Grid.SetCount(0, _realMode);
        _coordinator.OpenFiles(snapshot.Items.Select(i => i.Path!).ToList(), autoPlay: true, onAllOpened: () =>
        {
            _sync.SeekTo(snapshot.Position100ns);
            for (var i = 0; i < snapshot.Items.Count && i < _sync.Count; i++)
                _sync.Slots[i].Offset100ns = snapshot.Items[i].Offset100ns;
            if (snapshot.LoopEnabled && snapshot.LoopEnd100ns > snapshot.LoopStart100ns)
            {
                _sync.LoopStart100ns = snapshot.LoopStart100ns;
                _sync.LoopEnd100ns = snapshot.LoopEnd100ns;
                _sync.LoopEnabled = true;
                _transport.SetLoop(true);
                _timeline.SetLoopRange(snapshot.LoopStart100ns, snapshot.LoopEnd100ns, true);
            }
        });
    }

    private async void OnExportFrame(object? sender, RoutedEventArgs e)
    {
        var surface = Grid.GetSurface(Math.Max(0, Grid.SelectedIndex));
        if (surface is null || _sync.Count == 0)
        {
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                LanguageManager.T("Msg_SelectMedia"), LanguageManager.T("Settings_Ok"));
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageManager.T("Menu_ExportFrame"),
            SuggestedFileName = $"frame_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        // 抓帧：WgcFrameCapture（BitBlt/PrintWindow）→ 失败回退逐像素采样（WinForms 同退路）
        System.Drawing.Bitmap? bmp = null;
        try
        {
            if (surface.Hwnd != 0)
                bmp = _3FCompare.App.Capture.WgcFrameCapture.CaptureWindowFrame(surface.Hwnd);
            bmp ??= CapturePixelSampled(_sync.Slots.ElementAtOrDefault(Grid.SelectedIndex)?.Session);
        }
        catch (Exception ex)
        {
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                $"{LanguageManager.T("Msg_CaptureFail")}: {ex.Message}", LanguageManager.T("Settings_Ok"));
            return;
        }

        if (bmp is null)
        {
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                LanguageManager.T("Msg_CaptureUnavailable"), LanguageManager.T("Settings_Ok"));
            return;
        }

        using (bmp)
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        StatusInfo.Text = $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)}";
    }

    /// <summary>逐像素采样重建帧（WgcFrameCapture 不可用时的退路，~320px 宽）。</summary>
    private static System.Drawing.Bitmap? CapturePixelSampled(IPlayerSession? session)
    {
        if (session is null) return null;
        try
        {
            var media = session.ReadMediaInfo();
            var srcW = media?.VideoWidth ?? 0;
            var srcH = media?.VideoHeight ?? 0;
            if (srcW <= 0 || srcH <= 0) return null;
            var w = Math.Min(320, srcW);
            var h = (int)Math.Round((double)srcH * w / srcW);
            var bmp = new System.Drawing.Bitmap(w, h);
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (!session.TryReadPixel((int)((x + 0.5) / w * srcW), (int)((y + 0.5) / h * srcH), out var s))
                        return null;
                    bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(
                        To8(s.A), To8(s.R), To8(s.G), To8(s.B)));
                }
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static int To8(float v) => Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleSingleMulti(object? sender, RoutedEventArgs e)
    {
        Grid.SingleView = !Grid.SingleView;
        UpdateStatus();
    }

    // ══════════ 菜单：视图（M3 面板切换） ══════════

    private void OnToggleAbSlider(object? sender, RoutedEventArgs e)
    {
        if (AbSlider.IsVisible)
        {
            AbSlider.IsVisible = false;
            Grid.IsVisible = true;
            return;
        }
        if (_sync.Count < 2) return;
        var sel = Math.Max(0, Grid.SelectedIndex);
        AbSlider.SetPair(sel, (sel + 1) % Math.Max(2, _sync.Count));
        AbSlider.IsVisible = true;
        Grid.IsVisible = false;
    }

    private void OnToggleProbe(object? sender, RoutedEventArgs e) { ShowSidebar(); _sidebar.ActivateProbe(); }
    private void OnToggleBookmarks(object? sender, RoutedEventArgs e) { ShowSidebar(); _sidebar.ActivateBookmarks(); }
    private void OnToggleOffset(object? sender, RoutedEventArgs e) { ShowSidebar(); _sidebar.ActivateOffset(); }
    private void OnToggleMediaInfo(object? sender, RoutedEventArgs e) { ShowSidebar(); _sidebar.ActivateMedia(); }
    private void OnToggleAudio(object? sender, RoutedEventArgs e) { ShowSidebar(); _sidebar.ActivateAudio(); }

    private async void OnToggleDiff(object? sender, RoutedEventArgs e)
    {
        if (_sync.Slots.Count(s => !s.Failed) < 2)
        {
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                LanguageManager.T("Msg_DiffNeed2"), LanguageManager.T("Settings_Ok"));
            return;
        }
        var sel = Math.Max(0, Grid.SelectedIndex);
        var view = new DiffOverlayView
        {
            AIndex = sel,
            BIndex = (sel + 1) % _sync.Count,
        };
        view.SetSessionProvider(i => _sync.Slots.ElementAtOrDefault(i)?.Session);
        view.Resample();
        var win = new Window
        {
            Title = LanguageManager.T("Menu_Diff"),
            Width = 760, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 18)),
            Content = view,
        };
        await win.ShowDialog(this);
    }

    private void ShowSidebar()
    {
        SidebarHost.IsVisible = true;
        SidebarSplitter.IsVisible = true;
    }

    private void OnGridPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string preset })
            Grid.SetGridLayout(preset);
    }

    private void OnShowGridOnly(object? sender, RoutedEventArgs e)
    {
        // 复刻 WinForms「仅显示对比网格」：隐藏侧栏
        var show = !SidebarHost.IsVisible;
        SidebarHost.IsVisible = show;
        SidebarSplitter.IsVisible = show;
    }

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

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        var dlg = new Views.SettingsWindow(_settings) { };
        await dlg.ShowDialog(this);
        if (!dlg.Changed || dlg.Result is null) return;

        var result = dlg.Result;
        // 语言即时生效（绑定自动刷新）
        LanguageManager.SetLanguage(result.Language);

        // 立即可应用的项
        _sync.StepProfile = new StepProfile { FrameStep = result.FrameStep, SecondsStep = result.SecondsStep };
        UpdateStatus();

        // FFmpeg 路径变化 → 需重启（探测链在启动时装配）
        if (dlg.FfmpegChanged)
        {
            SettingsStore.Save(result);
            var restart = await Views.MessageBox.Show(this,
                LanguageManager.T("Msg_AppName"),
                LanguageManager.T("Msg_DemoModeRestartNeeded").Replace("\n", " "),
                primaryText: LanguageManager.T("Msg_DemoModeRestartNeeded").Contains("重新启动") ? "重启 / Restart" : "Yes",
                secondaryText: LanguageManager.T("Settings_Cancel"));
            if (restart)
            {
                // 重启：以新进程拉起自身后退出
                var exe = Environment.ProcessPath;
                if (exe is not null)
                {
                    using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                    Close();
                    Environment.Exit(0);
                }
            }
            return;
        }

        SettingsStore.Save(result);
        CopySettings(result);
        UpdateStatus();
    }

    private void CopySettings(AppSettings s)
    {
        _settings.HardwareDecode = s.HardwareDecode;
        _settings.PreferredAdapterIndex = s.PreferredAdapterIndex;
        _settings.ColorMode = s.ColorMode;
        _settings.FrameStep = s.FrameStep;
        _settings.SecondsStep = s.SecondsStep;
        _settings.StartFullscreen = s.StartFullscreen;
        _settings.HideChromeInFullscreen = s.HideChromeInFullscreen;
        _settings.DefaultGridCols = s.DefaultGridCols;
        _settings.DefaultGridRows = s.DefaultGridRows;
        _settings.VrrTearingPresent = s.VrrTearingPresent;
        _settings.VrrPacingEnabled = s.VrrPacingEnabled;
        _settings.Language = s.Language;
    }

    private void Pending(string what, string milestone) =>
        StatusInfo.Text = $"{what} —— {milestone} 实装";

    // ══════════ M3：面板联动 ══════════

    private void UpdatePanelsForSelection()
    {
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        var session = slot?.Session;
        _probe.AttachSession(session);
        _audioPanel.AttachSession(session, session?.ReadMediaInfo());
        _mediaPanel.ShowMediaInfo(session?.ReadMediaInfo());

        if (slot is null)
        {
            _offsetPanel.SetPlaceholder();
            return;
        }
        var master = _sync.ReadMasterSnapshot();
        var fps = master is not null ? SyncController.EstimateFps(master) : 24;
        _offsetPanel.SetFps(fps);
        _offsetPanel.SetOffset(slot.Offset100ns, fps);
    }

    /// <summary>中央区指针移动（隧道）：放大镜跟随 + 探针读点（选中表面）。</summary>
    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Source is not Visual src) return;
        PlayerSurface? surface = null;
        var v = (Visual?)src;
        while (v is not null)
        {
            if (v is PlayerSurface ps) { surface = ps; break; }
            v = v.GetVisualParent();
        }
        if (surface is null) return;
        var local = e.GetPosition(surface);

        if (_sidebar.MagnifierOn)
            Magnifier.UpdateAt(e.GetPosition(CenterPanel));
        if (ReferenceEquals(_sidebar.Active, _probe) && surface.Selected)
            _probe.UpdatePoint((int)local.X, (int)local.Y);
    }

    // ---- 偏移校准（相对第 1 路） ----

    private void OnOffsetAlign()
    {
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        var master = _sync.ReadMasterSnapshot();
        var target = slot?.Session?.ReadSnapshot();
        if (slot is null || master is null || target is null) return;
        slot.Offset100ns = master.Position100ns - target.Position100ns;
        _sync.RefreshAllPositions();
        UpdatePanelsForSelection();
    }

    private void OnOffsetNudge(long delta100ns)
    {
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        if (slot is null) return;
        slot.Offset100ns += delta100ns;
        _sync.RefreshAllPositions();
        UpdatePanelsForSelection();
    }

    private void OnOffsetReset()
    {
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        if (slot is null) return;
        slot.Offset100ns = 0;
        _sync.RefreshAllPositions();
        UpdatePanelsForSelection();
    }

    /// <summary>缺 FFmpeg/原生组件引导（WinForms MaybeExitDemoMode 对应）：
    /// 非真实模式且非自动化 → 提示打开设置或关闭。</summary>
    private async void MaybeExitDemoMode()
    {
        if (_realMode) return;
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--selftest") || args.Contains("--autodemo")) return;

        var openSettings = await Views.MessageBox.Show(this,
            LanguageManager.T("Msg_DemoModeTitle"),
            LanguageManager.T("Msg_DemoModeMissingFfmpeg"),
            primaryText: LanguageManager.T("Msg_DemoModeOpenSettings"),
            secondaryText: LanguageManager.T("Msg_DemoModeClose"));
        if (!openSettings) { Close(); return; }

        OnOpenSettings(this, new RoutedEventArgs());
    }

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
        MaybeExitDemoMode();
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

            // 自动播放断言（打开完成→统一 Play 契约；须在步进前验证——步进会暂停播放）
            _step = "自动播放断言";
            var deadline2 = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline2)
            {
                var s = _sync.ReadMasterSnapshot();
                if (s is { State: PlayerState.Playing }) { Log($"播放中 pos={TimeSpan.FromTicks(s.Position100ns):g}"); break; }
                await System.Threading.Tasks.Task.Delay(100);
            }
            if (_realMode && _sync.ReadMasterSnapshot() is not { State: PlayerState.Playing })
                throw new InvalidOperationException($"自动播放未启动（状态={_sync.ReadMasterSnapshot()?.State}）");

            // VRR 呈现路径覆盖：开启撕裂模式并记录支持状态（不支持则静默回退 VSync）
            _step = "VRR 呈现";
            var vrrSupported = _sync.Slots[0].Session.SetPresentConfig(true);
            Log($"VRR 撕裂呈现: {(vrrSupported ? "显示器链支持 ✓" : "不支持 → 保持 VSync 锁定")}");

            // A9 媒体率呈现节奏覆盖
            _step = "VRR 节奏";
            var pacingSupported = _sync.Slots[0].Session.SetPacingConfig(true);
            Log($"VRR 媒体率节奏: {(pacingSupported ? "已启用 ✓" : "不支持")}");

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
