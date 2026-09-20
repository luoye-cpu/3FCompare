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
using _3FCompare.Platform;
using _3FCompare.Services;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Imaging;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;
using _3FCompare.Diagnostics;

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

    /// <summary>被节流丢弃的变换的一次性补发定时器（UI 线程）。
    /// 见 ScheduleTransformFlush 的注释：16ms 节流会**直接丢弃**更新，滚轮事件停止后
    /// 不会再有调用 ⇒ 最后一次缩放永久丢失且不自愈。</summary>
    private readonly DispatcherTimer _transformFlushTimer;
    /// <summary>最近一次真正下发到内核的变换值（自测断言用）。
    /// 只由 SendViewTransform 写入，用于校验"UI 状态与内核实际状态一致"。</summary>
    internal float LastSentZoom = 1f;
    internal float LastSentPanX, LastSentPanY;
    /// <summary>被节流丢弃的 ApplyViewTransform 次数（自测断言用：确认用例没有空转）。</summary>
    internal int TransformDroppedCount;

    // M3：侧栏与面板
    private readonly ToolsSidebar _sidebar;
    private readonly ProbePanel _probe;
    private readonly BookmarkPanel _bookmarks;
    private readonly OffsetPanel _offsetPanel;
    private readonly MediaInfoPanel _mediaPanel;
    private readonly AudioPanel _audioPanel;

    // ── 多路对比布局覆盖层（阶段 3.1 接线，见 docs/26）──
    /// <summary>分割线 + 可拖动手柄的覆盖窗口。首次进入对比模式时创建，退出只 Hide（可复用）。</summary>
    private LayoutOverlayWindow? _layoutOverlay;
    /// <summary>当前对比模式；路数变化时经 <see cref="CompareLayout.CoerceMode"/> 收敛。</summary>
    private CompareMode _compareMode = CompareMode.Ab;
    /// <summary>当前分割参数；由覆盖层 <c>SplitChanged</c> 回灌。</summary>
    private SplitParams _compareSplit = SplitParams.Default(CompareMode.Ab);
    /// <summary>对比模式是否已进入（未进入时不驱动覆盖层）。</summary>
    private bool _compareActive;

    /// <summary>上一次观察到的叠加模式状态。仅用于在组件日志里把"叠加开/关"识别成一次状态跃迁
    /// （叠加的进入/退出实现在 MainWindow.CompareOverlay.cs，本次改动不碰它，
    /// 但两条路径都会经过 <see cref="ApplyCompareLayout"/>，故在那里比对）。</summary>
    private bool _lastOverlayActive;

    /// <summary>上一次记入组件日志的路数（-1 = 尚未记录）。
    /// <see cref="UpdateStatus"/> 是全部路数变更路径的共同汇聚点，但调用极频繁，
    /// 故只在路数真的变化时记一行。</summary>
    private int _loggedRouteCount = -1;

    /// <summary>注入钩子提醒是否已记入组件日志（每次运行只记一次，见 <see cref="UpdateStatusInfo"/>）。
    /// <para>提醒文案本身在多路期间<b>持续</b>显示（钩子在场是持续条件，一闪而过的提示等于没提示），
    /// 但"提醒过了"这件事只记一次日志，不随每次状态刷新重复落盘。</para></summary>
    private bool _hookNoticeLogged;

    // ── 悬浮传输栏（docs/31 阶段 4.3）──
    /// <summary>承载传输栏的 owned 顶层窗。开启悬浮模式时创建，关闭时销毁并交还控件。</summary>
    private FloatingTransportWindow? _floatingTransport;
    /// <summary>光标热区轮询。视频由子 HWND 渲染，Avalonia 收不到它上面的指针事件（airspace），
    /// 故用光标位置轮询统一覆盖「视频 / Avalonia 控件」两类区域，避免漏判。</summary>
    private readonly DispatcherTimer _transportHoverTimer;
    /// <summary>离开热区后的延迟隐藏定时器（避免沿底边移动时抖动）。</summary>
    private readonly DispatcherTimer _transportHideTimer;

    /// <summary>指针进入热区与浮条浮现之间的宽限（DIP）。</summary>
    private const double TransportHotZoneGrace = 14.0;
    /// <summary>离开热区后延迟隐藏的时间（ms）。</summary>
    private const int TransportHideDelayMs = 800;
    /// <summary>热区轮询周期（ms）。100ms 足够跟手，开销可忽略。</summary>
    private const int TransportHoverPollMs = 100;

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
            cmdArgs[1] is "--selftest" or "--screentest" or "--multitest" or "--sessiontest"
                       or "--comparemodetest" or "--magnifybench")
            _selfTestMode = true;

        // 可用性 P0-2：窗口位置/尺寸/状态的恢复统一在 RestoreWindowGeometry() 完成
        // （构造函数末尾调用——那里 Screens 已可用，且是唯一的恢复入口，避免双处恢复互相覆盖）。

        // 可用性 P0：FFmpeg 缺失时一次性引导——说明降级原因并提供"打开设置"入口。
        // 收敛为**单一弹窗**：由 OnOpened → MaybeExitDemoMode 统一负责（见该方法注释）。
        // 这里原先还挂了一个 Opened 处理器，但它与 MaybeExitDemoMode 的判定条件完全等价
        // （两者都是 !IsNativeAvailable() == !_realMode），却用了另一套文案与按钮
        // （"稍后再说" vs "关闭"），结果用户要连续应答两次、且两个模态框语义互相矛盾。
        // 更糟的是它**没有自测模式守卫**，在缺 FFmpeg 的机器上跑 --selftest/--sessiontest
        // 会弹一个无人应答的模态框 ⇒ 永久挂起（只有 --selftest 有 40s 看门狗兜底）。

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
        _sidebar.ModeChanged += OnSidebarModeChanged;
        SidebarHost.Content = _sidebar;

        // 恢复上次会话的侧栏几何（展开宽度 + 三态），实现见 MainWindow.Sidebar.cs
        RestoreSidebarGeometry();
        // 拖拽回写（D2）：不接这个事件，拖完的宽度既不入内存也不落盘，
        // 自动折叠展开一次就跳回旧宽度。
        WireSidebarSplitter();
        // 悬浮传输栏的两个定时器必须早于 ApplyBottomBarVisibility 建好：后者会据设置决定是否启动轮询。
        _transportHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TransportHoverPollMs) };
        _transportHoverTimer.Tick += (_, _) => OnTransportHoverTick();
        _transportHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TransportHideDelayMs) };
        _transportHideTimer.Tick += (_, _) => { _transportHideTimer.Stop(); HideFloatingTransport(); };
        // 失焦即收（含最小化）：浮条是"光标在附近才出现"的东西，不该在切走程序后仍留在屏幕上。
        // 例外是光标正停在浮条上 —— ComboBox 下拉是独立 Popup，展开时主窗可能被判为失焦，
        // 此时若收掉浮条会把下拉一起弄没（判定见 IsCursorOverFloatingBar）。
        Deactivated += (_, _) => { if (!IsCursorOverFloatingBar()) HideFloatingTransport(); };
        // 恢复底部栏（时间轴 / 状态栏 / 悬浮传输栏）的偏好（docs/31 阶段 4），实现见 ApplyBottomBarVisibility
        ApplyBottomBarVisibility();
        Grid.SelectionChanged += (_, _) => UpdatePanelsForSelection();
        UpdatePanelsForSelection();

        // 探针/放大镜：隧道指针移动定位命中表面
        CenterPanel.AddHandler(InputElement.PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);

        AbSlider.SliderChanged += _ => { /* 视觉滑块（WinForms 同语义） */ };

        // 轮询：16ms 播放中 / 250ms 空闲（WinForms PollSnapshots 移植）
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pollTimer.Tick += (_, _) => PollSnapshots();
        _pollTimer.Start();

        // 节流补发：默认 Stop，只在丢弃更新时 Start 一次（见 ApplyViewTransform）。
        _transformFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _transformFlushTimer.Tick += (_, _) => FlushPendingViewTransform();

        RestoreWindowGeometry();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true); // 启用窗口拖放

        // 对比模式的 Win32 裁剪接线（新逻辑全部在 MainWindow.CompareCrop.cs，此处只挂时机）
        WireCompareCrop();

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
        // --comparemodetest <video> [routes=3]：多路对比覆盖层接线验证（docs/26 阶段 3.1）
        else if (args.Length >= 3 && args[1] == "--comparemodetest")
            _ = RunCompareModeTestAsync(args[2],
                args.Length >= 4 && int.TryParse(args[3], out var cr) ? cr : 3);
        // --magnifybench <video> <routes> <zoom> [secondsPerPhase=8] [budgetMpx=0(默认闸门)]：
        // 无缝放大的稳态显存/吞吐实测（显存由外部脚本按 PID 采样，见 .review_pr/gpu_probe.ps1）
        else if (args.Length >= 5 && args[1] == "--magnifybench")
            _ = RunMagnifyBenchAsync(args[2],
                int.TryParse(args[3], out var br) ? br : 4,
                double.TryParse(args[4], out var bz) ? bz : 2.0,
                args.Length >= 6 && int.TryParse(args[5], out var bs) ? bs : 8,
                args.Length >= 7 && double.TryParse(args[6], out var bb) ? bb : 0);
    }

    /// <summary>async void 事件处理器的统一异常边界。
    /// <para><b>为什么必须要有</b>：<c>async void</c> 抛出的异常没有 Task 承载，会直接冒泡到
    /// <c>Dispatcher.UIThread.UnhandledException</c>；而 App.axaml.cs 里刻意<b>不置</b>
    /// <c>e.Handled</c>（保留既有崩溃语义）⇒ 进程照常闪退。典型受害者是「保存会话」：
    /// 路径非法/只读/被占用时 <c>SessionSnapshot.SaveToFile</c> 抛
    /// <c>UnauthorizedAccessException</c>，用户未保存的会话随之全部丢失。</para>
    /// <para>这里统一做两件事：落盘日志 + 给用户一条可见提示；成功路径完全不变。</para></summary>
    /// <param name="module">日志模块名（便于按菜单项检索）。</param>
    /// <param name="userMessage">面向用户的失败说明（已本地化或走 <see cref="Loc"/>）。</param>
    private async System.Threading.Tasks.Task ReportErrorAsync(string module, string userMessage, Exception ex)
    {
        _3FCompare.Core.Diagnostics.AppLog.Error(module, ex);
        try
        {
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_AppName"),
                // TODO: 补本地化键（Msg_OperationFailed）——语言表现在没有"操作失败"条目，
                // 且 Localization/ 不在本次改动范围内，先用 Loc 保持中英并列。
                $"{userMessage}\n\n{Loc("错误：", "Error: ")}{ex.Message}",
                LanguageManager.T("Settings_Ok"));
        }
        catch
        {
            // 提示本身也失败（窗口已关闭 / 正处于关机路径）：日志已落盘，绝不再抛
        }
    }

    /// <summary>临时双语文案助手：语言表里缺键、且本地化表不在本次改动范围内时使用。
    /// 与既有硬编码文案（如"打开设置 / Open Settings"）保持同一风格。</summary>
    private static string Loc(string zh, string en) => LanguageManager.IsEnglish ? en : zh;

    /// <summary>--autodemo：窗口显示后自动打开并播放。</summary>
    public void AutoOpenFiles(string[] files)
    {
        Opened += (_, _) =>
        {
            Grid.SetCount(Math.Max(1, Math.Min(9, files.Length)), _realMode);
            // 组件日志：会话打开请求（--autodemo 路径）
            ComponentLog.Log(Comp.Engine, "SessionOpen", -1,
                $"src=autodemo files={files.Length} autoPlay=True");
            _coordinator.OpenFiles(files, autoPlay: true);
        };
    }

    /// <summary>--screentest：打开→就绪+500ms 渲染→抓表面 0→存 PNG（>1000B 判过）。</summary>
    private async void OnOpenVideos(object? sender, RoutedEventArgs e)
    {
        // D1：async void 必须有异常边界，否则 StorageProvider 抛出时直接闪退
        try { await OpenViaPickerAsync(); }
        catch (Exception ex)
        {
            await ReportErrorAsync("Menu.Open",
                Loc("打开视频失败。", "Failed to open videos."), ex);
        }
    }

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
        // D12：超上限必须给可见反馈。PlaybackCoordinator.OpenFilesCore 里 `count <= 0`
        // 是**静默** return（不修改该文件，故在此侧提示），用户拖入文件后界面毫无变化，
        // 表现为"拖了没反应"。这里在 UI 侧补一条状态栏提示并落盘日志。
        var room = 9 - _sync.Count;
        if (room <= 0)
        {
            NotifyRouteLimit(0, paths.Count);
            return;
        }
        if (paths.Count > room)
            NotifyRouteLimit(room, paths.Count); // 只开前 room 个，其余静默丢弃 ⇒ 也要说清楚

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
        // 组件日志：会话打开请求（拖放/选择文件路径）。记请求路数与既有路数——
        // "打开进行中"与"呈现线程停滞"若在时间上相邻，这条就是关键锚点。
        ComponentLog.Log(Comp.Engine, "SessionOpen", -1,
            $"src=paths files={paths.Count} existing={_sync.Count} autoPlay=False");
        _coordinator.OpenFiles(paths, autoPlay: false);
        UpdateStatus();
    }

    /// <summary>路数超上限的可见提示（D12）。
    /// <para>只写状态栏 + 日志，不弹模态框：本方法也会从子 HWND 的 WM_DROPFILES 转发
    /// （<see cref="OnSurfaceFilesDropped"/>）里被调用，那是在 WndProc 调用栈上，
    /// 弹模态框会重入消息循环，风险远大于收益；状态栏同样是用户可见的反馈。</para></summary>
    private void NotifyRouteLimit(int accepted, int requested)
    {
        var msg = accepted <= 0
            ? Loc($"已达 9 路上限，本次 {requested} 个文件未能打开（请先移除一路再试）。",
                  $"Limit of 9 lanes reached; {requested} file(s) were not opened (remove a lane first).")
            : Loc($"已达 9 路上限：本次 {requested} 个文件只打开了前 {accepted} 个。",
                  $"Limit of 9 lanes reached: only the first {accepted} of {requested} file(s) were opened.");
        StatusInfo.Text = msg;
        _3FCompare.Core.Diagnostics.AppLog.Warn("Open", msg);
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
        // 停滞判定相关的三个状态也必须一并复位，否则上一次的"轻量恢复"痕迹会跨媒体残留：
        // _stalledAfterLightRecovery 一旦为 true，打开新媒体后的**第一次**真实停滞就会被
        // 跳级成完整会话重建（丢偏移与播放位置），而它本应先走 Pause→Play 轻量路径
        // （docs/14 §3.2）。
        // 看门狗内部同时持有"上次 presented"与"是否已试过轻量恢复"，
        // 复位语义已收敛到 Core 的 RenderStallWatchdog.Reset()
        _stallWatch.Reset();
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

    // ══════════ 多路对比：布局覆盖层（阶段 3.1 接线） ══════════
    //
    // anchor 取 Grid（CompareGridView）：它就是承载 1~9 路 PlayerSurface 的对比区容器，
    // 也是 LayoutOverlayWindow 文档里点名的默认 anchor。CenterPanel 虽包住它，但还叠放了
    // AbSlider / Magnifier，覆盖矩形会包含这些非对比区，故不选。

    /// <summary>进入对比模式：按当前路数收敛模式后在对比区之上显示分割线覆盖层。
    /// <para>最小入口，暂未接菜单/快捷键；将来从"视图"菜单调用即可。</para></summary>
    internal void EnterCompareMode(CompareMode mode)
    {
        // 叠加模式是 AB 的呈现变体，任何"切模式"的调用（含 C 键循环）都必须先退出它 ——
        // 否则"两路铺满"的格表会与新模式的分格打架。见 MainWindow.CompareOverlay.cs。
        ExitCompareOverlay();

        var count = _sync.Count;
        if (CompareLayout.AvailableModes(count).Count == 0) return; // <2 路：对比功能未启动

        _compareMode = CompareLayout.CoerceMode(mode, count);
        _compareSplit = SplitParams.Default(_compareMode);
        _compareActive = true;

        // 组件日志：对比模式进入（含收敛后的实际模式与当时路数）。
        // 紧随其后的 VerifyOverlayDegradation 可能立刻降级退出 —— 那时日志里会看到
        // Enter 紧跟 Exit，这正是"覆盖层 fail-fast"的时序证据。
        ComponentLog.Log(Comp.Overlay, "CompareModeEnter", -1, $"mode={_compareMode} routes={count}");

        if (_layoutOverlay is null)
        {
            _layoutOverlay = new LayoutOverlayWindow();
            _layoutOverlay.SplitChanged += OnCompareSplitChanged;
        }
        _layoutOverlay.ShowOverlay(this, Grid, _compareMode, _compareSplit);

        // 覆盖层只画线；真正的画面重排由 Grid 的格表覆盖驱动（见 ApplyCompareLayout）。
        // 放在 VerifyOverlayDegradation 之前：后者若 fail-fast 退出，会由 ExitCompareMode 置回 null。
        ApplyCompareLayout();

        // 裁剪区域依赖格表与实际排布（Bounds），此刻布局尚未跑 ⇒ 延后一拍下发（合并去重）。
        // 若紧接着被 VerifyOverlayDegradation 降级退出，那一拍会因 _compareActive=false 而走清除路径。
        ScheduleCompareCrop();

        // B3：ShowOverlay 之后必须立刻消费覆盖层上报的两个诊断量，失败即降级（见方法注释）。
        VerifyOverlayDegradation();
    }

    /// <summary>把「对比模式 + 分割参数」换算成归一化格表交给 <see cref="CompareGridView"/>，
    /// 让分割线拖动真正改变画面排版（此前只有覆盖层的线条在动，视频格仍是均匀网格）。
    ///
    /// <para>未激活对比模式时传 <c>null</c> ⇒ Grid 回到既有均匀 N×M 逻辑，
    /// 普通网格浏览路径行为完全不变（本次改动的兼容性前提）。</para>
    ///
    /// <para>格数由 <see cref="CompareLayout.CellCount"/> 决定（AB=2/ABC=3/ABCD=4），
    /// 可以小于实际路数：Grid 只显示前 N 路并隐藏其余，避免"选了模式却看到不匹配的格数"。</para>
    ///
    /// <para><b>叠加模式（docs/31 阶段 2）</b>：它是 <see cref="CompareMode.Ab"/> 的呈现变体 ——
    /// 两路都铺满整个对比区，谁露出来由 Z 序 + 窗口区域决定（见 <c>MainWindow.CompareOverlay.cs</c>）。
    /// 故叠加激活时格表固定为"两路铺满"，而不是 AB 的左右分栏。</para></summary>
    private void ApplyCompareLayout()
    {
        var overlay = CompareOverlayActive;
        Grid.CellOverride = !_compareActive
            ? null
            : overlay
                ? CompareOverlayLayout()
                : CompareLayout.ComputeCells(_compareMode, _compareSplit);

        // 组件日志：格表重排是"结构变化"（不是逐帧），值得逐次留痕。
        // 叠加开/关单独记一条跃迁事件——它把窗口区域的语义从"裁出格"换成"互补半区"，
        // 是裁剪/呈现相关崩溃的高嫌疑前置事件，混在通用 LayoutApplied 里不易检索。
        if (ComponentLog.IsEnabled)
        {
            if (overlay != _lastOverlayActive)
                ComponentLog.Log(Comp.Overlay, overlay ? "OverlayOn" : "OverlayOff", -1,
                    $"mode={_compareMode}");
            _lastOverlayActive = overlay;
            ComponentLog.Log(Comp.Overlay, "LayoutApplied", -1,
                $"active={_compareActive} overlay={overlay} mode={_compareMode} " +
                $"split={_compareSplit.X:0.###},{_compareSplit.Y:0.###}");
        }
    }

    /// <summary>B3：覆盖层的两个诊断量必须有人消费，否则"接线"等于埋雷 ——
    /// 这两种失效都只在真机上出现，且都让覆盖层从"装饰"变成"故障"：
    /// <list type="bullet">
    /// <item><description><see cref="LayoutOverlayWindow.HitTestHookInstalled"/> 为 false：
    /// <c>WM_NCHITTEST</c> 未被拦截 ⇒ 铺满对比区的覆盖窗口吞掉全部鼠标消息，
    /// 选中 / 滚轮缩放 / 拖动平移全部失效。</description></item>
    /// <item><description><see cref="LayoutOverlayWindow.AchievedTransparency"/> 不是
    /// <see cref="WindowTransparencyLevel.Transparent"/>：窗口以不透明底呈现，整块盖住视频。</description></item>
    /// </list>
    ///
    /// <para><b>取舍：为什么是"失败即退出对比模式"，而不是用 WindowRegionClipper 把窗口裁到
    /// 只剩手柄矩形</b>——① 覆盖层只提供分割线 + 手柄，是纯装饰加一个可选的拖动入口；
    /// 丢掉它的代价只是"不能用鼠标拖分割线"，而两种失效的代价分别是"对比区不能选中/缩放/平移"
    /// 与"看不见画面"，量级完全不同，不值得为保住装饰而冒风险。② 区域裁剪依赖
    /// <c>SetWindowRgn</c> 与内核 D3D11 flip-model swapchain 的交互，<c>WindowRegionClipper</c>
    /// 自己的注释已写明"官方文档未明确、本项目尚未实测"——那等于拿一个**已知**失效去换一个
    /// **未知**失效。③ 若走区域裁剪，还得在每次分割参数 / 几何变化时重算手柄矩形并重新下发，
    /// 把一处断言变成一条新的热路径。故选择 fail-fast；等真机验证过裁剪行为后，再考虑把它
    /// 升级成"保留手柄"的降级方案。</para></summary>
    private void VerifyOverlayDegradation()
    {
        if (_layoutOverlay is null) return;

        var hookOk = _layoutOverlay.HitTestHookInstalled;
        var transparencyOk = _layoutOverlay.AchievedTransparency == WindowTransparencyLevel.Transparent;
        if (hookOk && transparencyOk) return;

        var reason = !hookOk
            ? Loc("覆盖层命中测试钩子未安装，会吞掉对比区全部鼠标消息",
                  "overlay hit-test hook was not installed; it would swallow all mouse input over the compare area")
            : Loc($"覆盖层未取得逐像素透明（实际 {_layoutOverlay.AchievedTransparency}），会遮挡画面",
                  $"overlay did not get per-pixel transparency (actual {_layoutOverlay.AchievedTransparency}); it would cover the video");
        var msg = Loc($"已退出对比模式：{reason}。", $"Compare mode disabled: {reason}.");

        _3FCompare.Core.Diagnostics.AppLog.Warn("CompareOverlay", msg);
        ExitCompareMode();
        // 直接写状态栏：UpdateStatus 会整体重算并覆盖它，但下一次状态刷新本就该回到常规文案，
        // 此处只需保证用户当下看得到原因（同 NotifyRouteLimit 的处理方式）。
        StatusInfo.Text = msg;
    }

    /// <summary>退出对比模式（只隐藏，实例保留以便复用；CloseAndDispose 见 OnClosed）。</summary>
    internal void ExitCompareMode()
    {
        ComponentLog.Log(Comp.Overlay, "CompareModeExit", -1,
            $"mode={_compareMode} overlay={CompareOverlayActive}");
        _compareActive = false;
        // 叠加模式必须一并退出：它的两路"铺满"格表与 B 的窗口区域都要撤掉。
        // 先置 _compareActive=false 再调，ExitCompareOverlay 就只做"清状态 + 同步清区域"，不会重复排版。
        ExitCompareOverlay();
        // 退出即恢复均匀网格：CellOverride 置 null 后 Grid 走既有逻辑（含恢复被隐藏的路）。
        ApplyCompareLayout();
        // 无缝放大（子窗口放大 + 裁剪）必须一并复位：否则子窗口会保持放大尺寸，而格表已撤掉，
        // 放大窗口会盖住相邻路。默认关闭（z=1）时本调用是无副作用的。见 MainWindow.CompareCrop.cs。
        ResetCompareMagnify();
        // 必须**同步**清除裁剪：延后一拍的话这一拍内窗口仍被裁着，而任何异常路径（含 OnClosing）
        // 都不会再来补一次 ⇒ 窗口被永久裁剪。见 MainWindow.CompareCrop.cs。
        ClearCompareCrop();
        // B6：必须走 HideOverlay 而不是 Hide —— 后者会走基类 Window.Hide，不解除对本窗口的
        // 跟随订阅（见 LayoutOverlayWindow.HideOverlay 的注释）。
        _layoutOverlay?.HideOverlay();
    }

    /// <summary>覆盖层拖动回调：回灌分割参数（覆盖层已做乐观本地更新，这里只同步宿主状态）。
    /// <para>这是"拖动真正改变画面"的关键一环：回灌 ⇒ 重算格表 ⇒ Grid 重排 ⇒ 视频格跟手。
    /// 覆盖层线条本就由它自己的本地乐观值绘制，两者共用同一份 <see cref="CompareLayout.ComputeCells"/>
    /// 结果，不会漂移。</para></summary>
    private void OnCompareSplitChanged(SplitParams split)
    {
        _compareSplit = split;
        // 组件日志：分割变化（拖动中可能连续触发）。它是裁剪重算的直接诱因，
        // 必须与随后的 RegionApply/RegionClear 成对出现才能还原时序。
        ComponentLog.Log(Comp.Overlay, "SplitChanged", -1, $"x={split.X:0.###} y={split.Y:0.###}");
        _layoutOverlay?.Update(_compareMode, _compareSplit);
        ApplyCompareLayout();
        // 分割参数变了 ⇒ 格表变了 ⇒ 裁剪矩形要重算（同样等布局落地后再下发）
        ScheduleCompareCrop();
    }

    /// <summary>路数变化时自动收敛对比模式（<see cref="CompareLayout.CoerceMode"/>）；
    /// 路数掉到 2 以下（可用模式集为空）、或对比区本身不再存在时直接退出对比模式。
    /// <para>幂等：模式未变时不做任何事，可安全地每次 UpdateStatus 都调一次。</para></summary>
    private void SyncCompareModeToRouteCount()
    {
        if (!_compareActive || _layoutOverlay is null) return;

        // U1：路数不是唯一条件。单屏模式（Grid.SingleView）或 Grid 被隐藏时，对比区根本不呈现，
        // 覆盖层却仍按"多格分割"算几何 ⇒ 分割线画在别的控件之上、且与实际布局不符。
        // 这一条必须先于路数判断：Grid 是否可用与路数无关。
        if (Grid.SingleView || !Grid.IsVisible)
        {
            ExitCompareMode();
            return;
        }

        var count = _sync.Count;
        if (CompareLayout.AvailableModes(count).Count == 0)
        {
            ExitCompareMode();
            return;
        }

        var coerced = CompareLayout.CoerceMode(_compareMode, count);
        if (coerced == _compareMode) return;
        _compareMode = coerced;
        _compareSplit = _compareSplit.Clamp();
        _layoutOverlay.Update(_compareMode, _compareSplit);
        // 模式收敛后格数与形状都变了（例如 4 路 ABCD 掉到 3 路 → ABC），必须同步重排画面。
        ApplyCompareLayout();
    }

    /// <summary>对比模式入口（快捷键 C）：单键循环「进入 → 逐级切换可用模式 → 退出」。
    /// <list type="bullet">
    /// <item>未进入 → 进入最小可用模式（2/3/4+ 路均为 AB）；</item>
    /// <item>已进入 → 切到可用集合中的下一个模式；</item>
    /// <item>已是最大可用模式 → 退出。</item>
    /// </list>
    ///
    /// <para><b>为什么单键循环而不是三个按钮</b>：可用集合与"按路数自动收敛"完全由
    /// <see cref="CompareLayout.AvailableModes"/> 决定（2 路→{AB}，3 路→{AB,ABC}，
    /// ≥4 路→{AB,ABC,ABCD}），循环天然只走合法模式，无需在 UI 侧重复一份规则。
    /// 路由到 <see cref="EnterCompareMode"/> 后，后者内部的
    /// <see cref="CompareLayout.CoerceMode"/> 还会再收敛一次（双保险）。</para>
    ///
    /// <para><b>进入失败不覆盖状态栏</b>：B3 的 fail-fast（<see cref="VerifyOverlayDegradation"/>）
    /// 会在钩子/透明任一不满足时自动 <see cref="ExitCompareMode"/> 并写下原因；此处仅在
    /// <c>_compareActive</c> 仍为 true 时才提示模式名，避免把失败原因冲掉。</para></summary>
    private void CycleCompareMode()
    {
        var modes = CompareLayout.AvailableModes(_sync.Count);
        if (modes.Count == 0)
        {
            // 0/1 路：对比功能未启动，给出可见反馈（不静默）。
            StatusInfo.Text = Loc("对比模式需要至少 2 路视频。", "Compare mode requires at least 2 routes.");
            return;
        }

        if (!_compareActive)
        {
            EnterCompareMode(modes[0]);
            if (_compareActive) NotifyCompareMode();
            return;
        }

        var idx = modes.IndexOf(_compareMode);
        if (idx >= 0 && idx < modes.Count - 1)
        {
            EnterCompareMode(modes[idx + 1]);
            if (_compareActive) NotifyCompareMode();
            return;
        }

        ExitCompareMode();
        NotifyCompareMode();
    }

    /// <summary>在状态栏提示当前对比模式（或已退出）。文案走 <see cref="Loc"/>：
    /// 语言表不在本次改动范围，与既有硬编码双语文案同一风格。</summary>
    private void NotifyCompareMode()
    {
        if (!_compareActive)
        {
            StatusInfo.Text = Loc("已退出对比模式。", "Compare mode off.");
            return;
        }

        var name = _compareMode switch
        {
            CompareMode.Abc => "ABC",
            CompareMode.Abcd => "ABCD",
            _ => "AB",
        };
        // 格数 < 路数时（如 4 路用 AB 只看 2 路）显式告知"只显示前 N 路"，
        // 否则用户会以为剩下几路"消失了"。这是选择"只显示前 N 路"策略的必要配套提示。
        var cells = CompareLayout.CellCount(_compareMode);
        var scope = _sync.Count > cells
            ? Loc($"，显示前 {cells} / 共 {_sync.Count} 路", $" (showing first {cells} of {_sync.Count} routes)")
            : "";
        StatusInfo.Text = Loc($"对比模式：{name}{scope}（按 C 切换 / 退出）",
                              $"Compare mode: {name}{scope} (press C to cycle / exit)");
    }

    // ══════════ 模式家族入口（docs/31 阶段 3）：叠加 / 分屏 / 网格 ══════════
    //
    // 三个入口共用同一套"以真实状态为准"的勾选刷新（RefreshCompareModeChecks），
    // 菜单（视图 → 对比模式）与快捷键（S / V / G）走的是同一条路径，不会出现两套语义。

    /// <summary>分屏模式入口（菜单「对比模式 → 分屏」/ 快捷键 <c>V</c>）。
    ///
    /// <para><b>按路数收敛</b>：目标模式一律取 <see cref="CompareLayout.CoerceMode"/> 对
    /// <see cref="CompareMode.Abcd"/> 的收敛结果 —— 2 路→AB、3 路→ABC、≥4 路→ABCD。
    /// 于是 5~9 路点"分屏"会进入 ABCD 并<b>只显示前 4 路</b>（其余由
    /// <c>CompareGridView.ArrangeOverride</c> 隐藏），这与 <c>NotifyCompareMode</c> 里
    /// "显示前 N / 共 M 路"的提示一致。</para>
    ///
    /// <para><b>已是目标模式时不重进</b>：<see cref="EnterCompareMode"/> 会把分割参数重置为
    /// <see cref="SplitParams.Default"/>，用户已拖好的分割位置不该因为再点一次"分屏"被抹掉。
    /// 叠加是 AB 的呈现变体，此时只退出叠加（同一份分割参数被保留），不重进对比模式。</para></summary>
    private void EnterSplitMode()
    {
        if (CompareLayout.AvailableModes(_sync.Count).Count == 0)
        {
            StatusInfo.Text = Loc("对比模式需要至少 2 路视频。", "Compare mode requires at least 2 routes.");
            return;
        }

        var target = CompareLayout.CoerceMode(CompareMode.Abcd, _sync.Count);
        if (_compareActive && _compareMode == target)
        {
            if (_compareOverlayActive)
            {
                ExitCompareOverlay(); // 内部会回到 AB 左右分栏
                NotifyCompareMode();
            }
            return;
        }

        EnterCompareMode(target);
        // 进入失败（B3 fail-fast 自动降级）时不覆盖状态栏里的失败原因，同 CycleCompareMode。
        if (_compareActive) NotifyCompareMode();
    }

    /// <summary>网格模式入口（菜单「对比模式 → 网格」/ 快捷键 <c>G</c>）：退出叠加 + 退出对比模式
    /// ⇒ <c>Grid.CellOverride</c> 置 null，Grid 回到既有的均匀 N×M 逻辑，被对比模式隐藏的路全部恢复
    ///（2~9 路全部显示；排布由 <see cref="GridLayout.ComputeGrid"/> 决定）。
    ///
    /// <para>已经是网格（未进入对比模式）时直接返回：既不做无谓的"已退出"提示，也不重复下发裁剪。</para></summary>
    private void EnterGridMode()
    {
        if (!_compareActive && !_compareOverlayActive) return;
        ExitCompareMode(); // 内部先退出叠加，再撤格表、复位无缝放大与窗口裁剪
        NotifyCompareMode();
    }

    /// <summary>刷新「对比模式」子菜单三项的勾选 / 可用状态。
    ///
    /// <para><b>刷新时机</b>：父菜单 <c>SubmenuOpened</c> 时 + 三项被点击之后。
    /// 前者保证"用 C / S / V / G 键或路数变化改了模式后，菜单里不会留下陈旧勾选"；
    /// 后者是因为 Avalonia 的 <c>DefaultMenuInteractionHandler.Click</c> 会<b>先</b>把
    /// <c>ToggleType=CheckBox</c> 的 <c>IsChecked</c> 取反、<b>再</b>触发 <c>Click</c>
    /// ——不纠正的话勾选会与实际状态相反（直到下次打开菜单才被刷新）。</para>
    ///
    /// <para>三项互斥：叠加 ⊂ 分屏（叠加是 AB 的呈现变体），两者都未激活即网格。
    /// 路数 &lt; 2 时叠加 / 分屏不可用（<see cref="CompareLayout.AvailableModes"/> 为空集合）。</para></summary>
    private void RefreshCompareModeChecks()
    {
        var canCompare = CompareLayout.AvailableModes(_sync.Count).Count > 0;
        MenuModeOverlay.IsEnabled = canCompare;
        MenuModeSplit.IsEnabled = canCompare;
        MenuModeGrid.IsEnabled = true;

        MenuModeOverlay.IsChecked = _compareOverlayActive;
        MenuModeSplit.IsChecked = _compareActive && !_compareOverlayActive;
        MenuModeGrid.IsChecked = !_compareActive;
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

    /// <summary>选中路的像素可信度（docs/26 D1）。由 <see cref="UpdateStatusInfo"/> 写入，
    /// <see cref="UpdateStatusView"/> 展示；不可用时 Tier 为 Unknown（不展示）。</summary>
    private PixelFidelityResult _lastFidelity;

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

    /// <summary>引擎标签当前是否被"打开降级原因"临时占用（D9）。
    /// <para>占用期间下一次 <see cref="UpdateStatus"/> 必须把它刷回
    /// <see cref="BuildEngineLabel"/>，否则状态栏会永久停留在"打开失败…"，
    /// 用户后来成功打开了也看不出来（旧实现正是如此）。</para></summary>
    private bool _engineLabelOverridden;

    private void UpdateStatus()
    {
        // 组件日志：路数变化（打开/加路/减路/会话重载的唯一汇聚点，见下方 SyncCompareModeToRouteCount）。
        // 只在计数真的变了时记——UpdateStatus 每次状态刷新都会被调用，不能逐次记录。
        if (ComponentLog.IsEnabled && _sync.Count != _loggedRouteCount)
        {
            _loggedRouteCount = _sync.Count;
            ComponentLog.Log(Comp.Engine, "RouteCountChanged", -1, $"count={_sync.Count}");
        }

        // 路数变化时收敛对比模式。UpdateStatus 是全部路数变更路径（打开/加路/减路/会话重载）
        // 的共同汇聚点，挂这里即可覆盖，不必在 6 处 Grid.SetCount 逐个插桩。
        SyncCompareModeToRouteCount();

        // D9：先还原上一次被降级文案覆盖的引擎标签，保证它是"一次性的"。
        if (_engineLabelOverridden)
        {
            StatusEngine.Text = BuildEngineLabel();
            _engineLabelOverridden = false;
        }

        // 消费打开降级原因（N1）：显示一次即清空，避免重复覆盖常规状态
        var openError = _coordinator.LastOpenError;
        if (!string.IsNullOrEmpty(openError))
        {
            _coordinator.ConsumeLastOpenError();
            StatusEngine.Text = openError;
            _engineLabelOverridden = true;
            // D9：本分支原先直接 return，StatusInfo 停留在打开前的旧内容，
            // 与刚显示出来的失败原因互相矛盾（左"打开失败"、右"就绪"）。
            // 引擎标签以外的部分照常刷新即可。
            UpdateStatusInfo();
            return;
        }
        UpdateStatusInfo();
    }

    private void UpdateStatusInfo()
    {
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
        // 帧率不一致时"第 N 帧"在各路指向的是不同时刻的内容，
        // 逐帧对比的语义随之改变，必须让用户看到（docs/15 §3.6）
        if (_sync.Count > 1 && _sync.HasFpsMismatch)
            sb.Append($" | ⚠ {LanguageManager.T("Status_FpsMismatch")}");
        if (runtimeError is not null) sb.Append($" | ⚠ {runtimeError}");

        // Try read RTInfo from selected session
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        if (slot?.Session is { } session && session.ReadRenderTargetInfo(out var rt))
        {
            _lastRtInfo = rt;
            // 3.1-UI（docs/26 D1）：dest 矩形 vs 源分辨率 → "能否宣称像素级"。
            // 源分辨率复用会话的 ReadMediaInfo（与导出帧/差异面板同一来源），不新开 P/Invoke。
            _lastFidelity = ResolveFidelity(readOk: true, rt, session.ReadMediaInfo());
            if (rt.SwapWidth > 0 && rt.SwapHeight > 0)
            {
                sb.Append($" | SW:{rt.ClientWidth}x{rt.ClientHeight}->{rt.SwapWidth}x{rt.SwapHeight}");
                if (rt.DestWidth > 0)
                    sb.Append($" | V:{rt.DestX},{rt.DestY} {rt.DestWidth}x{rt.DestHeight}");
                sb.Append($" | bpp:{rt.OutputBitDepth} {(rt.Hdr ? "HDR" : "SDR")}");
            }
        }
        else
        {
            // B2（D1 硬约束）：读不到 RT 时可信度必须**归零**，绝不能保留上一路的值。
            // 原实现只在读取成功时赋值 ⇒ 切到一条读不到 RT 的路之后，状态栏仍挂着上一路的
            // "像素级 1.00×"，等于对一条未经验证的路宣称像素级。
            _lastFidelity = ResolveFidelity(readOk: false, default, null);
        }
        // 注入钩子防护（docs/33 §八）：RTSS / MSI Afterburner 的注入钩子在场时，
        // 多路 flip-model Present 有 ~90% 概率在我们无法控制的 dxgi Present 内部被投递异常而崩溃。
        // 用户态改代码修不好它（加锁无效、vtable 实测干净），唯一出路是让用户把本程序
        // 加进 RTSS 排除列表 —— 所以这里必须让用户**看见原因**，而不是继续以为软件坏了。
        // 仅在路数 ≥ 2 时提示：单路实测不受影响（崩溃只在多路 Present 并发下出现）。
        // 沿用本方法既有的"⚠ + 语言表键"追加写法（同 Status_FpsMismatch），不新建提示机制。
        if (_sync.Count >= 2 && HookDetector.IsOverlayHookPresent)
        {
            sb.Append($" | ⚠ {LanguageManager.T("Status_OverlayHookWarning")}");
            if (!_hookNoticeLogged)
            {
                _hookNoticeLogged = true;
                ComponentLog.Log(Comp.Env, "HookNoticeShown", -1,
                    $"routes={_sync.Count} hooks={string.Join('+', HookDetector.DetectedHooks)}");
            }
        }

        StatusInfo.Text = sb.ToString();
        UpdateStatusView();
    }

    /// <summary>B2（D1）：把"RT 读取结果"映射成像素可信度，是**唯一**允许产生
    /// <see cref="PixelFidelityResult"/> 的入口。
    ///
    /// <para>规则只有一条：<paramref name="readOk"/> 为 false（或拿不到媒体信息 ⇒ 源分辨率未知）
    /// 时返回 <see cref="PixelFidelityTier.Unknown"/>，绝不复用任何"上一路"的结论。
    /// 展示侧 <see cref="UpdateStatusView"/> 只认 <see cref="PixelFidelityResult.IsUsable"/>，
    /// 因此 Unknown 意味着界面上不会出现任何档位字样。</para>
    ///
    /// <para>抽成纯静态函数是为了让这条硬约束能被单测直接覆盖 —— D1 不允许只靠人眼守着
    /// （<c>3FCompare.Platform.Tests</c> 经 <c>InternalsVisibleTo</c> 直接调用本方法，无需反射）。</para></summary>
    /// <param name="readOk">会话 <c>ReadRenderTargetInfo</c> 是否成功。</param>
    /// <param name="rt">成功时的渲染目标信息；失败时忽略。</param>
    /// <param name="media">成功时读到的媒体信息；为 null 表示源分辨率未知。</param>
    internal static PixelFidelityResult ResolveFidelity(bool readOk, RenderTargetInfo rt, EngineMediaInfo? media)
    {
        if (!readOk) return default;
        return media is null
            ? default
            : PixelFidelity.FromRenderTarget(rt, media.VideoWidth, media.VideoHeight);
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
        // 3.1-UI（docs/26 D1）：像素可信度——档位 + 缩放比 + 文案。
        if (_lastFidelity.IsUsable) parts.Add(DescribeFidelity(_lastFidelity));
        StatusView.Text = string.Join("   ", parts);
    }

    /// <summary>像素可信度展示文案（docs/26 D1）。
    /// <para><b>硬性约定</b>：档位不是 <see cref="PixelFidelityTier.PixelExact"/> 时，
    /// 界面上绝不能出现"像素级"字样。PixelFidelity 的文案以"，非像素级"结尾作否定，
    /// 但字面仍然出现了该词——这里剥掉该后缀（档位标签本身已表达"不可宣称"），并兜底清除残留。</para></summary>
    internal static string DescribeFidelity(PixelFidelityResult f)
    {
        var ratio = Math.Abs(f.RatioX - f.RatioY) <= 1e-6
            ? $"{f.RatioX:0.00}×"
            : $"{f.RatioX:0.00}×/{f.RatioY:0.00}×";
        var desc = f.CanClaimPixelExact ? f.Description : WithoutPixelClaim(f.Description);
        return $"{FidelityTierLabel(f.Tier)} {ratio} · {desc}";
    }

    /// <summary>档位中文标签。仅 <see cref="PixelFidelityTier.PixelExact"/> 允许出现"像素级"。</summary>
    private static string FidelityTierLabel(PixelFidelityTier tier) => tier switch
    {
        PixelFidelityTier.PixelExact => "像素级",
        PixelFidelityTier.IntegerScaled => "整数倍插值",
        PixelFidelityTier.Interpolated => "插值",
        _ => "不可用",
    };

    /// <summary>去掉"像素级"字样（D1 硬性约定）。否定后缀"，非像素级"整段移除，读起来更干净；
    /// 再做一次全量替换兜底，确保任何路径都不会把该词漏到界面上。</summary>
    internal static string WithoutPixelClaim(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        const string suffix = "，非像素级";
        if (text.EndsWith(suffix, StringComparison.Ordinal))
            text = text[..^suffix.Length];
        return text.Replace("像素级", "原生像素", StringComparison.Ordinal);
    }

// ══════════ 视图变换：缩放/平移 ══════════

    /// <summary>光标位置命中测试（探针/放大镜/选中用）。</summary>
    private PlayerSurface? HitSurfaceAt(Point windowPos)
    {
        // 叠加模式下两路都铺满、互相重叠（可见部分由 SetWindowRgn 裁成"揭示区"），按窗口矩形命中会
        // 恒选第 0 路（A）⇒ 改按揭示区命中。见 MainWindow.CompareOverlay.cs。
        if (CompareOverlayActive && _compareActive) return HitOverlayAt(windowPos);

        // 子窗口放大后各路窗口互相重叠（可见部分由 SetWindowRgn 裁成格），按窗口矩形命中会选错路
        // ⇒ 一律改按格命中。未启用放大时本分支不进入，下面的既有循环逐字不变。
        if (CompareMagnifyActive && _compareActive) return HitCompareCellAt(windowPos);

        foreach (var s in Grid.Surfaces)
        {
            if (!s.IsVisible) continue;
            var tl = s.TranslatePoint(new Point(0, 0), this);
            if (tl is not { } origin) continue;
            if (new Rect(origin, new Size(s.Bounds.Width, s.Bounds.Height)).Contains(windowPos)) return s;
        }
        return null;
    }

    /// <summary>按格命中（仅"对比模式 + 子窗口放大"时使用）。
    ///
    /// <para><b>为什么必须换</b>：放大后每一路的子窗口都是"格的 z 倍"且互相重叠，按
    /// <c>Bounds</c> 命中会命中"最后一个覆盖该点的窗口"，与用户实际看到的那一格不一致
    /// （窗口区域已经把越界部分裁掉了，但 Avalonia 侧读的是 <c>Bounds</c> 而不是区域）。</para>
    ///
    /// <para><b>为什么按格就够</b>：格互不重叠且铺满对比区，所以"点在哪个格里"是唯一确定的；
    /// 格表与 <c>CompareGridView.ArrangeOverride</c> 共用 <see cref="CompareCropPlanner.CellToContainerDip"/>
    /// 的同一套公式（自测 <c>AssertCellsDriveLayout</c> 钉住一致性），不会漂移。
    /// 点落在所有格之外（对比区之外）时返回 null —— 与"没命中任何一路"语义相同。</para></summary>
    private PlayerSurface? HitCompareCellAt(Point windowPos)
    {
        var origin = Grid.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } o) return null;
        var p = new Point(windowPos.X - o.X, windowPos.Y - o.Y);

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0) return null;

        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        for (var i = 0; i < cells.Length && i < Grid.Count; i++)
        {
            var s = Grid.GetSurface(i);
            if (s is null || !s.IsVisible) continue;
            if (CompareCropPlanner.CellToContainerDip(cells[i], w, h).Contains(p)) return s;
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
    /// <summary>窗口正在关闭（D4）。
    /// <para><c>volatile</c>：赋值发生在 UI 线程（OnClosing），读取发生在
    /// <c>RecoverFromFailedAsync</c> 的 await 续体（可能是线程池线程）——
    /// 且 <c>await Task.Delay</c> 之后的代码与 OnClosing 里的
    /// <c>DestroyAllSessions()</c> 之间没有其它同步，必须保证可见性。
    /// 原先只查 <c>_coordinator.IsClosed</c>，而它要等 <c>DestroyAllSessions</c>
    /// 真正执行到才置位，检查通过到 <c>_sync.Play()</c> 之间仍可能被关窗打断
    /// ⇒ 对已释放的会话做 P/Invoke（0xC0000005）。</para></summary>
    private volatile bool _closing;

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
            // 已同步下发最终值 ⇒ 取消挂起的补发（否则会用同一组值再发一次）
            _transformFlushTimer.Stop();
            try { SendViewTransform(); }
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
        if (now - _lastPanApplyTicks < 16)
        {
            // ⚠ 节流是「直接丢弃」，不是「合并后补发」。滚轮事件停止后不会再有调用，
            //   被丢弃的那一次就是最终值 ⇒ 画面停在旧缩放级别、与状态栏不符、且不自愈。
            //   触控板惯性滚动（间隔 8~16ms）极易命中；「缩放后立刻重置视图」同样会被吞。
            //   因此丢弃时必须挂一个一次性补发（见 ScheduleTransformFlush）。
            TransformDroppedCount++;
            ScheduleTransformFlush();
            return;
        }
        _lastPanApplyTicks = now;
        _transformFlushTimer.Stop();
        SendViewTransform();
    }

    /// <summary>挂起一次性补发。已经在等待时**不要**重复 Start：
    /// DispatcherTimer.Start() 会重置计时，连续丢弃会把补发无限推迟（饿死）。</summary>
    private void ScheduleTransformFlush()
    {
        if (_transformFlushTimer.IsEnabled) return;
        _transformFlushTimer.Start();
    }

    private void FlushPendingViewTransform()
    {
        _transformFlushTimer.Stop();
        // 会话已关闭/重建：原生句柄可能已释放，此时下发会 0xC0000005。
        if (_sync.Count == 0) return;
        _lastPanApplyTicks = Environment.TickCount64;
        SendViewTransform();
    }

    /// <summary>真正下发变换到内核。UI 线程同步调用——不要用 Task.Run：
    /// 线程池并发 P/Invoke 在会话重建/关闭时访问已释放句柄会触发 0xC0000005 闪退，
    /// 且跨线程读取 _viewZoom/_viewPanX/Y 无序会导致"拖不动"。</summary>
    private void SendViewTransform()
    {
        try
        {
            // 组件日志：视图变换下发（zoom/pan）。调用点已被 16ms 节流（见 ApplyViewTransform），
            // 故这里是"至多 ~60Hz 的用户交互路径"，不是逐帧路径；先判开关再拼串。
            if (ComponentLog.IsEnabled)
                ComponentLog.Log(Comp.Render, "SetViewTransform", -1,
                    $"zoom={_viewZoom:0.###} panX={_viewPanX:0.###} panY={_viewPanY:0.###}");
            _sync.SetViewTransform(_viewZoom, _viewPanX, _viewPanY);
            LastSentZoom = _viewZoom;
            LastSentPanX = _viewPanX;
            LastSentPanY = _viewPanY;
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
        // D8：文本输入框获得焦点时不接管全局快捷键。
        // 书签备注框（BookmarkPanel._note）里打字时，空格会触发播放/暂停、←/→ 会步进帧、
        // Delete 会删掉选中书签 —— 用户只是想输入 "delete" 这个词，数据却没了。
        // 焦点在列表/滑块/视频面时不受影响，快捷键照常生效。
        if (IsInsideTextBox(e.Source))
        {
            base.OnKeyDown(e);
            return;
        }
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
            // 侧栏三态循环（展开 → 图标栏 → 完全隐藏）：Ctrl+H。
            // 选键依据：① 本 switch + XAML 的 InputGesture + TimelineView 的 A/B 已全表核对，
            // Ctrl+H 未被占用（带修饰键的全局键只有 Ctrl+S 导出与 Ctrl+O 打开）；
            // ② **刻意不用 Ctrl+B**：它虽与"裸 B"分属不同修饰键，但 TimelineView.OnKeyDown
            // 对 Key.B 的判断**不带修饰键守卫**（TimelineView.cs:159），而时间轴在点击后
            // 就会拿到焦点（TimelineView.cs:113）—— 于是"刚拖过进度条再按 Ctrl+B"会被时间轴吞掉，
            // 实际发生的是"设一个 B 点"，与用户预期完全相反。跨控件改这条守卫不在本次改动范围内，
            // 故选一个不会被任何控件吞掉的组合；
            // ③ H 取「隐藏」首字母：三态里"完全隐藏"正是旧版做不到、需要新入口才能到达的一态。
            case Key.H when mods.HasFlag(KeyModifiers.Control): CycleSidebarMode(); break;
            // 多路对比模式入口：单键循环「进入 → 逐级切换可用模式 → 退出」（见 CycleCompareMode）。
            // 选快捷键而非菜单：菜单要改 MainWindow.axaml 并新增本地化键，而 Localization/ 不在
            // 本次改动范围（docs/26 §四）；快捷键只动本文件这一处 switch，改动最小且零新依赖。
            case Key.C when mods == KeyModifiers.None: CycleCompareMode(); break;
            // 叠加模式（对标 ICAT Single Screen，docs/31 阶段 2）开关：两路铺满 + B 置顶裁半。
            // 选 S 的依据：① 未被占用（全表只有 Ctrl+S 用于导出帧，见上）；② 语义对得上文档标题
            // "Single Screen"；③ 与 C（模式循环）分属"切模式"与"切呈现"两件事，不会混淆。
            case Key.S when mods == KeyModifiers.None: ToggleCompareOverlay(); break;
            // 模式家族入口（docs/31 阶段 3）：V = 分屏、G = 网格（叠加沿用 S）。
            // 选键依据：① 全表（本 switch + TimelineView 的 A/B + XAML 的 InputGesture）已核对，
            // G / V 两个裸键此前均未被占用，且带修饰键的 Ctrl+G/Ctrl+V 不受影响（有 None 守卫）；
            // ② 首字母对应 V(iew 的三种模式之一「分屏」) / G(rid)，与 C（循环）S（叠加）不冲突。
            case Key.V when mods == KeyModifiers.None: EnterSplitMode(); break;
            case Key.G when mods == KeyModifiers.None: EnterGridMode(); break;
            // 底部栏折叠（docs/31 阶段 4）：T = 时间轴，Shift+T = 状态栏。
            // 选键依据：① 全表（本 switch + TimelineView 的 A/B + XAML 的 InputGesture）已核对，
            // T 与 Shift+T 此前均未被占用，且带其它修饰键的组合（Ctrl+T 等）因 None/Shift 守卫不受影响；
            // ② T 取 Timeline 首字母，与 XAML 里 Menu_Timeline 标注的快捷键一致；
            // ③ 状态栏与时间轴同族，用同一键的 Shift 变体，不必再占一个裸键。
            case Key.T when mods == KeyModifiers.None: ToggleBottomBar("timeline"); break;
            case Key.T when mods == KeyModifiers.Shift: ToggleBottomBar("statusbar"); break;
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

    /// <summary>按键事件的来源是否位于文本输入控件内（D8 判定条件）。
    /// <para>两种来源都要覆盖：① 事件源本身就是 <c>TextBox</c>（焦点在 TextBox 上，最常见）；
    /// ② 事件源是 TextBox 模板内部的 <c>TextPresenter</c> 之类子元素——它才是真正持有
    /// 焦点的元素，只看 <c>e.Source is TextBox</c> 会漏掉这种情况。
    /// 沿视觉父链向上找 TextBox 即可同时覆盖两者。</para>
    /// <para>刻意<b>不</b>用 <c>FocusManager.GetFocusedElement()</c>：它在部分平台上会返回
    /// 顶层窗口本身（焦点尚未落到控件上），反而漏判；而按键事件的 Source 一定是真实的
    /// 命中元素，判定更可靠。</para></summary>
    private static bool IsInsideTextBox(object? source)
    {
        if (source is not Visual v) return false;
        // 注意 GetVisualParent() 返回的是 Visual?（视觉树顶端为 null）——局部变量必须
        // 声明成可空，否则 `p = p.GetVisualParent()` 会触发 CS8601 可空性警告
        // （本项目 Release 要求 0 警告）。
        Visual? p = v;
        while (p is not null)
        {
            if (p is TextBox) return true;
            p = p.GetVisualParent();
        }
        return false;
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
        // D1：本方法体内原先**没有任何**异常边界，而 SaveFilePickerAsync +
        // SessionSnapshot.SaveToFile（→ AtomicFile.WriteAllText）在路径非法 / 只读 /
        // 文件被占用时会抛 UnauthorizedAccessException / IOException。异常无人接住 ⇒
        // 进程闪退且未保存的会话全部丢失。这里补上边界，成功路径一字不改。
        try
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
        catch (Exception ex)
        {
            await ReportErrorAsync("Menu.SaveSession",
                Loc("保存会话失败。", "Failed to save the session."), ex);
        }
    }

    /// <summary>按当前状态构造会话快照（菜单保存与自测 --sessiontest 共用同一份，
    /// 避免测试另写一套而与真实保存路径漂移）。</summary>
    internal SessionSnapshot BuildSessionSnapshot() => new()
    {
        // 存"用户实际选择的预设"而不是按路数推导——否则 1/2/3/5/6 路的布局重载后会变
        // （docs/14 §1.1）。CodeFor 仅保留给"无预设可依"的旧快照兼容路径。
        GridLayout = _3FCompare.Core.Display.GridLayout.CodeFromPreset(Grid.Preset, Grid.SingleView),
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
        // D1：文件选择器与 SessionSnapshot.LoadFromFile 都可能抛（拒访 / 编码损坏 /
        // 磁盘错误），async void 无边界 ⇒ 直接闪退。
        try
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
        catch (Exception ex)
        {
            await ReportErrorAsync("Menu.LoadSession",
                Loc("加载会话失败。", "Failed to load the session."), ex);
        }
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
        ComponentLog.Log(Comp.Engine, "SessionReload", -1,
            $"routes={snapshot.Items.Count} pos={snapshot.Position100ns}");
        foreach (var s in Grid.Surfaces) s.DetachSession();
        _sync.Clear();
        Grid.SetCount(0, _realMode);
        Grid.SetCount(Math.Min(9, snapshot.Items.Count), _realMode);
        // 还原布局（GridLayout: 0=自动, 1=单屏, 2=2x2, 3=3x3）。
        // 快照存了却从不还原的话，单屏或 3x3 的会话重载后会跳回默认布局，"保存会话"只恢复一半。
        // SetGridLayout 只改预设覆盖、单屏与否由 SingleView 控制，两者必须一起设。
        Grid.SingleView = _3FCompare.Core.Display.GridLayout.IsSingleView(snapshot.GridLayout);
        Grid.SetGridLayout(_3FCompare.Core.Display.GridLayout.PresetOf(snapshot.GridLayout));
        // 会话文件里的路径来自外部（.3fcs 可能是别人给的），必须先校验再打开：
        // ① null / 空白 / **相对路径**：直接拒。相对路径会按当前工作目录解析，
        //    可能意外命中一个同名文件——等于让外部文件决定打开哪个视频。
        // ② UNC（\\server\share）：Windows 访问时会自动发起 NTLM 认证，把本机凭据
        //    送到路径里指定的服务器 ⇒ 凭据外泄。确有需要可手动拖放/选择文件打开，
        //    那是用户的主动意图，与本路径性质不同。
        // 原实现用 i.Path! 强转后一律直传，异常还在 PlaybackCoordinator 里被吞掉
        // ⇒ 静默标记该路失败，用户完全不知道少了路（docs/15 §5.1）。
        var acceptedPaths = new System.Collections.Generic.List<string>(snapshot.Items.Count);
        var acceptedIndex = new System.Collections.Generic.List<int>(snapshot.Items.Count);
        var rejected = new System.Collections.Generic.List<string>();
        for (var i = 0; i < snapshot.Items.Count; i++)
        {
            var p = snapshot.Items[i].Path;
            if (string.IsNullOrWhiteSpace(p) || !Path.IsPathRooted(p)
                || p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                rejected.Add(string.IsNullOrWhiteSpace(p) ? "(空路径)" : p);
                continue;
            }
            acceptedPaths.Add(p);
            acceptedIndex.Add(i);
        }
        if (rejected.Count > 0)
        {
            var msg = $"会话中有 {rejected.Count} 条路径未通过校验已跳过：{string.Join(", ", rejected)}";
            _3FCompare.Core.Diagnostics.AppLog.Warn("Session", msg);
            Console.Error.WriteLine($"[Session] {msg}");
        }

        _coordinator.OpenFiles(acceptedPaths, autoPlay: true, onAllOpened: () =>
        {
            // 先恢复偏移，再 SeekTo（SeekTo 内部会叠加偏移）。
            // 注意用 acceptedIndex 对位：跳过若干路径后，槽位索引已不等于原始 Items 索引，
            // 直接按 i 取会把偏移张冠李戴。
            for (var k = 0; k < acceptedIndex.Count && k < _sync.Count; k++)
                // master 偏移恒 0：老会话可能存了非 0 的 off0，加载时归零，
                // 否则一加载就带着"基准被挪走"的状态（docs/15 §3.1）
                _sync.Slots[k].Offset100ns = k == 0 ? 0 : snapshot.Items[acceptedIndex[k]].Offset100ns;
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
        // D1：Resample 会做像素回读、ShowDialog 会创建顶层窗口，都可能抛；
        // async void 无边界 ⇒ 闪退。补边界，成功路径不变。
        try
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
                Content = view,
            };
            // 主题令牌（新增式改动：原先写死 #101012）
            ThemePalette.SetBrush(win, Window.BackgroundProperty, "CanvasDarkBrush");
            await win.ShowDialog(this);
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Menu.Diff",
                Loc("差异叠加打开失败。", "Failed to open the diff overlay."), ex);
        }
    }

    // 侧栏几何（宽度记忆 / 折叠 / 响应式自动折叠 / ShowSidebar）已拆到 MainWindow.Sidebar.cs
    // ——MainWindow.axaml.cs 已近 1800 行，按职责继续向外拆。

    private void OnGridPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string preset })
            Grid.SetGridLayout(preset);
    }

    /// <summary>「视图 → 对比模式」三项的统一入口。写法与 <see cref="OnGridPreset"/> 一致
    ///（<c>Click</c> 事件 + <c>Tag</c> 字符串，不用 Command），保持本菜单风格统一。</summary>
    private void OnCompareModePreset(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string preset }) return;

        switch (preset)
        {
            case "overlay": EnterCompareOverlay(); break; // 内部自带"需 ≥2 路"提示与状态栏通知
            case "split": EnterSplitMode(); break;
            case "grid": EnterGridMode(); break;
            default: return;
        }

        // 勾选一律以真实状态为准：CheckBox 型的 IsChecked 已在 Click 之前被框架取反（见
        // RefreshCompareModeChecks 的注释），这里必须纠正回来。
        RefreshCompareModeChecks();
    }

    /// <summary>父菜单展开时刷新勾选 —— 这是唯一能覆盖"用户用快捷键改了模式"的时机。</summary>
    private void OnCompareModeMenuOpened(object? sender, RoutedEventArgs e) => RefreshCompareModeChecks();

    /// <summary>「视图」菜单展开时刷新其下全部勾选项（对比模式三项 + 底部栏三项 + 侧栏三项）。
    /// 在父菜单上刷新比只在子菜单上刷新覆盖更早：用户用 T / Shift+T / C / S / V / G / Ctrl+H
    /// 改过状态后，即使不展开对应子菜单也能看到正确的勾选。</summary>
    private void OnViewMenuOpened(object? sender, RoutedEventArgs e)
    {
        RefreshCompareModeChecks();
        RefreshBottomBarChecks();
        RefreshSidebarChecks();
    }

    /// <summary>刷新「视图 → 时间轴 / 状态栏」两项的勾选。
    ///
    /// <para><b>刷新时机</b>与 <see cref="RefreshCompareModeChecks"/> 完全同因：① 菜单展开时
    /// （覆盖"用 T / Shift+T 快捷键改了折叠态"）；② 点击处理末尾——Avalonia 的
    /// <c>DefaultMenuInteractionHandler.Click</c> 会<b>先</b>把 <c>ToggleType=CheckBox</c> 的
    /// <c>IsChecked</c> 取反、<b>再</b>触发 <c>Click</c>，不纠正的话勾选会与实际状态相反。</para>
    ///
    /// <para>勾选语义 = 面板<b>可见</b>（勾上表示显示），故与折叠标志互为取反。</para></summary>
    private void RefreshBottomBarChecks()
    {
        MenuTimeline.IsChecked = !_settings.TimelineCollapsed;
        MenuStatusBar.IsChecked = !_settings.StatusBarCollapsed;
        // 悬浮传输栏的勾选语义 = 已开启悬浮（与折叠项的"可见"语义不同，不取反）。
        MenuFloatingTransport.IsChecked = _settings.FloatingTransport;
    }

    /// <summary>「视图 → 时间轴 / 状态栏」的统一入口，写法与 <see cref="OnCompareModePreset"/>
    /// 一致（<c>Click</c> + <c>Tag</c> 字符串，不用 Command）。</summary>
    private void OnToggleBottomBar(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string which }) ToggleBottomBar(which);
    }

    /// <summary>切换底部栏折叠（<paramref name="which"/> = <c>"timeline"</c> / <c>"statusbar"</c>）
    /// 或悬浮传输栏（<c>"floating"</c>）。
    /// 菜单与快捷键共用同一实现，避免两处各写一遍"翻转 + 应用 + 落盘 + 刷勾"。</summary>
    private void ToggleBottomBar(string which)
    {
        switch (which)
        {
            case "timeline": _settings.TimelineCollapsed = !_settings.TimelineCollapsed; break;
            case "statusbar": _settings.StatusBarCollapsed = !_settings.StatusBarCollapsed; break;
            case "floating": _settings.FloatingTransport = !_settings.FloatingTransport; break;
            default: return;
        }

        ApplyBottomBarVisibility();
        // 自测模式不写用户配置（与 SaveWindowGeometry / 侧栏几何同规）
        if (!_selfTestMode) SettingsStore.Save(_settings);
        // 勾选一律以真实状态为准（Click 已把 IsChecked 取反，见 RefreshBottomBarChecks）
        RefreshBottomBarChecks();
    }

    /// <summary>把「用户折叠偏好」与「全屏隐藏 chrome」合成到底部两栏的 <c>IsVisible</c>
    /// （docs/31 阶段 4）。启动时、切换折叠时、进出全屏时都要调它。
    ///
    /// <para><b>优先级</b>：全屏隐藏（<c>_fullscreen &amp;&amp; HideChromeInFullscreen</c>）<b>高于</b>
    /// 用户折叠设置 —— 全屏时两栏一律不可见。但本方法<b>只读取、不改写</b>
    /// <c>_settings.TimelineCollapsed</c> / <c>StatusBarCollapsed</c>：用户的折叠偏好被"暂停"而非
    /// 被覆盖，因此退出全屏后再调一次本方法，恢复的是用户自己的折叠选择，而不是无条件全展开。</para>
    ///
    /// <para><b>传输栏</b>：全屏隐藏优先；此外它还受「悬浮传输栏」开关支配 —— 开启时从常驻布局
    /// 里摘走（改由 owned 顶层窗承载，见 <see cref="SyncFloatingTransport"/>），常驻位让出的 54px
    /// 归视频区。这是唯一会让视频区高度变化的时机（开关切换那一刻），浮条本身的浮现/隐藏不动布局。</para>
    ///
    /// <para>高度是 XAML 上的字面量 <c>Height</c>（无 MinHeight、无绑定、无动画），
    /// 改 <c>IsVisible</c> 即可，折叠后 <c>MainArea</c> 由 DockPanel 自动撑高。</para></summary>
    private void ApplyBottomBarVisibility()
    {
        var hideChrome = _fullscreen && _settings.HideChromeInFullscreen;
        TimelineHost.IsVisible = !hideChrome && !_settings.TimelineCollapsed;
        StatusBarHost.IsVisible = !hideChrome && !_settings.StatusBarCollapsed;
        TransportHost.IsVisible = !hideChrome && !_settings.FloatingTransport;
        // 浮条的显示/隐藏与上面三栏统一在同一入口协调：避免出现"全屏隐藏了 chrome，浮条却还在"
        // 或"折叠状态被开关覆盖"的两套状态打架。
        // 注意浮条**不**跟随 hideChrome：它是"光标靠近才出现"的，不破坏沉浸式全屏的意图，
        // 而 docs/31 阶段 4.3 明确要求全屏同样可用（全屏时菜单隐藏，用户仍能操作播放）。
        SyncFloatingTransport();
    }

    // ══════════ 悬浮传输栏（docs/31 阶段 4.3）══════════

    /// <summary>把「悬浮传输栏」开关落到实际对象上：开 → 建 owned 顶层窗并把传输栏搬进去 + 起轮询；
    /// 关 → 停轮询、隐藏、销毁窗口并把控件交还常驻位。幂等，可反复调用。</summary>
    private void SyncFloatingTransport()
    {
        if (_settings.FloatingTransport)
        {
            EnsureFloatingTransport();
            _transportHoverTimer.Start();
            return;
        }

        _transportHoverTimer.Stop();
        _transportHideTimer.Stop();
        HideFloatingTransport();
        DestroyFloatingTransport();
    }

    /// <summary>惰性创建浮条窗口，并把传输栏从常驻 Border 搬到窗口里。
    ///
    /// <para>⚠ <b>控件单亲约束</b>：必须先 <c>TransportHost.Child = null</c> 再 Attach，
    /// 否则 Avalonia 会因"控件已有父"抛异常。</para></summary>
    private void EnsureFloatingTransport()
    {
        if (_floatingTransport is not null) return;
        var win = new FloatingTransportWindow();
        TransportHost.Child = null;
        win.Attach(this, CenterPanel, _transport);
        _floatingTransport = win;
    }

    /// <summary>销毁浮条窗口并把传输栏交还常驻位。
    ///
    /// <para>⚠ <b>绝不能把传输栏随窗口一起销毁</b>：它是宿主的常驻控件（事件在构造函数里一次性
    /// 接好），被销毁后"关掉悬浮"就再也看不到传输栏了。故顺序固定为
    /// 「先 DetachContent 摘出 → 再 CloseAndDispose 窗口 → 最后挂回 TransportHost」。</para></summary>
    private void DestroyFloatingTransport()
    {
        if (_floatingTransport is null) return; // 传输栏从未被搬走，仍在 TransportHost 上

        var win = _floatingTransport;
        _floatingTransport = null;
        var content = win.DetachContent();
        win.CloseAndDispose();
        TransportHost.Child = content ?? _transport;
    }

    private void ShowFloatingTransport()
    {
        EnsureFloatingTransport();
        _floatingTransport?.ShowBar();
    }

    private void HideFloatingTransport() => _floatingTransport?.HideBar();

    /// <summary>热区轮询：指针在底部热区（或停在浮条上）⇒ 保持/浮现；离开 ⇒ 起 800ms 延迟隐藏。
    /// 延迟期间若指针回到热区，下一拍就把它取消（避免沿底边抖动时反复闪）。</summary>
    private void OnTransportHoverTick()
    {
        if (!_settings.FloatingTransport) return;

        if (ShouldRevealFloatingTransport())
        {
            _transportHideTimer.Stop();
            ShowFloatingTransport();
        }
        else if (_floatingTransport is { IsVisible: true } && !_transportHideTimer.IsEnabled)
        {
            _transportHideTimer.Start();
        }
    }

    /// <summary>指针是否应让浮条保持浮现。</summary>
    private bool ShouldRevealFloatingTransport()
    {
        if (_floatingTransport is null) return false;
        if (WindowState == WindowState.Minimized) return false;
        if (!GetCursorPos(out var pt)) return false;

        // 指针停在浮条自身矩形内 ⇒ 保持（点按钮 / 拉 ComboBox 时不该消失）。
        // 这一条必须排在 IsActive 判定之前：ComboBox 下拉是独立 Popup，展开时主窗可能被判失焦。
        if (IsCursorOverFloatingBar()) return true;

        // 失焦（切走别的程序）：不因指针恰好停在底边而让浮条常驻。
        if (!IsActive) return false;

        return IsCursorInTransportHotZone(pt.X, pt.Y);
    }

    private bool IsCursorOverFloatingBar()
    {
        if (_floatingTransport is not { IsVisible: true }) return false;
        if (!GetCursorPos(out var pt)) return false;
        return _floatingTransport.ContainsScreenPoint(new PixelPoint(pt.X, pt.Y));
    }

    /// <summary>光标（物理屏幕像素）是否落在「主窗口底部若干像素」的热区里。
    /// 热区下沿 = 主窗口客户区底边，上沿 = 浮条顶边再往上 <see cref="TransportHotZoneGrace"/> DIP
    /// —— 这样热区天然包含底部的常驻栏（时间轴 / 状态栏）与视频区的最下一条，
    /// 用户"往底部一靠"就能唤出，不必精确对准某一像素行。
    ///
    /// <para>用 <c>CenterPanel.PointToScreen</c> 而非窗口客户区原点换算：锚点的屏幕矩形是
    /// 直接可比的物理像素，不必再关心窗口边框/客户区偏移。</para></summary>
    private bool IsCursorInTransportHotZone(int screenX, int screenY)
    {
        var h = CenterPanel.Bounds.Height;
        if (h <= 0) return false;

        PixelPoint bottom;
        try { bottom = CenterPanel.PointToScreen(new Point(0, h)); }
        catch { return false; }

        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        var zoneTop = bottom.Y -
            (FloatingTransportWindow.BarHeight + FloatingTransportWindow.EdgeMargin + TransportHotZoneGrace) * scaling;
        return screenY >= zoneTop;
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
        // 时间轴 / 状态栏 / 传输栏的可见性一律由 ApplyBottomBarVisibility 统一裁决
        // （全屏隐藏优先，叠加用户折叠与悬浮开关）—— 这里不再单独赋值，避免两处规则打架。
        // 合成规则见 ApplyBottomBarVisibility。
        ApplyBottomBarVisibility();
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        // D1：设置对话框里做 FFmpeg 目录探测、落盘 SettingsStore 等 I/O，都可能抛；
        // async void 无边界 ⇒ 闪退。补边界，成功路径不变。
        try
        {
            var dlg = new Views.SettingsWindow(_settings) { };
            await dlg.ShowDialog(this);
            if (!dlg.Changed || dlg.Result is null) return;

            var result = dlg.Result;
            // 语言**不会**即时生效：LocExtension（Localization/LocExtension.cs）明写
            // "语言切换需重启生效（与 WinForms 版行为一致）"，设置窗口本身也会弹重启询问。
            // 这是设计约定，不是缺陷——旧注释写的"绑定自动刷新"是错的，会误导后来者
            // 去"修"一个根本不存在的问题。这里只记录当前语言，实际生效在下次启动。
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
                        // D11：Environment.Exit(0) 是硬退出，会跳过 OnClosed（定时器不停）
                        // 与 AppLog.Shutdown()（日志不落盘）。Close() 只是**请求**关闭，
                        // 消息循环未必有机会跑到 OnClosing，故这里显式复刻一次关闭清理。
                        // 刻意不做"等待窗口真正关闭"——重启路径一旦卡住就永远起不来新进程。
                        StopTimersAndFlushLog();
                        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                        Close();
                        Environment.Exit(0);
                    }
                }
                return;
            }

            // 组件日志：设置被采纳。设置项可改变路数/渲染/主题 —— 若崩溃紧随其后，
            // 这条能把它与"改设置"这个用户动作关联起来。
            ComponentLog.Log(Comp.Settings, "Saved", -1, "src=settingsDialog");
            SettingsStore.Save(result);
            CopySettings(result);
            UpdateStatus();
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Menu.Settings",
                Loc("打开设置失败。", "Failed to open Settings."), ex);
        }
    }

    /// <summary>停掉全部 DispatcherTimer 并冲刷日志（D11/D7 共用）。
    /// <para>重启路径上使用：不能依赖 OnClosed——<c>Environment.Exit</c> 直接终止进程，
    /// 定时器回调与日志后台线程都没有收尾机会。</para></summary>
    private void StopTimersAndFlushLog()
    {
        _pollTimer.Stop();
        _scrubTimer.Stop();
        _transformFlushTimer.Stop();
        _transportHoverTimer.Stop();
        _transportHideTimer.Stop();
        try { _3FCompare.Core.Diagnostics.AppLog.Shutdown(); } catch { }
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
        // 用**选中路自己**的 fps 换算"±1 帧"。
        // 原先一律取 master 的 fps：当选中路与 master 帧率不同时步长就算错了——
        // 选中 60fps 的从路、master 是 24fps，一次"±1 帧"会挪 41.7ms 而不是 16.7ms，
        // 用户微调时表现为"怎么都对不准"（docs/15 §3.6）。
        // master 仅在本路拿不到帧率时兜底。
        var targetSnap = slot.Session.ReadSnapshot();
        var fps = targetSnap is not null ? SyncController.EstimateFps(targetSnap) : 0;
        if (fps <= 0)
        {
            var masterSnap = _sync.ReadMasterSnapshot();
            fps = masterSnap is not null ? SyncController.EstimateFps(masterSnap) : 0;
        }
        if (fps <= 0) fps = 24; // 最终兜底
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
        {
            // 采样点必须用**该路 surface 内**的坐标并乘 DPI 缩放；原先传的是
            // e.GetPosition(CenterPanel)（面板全局坐标）且不乘 RenderScaling，
            // 于是多格布局 / DPI 非 100% 时显示的完全不是光标下的内容（docs/15 §2.2）。
            // session 也要绑定到**指针命中的那一路**，而不是当前选中路。
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            if (_sync.Slots.ElementAtOrDefault(surface.Index)?.Session is { } ms)
                Magnifier.AttachSession(ms);
            Magnifier.UpdateAt(local, scaling);
        }
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

    /// <summary>第 1 路是规范时间轴的基准（master），**不允许**设偏移：
    /// 给它加偏移等于把基准本身挪走，会让 SeekTo 与帧步进两套偏移语义互相矛盾
    /// 且每次微调都累积（docs/15 §3.1）。选中它时偏移操作一律不生效。</summary>
    private bool IsMasterSelected => Grid.SelectedIndex == 0;

    private void OnOffsetAlign()
    {
        if (IsMasterSelected) return;
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
        if (IsMasterSelected) return;
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
        // 自测模式一律不弹窗，改用 _selfTestMode 判据：它在构造函数最前面就已置好，
        // 覆盖全部自测模式。原先这里逐个比对命令行白名单，漏掉了
        // --screentest / --sessiontest / --multitest，在缺 FFmpeg 的机器上会弹模态框
        // 无人应答 ⇒ 永久挂起（只有 --selftest 有 40s 看门狗兜底）。
        if (_selfTestMode) return;

        // D1：async void 需异常边界（这里会 ShowDialog 并连锁打开设置窗口）。
        try
        {
            // D5：这是**唯一**的演示模式引导弹窗。构造函数里原来还挂了一个 Opened
            // 处理器，判定条件与本方法完全等价（!IsNativeAvailable() == !_realMode），
            // 却用另一套文案/按钮 ⇒ 用户要连续应答两次，且"稍后再说"与"关闭"语义冲突。
            // 已删除那一处，收敛到这里（本方法同时带自测模式守卫，更安全）。
            var openSettings = await Views.MessageBox.Show(this,
                LanguageManager.T("Msg_DemoModeTitle"),
                LanguageManager.T("Msg_DemoModeMissingFfmpeg"),
                primaryText: LanguageManager.T("Msg_DemoModeOpenSettings"),
                secondaryText: LanguageManager.T("Msg_DemoModeClose"));
            if (!openSettings) { Close(); return; }

            OnOpenSettings(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("DemoMode",
                Loc("引导设置打开失败。", "Failed to open the setup guide."), ex);
        }
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
                    // D4：跨越 await 之后必须重新判定关闭状态——OnClosing 可能已经跑完
                    // DestroyAllSessions()，此时 _sync 里的会话句柄均已释放。
                    if (_closing || _coordinator.IsClosed) return;
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
                        // D4：这一支会在 _sync 已被清空后再调 Play（已析构会话上的 P/Invoke）
                        if (_closing || _coordinator.IsClosed) return;
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
                if (_closing || _coordinator.IsClosed) return;
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
        // D4：进入重建前先判一次。OnClosing 已 DestroyAllSessions()，
        // 此时 _sync.Slots / 会话句柄都不再有效，重建本身没有意义且会访问野句柄。
        if (_closing || _coordinator.IsClosed) return;

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
        ComponentLog.Log(Comp.Engine, "SessionRebuild", -1,
            $"routes={paths.Length} pos={pos}");

        _sync.Pause();
        _sync.Stop();
        foreach (var s in Grid.Surfaces)
            s.DetachSession();
        _sync.Clear();

        // 等待子窗口稳定（PollSnapshots 在此期间被 _recovering 标志阻止）
        await System.Threading.Tasks.Task.Delay(300);

        _coordinator.OpenFiles(paths, autoPlay: true, onAllOpened: () =>
        {
            // D4：回调是**异步**触发的（打开完成后），期间窗口可能已关闭。
            // 这里会 _sync.SeekTo / _sync.Play，落在已释放的会话上就是进程级崩溃。
            if (_closing || _coordinator.IsClosed) return;

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
            // 组件日志：重建路径的就绪点（与 SessionReady 分开标记来源，便于区分
            // "首次打开"与"崩溃后重建"两类时序）
            ComponentLog.Log(Comp.Engine, "SessionReady", -1,
                $"src=rebuild routes={_sync.Count} pos={pos}");
            Console.Error.WriteLine($"[MainWindow] ✅ 会话重建完成，已恢复到 {TimeSpan.FromTicks(pos):g}");
        });

        // 等待打开完成（OpenFiles 是异步的）
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline && !_closing && !_coordinator.IsClosed)
        {
            var snap = _sync.ReadMasterSnapshot();
            if (snap is not null && PlaybackCoordinator.IsReadyState(snap.State)) break;
            await System.Threading.Tasks.Task.Delay(200);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // D4：置位必须在任何销毁动作之前——异步的会话重建流程（RecoverFromFailedAsync）
        // 此刻可能正挂在 await Task.Delay 上，它醒来后要据此放弃对已释放会话的访问。
        _closing = true;

        // 可用性 P0-2：保存窗口位置/尺寸/状态（唯一的持久化点）。
        SaveWindowGeometry();

        _pollTimer.Stop();
        // scrub 缩略图定时器只在 OnClosed 停是不够的：OnClosing 之后到窗口真正销毁之间
        // 若还有 tick，会对已销毁的 HWND 做 BitBlt。关闭路径上一并停掉。
        _scrubTimer.Stop();
        // D7：变换补发定时器同理——Closing→Closed 之间它仍可能 Tick 并调用
        // SendViewTransform（P/Invoke）。此前只靠 FlushPendingViewTransform 里的
        // `_sync.Count == 0` 间接保护，而 _sync 是在下面 DestroyAllSessions 之后才清零的，
        // 中间这段窗口期没有任何保护。
        _transformFlushTimer.Stop();
        // 悬浮传输栏（docs/31 阶段 4.3）：停轮询 + 销毁 owned 顶层窗。
        // 不销毁的话进程里会残留一个顶层窗口，Avalonia 会因仍有存活 Window 而不退出消息循环
        // （同 LayoutOverlayWindow 的处理）；DestroyFloatingTransport 内部会把传输栏交还常驻位，
        // 不会把控件一起销毁。
        _transportHoverTimer.Stop();
        _transportHideTimer.Stop();
        DestroyFloatingTransport();
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
        // 组件日志：会话销毁是"presenter 线程应随之停止"的契约点。若崩溃发生在 Present 路径上，
        // 本行之后是否还有呈现迹象，直接决定"是销毁后仍在呈现"还是"销毁前就已崩"。
        ComponentLog.Log(Comp.Engine, "SessionClose", -1, $"routes={_sync.Count} src=destroyAll");
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
        _transformFlushTimer.Stop();
        // P0-5 修复：缩略图预览是独立的顶层 Window，Hide() 只隐藏不销毁。
        // 不显式 Close 会在关窗后残留一个永不回收的顶层窗口
        // （并让 Avalonia 因仍有存活 Window 而不退出消息循环）。
        _thumbnail?.CloseAndDispose();
        _thumbnail = null;
        // 对比布局覆盖层同样是独立顶层窗口：HideOverlay() 只隐藏不销毁，必须显式 Close
        // （否则关窗后残留一个 owned 顶层窗口，Avalonia 会因仍有存活 Window 而不退出消息循环）。
        // 注：该窗口已由 Topmost 改为 Owner=主窗口（Z 序只高于本应用，不再压住其它程序）。
        _layoutOverlay?.CloseAndDispose();
        _layoutOverlay = null;
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
