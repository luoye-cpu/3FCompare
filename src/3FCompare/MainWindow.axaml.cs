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

        // 自测/压力测试模式标记必须在任何"写用户配置"的代码之前置位：
        // 侧栏几何恢复也会触发一次 SettingsStore.Save，若此时还没标记，
        // 测试进程会用测试窗口的状态覆盖用户配置（与 SaveWindowGeometry 同一类问题）。
        var cmdArgs = Environment.GetCommandLineArgs();
        // 新增自测模式时**必须同步加到这里**：漏一个就会出现"测试进程用测试窗口的状态
        // 覆盖用户配置"（--sessiontest 曾漏过，退出时把 800×600 的测试窗口几何写进
        // settings.json，用户下次启动窗口就变小了）。
        if (cmdArgs.Length >= 3 &&
            cmdArgs[1] is "--selftest" or "--screentest" or "--multitest" or "--sessiontest")
            _selfTestMode = true;

        // 可用性 P0-2：窗口位置/尺寸/状态的恢复统一在 RestoreWindowGeometry() 完成
        // （构造函数末尾调用——那里 Screens 已可用，且是唯一的恢复入口，避免双处恢复互相覆盖）。

        // 可用性 P0：FFmpeg 缺失时一次性引导——说明降级原因并提供"打开设置"入口。
        // 引擎类型进程内不变，构造期检测一次即可；挂 Opened 保证窗口就绪后弹窗。
        if (!_3FCompare.Core.Backend.EngineFactory.IsNativeAvailable())
        {
            Opened += async (_, _) =>
            {
                var reason = _3FCompare.Core.Backend.EngineFactory.LastUnavailableReason ?? "未知原因";
                var openSettings = await Views.MessageBox.Show(this,
                    "演示模式 / Demo Mode",
                    $"未找到 FFmpeg 核心库，已回退演示模式。\n原因：{reason}\n\n" +
                    "可在设置中指定包含 avcodec-*.dll 的目录，或将 ffmpeg-full 文件夹放在程序旁。\n\n" +
                    "FFmpeg core libraries were not found; running in demo mode.\n" +
                    "Point to the folder containing avcodec-*.dll in Settings.",
                    "打开设置 / Open Settings",
                    "稍后再说 / Later");
                if (openSettings) new Views.SettingsWindow(_settings).Show();
            };
        }

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

        StatusEngine.Text = BuildEngineLabel();
        // P1-2：静态事件若强持有窗口，关窗后整个对象图（含引擎会话与 D3D 设备）都不会释放。
        // 改为弱订阅：目标被回收后订阅自动失效。
        LanguageManager.SubscribeWeak(this, w => w.StatusEngine.Text = w.BuildEngineLabel());

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
            // 3FCompare 修复：滚轮缩放走 WndProc（子 HWND 截获鼠标，Avalonia
            // PointerWheel 路由收不到）→ SurfaceWheel 事件直接驱动
            s.SurfaceWheel += OnSurfaceWheel;
            // 3FCompare 修复：文件拖入走子 HWND 的 WM_DROPFILES（NativeControlHost
            // 子窗口不是 OLE 拖放目标，Avalonia Drop 事件收不到）
            s.SurfaceFilesDropped += OnSurfaceFilesDropped;
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
        _sidebar.CollapsedChanged += OnSidebarCollapsedChanged;
        SidebarHost.Content = _sidebar;

        // 恢复上次会话的侧栏几何（展开宽度 + 折叠态），实现见 MainWindow.Sidebar.cs
        RestoreSidebarGeometry();
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

        // 自动化 selftest / screentest / multitest 模式（GetCommandLineArgs 返回进程原始参数）
        var args = cmdArgs;
        // 自测/压力测试模式：退出前会 Close() 窗口以销毁子 HWND，
        // 必须跳过窗口几何持久化，否则会用测试窗口的位置/尺寸覆盖用户配置。
        // （标志位已在构造函数最前面置好，见上方 cmdArgs 判定。）

        // --selftest <video> [video2]：video2 用于嵌入式拖入测试（可选）
        if (args.Length >= 3 && args[1] == "--selftest")
            _ = RunSelftestAsync(args[2], args.Length >= 4 ? args[3] : null);
        else if (args.Length >= 4 && args[1] == "--screentest")
            _ = RunScreentestAsync(args[2], args[3]);
        // --sessiontest <video> [video2] [video3]：会话存取往返回归（P0-1/P0-3）
        else if (args.Length >= 3 && args[1] == "--sessiontest")
            _ = RunSessiontestAsync(args[2..]);
        // --multitest <video> [routes=4] [durationSec=30]：多路同步压力测试
        else if (args.Length >= 3 && args[1] == "--multitest")
            _ = RunMultitestAsync(args[2],
                args.Length >= 4 && int.TryParse(args[3], out var r) ? r : 4,
                args.Length >= 5 && int.TryParse(args[4], out var d) ? d : 30);
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
        ResetRecoveryState();
        // 3FCompare 修复：先把网格扩到能容纳"现有路数 + 新拖入文件"的数量。
        // PlaybackCoordinator.OpenFiles 用 _surfaceAt(_sync.Count) 取 surface，
        // 若网格没预建足够 surface（拖入第 3 路/多个文件）会静默跳过打开。
        var needed = Math.Min(9, _sync.Count + paths.Count);
        if (Grid.Count < needed)
        {
            Console.Error.WriteLine($"[MainWindow] OpenPaths: 扩展网格 {Grid.Count}→{needed}");
            Grid.SetCount(needed, _realMode);
        }
        // Keep every route on a common paused timeline while files finish opening.
        // Auto-playing the first route lets its clock advance before later routes are ready.
        _sync.Pause();
        SetPlaying(false);
        // P2 清理：这里原先套着 try/catch，但 OpenFiles 是 async void 且内部已自行兜底捕获
        // ——异常不会传播到这里，catch 永远不触发，只会误导后来者以为打开失败已被处理。
        // 失败通知走 PlaybackCoordinator.LastOpenError + StateChanged 事件。
        _coordinator.OpenFiles(paths, autoPlay: false);
        UpdateStatus();
    }

    /// <summary>重置重建风暴抑制状态。
    /// <para>注释承诺的是"连续失败 ≥3"，但 <c>_recoveryAttempts</c> 只在成功恢复时清零，
    /// 实际语义却是**进程内累计**：① 暂停中触发恢复时，确认循环要求 State==Playing 才算 stable，
    /// 暂停态必然拿不到 stable，白白烧掉一次计数；② 累计满 3 次后，即使 SwapChain 后来恢复健康，
    /// 再遇 Failed 也永不重建 → 永久黑屏，只能重启进程。</para>
    /// 因此打开新媒体时必须显式重置，"连续失败"的语义才真正成立。</summary>
    private void ResetRecoveryState()
    {
        System.Threading.Interlocked.Exchange(ref _recoveryAttempts, 0);
        _recoveryAbandoned = false;
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
        // StepFrames 内含 50ms 复测等待（Thread.Sleep）——移到线程池执行，避免阻塞 UI
        _transport.FrameStepClicked += (_, d) => StepFramesAsync(d * _sync.StepProfile.FrameStep);
        _transport.SecondsStepClicked += (_, d) => _sync.StepSeconds(d);
        _transport.LoopToggled += (_, on) => ToggleLoop(on);
        _transport.AddClicked += (_, _) => AddSlotPlaceholder();
        _transport.RemoveClicked += (_, _) => RemoveLastSlot();
        _transport.SpeedChanged += (_, s) =>
        {
            _playbackSpeed = s;
            // 伪变速基准点必须随倍速切换复位：_speedBasePos 初值为 0 且只在每次 Seek 后更新，
            // 若切换时不复位，下一次轮询算出的 mediaElapsed 会是"整个已播放时长"而非"近 1s"
            // —— 30s 处切 2× 会直接跳到 60s（4× 则跳到片尾）。
            _speedBasePos = _sync.GetMasterPosition100ns();
            _lastSpeedSeekTicks = Environment.TickCount64;
        };
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
        // P1-8：色调映射必须在整个对比会话内统一。若让每一路各自按自身媒体信息判定 HDR，
        // 那么"同一素材的两个编码版本"只要有一路 HDR 元数据丢失（重编码时常见），
        // 两路就会走不同的色调映射曲线，亮度不再可比——而这正是本软件的核心场景。
        // 取并集：任一路为 HDR 则全体按 HDR 处理，并在状态栏显式提示。
        var slots = _sync.Slots;
        var hdrCount = 0;
        var sdrCount = 0;
        foreach (var s in slots)
        {
            try
            {
                switch (s.Session.ReadMediaInfo()?.IsHdr)
                {
                    case true: hdrCount++; break;
                    case false: sdrCount++; break;
                }
            }
            catch { /* 未打开或演示模式：不参与判定 */ }
        }
        bool? unifiedHdr = hdrCount > 0 ? true : sdrCount > 0 ? false : null;

        foreach (var slot in slots)
        {
            try { slot.Session.SetColorMode(mode, unifiedHdr); } catch { /* 演示模式无操作 */ }
        }

        if (hdrCount > 0 && sdrCount > 0)
            StatusInfo.Text = LanguageManager.T("Status_ColorModeUnified");
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



    // 选中路 RTInfo 用于状态栏诊断
    private RenderTargetInfo? _lastRtInfo;

    /// <summary>状态栏引擎标签（可用性 P1-3）：真实模式显示引擎名；演示模式除"演示模式"
    /// 外还附上降级原因（EngineFactory.CurrentModeName 已拼好，如"演示模式：DllNotFoundException：
    /// FFF.Native.dll 加载失败"）。原因是英文异常名，不参与本地化，仅前缀走语言表。</summary>
    private string BuildEngineLabel()
    {
        var prefix = LanguageManager.T(_realMode ? "Status_EngineReal" : "Status_EngineDemo");
        if (_realMode) return prefix;
        // 演示模式：前缀已含"(Simulated)"，把降级原因追加在后面
        var reason = _3FCompare.Core.Backend.EngineFactory.LastUnavailableReason;
        return reason is null ? prefix : $"{prefix} · {reason}";
    }

    private void UpdateStatus()
    {
        // 消费打开降级原因（N1）：显示一次即清空，避免重复覆盖常规状态
        var openError = _coordinator.LastOpenError;
        if (!string.IsNullOrEmpty(openError))
        {
            _coordinator.ConsumeLastOpenError();
            StatusEngine.Text = openError;
            return;
        }
        if (_sync.Count == 0)
        {
            StatusInfo.Text = LanguageManager.T(_realMode ? "Status_Ready" : "Status_DemoHint");
            return;
        }
        var mode = Grid.SingleView ? LanguageManager.T("Status_SingleMode") : LanguageManager.T("Status_GridMode");
        var failed = _sync.Slots.Count(s => s.Failed);
        var runtimeError = _sync.LastRuntimeError;

        // 3FCompare M6: 选中路渲染目标诊断
        var sb = new System.Text.StringBuilder();
        sb.Append($"{mode}模式 | 路数 {_sync.Count}/9 | {LanguageManager.T("Status_Steps")}: {_sync.StepProfile.FrameStep}帧/{_sync.StepProfile.SecondsStep:0.#}秒");
        if (failed > 0) sb.Append($" | {failed} 路失败");
        if (runtimeError is not null) sb.Append($" | ⚠ {runtimeError}");

        // Try read RTInfo from selected session
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        if (slot?.Session is { } session && session.ReadRenderTargetInfo(out var rt))
        {
            _lastRtInfo = rt;
            if (rt.SwapWidth > 0 && rt.SwapHeight > 0)
            {
                sb.Append($" | SW:{rt.ClientWidth}x{rt.ClientHeight}->{rt.SwapWidth}x{rt.SwapHeight}");
                if (rt.DestWidth > 0)
                    sb.Append($" | V:{rt.DestX},{rt.DestY} {rt.DestWidth}x{rt.DestHeight}");
                sb.Append($" | bpp:{rt.OutputBitDepth} {(rt.Hdr ? "HDR" : "SDR")}");
            }
        }
        StatusInfo.Text = sb.ToString();
        UpdateStatusView();
    }

    /// <summary>状态栏右区：选中路渲染分辨率 + 当前缩放。
    /// 主流播放器/编辑器（PotPlayer 状态栏、Premiere Program Monitor 信息条）都在右下角常驻
    /// 这两项；此前它们混在中间那串诊断文本里，视线要横扫整条状态栏。</summary>
    private void UpdateStatusView()
    {
        var parts = new System.Collections.Generic.List<string>(2);
        var rt = _lastRtInfo;
        if (rt is { } r && r.SwapWidth > 0 && r.SwapHeight > 0)
            parts.Add($"{r.SwapWidth}×{r.SwapHeight}");
        var zoom = PlayerSurface.SharedZoom;
        if (zoom > 1.001f) parts.Add($"×{zoom:0.##}");
        StatusView.Text = string.Join("   ", parts);
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
    private int _recoveryAttempts; // 重建风暴抑制：失败计数，≥3 放弃避免死循环（语义见 ResetRecoveryState）
    private bool _recoveryAbandoned; // 已放弃重建：只用于抑制日志洪水，别让每 tick 都打一行

    /// <summary>滚轮缩放（PlayerSurface WndProc 转发）。delta&gt;0 放大，&lt;0 缩小。
/// NativeControlHost 子 HWND 截获鼠标消息，Avalonia 顶层窗口的 OnPointerWheelChanged
/// 收不到；改为子类化 WndProc 处理 WM_MOUSEWHEEL 后通过 SurfaceWheel 事件回传。</summary>
    private void OnSurfaceWheel(short delta)
    {
        var factor = delta > 0 ? 1.15f : 1f / 1.15f;
        _viewZoom = Math.Clamp(_viewZoom * factor, 1f, 32f);
        ApplyViewTransform();
    }

    /// <summary>文件拖入（PlayerSurface 子 HWND/覆盖层 WM_DROPFILES 转发）。
    /// NativeControlHost 子 HWND 不是 OLE 拖放目标，Avalonia 顶层 Drop 事件收不到；
    /// 通过 DragAcceptFiles + WM_DROPFILES 让子 HWND 直接接收文件拖入。</summary>
    private void OnSurfaceFilesDropped(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        if (paths is { Count: > 0 })
            OpenPaths(paths);
    }

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
        // P1-18 修复：GetCursorPos 返回的是**物理屏幕像素**，而 HitSurfaceAt（内部用
        // TranslatePoint）用的是**客户区 DIP**。旧的 `pt - Position` 写法错在两处：
        //   ① 没做物理像素 → DIP 换算（150% 缩放偏 1.5 倍、250% 偏 2.5 倍）；
        //   ② Position 是窗口外框左上角（含标题栏与边框），客户区原点在其下方。
        // 两者叠加会让高 DPI 用户点 A 画面却选中 B 画面，且缩放越大偏得越多。
        var windowPos = this.PointToClient(new PixelPoint(pt.X, pt.Y));
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
        UpdateStatusView();

        // 3FCompare M7: 16ms (~60Hz) 节流，内核 0006 后 SetViewTransform 仅写 3 个 atomic（毫秒级）。
        // 删除高频 stderr WriteLine（管道满时反压 UI 线程）。
        var now = Environment.TickCount64;
        if (now - _lastPanApplyTicks < 16) return;
        _lastPanApplyTicks = now;

        // UI 线程同步调用。不要用 Task.Run——线程池并发 P/Invoke 在会话重建/关闭时访问已释放句柄
        // 会触发 0xC0000005 闪退，且跨线程读取 _viewZoom/_viewPanX/Y 无序导致"拖不动"。
        try
        {
            _sync.SetViewTransform(_viewZoom, _viewPanX, _viewPanY);
        }
        catch { /* 忽略避免 stderr 反压 */ }
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
            // P1-4：键盘步进同样必须走线程池（内含 50ms 复测等待），
            // 否则每按一次 ←/→ 都会冻结 UI 50ms，连按时手感明显卡顿。
            case Key.Left when mods == KeyModifiers.None: StepFramesAsync(-_sync.StepProfile.FrameStep); break;
            case Key.Right when mods == KeyModifiers.None: StepFramesAsync(_sync.StepProfile.FrameStep); break;
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

        var snapshot = BuildSessionSnapshot();
        SessionSnapshot.SaveToFile(path, snapshot);
        StatusInfo.Text = $"{LanguageManager.T("Status_ExportDone")}: {Path.GetFileName(path)}";
    }

    /// <summary>按当前状态构造会话快照（菜单保存与自测 --sessiontest 共用同一份，
    /// 避免测试另写一套而与真实保存路径漂移）。</summary>
    internal SessionSnapshot BuildSessionSnapshot() => new()
    {
        GridLayout = _3FCompare.Core.Display.GridLayout.CodeFor(Grid.SingleView, _sync.Count),
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

        LoadSessionSnapshot(snapshot);
    }

    /// <summary>按会话快照重开全部路（菜单加载与自测 --sessiontest 共用同一条路径）。
    /// <para><b>P0-1 关键点</b>：顺序必须是 SetCount(0) → SetCount(会话路数) → OpenFiles。
    /// OpenFilesCore 用 _surfaceAt(_sync.Count) 取播放面板，SetCount(0) 之后该处必为 null，
    /// 会直接命中"第 1 路没有可用的播放面板"分支并 return，
    /// 导致 onAllOpened 回调不执行——不 Seek、不恢复循环区间、不播放（表现为加载会话后一片空白）。
    /// 因此重建网格必须发生在打开之前，且要在清空之后。</para></summary>
    internal void LoadSessionSnapshot(SessionSnapshot snapshot)
    {
        // 清空后按会话文件重开；全部打开后 Seek 到保存位置并恢复循环区间
        ResetRecoveryState(); // 会话重载等同打开新媒体：重置重建抑制，避免沿用旧会话的累计计数
        foreach (var s in Grid.Surfaces) s.DetachSession();
        _sync.Clear();
        Grid.SetCount(0, _realMode);
        Grid.SetCount(Math.Min(9, snapshot.Items.Count), _realMode);
        // 还原布局（GridLayout: 0=自动, 1=单屏, 2=2x2, 3=3x3）。
        // 快照存了却从不还原的话，单屏或 3x3 的会话重载后会跳回默认布局，"保存会话"只恢复一半。
        // SetGridLayout 只改预设覆盖、单屏与否由 SingleView 控制，两者必须一起设。
        Grid.SingleView = _3FCompare.Core.Display.GridLayout.IsSingleView(snapshot.GridLayout);
        Grid.SetGridLayout(_3FCompare.Core.Display.GridLayout.PresetOf(snapshot.GridLayout));
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

    /// <summary>探针坐标：表面 DIP → 物理后台缓冲像素 → destination 矩形内 → 源视频像素。
    /// 3FCompare M3：旧实现直接用源分辨率等比映射，既漏了 RenderScaling 也漏了 letterbox。</summary>
    private (int X, int Y)? MapPointerToVideoPixel(PlayerSurface surface, IPlayerSession session, Point local)
    {
        var media = session.ReadMediaInfo();
        if (media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0 ||
            surface.Bounds.Width <= 0 || surface.Bounds.Height <= 0) return null;

        // 1) DIP → 物理像素（子 HWND client 尺寸 == Bounds × RenderScaling）
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var physX = local.X * scale;
        var physY = local.Y * scale;
        if (!session.ReadRenderTargetInfo(out var rt) || rt.SwapWidth == 0 || rt.SwapHeight == 0)
        {
            // 无诊断信息（演示模式 ReadRenderTargetInfo 恒返回 false、旧内核同样不支持）：
            // 退回整面等比映射（漏 letterbox，但不越界）。
            return _3FCompare.Core.Display.VideoPixelMap.MapFallback(
                physX, physY,
                surface.Bounds.Width * scale, surface.Bounds.Height * scale,
                media.VideoWidth, media.VideoHeight);
        }

        // 2) 表面 → 后台缓冲（ShowInBounds 使 chain 尺寸 == client 尺寸，直接可用）
        // 3) 落在 destination 之外（letterbox 黑边）由 MapToSource 判为 null
        // 4) destination 内 → 源视频像素
        return _3FCompare.Core.Display.VideoPixelMap.MapToSource(
            physX, physY,
            rt.DestX, rt.DestY, rt.DestWidth, rt.DestHeight,
            media.VideoWidth, media.VideoHeight);
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

    // 侧栏几何（宽度记忆 / 折叠 / 响应式自动折叠 / ShowSidebar）已拆到 MainWindow.Sidebar.cs
    // ——MainWindow.axaml.cs 已近 1800 行，按职责继续向外拆。

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
        // 状态栏同样属于 chrome：全屏时留一条 24px 亮条会破坏画面沉浸感
        // （docs/02 要求「全屏模式隐藏时间轴/工具栏」，状态栏语义同类）
        StatusBarHost.IsVisible = !hideChrome;
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
        // M5: bind magnifier to selected session so it can sample pixels
        Magnifier.AttachSession(session);

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
            var slot = _sync.Slots.ElementAtOrDefault(surface.Index);
            if (slot?.Session is { } session)
            {
                var mapped = MapPointerToVideoPixel(surface, session, local);
                if (mapped is { } p)
                    _probe.UpdatePoint(p.X, p.Y);
                else
                    _probe.UpdatePoint(-1, -1); // outside video content
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

    /// <summary>恢复上次关闭时的窗口几何（可用性 P0-2）。首次运行、值无效或坐标已不在
    /// 任何屏幕内时保留默认。恢复的窗口状态只认 Normal/Maximized——FullScreen 启动
    /// 会让用户莫名全屏，Minimized 无意义，二者都按默认 Normal 处理。</summary>
    private void RestoreWindowGeometry()
    {
        if (_settings.WindowWidth is not > 0 || _settings.WindowHeight is not > 0) return;

        var restoredSize = new Size(_settings.WindowWidth.Value, _settings.WindowHeight.Value);
        Width = restoredSize.Width;
        Height = restoredSize.Height;

        // 预置 _lastNormal：本次若以 Maximized 启动，OnOpened 只在 Normal 时记录几何，
        // Maximized 时不会记录；而 SaveWindowGeometry 又优先取 _lastNormal——
        // 不预置就会在"最大化关闭"时把屏幕尺寸当成用户偏好的窗口尺寸存下来。
        // 关键：这里只依赖"有可恢复的尺寸"，**不能**依赖坐标是否恢复成功，否则
        // 首次运行（无坐标、有尺寸）后再最大化关闭同样会写坏尺寸。
        _lastNormal = (Position, restoredSize);

        if (_settings.WindowX is { } x && _settings.WindowY is { } y)
        {
            var target = new PixelPoint(x, y);
            // 完全越界（显示器拔掉/分辨率变化）→ 跳过坐标恢复，保留默认位置
            if (Screens.All.Any(s => s.Bounds.Contains(target))
                && (Screens.ScreenFromPoint(target) ?? Screens.Primary) is { } screen)
            {
                // 轻微越界（标题栏跑出屏幕）时夹回工作区内，保证可拖动
                var wa = screen.WorkingArea;
                Position = new PixelPoint(
                    Math.Clamp(target.X, wa.X, Math.Max(wa.X, wa.Right - 200)),
                    Math.Clamp(target.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - 100)));
                _lastNormal = (Position, restoredSize);
            }
        }

        if (_settings.WindowState == (int)WindowState.Maximized)
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

    // 3FCompare 修复：嵌入式 UI 消息注入测试 P/Invoke
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalFree(nint hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern void DragAcceptFiles(nint hwnd, bool fAccept);

    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", SetLastError = true)]
    private static extern uint DragQueryFileW(nint hDrop, uint iFile, nint lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint hDrop);

    // 3FCompare 修复：HDROP 结构（用于构造 WM_DROPFILES 所需的结构）
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DROPFILES
    {
        public uint pFiles;
        public POINT pt;
        public bool fNC;
        public bool fWide;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    private const uint WM_DROPFILES = 0x0233;
    private const uint GHND = 0x0042; // GMEM_MOVEABLE | GMEM_ZEROINIT
    private const uint GMEM_ZEROINIT = 0x0040;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_HDROP = 15;

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
            // 3FCompare M2: 移除最大化/还原的 Pause→500ms→Play hack。
            // 内核(0003 单所有者 resize + 0008 0×0 保护 + 0011 Present(0,0) 解楔 + K1
            // presenter 尺寸同步)已结构性支持渲染中尺寸转换，暂停反而制造
            // "暂停/pacing 时不 resize" 死区。
            // 子 HWND 尺寸由 PlayerSurface.WM_SIZE → Redraw 自然驱动，无需手动触发。
            if (newState == WindowState.Normal)
                _lastNormal = (Position, Bounds.Size);
        }
        else if (change.Property == BoundsProperty)
        {
            // 响应式：窗口变窄时自动收起侧栏（在用户手动操作过之前生效）
            AutoCollapseSidebar();
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
                    ResetRecoveryState(); // 成功 → 重置计数与放弃标志
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
        // 可用性 P0-2：保存窗口位置/尺寸/状态（唯一的持久化点）。
        SaveWindowGeometry();

        _pollTimer.Stop();
        // scrub 缩略图定时器只在 OnClosed 停是不够的：OnClosing 之后到窗口真正销毁之间
        // 若还有 tick，会对已销毁的 HWND 做 BitBlt。关闭路径上一并停掉。
        _scrubTimer.Stop();
        DestroyAllSessions();
        _3FCompare.Core.Diagnostics.AppLog.Shutdown();
        base.OnClosing(e);
    }

    /// <summary>销毁全部播放器会话（含 _coordinator.Close() 与 _sync.Clear()）。
    /// 内核工作线程持有托管事件回调的函数指针：会话不 Destroy 就退出进程，这些线程会在
    /// CLR 停机后继续反向 P/Invoke，触发 coreclr/vm/ceemain.cpp:1750 断言
    /// （"Attempt to execute managed code after the .NET runtime thread state has been
    /// destroyed."）并使进程以 127 退出。此前全代码库无任何 Dispose 会话的调用点。</summary>
    private void DestroyAllSessions()
    {
        try
        {
            foreach (var slot in _sync.Slots)
            {
                try { (slot.Session as IDisposable)?.Dispose(); } catch { }
            }
        }
        catch { }
        try { _coordinator.Close(); } catch { }
        try { _sync.Clear(); } catch { }
    }

    /// <summary>保存窗口几何（可用性 P0-2）。
    /// 最小化时 Position/Width/Height 无意义（Avalonia 报告的是还原前的遗留值），跳过；
    /// Maximized 仍记录 Normal 几何，恢复时先按 Normal 摆位再最大化。
    /// 记录坐标完全落在所有屏幕之外（显示器拔掉/分辨率变化）时只存状态不存坐标。</summary>
    private void SaveWindowGeometry()
    {
        if (_selfTestMode) return; // 自测模式：不把测试窗口的几何写进用户配置
        var state = WindowState;
        if (state == WindowState.Minimized)
        {
            // 只更新状态位，几何沿用上次的值
            _settings.WindowState = (int)state;
        }
        else
        {
            // 最大化时 Position/Bounds 是最大化后的值，用 _lastNormal 记录的 Normal 几何
            var normal = state == WindowState.Maximized
                ? _lastNormal ?? (Position, Bounds.Size)
                : (Position, Bounds.Size);
            if (normal.Item2.Width >= 1 && normal.Item2.Height >= 1)
            {
                _settings.WindowWidth = (int)normal.Item2.Width;
                _settings.WindowHeight = (int)normal.Item2.Height;
            }
            // 多显示器越界校验：坐标不在任何屏幕工作区内则不覆盖上次的有效坐标
            var pos = normal.Item1;
            if (Screens.All.Any(s => s.Bounds.Contains(pos)))
            {
                _settings.WindowX = pos.X;
                _settings.WindowY = pos.Y;
            }
            _settings.WindowState = (int)state;
        }

        SettingsStore.Save(_settings);
    }

    /// <summary>可用性 P1-5：关闭后停止存活的 DispatcherTimer。
    /// 16ms 轮询与 150ms 拖拽缩略图定时器都持有回调，窗口关闭后继续 Tick 会让
    /// 进程在 AppLog.Shutdown() 之后仍写日志、并延长退出延迟。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        _scrubTimer.Stop();
        // P0-5 修复：缩略图预览是独立的顶层 Window，Hide() 只隐藏不销毁。
        // 不显式 Close 会在关窗后残留一个永不回收的顶层窗口
        // （并让 Avalonia 因仍有存活 Window 而不退出消息循环）。
        _thumbnail?.CloseAndDispose();
        _thumbnail = null;
        base.OnClosed(e);
    }

    // ══════════ 自动化 selftest（走真实打开管线；WinForms RunSelfTest 断言移植） ══════════

    private static volatile string _step = "启动";

    private static void Log(string msg)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} selftest[{_step}]: {msg}");
        Console.Out.Flush();
    }

    /// <summary>构造 HDROP 内存结构（GMEM_MOVEABLE + ZEROINIT），供 WM_DROPFILES 注入测试用。
    /// 布局：DROPFILES 头（pFiles=偏移, fWide=TRUE）+ UTF-16 文件路径 + 双 \\0 结尾。
    /// 调用方用完后必须 GlobalFree(hDrop)（或由 DragFinish 释放——测试里显式 GlobalFree）。</summary>
    private static nint BuildHDrop(string filePath)
    {
        var structSize = Marshal.SizeOf<DROPFILES>();
        var pathBytes = System.Text.Encoding.Unicode.GetBytes(filePath + "\0\0");
        var total = (nuint)(structSize + pathBytes.Length);
        var hMem = GlobalAlloc(GHND, total);
        if (hMem == nint.Zero) return nint.Zero;
        var ptr = GlobalLock(hMem);
        if (ptr == nint.Zero) { GlobalFree(hMem); return nint.Zero; }
        try
        {
            Marshal.WriteInt32(ptr, Marshal.OffsetOf<DROPFILES>("pFiles").ToInt32(), structSize);
            Marshal.WriteByte(ptr, Marshal.OffsetOf<DROPFILES>("fWide").ToInt32(), 1);
            var dst = nint.Add(ptr, structSize);
            Marshal.Copy(pathBytes, 0, dst, pathBytes.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }
        return hMem;
    }

}
