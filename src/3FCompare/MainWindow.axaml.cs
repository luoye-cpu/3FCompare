using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Controls;
using _3FCompare.Panels;
using _3FCompare.Services;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;

namespace _3FCompare;

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
    private bool _fullscreen;
    /// <summary>平移节流：上次 ApplyViewTransform 时间。</summary>
    private long _lastPanApplyTicks;

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

        // FFmpeg 目录：手动设置优先；仍不可用时回退自动探测
        // 通过 SetDllDirectory 将目录加入 DLL 搜索路径，不再复制 DLL 到应用目录
        if (!string.IsNullOrWhiteSpace(_settings.FfmpegDirectory))
            NativeRuntime.SetFfmpegDirectory(_settings.FfmpegDirectory);
        if (!NativeRuntime.IsFfmpegAvailable())
        {
            var autoDir = NativeRuntime.AutoDetectFfmpegDirectory();
            if (autoDir is not null)
                NativeRuntime.SetFfmpegDirectory(autoDir);
        }

        _engine = EngineFactory.Create();
        _realMode = _engine is Fff3FpEngine;
        // 应用缩放小地图设置
        PlayerSurface.SharedMinimapEnabled = _settings.MinimapEnabled;
        _sync.StepProfile = new StepProfile { FrameStep = _settings.FrameStep, SecondsStep = _settings.SecondsStep };
        _coordinator = new PlaybackCoordinator(_engine, _sync, _settings, Grid.GetSurface);
        _coordinator.StateChanged += (_, _) => { UpdateStatus(); UpdatePanelsForSelection(); };

        StatusEngine.Text = LanguageManager.T(_realMode ? "Status_EngineReal" : "Status_EngineDemo");

        TransportHost.Child = _transport;
        TimelineHost.Child = _timeline;
        WireTransport();
        WireTimeline();

        // SurfaceCreated 必须在 SetCount 之前注册（否则初始表面缺少事件绑定）
        Grid.SurfaceCreated += s =>
        {
            
            s.SurfacePressed += OnSurfacePress;
            s.SurfaceMoved += OnSurfaceMove;
            s.SurfaceReleased += OnSurfaceRelease;
        };

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
        _sidebar.CollapsedChanged += collapsed =>
        {
            MainArea.ColumnDefinitions[0].Width = new GridLength(
                collapsed ? 24 : _sidebar.ExpandedWidth, GridUnitType.Pixel);
            SidebarSplitter.IsVisible = !collapsed;
        };
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
        DragDrop.SetAllowDrop(this, true); // 启用窗口拖放

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
        e.DragEffects = e.DataTransfer.Contains(global::Avalonia.Input.DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(global::Avalonia.Input.DataFormat.File)) return;
        var paths = e.DataTransfer.TryGetFiles()?
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
                var hwnd = surface?.Hwnd ?? 0;
                var caps = hwnd != 0
                    ? _3FCompare.Core.Display.DisplayCapabilities.ReadForWindow(hwnd) : null;
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
        // 关闭时同步清除时间轴视觉区间（否则绿色 A-B 区间残留）
        if (!on)
            _timeline.SetLoopRange(0, 0, false);
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

// 3FCompare 优化项⑤：Scrub 预览降载 —— 抓帧移出 UI 线程 + 缩放目标，
        // 避免拖动时间轴时 UI 线程被顶层窗口 BitBlt（4K 下 ~10-30ms）周期性阻塞。
        private void OnScrubTimerTick(object? sender, EventArgs e)
        {
            if (!_scrubbing) { _scrubTimer.Stop(); return; }
            SafeSeek(_scrubTarget);
            // 缩略图预览可关闭（低配设备）：关闭时仅 Seek，不触发 BitBlt 屏幕抓取
            if (!_settings.ScrubPreviewEnabled) return;
            try
            {
                if (_thumbnail is null) return;
                var surface = Grid.GetSurface(0);
                if (surface is null || surface.Hwnd == 0) return;
                var hwnd = surface.Hwnd;
                var target = _scrubTarget;
                var dur = _sync.GetMasterDuration100ns();
                var ratio = dur > 0 ? (double)target / dur : 0;
                var screen = _timeline.PointToScreen(new Point(ratio * _timeline.Bounds.Width, 0));
                // 后台抓帧：BitBlt 顶层窗口并缩放到预览尺寸（~480px 宽），完成后回 UI 线程展示。
                // 跨线程只传 Bitmap + 屏幕坐标；旧帧未赶上时丢弃中间帧（_tellTimeline 自然节流）。
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        using var bmp = _3FCompare.App.Capture.WgcFrameCapture.CaptureWindowFrame(hwnd);
                        if (bmp is null) return;
                        var preview = ThumbnailPopup.ScaleTo(bmp, 480);
                        if (preview is null) return;
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!_scrubbing || _thumbnail is null) { preview.Dispose(); return; }
                            _thumbnail.ShowAt(screen, preview);
                        });
                    }
                    catch { /* 后台抓帧失败静默降级 */ }
                });
            }
            catch (Exception ex) { Console.Error.WriteLine($"Scrub capture 异常: {ex.Message}"); }
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

    private long _lastPresentedCount;
    private int _stalledPolls;
    private bool _stalledAfterLightRecovery;
    // 伪变速 Seek 节流（1s 最小间隔，见 PollSnapshotsCoreAsync）
    private long _lastSpeedSeekTicks;
    private long _speedBasePos;

    private async void PollSnapshots()
    {
        // async void 顶层兜底：任何未预期异常只记录，不崩进程（DispatcherTimer 回调）
        try
        {
            await PollSnapshotsCoreAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainWindow] PollSnapshots 异常: {ex.Message}");
        }
    }

    private async System.Threading.Tasks.Task PollSnapshotsCoreAsync()
    {
        if (_sync.Count == 0) return;
        if (_recovering != 0) return; // 会话重建中跳过，防止读取中间状态导致崩溃

        var snaps = _sync.ReadAllSnapshots();
        for (var i = 0; i < snaps.Count && i < Grid.Count; i++)
            Grid.GetSurface(i)?.UpdateSnapshot(snaps[i]);

        var master = snaps.Count > 0 ? snaps[0] : null;
        if (master is not null)
        {
            // 捕获到局部变量：跨 await 后编译器无法证明 master 仍非空（CS8602）
            var m = master;
            // 检测引擎 Failed 状态（窗口最大化时 SwapChain 重建失败导致 D3D11 设备丢失）
            // 3FCompare (F-LOG)：重建风暴抑制——连续重建失败 ≥3 次后放弃，
            // 避免死循环（日志显示曾 7+ 轮无限重建）
            if (m.State == PlayerState.Failed && _realMode && _sync.Count > 0
                && System.Threading.Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
            {
                var attempts = System.Threading.Interlocked.Increment(ref _recoveryAttempts);
                if (attempts > 3)
                {
                    Console.Error.WriteLine($"[MainWindow] 重建已尝试 {attempts} 次仍失败，放弃避免死循环");
                    System.Threading.Interlocked.Exchange(ref _recovering, 0);
                }
                else
                {
                    Console.Error.WriteLine($"[MainWindow] 引擎状态=Failed，尝试重建会话 (第 {attempts} 次)...");
                    _ = RecoverFromFailedAsync();
                }
            }

            // P3 轻量恢复：Playing 但 presented 停滞 = 解码/呈现线程锁死（高码率 HDR + 缩放时出现）。
            // 连续 N 次轮询无增长则 Pause→Play 重启呈现管线，比全会话重建快一个数量级。
            // Ready/Paused 且 UI 认为在播放：还原后 presented 不涨的停滞场景，同样轻量恢复。
            var shouldPlay = _isPlaying || m.State is PlayerState.Ready or PlayerState.Paused;
            if (_realMode && m.State is PlayerState.Playing or PlayerState.Ready or PlayerState.Paused
                && shouldPlay)
            {
                var presented = m.PresentedVideoFrames;
                if (presented == _lastPresentedCount)
                {
                    _stalledPolls++;
                    // 播放中轮询 250ms，5 次 ≈ 1.25 秒无增长判定停滞
                    if (_stalledPolls >= 5 && _isPlaying
                        && System.Threading.Interlocked.CompareExchange(ref _recovering, 1, 0) == 0)
                    {
                        // 第一级：轻量恢复 Pause→Play（重启呈现管线）
                        if (!_stalledAfterLightRecovery)
                        {
                            Console.Error.WriteLine($"[MainWindow] presented 停滞 ({presented})，轻量恢复 Pause→Play...");
                            try
                            {
                                _sync.Pause();
                                await Task.Delay(120);
                                _sync.Play();
                                Console.Error.WriteLine("[MainWindow] ✅ 轻量恢复完成");
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[MainWindow] 轻量恢复失败: {ex.Message}");
                            }
                            finally
                            {
                                System.Threading.Interlocked.Exchange(ref _recovering, 0);
                            }
                            _stalledAfterLightRecovery = true;
                            _stalledPolls = 0;
                        }
                        else
                        {
                            // 第二级：轻量恢复无效 → 完整会话重建（复用 Failed 路径）
                            Console.Error.WriteLine("[MainWindow] 轻量恢复无效，升级为完整重建...");
                            _ = RecoverFromFailedAsync();
                        }
                    }
                }
                else
                {
                    _stalledPolls = 0;
                    _stalledAfterLightRecovery = false; // 恢复增长后重置升级标志
                    _lastPresentedCount = presented;
                }
            }
            else
            {
                _stalledPolls = 0;
                if (master is not null) _lastPresentedCount = master.PresentedVideoFrames;
            }

            _timeline.SetDuration(m.Duration100ns);
            if (!_timeline.IsScrubbing)
                _timeline.SetPosition(m.Position100ns);
            _transport.SetTime(
                TimeSpan.FromTicks(m.Position100ns),
                TimeSpan.FromTicks(m.Duration100ns),
                FrameInSecond(m));
        }

        // 播放状态回显（若被原生事件改变）
        if (master is { State: PlayerState.Playing } && !_isPlaying) SetPlaying(true);
        else if (master is not null and { State: not PlayerState.Playing } && _isPlaying) SetPlaying(false);

        // 循环回绕
        if (_sync.LoopEnabled) _sync.TickLoop();

        // 伪变速：真实模式下按速度节流 Seek（A2 落地前的临时方案）。
        // 3FCompare 优化：Seek 最小间隔 1s——每次 Seek 是 9 路 av_seek_frame + 双解码器
        // flush（CPU 尖峰），250ms 一次会造成周期性顿挫；1s 粒度下跳变仍平滑可接受。
        if (_isPlaying && _realMode && Math.Abs(_playbackSpeed - 1.0) > 0.01 && master is not null)
        {
            var now = Environment.TickCount64;
            var pos = master.Position100ns;
            if (_lastShownPos == 0) _lastShownPos = pos;
            if (now - _lastSpeedSeekTicks >= 1000)
            {
                _lastSpeedSeekTicks = now;
                var mediaElapsed = pos - _speedBasePos;
                if (_playbackSpeed > 1.0 && mediaElapsed > 0)
                    SafeSeek(pos + (long)(mediaElapsed * (_playbackSpeed - 1.0)));
                _speedBasePos = master.Position100ns;
            }
            _lastShownPos = pos;
        }
        else if (_lastShownPos != 0 && master is not null)
        {
            _lastShownPos = master.Position100ns;
        }

// 自适应频率三档：播放 250ms / 暂停有会话 250ms / 空闲无会话 1000ms
        int target;
        if (_isPlaying && _sync.Count > 0)
            target = 250;  // 播放中：4Hz 刷新时间码（降低Avalonia重绘/GC/P-Invoke频率）
        else if (_sync.Count > 0)
            target = 250;  // 暂停有会话：保持状态同步
        else
            target = 1000; // 无会话：纯 keepalive
        // 3FCompare 优化项⑧：拖动释放后的 83ms 高刷豁免窗（否则被上面的覆盖逻辑立即改回，从未生效）
        if (Environment.TickCount64 - _lastPanApplyTicks < 800)
            target = Math.Min(target, 83);
        var current = _pollTimer.Interval.TotalMilliseconds;
        if (Math.Abs(current - target) > 1)
            _pollTimer.Interval = TimeSpan.FromMilliseconds(target);
    }

    /// <summary>PR 时间码的秒内帧号（1 起；帧率由快照时间基估算，缺省 24）。</summary>
    private static int FrameInSecond(EngineSnapshot snap)
    {
        // 原生帧索引（0 起）
        if (snap.FrameIndex >= 0)
        {
            var fps = SyncController.EstimateFps(snap);
            if (fps <= 0) return 0;
            return (int)(snap.FrameIndex % Math.Max(1, (int)Math.Round(fps)));
        }
        // 回退：从位置计算，0 起
        var frameRate = SyncController.EstimateFps(snap);
        if (frameRate <= 0) return 0;
        var sec = TimeSpan.TicksPerSecond;
        var frac = (double)(snap.Position100ns % sec) / sec;
        return Math.Clamp((int)Math.Floor(frac * frameRate), 0, (int)Math.Round(frameRate) - 1);
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

// ══════════ 视图变换：缩放/平移 ══════════

    /// <summary>光标位置命中测试（探针/放大镜/选中用）。</summary>
    private PlayerSurface? HitSurfaceAt(Point windowPos)
    {
        foreach (var s in Grid.Surfaces)
        {
            if (!s.IsVisible) continue;
            var tl = s.TranslatePoint(new Point(0, 0), this);
            if (tl is not { } origin) continue;
            if (new Rect(origin, new Size(s.Bounds.Width, s.Bounds.Height)).Contains(windowPos)) return s;
        }
        return null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // WM_MOUSEWHEEL 发给焦点窗口，经 Avalonia 视觉树路由。唯一滚轮处理器。
        if (HitSurfaceAt(e.GetPosition(this)) is not null)
        {
            var factor = e.Delta.Y > 0 ? 1.15f : 1f / 1.15f;
            _viewZoom = Math.Clamp(_viewZoom * factor, 1f, 32f);
            ApplyViewTransform();
            e.Handled = true;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point pt);

    private bool _panDragging;
    private int _panLastX, _panLastY;
    private int _recovering; // 0=空闲, 1=恢复中（防止并发重建导致崩溃）
    private int _recoveryAttempts; // 重建风暴抑制：连续失败计数，≥3 放弃避免死循环

    /// <summary>左键按下（WndProc 转发）：放大中→开始平移。
    /// 降低轮询频率（不停止，保持 Failed 检测和位置同步）。</summary>
    private void OnSurfacePress(double x, double y)
    {
        Console.Error.WriteLine($"[Pan] OnSurfacePress zoom={_viewZoom:F3} willDrag={_viewZoom > 1.001f}");
        if (_viewZoom > 1.001f)
        {
            _panDragging = true;
            // 降低但不停止：保持 Failed 状态检测和多路同步
            _pollTimer.Interval = TimeSpan.FromMilliseconds(250);
            GetCursorPos(out var pt);
            _panLastX = pt.X;
            _panLastY = pt.Y;
            Console.Error.WriteLine($"[Pan] Dragging started at screen ({pt.X},{pt.Y})");
        }
    }

    /// <summary>鼠标移动（WndProc 转发）：拖拽平移中持续更新偏移。</summary>
    private void OnSurfaceMove(double x, double y)
    {
        if (!_panDragging) return;
        GetCursorPos(out var pt);
        var dx = pt.X - _panLastX;
        var dy = pt.Y - _panLastY;
        _panLastX = pt.X;
        _panLastY = pt.Y;
        // 归一化到 [-1,1]：以窗口短边为基准。旧实现用长边导致宽屏下横向
        // 灵敏度减半，叠加节流后"左右拖不动"。（3FCompare patch 0006 配套）
        var scale = 2.0f / (float)Math.Min(Bounds.Width, Bounds.Height);
        // 跟手语义：鼠标右/下移 → 画面跟手右/下移（内核视口同向平移）
        _viewPanX = Math.Clamp(_viewPanX + dx * scale, -1f, 1f);
        _viewPanY = Math.Clamp(_viewPanY + dy * scale, -1f, 1f);
        ApplyViewTransform();
    }

    /// <summary>左键释放（WndProc 转发）：结束平移或触发选中。
    /// 立即发送最终变换值确保松手后精确对齐，恢复轮询频率。</summary>
    private void OnSurfaceRelease(double x, double y)
    {
        if (_panDragging)
        {
            _panDragging = false;
            // 立即发送最终位置（UI 线程同步调用：原生 SetViewTransform 只写
            // 三个 atomic，毫秒级；不要用 Task.Run 后台调用——会话重建/关闭时
            // UI 线程已释放原生句柄，后台 P/Invoke 访问会 0xC0000005 闪退）。
            var now = Environment.TickCount64;
            _lastPanApplyTicks = now;
            try { _sync.SetViewTransform(_viewZoom, _viewPanX, _viewPanY); }
            catch (Exception ex) { Console.Error.WriteLine($"[Transform] Release FAIL: {ex.Message}"); }
            // 恢复轮询频率
            if (_isPlaying && _sync.Count > 0)
                _pollTimer.Interval = TimeSpan.FromMilliseconds(83);
            else if (_sync.Count > 0)
                _pollTimer.Interval = TimeSpan.FromMilliseconds(250);
            return;
        }
        // 未放大时的点击 → 选中表面
        GetCursorPos(out var pt);
        var windowPos = new Point(pt.X - Position.X, pt.Y - Position.Y);
        if (HitSurfaceAt(windowPos) is { } clicked)
        {
            Grid.SelectedIndex = clicked.Index;
            UpdatePanelsForSelection();
        }
    }

    private void ApplyViewTransform()
    {
        PlayerSurface.SharedZoom = _viewZoom;
        PlayerSurface.SharedPanX = _viewPanX;
        PlayerSurface.SharedPanY = _viewPanY;

        // 节流：150ms (~7Hz)，平衡响应速度与 Redraw() 开销
        var now = Environment.TickCount64;
        if (now - _lastPanApplyTicks < 150) return;
        _lastPanApplyTicks = now;

        // UI 线程同步调用：原生 SetViewTransform 仅写 3 个 atomic（毫秒级）。
        // 不要用 Task.Run——线程池并发 P/Invoke 在会话重建/关闭时访问已释放句柄
        // 会触发 0xC0000005 闪退，且跨线程读取 _viewZoom/_viewPanX/Y 无序导致"拖不动"。
        try
        {
            Console.Error.WriteLine($"[Transform] zoom={_viewZoom:F2} pan=({_viewPanX:F3},{_viewPanY:F3})");
            _sync.SetViewTransform(_viewZoom, _viewPanX, _viewPanY);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Transform] FAIL: {ex.Message}"); }
    }


    private void ResetViewTransform()
    {
        _viewZoom = 1f;
        _viewPanX = _viewPanY = 0f;
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
            // 先恢复偏移，再 SeekTo（SeekTo 内部会叠加偏移）
            for (var i = 0; i < snapshot.Items.Count && i < _sync.Count; i++)
                _sync.Slots[i].Offset100ns = snapshot.Items[i].Offset100ns;
            _sync.SeekTo(snapshot.Position100ns);
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

/// <summary>批量采样重建帧（WgcFrameCapture 不可用时的退路，~320px 宽）。
    /// 3FCompare patch (0004)：优先走内核单次 staging 拷贝 API；不支持时回退逐像素。</summary>
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
            // 批量路径：一次 P/Invoke 取整块区域（内核 staging 拷贝 + Map）
            var stride = 2;
            var sw = (w + stride - 1) / stride;
            var sh = (h + stride - 1) / stride;
            var buffer = new float[sw * sh * 4];
            if (session.TryReadPixelRegion(0, 0, sw, sh, buffer, out _))
            {
                for (var sy = 0; sy < sh; sy++)
                {
                    for (var sx = 0; sx < sw; sx++)
                    {
                        var i = (sy * sw + sx) * 4;
                        var x = sx * stride;
                        var y = sy * stride;
                        var c = System.Drawing.Color.FromArgb(
                            To8(buffer[i + 3]), To8(buffer[i]), To8(buffer[i + 1]), To8(buffer[i + 2]));
                        bmp.SetPixel(x, y, c);
                        if (x + 1 < w) bmp.SetPixel(x + 1, y, c);
                        if (y + 1 < h) bmp.SetPixel(x, y + 1, c);
                        if (x + 1 < w && y + 1 < h) bmp.SetPixel(x + 1, y + 1, c);
                    }
                }
                return bmp;
            }
            // 回退路径：逐像素（旧行为）
            for (var y = 0; y < h; y += stride)
            {
                for (var x = 0; x < w; x += stride)
                {
                    if (!session.TryReadPixel((int)((x + 0.5) / w * srcW), (int)((y + 0.5) / h * srcH), out var s))
                        return null;
                    var c = System.Drawing.Color.FromArgb(To8(s.A), To8(s.R), To8(s.G), To8(s.B));
                    bmp.SetPixel(x, y, c);
                    if (x + 1 < w) bmp.SetPixel(x + 1, y, c);
                    if (y + 1 < h) bmp.SetPixel(x, y + 1, c);
                    if (x + 1 < w && y + 1 < h) bmp.SetPixel(x + 1, y + 1, c);
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
        _sidebar.Expand();
        SidebarSplitter.IsVisible = true;
    }

    private void OnGridPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string preset })
            Grid.SetGridLayout(preset);
    }

    private void OnShowGridOnly(object? sender, RoutedEventArgs e)
    {
        if (_sidebar.Collapsed)
            _sidebar.Expand();
        else
            _sidebar.ToggleCollapse();
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
            CopySettings(result); // 更新 _settings，避免 OnClosing 时用旧值覆盖
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
        _settings.FfmpegDirectory = s.FfmpegDirectory;
        _settings.ColorMode = s.ColorMode;
        _settings.FrameStep = s.FrameStep;
        _settings.SecondsStep = s.SecondsStep;
        _settings.StartFullscreen = s.StartFullscreen;
        _settings.HideChromeInFullscreen = s.HideChromeInFullscreen;
        _settings.DefaultGridCols = s.DefaultGridCols;
        _settings.DefaultGridRows = s.DefaultGridRows;
        _settings.VrrTearingPresent = s.VrrTearingPresent;
        _settings.VrrPacingEnabled = s.VrrPacingEnabled;
        _settings.ScrubPreviewEnabled = s.ScrubPreviewEnabled;
        _settings.MinimapEnabled = s.MinimapEnabled;
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
        {
            // 坐标从表面客户区像素逆缩放到视频原生分辨率（TryReadPixel 需要原生坐标）
            var slot = _sync.Slots.ElementAtOrDefault(surface.Index);
            var media = slot?.Session?.ReadMediaInfo();
            if (media is not null && surface.Bounds.Width > 0 && surface.Bounds.Height > 0)
            {
                var nx = (int)(local.X / surface.Bounds.Width * media.VideoWidth);
                var ny = (int)(local.Y / surface.Bounds.Height * media.VideoHeight);
                _probe.UpdatePoint(nx, ny);
            }
            else
            {
                _probe.UpdatePoint((int)local.X, (int)local.Y);
            }
        }
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
        // 关键：确保 Avalonia 主窗口有 WS_CLIPCHILDREN 样式。
        // 没有此样式时，Avalonia 的 OpenGL 渲染会覆盖 NativeControlHost 子 HWND 区域，
        // 导致视频画面被 UI 渲染覆盖而"卡死"（引擎仍在呈现帧但用户看不到）。
        TryEnableClipChildren();

        if (WindowState == WindowState.Normal)
            _lastNormal = (Position, new Size(Width, Height));
        MaybeExitDemoMode();
        base.OnOpened(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    private const int GWL_STYLE = -16;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE = 9;

    /// <summary>给 Avalonia 顶层窗口添加 WS_CLIPCHILDREN 样式，
    /// 防止 Avalonia 的 WGL 渲染覆盖 NativeControlHost 子窗口。</summary>
    private unsafe void TryEnableClipChildren()
    {
        try
        {
            var handle = this.TryGetPlatformHandle();
            if (handle is null || handle.Handle == nint.Zero) return;
            var style = GetWindowLongW(handle.Handle, GWL_STYLE);
            if ((style & WS_CLIPCHILDREN) == 0)
            {
                SetWindowLongW(handle.Handle, GWL_STYLE, style | WS_CLIPCHILDREN);
                Console.Error.WriteLine($"[MainWindow] WS_CLIPCHILDREN added (was 0x{style:X})");
            }
            else
            {
                Console.Error.WriteLine($"[MainWindow] WS_CLIPCHILDREN already set");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainWindow] TryEnableClipChildren failed: {ex.Message}");
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            var oldState = (WindowState)(change.OldValue ?? WindowState.Normal);
            var newState = (WindowState)(change.NewValue ?? WindowState.Normal);
            // 窗口最大化/还原时，D3D11 SwapChain ResizeBuffers 在渲染中调用会失败。
            // 先暂停播放，让引擎在空闲状态下完成尺寸转换，避免进入 Failed 状态。
            if ((newState == WindowState.Maximized || oldState == WindowState.Maximized) && _sync.Count > 0)
            {
                var wasPlaying = false;
                try
                {
                    var snap = _sync.ReadMasterSnapshot();
                    wasPlaying = snap is { State: PlayerState.Playing };
                    if (wasPlaying)
                    {
                        Console.Error.WriteLine($"[MainWindow] 窗口状态变化，暂停播放中...");
                        _sync.Pause();
                    }
                }
                catch { /* 忽略 */ }

                // 延迟恢复播放（等待窗口尺寸转换完成）
                if (wasPlaying)
                {
                    _ = ResumeAfterResizeAsync();
                }
            }

            if (newState == WindowState.Normal)
                _lastNormal = (Position, Bounds.Size);
        }
    }

    private async System.Threading.Tasks.Task ResumeAfterResizeAsync()
    {
        try
        {
            // 等待 500ms 确保窗口尺寸转换完成
            await System.Threading.Tasks.Task.Delay(500);
            if (_sync.Count > 0)
            {
                _sync.Play();
                Console.Error.WriteLine($"[MainWindow] 窗口尺寸转换完成，恢复播放");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainWindow] 恢复播放失败: {ex.Message}");
        }
    }

    /// <summary>引擎进入 Failed 状态时重建会话（窗口最大化导致 D3D11 SwapChain 损坏后的恢复路径）。
    /// 重建后轮询确认 presented 持续增长才算恢复；未稳定则重试完整重建（最多 2 轮）。</summary>
    private async System.Threading.Tasks.Task RecoverFromFailedAsync()
    {
        try
        {
            await RecoverFromFailedCoreAsync();

            // 竞态加固：重建后 SwapChain 可能仍在恢复中（presented 不增长）。
            // 轮询确认渲染真正恢复；未恢复则递归再走一轮完整重建（最多 2 次），
            // 避免单次 Play 后即认为成功。
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var stable = false;
                long lastPresented = -1;
                for (var tick = 0; tick < 10; tick++) // 最多 5s 确认窗口
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    if (_coordinator.IsClosed) return;
                    var snap = _sync.ReadMasterSnapshot();
                    if (snap is null) continue;
                    if (snap.State == PlayerState.Failed) break; // 又挂了 → 需要再来一轮
                    if (snap.State == PlayerState.Playing && snap.PresentedVideoFrames > lastPresented)
                    {
                        if (lastPresented >= 0) { stable = true; break; } // 连续两次增长才算稳定
                        lastPresented = snap.PresentedVideoFrames;
                    }
                    else if (snap.State is PlayerState.Paused or PlayerState.Ready or PlayerState.Ended)
                    {
                        // 重建回调里的 Play 可能再次撞上 InvalidState/尺寸转换，补一次 Play
                        try { _sync.Play(); } catch { }
                        lastPresented = snap.PresentedVideoFrames;
                    }
                }
                if (stable)
                {
                    Console.Error.WriteLine($"[MainWindow] ✅ 渲染已稳定恢复");
                    System.Threading.Interlocked.Exchange(ref _recoveryAttempts, 0); // 成功 → 重置计数
                    return;
                }
                if (_coordinator.IsClosed) return;
                Console.Error.WriteLine($"[MainWindow] ⚠ 渲染未稳定（第 {attempt + 1} 次确认失败），重试完整重建...");
                await RecoverFromFailedCoreAsync();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MainWindow] 会话重建失败: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _recovering, 0);
        }
    }

    /// <summary>会话重建核心（打开→恢复位置→播放）。供 RecoverFromFailedAsync 与其重试循环复用。</summary>
    private async System.Threading.Tasks.Task RecoverFromFailedCoreAsync()
    {
        // 只重建未失败的路；快照（位置/偏移/循环）也必须按同一过滤集合采集，
        // 否则 Failed 路被剔除后 offsets 会与新建会话错位。
        var aliveSlots = _sync.Slots.Where(s => !s.Failed).ToList();
        var paths = aliveSlots.Select(s => s.Path).ToArray();
        if (paths.Length == 0) return;
        // master（第 0 路）若本身是 Failed 路则快照冻结，取第一个存活路的位置兜底
        var pos = _sync.Slots.Count > 0 && !_sync.Slots[0].Failed
            ? _sync.GetMasterPosition100ns()
            : _sync.Slots.FirstOrDefault(s => !s.Failed)?.Session?.ReadSnapshot().Position100ns ?? 0;
        var offsets = aliveSlots.Select(s => s.Offset100ns).ToArray();
        // A-B 循环区间随会话一起恢复（对齐 RecoverFromSessionSnapshot 的语义）
        var loopEnabled = _sync.LoopEnabled && _sync.LoopEnd100ns > _sync.LoopStart100ns;
        var loopStart = _sync.LoopStart100ns;
        var loopEnd = _sync.LoopEnd100ns;

        Console.Error.WriteLine($"[MainWindow] 重建会话: {paths.Length}路, pos={TimeSpan.FromTicks(pos):g}");

        _sync.Pause();
        _sync.Stop();
        foreach (var s in Grid.Surfaces)
            s.DetachSession();
        _sync.Clear();

        // 等待子窗口稳定（PollSnapshots 在此期间被 _recovering 标志阻止）
        await System.Threading.Tasks.Task.Delay(300);

        _coordinator.OpenFiles(paths, autoPlay: true, onAllOpened: () =>
        {
            for (var i = 0; i < offsets.Length && i < _sync.Count; i++)
                _sync.Slots[i].Offset100ns = offsets[i];
            if (loopEnabled)
            {
                _sync.LoopStart100ns = loopStart;
                _sync.LoopEnd100ns = loopEnd;
                _sync.LoopEnabled = true;
                _timeline.SetLoopRange(loopStart, loopEnd, true);
            }
            _sync.SeekTo(pos);
            _sync.Play();
            Console.Error.WriteLine($"[MainWindow] ✅ 会话重建完成，已恢复到 {TimeSpan.FromTicks(pos):g}");
        });

        // 等待打开完成（OpenFiles 是异步的）
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !_coordinator.IsClosed)
        {
            var snap = _sync.ReadMasterSnapshot();
            if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
            await System.Threading.Tasks.Task.Delay(200);
        }
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
        _3FCompare.Core.Diagnostics.AppLog.Shutdown();
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
                    Log($"⚠ 帧步进位置后退（已知问题）{before} → {after}");
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
                    Log($"⚠ 秒步进位置后退（已知问题）{before} → {after}");
            }

            // 窗口最大化/还原测试：验证窗口最大化后视频渲染是否继续
            _step = "窗口最大化测试";
            if (_realMode)
            {
                // 确保播放中
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(500);

                var hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
                if (hwnd != nint.Zero)
                {
                    var before = _sync.GetMasterPosition100ns();
                    var beforeSnap = _sync.ReadMasterSnapshot();
                    Log($"最大化前: pos={TimeSpan.FromTicks(before):g} presented={beforeSnap?.PresentedVideoFrames} state={beforeSnap?.State}");

                    // 最大化
                    ShowWindow(hwnd, SW_MAXIMIZE);
                    await System.Threading.Tasks.Task.Delay(3000);

                    var midSnap = _sync.ReadMasterSnapshot();
                    Log($"最大化后: pos={TimeSpan.FromTicks(midSnap?.Position100ns ?? 0):g} presented={midSnap?.PresentedVideoFrames} state={midSnap?.State}");

                    // 如果进入 Failed 状态，等待恢复尝试（PollSnapshots 中的 recovery 逻辑）
                    if (midSnap?.State == PlayerState.Failed)
                    {
                        Log($"引擎进入 Failed 状态，等待恢复...");
                        await System.Threading.Tasks.Task.Delay(3000);
                        var recoverySnap = _sync.ReadMasterSnapshot();
                        Log($"恢复后: state={recoverySnap?.State} presented={recoverySnap?.PresentedVideoFrames}");
                        if (recoverySnap?.State == PlayerState.Failed)
                        {
                            Log($"❌ 引擎恢复失败，渲染管线永久停滞");
                            throw new InvalidOperationException("窗口最大化导致引擎永久失败");
                        }
                        else
                        {
                            Log($"✅ 引擎恢复成功 (state={recoverySnap?.State})");
                        }
                    }

                    // 还原
                    ShowWindow(hwnd, SW_RESTORE);
                    await System.Threading.Tasks.Task.Delay(3000);

                    var afterSnap = _sync.ReadMasterSnapshot();
                    var after = _sync.GetMasterPosition100ns();
                    var presentedDelta = (afterSnap?.PresentedVideoFrames ?? 0) - (midSnap?.PresentedVideoFrames ?? 0);
                    Log($"还原后: pos={TimeSpan.FromTicks(after):g} state={afterSnap?.State} presented={afterSnap?.PresentedVideoFrames} (Δpresented/3秒={presentedDelta})");

                    if (presentedDelta <= 0 && afterSnap?.State == PlayerState.Playing)
                    {
                        Log($"❌ 窗口最大化/还原后视频卡死！presented 未增长");
                        throw new InvalidOperationException("最大化/还原导致渲染停滞");
                    }
                    else if (afterSnap?.State == PlayerState.Failed)
                    {
                        Log($"❌ 引擎处于 Failed 状态，渲染管线已死锁");
                        throw new InvalidOperationException("最大化/还原导致引擎永久失败");
                    }
                    else if (afterSnap?.State != PlayerState.Playing)
                    {
                        Log($"⚠ 播放状态变为 {afterSnap?.State}（非卡死，尝试恢复播放）");
                        _sync.Play();
                        await System.Threading.Tasks.Task.Delay(1000);
                        var final = _sync.ReadMasterSnapshot();
                        Log($"恢复播放后: state={final?.State} presented={final?.PresentedVideoFrames}");
                    }
                    else
                    {
                        Log($"✅ 最大化/还原测试通过：presented +{presentedDelta}/3秒");
                    }
                }
                else
                {
                    Log("⚠ 无法获取窗口句柄，跳过最大化测试");
                }
            }
            _step = "媒体信息";
            var media = _sync.Slots[0].Session.ReadMediaInfo();
            if (media is not null)
                Log($"媒体 {media.VideoWidth}x{media.VideoHeight} @{SyncController.EstimateFps(ready):0.##}fps {media.Codec} HDR={media.IsHdr}");

            // 视图变换压力测试：模拟用户快速滚动缩放，检测是否导致视频卡死
            _step = "视图变换压力测试";
            {
                // 先 Seek 到视频开头，确保有足够的播放时长
                _sync.SeekTo(0);
                await System.Threading.Tasks.Task.Delay(300);
                // 启用循环防止短视频播完
                var dur = _sync.GetMasterDuration100ns();
                _sync.LoopEnabled = true;
                _sync.LoopStart100ns = 0;
                _sync.LoopEnd100ns = dur;
                _sync.Play();
                await System.Threading.Tasks.Task.Delay(500);

                // 确认播放中 + 读取呈现计数器基线
                var beforePos = _sync.GetMasterPosition100ns();
                var beforeSnap = _sync.ReadMasterSnapshot();
                var beforePresented = beforeSnap?.PresentedVideoFrames ?? -1;
                var beforeSwapPresents = beforeSnap?.SwapChainPresents ?? -1;
                var beforeState = beforeSnap?.State;
                Log($"变换前: pos={TimeSpan.FromTicks(beforePos):g} state={beforeState} presented={beforePresented} swap={beforeSwapPresents}");

                // 模拟用户快速滚动 20 次（每次间隔 50ms，模拟快速滚轮）
                for (var i = 0; i < 20; i++)
                {
                    var z = 1f + (i % 5) * 0.5f; // 1.0 → 3.0 循环
                    _sync.SetViewTransform(z, 0.1f * (i % 3), 0.05f * (i % 2));
                    await System.Threading.Tasks.Task.Delay(50);
                }

                // 等待 2 秒让引擎处理完排队的变换
                await System.Threading.Tasks.Task.Delay(2000);

                // 检查视频是否仍在播放 + 呈现计数器是否继续增长
                var midSnap = _sync.ReadMasterSnapshot();
                var midState = midSnap?.State;
                var midPresented = midSnap?.PresentedVideoFrames ?? -1;
                var afterTransforms = _sync.GetMasterPosition100ns();

                await System.Threading.Tasks.Task.Delay(1000);
                var finalSnap = _sync.ReadMasterSnapshot();
                var finalPresented = finalSnap?.PresentedVideoFrames ?? -1;
                var finalCheck = _sync.GetMasterPosition100ns();

                var presentDelta = finalPresented - midPresented;
                Log($"变换中: state={midState} presented={midPresented} (+{midPresented - beforePresented})");
                Log($"1秒后: pos={TimeSpan.FromTicks(finalCheck):g} presented={finalPresented} (Δpresented/秒={presentDelta})");
                Log($"位置增量(1秒) = {(finalCheck - afterTransforms) / 10000}ms");

                if (finalCheck <= afterTransforms && midState == PlayerState.Playing)
                {
                    Log($"❌ 视频已卡死！presented 停在 {finalPresented}");
                    throw new InvalidOperationException(
                        $"视图变换导致视频卡死: presented Δ={presentDelta}");
                }
                else if (presentDelta <= 0 && midState == PlayerState.Playing)
                {
                    Log($"❌ 渲染管线停滞！presented 计数不再增长");
                    throw new InvalidOperationException(
                        $"渲染管线停滞: presented Δ={presentDelta}, pos Δ={(finalCheck - afterTransforms) / 10000}ms");
                }
                else if (midState != PlayerState.Playing)
                {
                    Log($"⚠ 播放状态变为 {midState}（非卡死）");
                }
                else
                {
                    Log($"✅ 压力测试通过：20 次快速变换后视频继续播放 (presented +{presentDelta}/秒)");
                }

                // 恢复正常视图并继续播放
                _sync.SetViewTransform(1.0f, 0f, 0f);
                _sync.Play();
            }

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

