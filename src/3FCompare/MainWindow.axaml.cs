using System;
using _3FCompare.Themes;
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
using _3FCompare.Localization;
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

    /// <summary>崩溃自愈用的会话自动保存定时器（docs/43）。
    ///
    /// <para><b>为什么只能"边看边写"</b>：要防的崩溃是原生访问违规 / 非法指令，
    /// 它是<b>即时终止</b>——不跑 finally、不跑 ProcessExit、不跑 OnClosed，
    /// 任何"退出时保存"的钩子在它面前都等于不存在。所以必须在崩溃<b>之前</b>落盘。</para>
    ///
    /// <para><b>为什么是 5 秒</b>：更密会与 8 路 4K 的顺序读盘抢 I/O（每次写都 fsync）；
    /// 更疏则恢复出来的位置偏差过大。5 秒的代价是崩溃时最多丢 5 秒进度，
    /// 而保住的是"不用重新打开 8 路 4K"——后者才是真正昂贵的东西。</para></summary>
    private readonly DispatcherTimer _autosaveTimer;
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
    /// <summary>当前网格分区（形状 + 各列/行权重）。路数或预设变化时经
    /// <see cref="GridPartition.Retune"/> 收敛；分割线拖动由覆盖层回灌新分区。</summary>
    private GridPartition _partition = GridPartition.Uniform(2, 2, 1);
    /// <summary>左右拉动（叠加）专用的那一条揭示线：形状恒为 2 路单分割，
    /// 位置就是"露出 B 的比例"。与 <see cref="_partition"/> 分开存，退出拉动时网格的
    /// 分格宽度才不会被揭示位置顶掉。</summary>
    private GridPartition _wipePartition = GridPartition.Uniform(2, 2, 1);
    /// <summary>合并态是否已激活（未激活 = 均匀网格，无覆盖层、不可拖分割线）。</summary>
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
    /// <summary>承载放大镜的 owned 顶层窗（P1-4）：视频子 HWND 恒盖在主窗自绘内容之上
    /// （airspace），放大镜挂回 CenterPanel 只会被整块遮住，必须用独立窗口呈现。
    /// 随主窗构造创建、OnClosed 销毁；Show/Hide 由 <see cref="OnMagnifierPresentationChanged"/> 驱动。</summary>
    private readonly MagnifierOverlayWindow _magnifierWindow;

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
        // docs/41 P0-2 复核：阈值由 3 降到 2。argc==2 意味着"只给了模式名、一个参数都没有"，
        // 所有自测模式都至少需要 1 个素材路径 ⇒ 这也是参数不足，必须置位以便
        // 分派链末尾的守卫拦住（否则照样启动完整 GUI 且不退出）。
        if (cmdArgs.Length >= 2 &&
            cmdArgs[1] is "--selftest" or "--screentest" or "--multitest" or "--sessiontest"
                       or "--comparemodetest" or "--magnifybench"
                       // --dragbench 同为自测态（分割线拖动的提交成本实测，见 MainWindow.DragBench.cs）：
                       // 漏这一行 ⇒ 测试窗口的几何会在退出时被 SaveWindowGeometry 写进用户 settings.json
                       // （与 --sessiontest 的历史坑同类）。
                       or "--dragbench"
                       // --autodemo 同样是"测试态"：无人值守演示会把自己的窗口几何写进
                       // 用户 settings.json（SaveWindowGeometry 只以 _selfTestMode 把关），
                       // 与 --sessiontest 的历史坑完全同类（docs/45 P1-3）。
                       or _3FCompare.Diagnostics.CrashGuard.AutodemoArg)
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
        RebuildKeyMap();   // 键位表：只在构造与设置被采纳两处重建
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

        _bookmarks.JumpRequested += pos => SafeSeek(pos);
        _offsetPanel.AlignRequested += (_, _) => OnOffsetAlign();
        _offsetPanel.OffsetNudge += delta => OnOffsetNudge(delta);
        _offsetPanel.OffsetReset += (_, _) => OnOffsetReset();

        _sidebar = new ToolsSidebar(_probe, _bookmarks, _offsetPanel, _mediaPanel, _audioPanel);
        // 悬浮传输栏（docs/31 阶段 4.3）由 _floatingTransport 承载；
        // 放大镜（P1-4）同理用独立 owned 顶层窗承载：视频子 HWND 恒盖在主窗自绘内容之上
        // （airspace），挂回 CenterPanel 只会被整块遮住 —— 见 MagnifierOverlayWindow 类注释。
        // XAML 仍声明命名控件（保留生成字段供 SelfTest 引用），但构造时立刻摘出主窗视觉树。
        CenterPanel.Children.Remove(Magnifier);
        _magnifierWindow = new MagnifierOverlayWindow(Magnifier);
        Magnifier.WindowHosted = true;
        Magnifier.HostContainer = CenterPanel;
        Magnifier.PresentationChanged += OnMagnifierPresentationChanged;
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
        // 旧版这里有一条"光标停在浮条上则不收"的例外，专为 ComboBox 下拉兜底（下拉是独立
        // Popup 窗口，展开时主窗会被判失焦）；底栏换成无 Popup 的 chip/栏内候选行后例外已无必要。
        Deactivated += (_, _) => HideFloatingTransport();
        // 恢复底部栏（时间轴 / 状态栏 / 悬浮传输栏）的偏好（docs/31 阶段 4），实现见 ApplyBottomBarVisibility
        ApplyBottomBarVisibility();
        Grid.SelectionChanged += (_, _) => UpdatePanelsForSelection();
        // 占格路数变了 → 延后一拍把"没占格"的那几路停用（不是只隐藏：实测未占格路仍以满速解码+Present）
        Grid.VisibleRoutesChanged += OnVisibleRoutesChanged;
        UpdatePanelsForSelection();

        // 探针/放大镜：隧道指针移动定位命中表面
        CenterPanel.AddHandler(InputElement.PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);

        // 轮询：16ms 播放中 / 250ms 空闲（WinForms PollSnapshots 移植）
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pollTimer.Tick += (_, _) => PollSnapshots();
        _pollTimer.Start();

        // 节流补发：默认 Stop，只在丢弃更新时 Start 一次（见 ApplyViewTransform）。
        _transformFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _transformFlushTimer.Tick += (_, _) => FlushPendingViewTransform();

        // 崩溃自愈：会话快照周期落盘（间隔的取舍见字段注释）
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autosaveTimer.Tick += (_, _) => TryAutosave();
        _autosaveTimer.Start();

        RestoreWindowGeometry();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        // 传输动作的可绑定快捷键必须在**隧道层**截：焦点落在按钮上时，Avalonia 的 Button 会把
        // Space 当成"按下该按钮"并置 Handled ⇒ 窗口级 OnKeyDown（冒泡）根本收不到，
        // 用户看到的正是"空格暂停后再按空格不播了"。文本输入框由 IsTextInputFocused 放行，
        // 否则侧栏搜索框里打不出空格——那是更严重的倒退。
        AddHandler(KeyDownEvent, OnTransportKeyDownTunnel, global::Avalonia.Interactivity.RoutingStrategies.Tunnel);

        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true); // 启用窗口拖放

        // 对比模式的 Win32 裁剪接线（新逻辑全部在 MainWindow.CompareCrop.cs，此处只挂时机）
        WireCompareCrop();

        // 外部色彩变换模块的解锁入口（docs/78）：**只有盘上有那枚模块且它没解锁**时
        // Core 侧才会来请求一次；注册与否就是"有没有人能弹框"——
        // 自测 / 无人值守模式刻意不注册 ⇒ 恒不提示（否则无人值守会被一个模态框挂死）。
        // 请求发生在后台线程上，所以这里负责切回 UI 线程；绝不 await（打开编排的关键路径）。
        if (!_selfTestMode)
        {
            _3FCompare.Core.Backend.ColorTransformUnlock.RegisterRequester(
                () => Dispatcher.UIThread.InvokeAsync(() => Views.UnlockCodeDialog.ShowAsync(this)));
        }

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
        // --comparemodetest <video> [routes=3] [video2 …]：多路对比覆盖层接线验证（docs/26 阶段 3.1）
        // 路数之后的参数是**额外素材**（#29）：给了就按路轮转，内容互异那条判据才有鉴别力；
        // 不给与既往逐字相同（同一份素材重复打开 N 路）。
        else if (args.Length >= 3 && args[1] == "--comparemodetest")
            _ = RunCompareModeTestAsync(args[2],
                args.Length >= 4 && int.TryParse(args[3], out var cr) ? cr : 3,
                args.Length >= 5 ? args[4..] : Array.Empty<string>());
        // --magnifybench <video> <routes> <zoom> [secondsPerPhase=8] [budgetMpx=0(默认闸门)]：
        // 无缝放大的稳态显存/吞吐实测（显存由外部脚本按 PID 采样，见 .review_pr/gpu_probe.ps1）
        else if (args.Length >= 5 && args[1] == "--magnifybench")
            _ = RunMagnifyBenchAsync(args[2],
                int.TryParse(args[3], out var br) ? br : 4,
                double.TryParse(args[4], out var bz) ? bz : 2.0,
                args.Length >= 6 && int.TryParse(args[5], out var bs) ? bs : 8,
                args.Length >= 7 && double.TryParse(args[6], out var bb) ? bb : 0);
        // --dragbench <video> <routes>：分割线拖动的**宿主提交成本**实测（新增档，实现全在
        // MainWindow.DragBench.cs；参数个数判据也写在方法里 ShouldRunDragBench，与上面的白名单同源）。
        else if (ShouldRunDragBench(args))
            _ = RunDragBenchAsync(args[2], int.TryParse(args[3], out var dr) ? dr : 5);
        // ⚠ 参数个数不足的兜底（docs/41 P0-2）：白名单在 cmdArgs.Length >= 3 时就置
        //    _selfTestMode，而各模式的分派要求 4~5 个参数（--screentest>=4、--magnifybench>=5）。
        //    漏了这里 ⇒ 不进入任何自测分支 ⇒ 启动完整 GUI 且永不退出，
        //    而这条路径没有看门狗 ⇒ 门禁看似在跑、实际挂到超时。
        else if (_selfTestMode)
        {
            Console.Error.WriteLine(
                $"自测参数不足：{args[1]} 需要更多参数（argc={args.Length}），未进入任何自测分支");
            ExitSelfTest(2);
        }
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
            await Views.MessageBox.Show(this, LanguageManager.T("Msg_OperationFailed"),
                $"{userMessage}\n\n{ex.Message}",
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
        // 全新一批素材（当前没有任何路）⇒ 清掉上一次的"格→路"映射。
        // 不清的话：上次 4 路 ABCD 轮换到 [1,2,3,0] 后关掉全部文件，再打开 2 个文件时
        // NormalizeRoute(2) 会由旧值拼出 [1,0] ⇒ 全新素材一进来就是左右互换，
        // 而菜单里没有任何勾选反映它（RefreshCompareModeChecks 不刷这一项）。
        if (_sync.Count == 0) _compareRoute = null;

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
        // 崩溃自愈：全部打开完成的瞬间记一次快照——此刻最容易崩（8 路 4K 解码同时起飞），
        // 而 5 秒的定时轮询还没轮到。理由见 TryAutosave 的注释。
        //
        // 「自动进入对比模式」也挂在这个回调上：OpenFiles 是 async void，OpenPaths 末尾的
        // _sync.Count 还是打开前的旧值，在那里调会按错的路数收敛模式。onAllOpened 是唯一
        // "路数已确定"的时机。
        _coordinator.OpenFiles(paths, autoPlay: false, onAllOpened: OnAllFilesOpened);
        UpdateStatus();
    }

    /// <summary>全部文件打开完成后的收尾（快照 + 可选的自动进对比模式）。</summary>
    private void OnAllFilesOpened()
    {
        TryAutosave();

        // 批次打开期间的暂停只为"各路对齐"（见 OpenPaths 里 _sync.Pause() 的注释）。
        // 但全部就绪后**原先没有人恢复播放** ⇒ 拖放/菜单打开的结局是停在暂停态，
        // 用户报的"拖进去完全没反应"就是这个（四次真拖放都走到了 SessionReady routes=2 failed=0）。
        // 2026-09-26 由用户实机确认为已修。
        if (_sync.Count > 0)
        {
            try { _sync.Play(); } catch { }
            try { _sync.RedrawAll(); } catch { }   // 幂等；契约见 SyncController.RedrawAll
            SetPlaying(true);
            ComponentLog.Log(Comp.Engine, "AutoResumeAfterOpen", -1, $"routes={_sync.Count} src=allFilesOpened");
        }
        if (!_settings.AutoEnterCompare) return;

        // 合并态的进入是幂等的：已在该态时直接返回，故"追加文件让路数从 2 变 3"
        // 不会把用户已拖好的分割位置重置成均匀（旧实现要为此先抄一份 keepSplit，
        // 因为 EnterCompareMode 当时无条件写 SplitParams.Default(...)）。
        // 路数变化本身经 SyncCompareModeToRouteCount → Retune 处理：形状不变则权重原样保住。
        EnterCompareMode();
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

    /// <summary>可自定义播放快捷键的分派表：由 <c>AppSettings.KeyBindings</c> 经
    /// <see cref="TransportKeys.BuildMap"/> 解析而来，只在「构造」与「设置被采纳」两处重建。</summary>
    private System.Collections.Generic.Dictionary<(global::Avalonia.Input.Key, global::Avalonia.Input.KeyModifiers), TransportKeys.Slot> _keyMap = new();

    /// <summary>按当前设置重建键位表。只在构造与"设置被采纳"两处调用。</summary>
    private void RebuildKeyMap() => _keyMap = TransportKeys.BuildMap(_settings.KeyBindings);

    /// <summary>命中一个可绑定的传输动作。修饰键位先做掩码，与 <see cref="TransportKeys.BuildMap"/> 同口径
    /// —— 不掩码会出现"存的是 Ctrl+Left、按下算成 Ctrl+Left+附加位"⇒ 用户看到设了却没反应。</summary>
    private bool TryHandleTransportKey(global::Avalonia.Input.Key key, global::Avalonia.Input.KeyModifiers mods)
    {
        if (!_keyMap.TryGetValue((key, TransportKeys.Normalize(mods)), out var slot)) return false;
        switch (slot)
        {
            case TransportKeys.Slot.StepSecondBackward: StepSecondsAsync(-_sync.StepProfile.SecondsStep); break;
            case TransportKeys.Slot.StepSecondForward: StepSecondsAsync(_sync.StepProfile.SecondsStep); break;
            case TransportKeys.Slot.StepFrameBackward: StepFramesAsync(-_sync.StepProfile.FrameStep); break;
            case TransportKeys.Slot.StepFrameForward: StepFramesAsync(_sync.StepProfile.FrameStep); break;
            case TransportKeys.Slot.PlayPause: TogglePlay(); break;
            case TransportKeys.Slot.Stop: StopPlayback(); break;   // 底栏按钮已删 ⇒ 只能由用户自己绑
        }
        return true;
    }

    /// <summary>隧道层的可绑定快捷键分派（挂接处见构造函数注释）。</summary>
    private void OnTransportKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (IsTextInputFocused(e.Source as Visual) || IsTextInputFocused(FocusManager?.GetFocusedElement() as Visual))
        {
            global::_3FCompare.Diagnostics.DropTrace.Line(
                $"[key] {e.Key}+{e.KeyModifiers} 命中键位表但焦点在文本框 ⇒ 放行给输入框");
            return;
        }
        if (!TryHandleTransportKey(e.Key, e.KeyModifiers)) return;
        e.Handled = true;      // 已执行动作 ⇒ 不再让它落到焦点按钮（否则一次空格＝暂停＋点按钮两件事）
    }

    /// <summary>从 <paramref name="start"/> 往上走可视树，看是否落在文本输入控件里。</summary>
    private static bool IsTextInputFocused(Visual? start)
    {
        for (var v = start; v is not null; v = v.Parent as Visual)
            if (v is global::Avalonia.Controls.TextBox) return true;
        return false;
    }

    /// <summary>上一次 DragOver 算出的效果，用来把连续事件压成"只在变化那一拍写日志"。</summary>
    private DragDropEffects? _lastDragOverEffect;

    /// <summary>拖放进入/悬停：只有带文件格式才给 Copy，否则系统光标显示"禁止投放"。
    /// <para>诊断只在**效果发生变化**那一拍写一条 —— DragOver 是连续事件，无条件写日志会把
    /// AppLog 的有界队列刷爆（本仓有组件日志全局节流被刷满导致判据假红的历史）。</para></summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasFile = e.DataTransfer.Contains(global::Avalonia.Input.DataFormat.File);
        e.DragEffects = hasFile ? DragDropEffects.Copy : DragDropEffects.None;
        if (_lastDragOverEffect != e.DragEffects)
        {
            _lastDragOverEffect = e.DragEffects;
            _3FCompare.Core.Diagnostics.AppLog.Info("Drop", $"DragOver 效果={e.DragEffects} 含File格式={hasFile}");
        }
    }

    /// <summary>Avalonia/OLE 这一路的拖放落点（另一路是子 HWND 的 <c>WM_DROPFILES</c>，
    /// 见 <see cref="Controls.PlayerSurface"/>）。
    /// <para><b>为什么这里每条早退都要落日志</b>：拖放有两条入口，而 Explorer 优先走 OLE
    /// —— <c>Avalonia.Win32.dll</c> 里实测有 <c>RegisterDragDrop</c> ⇒ 主窗是注册过的 OLE 目标，
    /// 于是"拖了没反应"可能是①这一路静默早退、②那一路根本没收到消息，**两种在改之前无法区分**
    /// （原先 <c>Contains(File)</c> 为假就直接 return，什么也不留）。现在三条出口各有一条读数。</para></summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(global::Avalonia.Input.DataFormat.File))
        {
            _3FCompare.Core.Diagnostics.AppLog.Warn("Drop", "OnDrop 到达但不含 File 格式 ⇒ 直接返回（不打开任何文件）");
            return;
        }
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        var paths = items?
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Cast<string>()
            .ToList();
        _3FCompare.Core.Diagnostics.AppLog.Info("Drop",
            $"OnDrop：TryGetFiles={items?.Count.ToString() ?? "<null>"} 解析出本地路径={paths?.Count.ToString() ?? "<null>"}" +
            (paths is { Count: > 0 } ? $" 首个={paths[0]}" : ""));
        if (paths is { Count: > 0 })
            OpenPaths(paths);
    }

    // ══════════ 传输栏 ══════════

    private void WireTransport()
    {
        _transport.StepProfileSecondsProvider = () => _sync.StepProfile.SecondsStep;
        _transport.FrameStepProvider = () => _sync.StepProfile.FrameStep;
        // tooltip 里的键位必须跟着用户设置走（写死在文案里就会变成假话）
        _transport.KeyHintProvider = slot => global::_3FCompare.Services.TransportKeys.Display(
            global::_3FCompare.Services.TransportKeys.Get(slot, _settings.KeyBindings));
        _transport.RefreshTips();
        _transport.PlayPauseClicked += (_, _) => TogglePlay();
        // StepFrames 内含 50ms 复测等待（Thread.Sleep）——移到线程池执行，避免阻塞 UI
        _transport.FrameStepClicked += (_, d) => StepFramesAsync(d * _sync.StepProfile.FrameStep);
        _transport.SecondsStepClicked += (_, d) => StepSecondsAsync(d);
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


    /// <summary>网格路数上限。一条 lane 无论有没有素材都算一路，与
    /// <see cref="CompareGridView.SetCount"/> 内部的钳制同值。</summary>
    internal const int MaxLaneCount = 9;

    /// <summary>「加路」要设成的网格路数；返回原值即表示已在 <see cref="MaxLaneCount"/> 上限，
    /// 调用方必须给可见反馈（不静默）。</summary>
    /// <remarks><b>锚点取的是网格路数，不是会话数</b>：素材只占前 <c>_sync.Count</c> 条 lane，
    /// 尾部允许存在空 lane（冷启动的默认形态就是 2 条空 lane）。按会话数算时，只要已经有空 lane，
    /// 「加路」就会把 <c>SetCount</c> 调小 ⇒ 加路键变成减路键，还会连带撤掉空 lane。</remarks>
    internal static int LaneCountAfterAdd(int laneCount) => Math.Min(MaxLaneCount, laneCount + 1);

    /// <summary>「减路」该撤什么：尾部是空 lane 时只撤 lane（素材一路都不动），
    /// 没有空 lane 才撤最后一路素材，两者皆零则不动作。
    /// <para><c>public</c> 而非 <c>internal</c>：单测要把它当 <c>[Theory]</c> 的参数类型，
    /// 而公开方法的签名不允许暴露 internal 类型（CS0051）。</para></summary>
    public enum LaneRemoveAction { None, DropEmptyLane, DropLastRoute }

    /// <summary>减路的决策。<see cref="LaneRemoveAction.DropEmptyLane"/> 与
    /// <see cref="LaneRemoveAction.DropLastRoute"/> 的区分见 <see cref="LaneCountAfterAdd"/>。</summary>
    /// <remarks>不变量：<b>素材始终占前 <c>routeCount</c> 条 lane</b> —— 会话只在下标
    /// <c>_sync.Count</c> 上追加（见 <c>PlaybackCoordinator</c> 的 <c>_surfaceAt(_sync.Count)</c>），
    /// 减路只从尾部摘（<see cref="SyncController.RemoveSlotAt"/> 的唯一调用点），
    /// <c>SwapSlots</c> 换的是槽位次序、不改变"前 routeCount 条 lane 有素材"这一点。
    /// 于是"尾部是否空 lane"等价于 <c>laneCount &gt; routeCount</c>，而
    /// <see cref="CompareGridView.SetCount"/> 撤的恰是最后一条 lane ⇒ 两个方向对齐，
    /// 「加路 / 减路」才互为逆运算。旧实现按会话数锚定时，撤一条空 lane 会把最后一路素材
    /// 连同它的解码位置与偏移一起销毁（真机复现见 <c>AssertLaneAddRemoveContractAsync</c>）。</remarks>
    internal static LaneRemoveAction PlanLaneRemove(int laneCount, int routeCount)
    {
        if (laneCount > routeCount) return LaneRemoveAction.DropEmptyLane;
        if (routeCount > 0) return LaneRemoveAction.DropLastRoute;
        return LaneRemoveAction.None;
    }

    /// <summary>加路撞上限的反馈。走 <see cref="NotifyRouteLimit"/> 同一套"必须看得见"的口径：
    /// 旧实现这里连状态栏都不写，而且因为锚点取错，还会反过来把网格缩小。</summary>
    private void NotifyLaneLimit()
    {
        var msg = Loc($"已达 {MaxLaneCount} 路上限，无法再加路。",
                      $"Limit of {MaxLaneCount} lanes reached; cannot add more.");
        StatusInfo.Text = msg;
        _3FCompare.Core.Diagnostics.AppLog.Warn("Lane", msg);
    }

    private void AddSlotPlaceholder()
    {
        // ── 左右拉动下「加路」= 选择文件并**真正多开一路**（用户 2026-10-07 决策）──
        // 为什么在拉动下改语义：用户报的原始症状是"在左右拉动对比的时候，加路功能失效"，
        // 而他要的是"这个对比也应该支持多路"。在拉动下只加一条**空** lane 是没有意义的
        // （拉动每条带都要显示一路画面，空 lane 没有画面可显示）——
        // 所以拉动下这个键的语义是"再加一路素材进来对比"，与网格下"扩一条空 lane"不同。
        // 两者的分野在注释与自测里都写明，避免下次有人按网格的契约去改这一支。
        if (_compareOverlayActive)
        {
            if (_sync.Count >= _3FCompare.Services.PlaybackCoordinator.MaxRoutes)
            {
                NotifyRouteLimit(0, 1);
                return;
            }
            _ = AddRouteViaPickerAsync();
            return;
        }

        var target = LaneCountAfterAdd(Grid.Count);
        if (target == Grid.Count)
        {
            NotifyLaneLimit();
            return;
        }
        Grid.SetCount(target, _realMode);
        UpdateStatus();
    }

    /// <summary>拉动下「加路」的实现：弹文件选择 → 经既有打开路径**追加**一路 → 让新路拿到自己那条带。
    ///
    /// <para><b>为什么复用 <c>OpenPaths</c> 而不是自己建会话</b>：打开编排涉及配额、原子闸门、
    /// 失败归还、自动播放意图、会话快照落盘等一整套逻辑（<c>PlaybackCoordinator</c> 的
    /// <c>OpenFilesCore</c>），自己再写一份必然与它漂移。既有路径本身就是**追加**语义
    /// （<c>ComputeOpenCount</c> 对 <c>existing</c> 取余量），正是这里要的。</para>
    ///
    /// <para><b>顺序</b>：先开文件（会话进来会经 <c>UpdateStatus → SyncCompareModeToRouteCount</c>
    /// 收敛路数），再扩带表 —— 于是"新路有带可落"与"带数跟着路数"两件事由既有汇聚点收口，
    /// 不必在这里手工同步两份计数。</para></summary>
    private async System.Threading.Tasks.Task AddRouteViaPickerAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LanguageManager.T("Pb_AddRoute"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Media")
                    {
                        Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.webm", "*.avi", "*.ts", "*.m2ts", "*.flv", "*.wmv" },
                    },
                    FilePickerFileTypes.All,
                },
            });
            if (files is not { Count: > 0 }) return;   // 用户取消：不动现状

            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList();
            if (paths.Count == 0) return;

            OpenPaths(paths);
        }
        catch (Exception ex)
        {
            await ReportErrorAsync("Lane.AddRoute",
                Loc("加路失败。", "Failed to add a route."), ex);
        }
    }

    /// <summary>加路后该 lane 是否真的能在拉动里看到（用它自己的带去承载）。
    /// <para>带表未被用户指定（恒等）时新 lane 必有自己那条带 ⇒ 恒 true；
    /// 被指定过则不自动改写，返回它在不在表里。</para></summary>
    private bool IsBandVisibleAfterAdd(int laneCount)
    {
        var routes = WipeRoutes;
        if (routes is null || routes.Length == 0) return true;   // 恒等：第 i 条带就是第 i 路
        var lane = laneCount - 1;
        foreach (var r in routes) if (r == lane) return true;
        return false;
    }

    private void RemoveLastSlot()
    {
        switch (PlanLaneRemove(Grid.Count, _sync.Count))
        {
            case LaneRemoveAction.DropEmptyLane:
                // 撤的是尾部的空 lane：素材、偏移、播放位置一律不动。
                Grid.SetCount(Grid.Count - 1, _realMode);
                UpdateStatus();
                return;
            case LaneRemoveAction.DropLastRoute:
                Grid.GetSurface(_sync.Count - 1)?.DetachSession();
                _sync.RemoveSlotAt(_sync.Count - 1);
                // 此分支下 laneCount == routeCount，故 _sync.Count 恰为撤一路后的网格路数；
                // 万一不变量被破坏（网格少于路数），这里会把网格补回与会话数一致。
                Grid.SetCount(_sync.Count, _realMode);
                UpdateStatus();
                return;
        }
    }

    // ══════════ 多路对比：布局覆盖层（阶段 3.1 接线） ══════════
    //
    // anchor 取 Grid（CompareGridView）：它就是承载 1~9 路 PlayerSurface 的对比区容器，
    // 也是 LayoutOverlayWindow 文档里点名的默认 anchor。CenterPanel 虽包住它，但还叠放了
    // Magnifier 覆盖层，覆盖矩形会包含这些非对比区，故不选。

    /// <summary>进入网格对比（合并态，快捷键 <c>G</c> / <c>V</c> 与菜单同一入口）：
    /// 按当前路数解析形状后，在对比区之上显示分割线覆盖层。
    ///
    /// <para><b>合并了什么</b>：旧的"标准模式（均匀网格、全部路显示、<b>不可拖</b>）"与
    /// "A/B 可拖动对比（AB/ABC/ABCD 固定形状、可拖、<b>只显示前 2~4 路</b>）"合成一项。
    /// 基准取网格的渲染覆盖（一路一格，5~9 路全部显示、全部启用），再挂上可拖的分割线
    /// （内部竖线 <c>cols-1</c> 条 + 横线 <c>rows-1</c> 条，最多 4 条）。形状仍由
    /// <see cref="GridLayout.ResolveGrid"/> 按路数与预设解析，各列/行占多宽由
    /// <see cref="GridPartition"/> 的权重决定。</para>
    ///
    /// <para><b>已是该态时不重进</b>：重进会把权重重置为均匀，用户拖好的分格宽度不该因为
    /// 再按一次键被抹掉。路数变化走 <see cref="SyncCompareModeToRouteCount"/> 的
    /// <see cref="GridPartition.Retune"/>，形状不变时同样保住权重。</para>
    ///
    /// <para><b>覆盖层不健康就退回均匀网格</b>：<see cref="VerifyOverlayDegradation"/> 是既有的
    /// fail-fast —— 命中钩子没装上会吞掉整块对比区的鼠标，透明没拿到会盖住画面，
    /// 两者都比"没有分割线"严重得多，故撤掉本态回到默认视图（旧实现退回的也是这个状态）。</para></summary>
    internal void EnterCompareMode()
    {
        // 左右拉动与本态互斥：它要"两路铺满 + 区域裁半区"，本态要"一路一格"。
        ExitCompareOverlay();

        // 两个计数各有职责，别混：**素材路数**决定"能不能对比"（少于 2 路没有对比对象），
        // **网格 lane 数**决定铺几格（合并态取网格的渲染覆盖为基准：一条 lane 一格，
        // 空占位 lane 也占格 —— 与今天的均匀网格完全同形，这是"以网格为基准"的字面含义）。
        if (_sync.Count < 2) return;   // 不足 2 路素材：没有可对比的对象
        if (_compareActive) return;    // 幂等：保住用户拖好的权重

        var (pc, pr) = GridLayout.OverrideOf(Grid.Preset);
        _partition = GridPartition.Resolve(Grid.Count, Grid.SingleView, pc, pr);
        _compareActive = true;

        // 组件日志：对比模式进入（含解析出的形状与当时的 lane/路数）。
        // 紧随其后的 VerifyOverlayDegradation 可能立刻降级退出 —— 那时日志里会看到
        // Enter 紧跟 Exit，这正是"覆盖层 fail-fast"的时序证据。
        ComponentLog.Log(Comp.Overlay, "CompareModeEnter", -1,
            $"grid={_partition.Cols}x{_partition.Rows} cells={_partition.CellCount} " +
            $"lanes={Grid.Count} routes={_sync.Count} preset={Grid.Preset}");

        if (_layoutOverlay is null)
        {
            _layoutOverlay = new LayoutOverlayWindow();
            _layoutOverlay.PartitionChanged += OnPartitionChanged;
            // 自由拼接的线段拖动是**另一条通道**（线段模型 ≠ 分区模型，见该事件的注释）。
            // 不接这一条，自由拼接的线就只是画出来、能命中、却拖不动。
            _layoutOverlay.FreeformLineDragged += OnFreeformLineDragged;
        }
        _layoutOverlay.ShowOverlay(this, Grid, ActivePartition());

        // 覆盖层只画线；真正的画面重排由 Grid 的格表覆盖驱动（见 ApplyCompareLayout）。
        // 放在 VerifyOverlayDegradation 之前：后者若 fail-fast 退出，会由 ExitCompareMode 置回 null。
        ApplyCompareLayout();

        // 裁剪区域依赖格表与实际排布（Bounds），此刻布局尚未跑 ⇒ 延后一拍下发（合并去重）。
        // 若紧接着被 VerifyOverlayDegradation 降级退出，那一拍会因 _compareActive=false 而走清除路径。
        ScheduleCompareCrop();

        // B3：ShowOverlay 之后必须立刻消费覆盖层上报的两个诊断量，失败即降级（见方法注释）。
        VerifyOverlayDegradation();
    }

    /// <summary>当前该由覆盖层呈现、并由拖动回灌的那份分区：左右拉动用它自己的单条揭示线，
    /// 其余情况就是网格分区。<para>只有一处"选哪份"的判断，避免绘制、命中、回灌三条路径各写一遍
    /// （写歪的表现是"拖动动了网格，画出来的线却停在揭示位"）。</para></summary>
    private GridPartition ActivePartition() => _compareOverlayActive ? _wipePartition : _partition;

    /// <summary><b>测量用的排布对照开关</b>（只在 <c>FC_NOMOSAIC=1</c> 时生效）：把网格对比退回
    /// "窗口 == 格"的历史形态，用来在<b>同一份二进制</b>上量"改成拼合到底改变了什么"
    /// （每拍 present 的像素、出帧率、崩溃产出率）。与 <c>--magnifybench</c> 的
    /// <c>_3FC_BENCH_MAXIMIZE</c> 同族（docs/50 §八 M12/M13）。
    /// ⚠ 这不是产品选项，也不参与判据验收：拼合专属的那条判据（<c>view.mosaic-tile</c>）在
    /// 本开关打开时明写"不适用"而不是悄悄通过，其余判据按 <c>Grid.MosaicWindows</c> 自己换口径。
    /// 默认关 ⇒ 产品路径一次布尔判断，零行为改动。</summary>
    private static readonly bool BenchNoMosaic =
        System.Environment.GetEnvironmentVariable("FC_NOMOSAIC") == "1";

    /// <summary><b>测量档：跳过放大镜的"注入 Below + 自愈"那一段</b>（只在 <c>FC_SKIP_ZINJECT=1</c> 生效）。
    ///
    /// <para>为什么要这个开关：要判"是不是动 owned 覆盖窗把 dxgi 那条带点着了"，只能单变量地
    /// 把它关掉比产出率（见 docs/50 §十八）。⚠ 当初立这条的理由写的是"崩溃锁在开轮后 6.8 秒"——
    /// 那个理由<b>已被否证</b>：7.8 秒是整轮的长度，干净会话同样停在 7.8/8.2 秒，秒数不是定位。
    /// 开关本身保留（它已被量出否证结论，结论需要可复现的仪器），但不再当作候选机制。</para>
    ///
    /// <para>与 <c>FC_NOMOSAIC</c> 同族：只为测量存在，不是产品选项；默认关 ⇒ 产品路径零改动，
    /// 打开时那一步会被登记成<b>未判</b>而不是悄悄通过。</para></summary>
    private static readonly bool BenchSkipMagnifierZInject =
        System.Environment.GetEnvironmentVariable("FC_SKIP_ZINJECT") == "1";

    /// <summary><b>测量档：退出对比模式时不隐藏布局覆盖层</b>（只在 <c>FC_KEEP_LAYOUTOVERLAY=1</c> 生效）。
    ///
    /// <para>为什么靶在这里：§二十 把死亡位置钉到了 <c>entry.grid</c>（<c>GridEntryUnit</c>）
    /// 退出侧的尾端 —— 那一段最后一条组件事件固定是 <c>OverlayZOrder where=HideOverlay</c>，
    /// 而<b>发这行的是 <see cref="LayoutOverlayWindow"/>，不是放大镜</b>（§十八 的归属更正）。
    /// 要单变量判"是不是这个 owned 覆盖窗的 Hide 点了着 dxgi 那条带"，只能把它按住不动。</para>
    ///
    /// <para>与 <c>FC_NOMOSAIC</c> / <c>FC_SKIP_ZINJECT</c> 同族：只为测量存在，默认关 ⇒ 产品路径零改动。
    /// 打开时"退出后覆盖层已隐藏"那两条判据会登记成<b>未判</b>而不是悄悄通过（见 SelfTest 的两处
    /// <c>RecordSelfTestSkip</c>），并由 <c>[Overlay] KeepLayoutOverlay</c> 见证行自证臂真生效。</para></summary>
    internal static readonly bool BenchKeepLayoutOverlayOnExit =
        System.Environment.GetEnvironmentVariable("FC_KEEP_LAYOUTOVERLAY") == "1";

    private static bool _keepLayoutOverlayWitnessed;

    /// <summary><b>测量档：把每拍 9 连发的 <c>SetWindowRgn</c> 之间插一段间隔</b>
    /// （<c>FC_REGION_SPACING_MS=N</c>，N∈[1,200]；未设或 0 ⇒ 与产品完全一致）。
    ///
    /// <para>为什么靶换成这里：<c>death-localize.py</c> 按 pid 切段读逐行日志后，死者的末条动作
    /// 全部落在"一次派发里对多路子窗口连做窗口状态/区域操作"这一段 —— 9 条 <c>RegionApply</c>
    /// 挤在 4 ms 内、或 <c>RouteActivity 启用 9/9 路</c> 之后数毫秒。窗口区域与 flip-model 交换链
    /// 的交互本就是本项目已知高风险面（见 <c>ApplyCropRegion</c> 注释），而"背靠背连发"正是它唯一
    /// 可单变量拆出来的那一半。</para>
    ///
    /// <para>只为测量存在：默认 0 ⇒ 产品路径是一次整数比较，零行为改动；>0 时会在组件日志落一条
    /// <c>RegionSpacing ms=N</c> 见证行，A/B 两侧靠这条行自证臂有没有生效（<b>不靠"没崩"反推臂生效</b>）。
    /// ⚠ 间隔是在 UI 线程上 <c>Thread.Sleep</c>，会真实拖慢那一拍派发 —— 这是测量代价，不是产品形态。</para></summary>
    internal static readonly int BenchRegionApplySpacingMs = RegionSpacingFromEnv();

    private static int RegionSpacingFromEnv()
    {
        var raw = System.Environment.GetEnvironmentVariable("FC_REGION_SPACING_MS");
        return int.TryParse(raw, out var v) && v > 0 && v <= 200 ? v : 0;
    }

    /// <summary>把当前分区换算成归一化格表交给 <see cref="CompareGridView"/>，
    /// 让分割线拖动真正改变画面排版（覆盖层自己那几条线只是装饰，格表才驱动子 HWND 重排）。
    ///
    /// <para>未激活对比时传 <c>null</c> ⇒ Grid 回到既有均匀 N×M 逻辑，
    /// 普通网格浏览路径行为完全不变。</para>
    ///
    /// <para><b>合并态下格数 ≡ 路数</b>（一路一格），于是"只显示前 N 路、其余隐藏并停用"
    /// 这件事在本态里不会发生；该语义只在左右拉动（恒两格、其余路隐藏）时保留。</para>
    ///
    /// <para><b>左右拉动</b>：它是"N 路铺满 + 区域裁各自那条带"的呈现变体（见
    /// <c>MainWindow.CompareOverlay.cs</c>），故激活时格表是"路数份全幅"，
    /// 长度 = 参与对比的路数（旧实现长度恒 2 ⇒ 第 3 路起被隐藏，用户点「加路」看不到变化）。</para></summary>
    private void ApplyCompareLayout()
    {
        var overlay = CompareOverlayActive;
        Grid.CellOverride = !_compareActive
            ? null
            : overlay
                ? CompareOverlayLayout(OverlayWindowCount, WipeMosaicActive)
                : _partition.ComputeCells();

        // 拼合（mosaic）：网格对比态下每路子窗口都铺满对比区，格表只用来决定"这一路露出哪一块"。
        // 这是本模式的语义本身 —— 几块拼起来是<b>一整幅完整画面</b>，拖分割线改变的是
        // "哪一块归哪一路"，画面不被逐格缩小。左右拉动不用它（它有自己的两路铺满 + 互补半区）。
        // ⚠ 代价：各路 backbuffer 从"格大小"变成"对比区大小" ⇒ 总交换链面积 = 路数 × 对比区。
        // 对照开关见 BenchNoMosaic：量"拼合到底改变了什么"用的测量臂，不是产品选项。
        Grid.MosaicWindows = _compareActive && !overlay && !BenchNoMosaic;

        // 格→路映射。叠加模式下不生效：它的"露出哪一路"由窗口区域（互补半区）表达，
        // 再叠一层格映射会让两个机制互相打架（见 OnSwapCompareCells 的守卫）。
        Grid.CellRoute = !_compareActive || overlay
            ? null
            : NormalizeRoute(_partition.CellCount);

        // 组件日志：格表重排是"结构变化"（不是逐帧），值得逐次留痕。
        // 叠加开/关单独记一条跃迁事件——它把窗口区域的语义从"裁出格"换成"互补半区"，
        // 是裁剪/呈现相关崩溃的高嫌疑前置事件，混在通用 LayoutApplied 里不易检索。
        if (ComponentLog.IsEnabled)
        {
            if (overlay != _lastOverlayActive)
                ComponentLog.Log(Comp.Overlay, overlay ? "OverlayOn" : "OverlayOff", -1,
                    $"grid={_partition.Cols}x{_partition.Rows}");

            _lastOverlayActive = overlay;
            ComponentLog.Log(Comp.Overlay, "LayoutApplied", -1,
                $"active={_compareActive} overlay={overlay} grid={_partition.Cols}x{_partition.Rows} " +
                $"cells={_partition.CellCount} mosaic={Grid.MosaicWindows} " +
                $"boundaries={DescribeBoundaries(_partition)}" + StepTag());
        }

        // 布局落过地 ⇒ 刷新原生崩溃取证的模块表（内部按 2 秒节流，非 bench/未装时是一次布尔判断）。
        // 为什么挂这里：`dxgi.dll` / 内核呈现相关模块都在**第一次合成**之后才加载，而表建在
        // Program.Main 早期；不刷新就会把崩溃带里最有信息量的那一格（IP 落在哪个模块）
        // 稳定读成 outside（2026-10-01 实测到过一次，见 NativeFaultRecorder.MaybeRefreshTable）。
        _3FCompare.Diagnostics.NativeFaultRecorder.MaybeRefreshTable();
    }

    /// <summary>把分区的全部边界位置压成一行日志文本（拖动 / 换形状都读得回具体几何，
    /// 而不是只看到"模式变了"）。</summary>
    private static string DescribeBoundaries(GridPartition p)
    {
        var handles = p.HandlePositions();
        if (handles.Length == 0) return "none";
        var parts = new string[handles.Length];
        for (var i = 0; i < handles.Length; i++)
            parts[i] = $"{(p.HandleAxisAt(i) == SplitAxis.X ? 'v' : 'h')}{handles[i].X:0.###},{handles[i].Y:0.###}";
        return string.Join(" ", parts);
    }

    /// <summary>格 → 路的映射（<c>route[格号] = 路号</c>）；<c>null</c> = 恒等（第 i 格显示第 i 路）。
    ///
    /// <para><b>为什么是"格 → 路"而不是"路 → 格"</b>：格数可以小于路数（4 路用 AB 只有 2 格），
    /// 于是"每一路对应哪一格"对多出来的路没有答案，而"每一格对应哪一路"恒有答案。
    /// 反向查询由 <c>CompareGridView.CellIndexOfRoute</c> 提供。</para>
    ///
    /// <para><b>不随退出对比模式清除</b>：互换是一种呈现偏好，退出再进入应当保持，
    /// 否则用户每次按 V 都要再点一次互换。</para></summary>
    private int[]? _compareRoute;

    /// <summary>把 <see cref="_compareRoute"/> 归一到"格数 == <paramref name="cellCount"/>"的形态；
    /// 结果为恒等时返回 <c>null</c>（让 Grid 走与改动前逐字一致的路径）。
    ///
    /// <para><b>为什么需要归一化</b>：模式切换会改变格数（AB 2 格 → ABC 3 格），旧映射的长度
    /// 与值域都可能不再合法 —— 长度不符会让 <c>cells[route]</c> 越界、值域超出路数会让某一格
    /// 空白、重复路号会让一路占两格而另一路彻底消失。三者都必须在这里挡住，不能指望调用方。</para>
    ///
    /// <para>缺失的格按恒等补齐（第 cell 格显示第 cell 路），冲突时取"第一个尚未被占用的路号"
    /// —— 结果恒为 <c>[0, 路数)</c> 内的一个排列（格数 ≤ 路数时）。</para></summary>
    private int[]? NormalizeRoute(int cellCount)
    {
        if (_compareRoute is null || cellCount <= 0) return null;

        var routeCount = Math.Max(1, Grid.Count); // 合法路号集合 = [0, 路数)
        var result = new int[cellCount];
        var used = new bool[Math.Max(routeCount, cellCount)];
        var identity = true;

        for (var cell = 0; cell < cellCount; cell++)
        {
            var r = cell < _compareRoute.Length ? _compareRoute[cell] : cell;
            if (r < 0 || r >= routeCount || used[r])
                r = FirstUnusedRoute(used, routeCount, cell);

            result[cell] = r;
            if (r >= 0 && r < used.Length) used[r] = true;
            if (r != cell) identity = false;
        }

        return identity ? null : result;
    }

    /// <summary>从 <paramref name="preferred"/> 开始找第一个尚未被占用的路号；找不到（路数 &lt; 格数，
    /// 正常路径不会发生）就退回 <paramref name="preferred"/>，保证返回值始终在 [0, 路数) 内。</summary>
    private static int FirstUnusedRoute(bool[] used, int routeCount, int preferred)
    {
        for (var i = preferred; i < routeCount; i++) if (!used[i]) return i;
        for (var i = 0; i < routeCount && i < preferred; i++) if (!used[i]) return i;
        return preferred < routeCount ? preferred : 0;
    }

    /// <summary>「左右互换」入口（菜单「视图 → 对比选项 → 左右互换（AB）」）。
    ///
    /// <para><b>语义</b>：交换<b>前两格</b>所显示的路。AB 下就是左右互换、ABC 下是 A 与 B 互换、
    /// ABCD 下是左上与右上互换 —— 一律是"视觉上相邻的前两格"，与模式无关。
    /// 它<b>不改格数、不改分割位置、不换模式</b>，只是把两路画面调个位置。</para>
    ///
    /// <para><b>为什么不动 <c>surface↔slot</c> 映射</b>：路号还牵着同步、偏移、选中、媒体信息面板，
    /// 换它会连带把"第 1 路"的偏移一起搬走，那是"改了数据"而不是"改了呈现"。
    /// 只换格→路的映射，则路号语义全部保持不变，撤销也只是再点一次。</para></summary>
    private void OnSwapCompareCells(object? sender, RoutedEventArgs e)
    {
        if (!_compareActive || CompareOverlayActive) return; // 叠加下互换会与揭示区打架

        var cellCount = _partition.CellCount;
        if (cellCount < 2) return;

        var route = NormalizeRoute(cellCount) ?? IdentityRoute(cellCount);
        (route[0], route[1]) = (route[1], route[0]);
        _compareRoute = route;

        ApplyCompareLayout();
        // 格换了 ⇒ 各路窗口要挪到新格 ⇒ 裁剪矩形必须重算（等布局落地后下发）
        ScheduleCompareCrop();
        RefreshCompareModeChecks();
        StatusInfo.Text = Loc("已互换前两格的画面（再点一次还原）。",
                              "Swapped the first two cells (click again to restore).");
    }

    /// <summary>「轮换画面」入口：每一格改显示<b>下一路</b>（在 [0, 路数) 上循环）。
    ///
    /// <para><b>为什么用它而不是"每格下拉框选源"</b>：下拉框要放在覆盖窗口上，而"覆盖窗口上的
    /// ComboBox 能否正常交互"在本项目<b>从未实测</b>（docs/30:108/135、docs/31:361-363、docs/42:63
    /// 三项均列为待做）。轮换用同一个按键就能遍历全部组合 —— 2 路时它就是互换，
    /// 4 路用 AB 时连点可依次看到 (1,2) (2,3) (3,0) —— 既覆盖了"任意路 → 任意格"的实际需求，
    /// 又不引入那块未验证的风险面。等真机验过覆盖窗口的下拉交互，再升级成完整下拉框。</para>
    ///
    /// <para><b>为什么是"路号 +1"而不是"格左移"</b>：路号在 [0, 路数) 上循环，于是格数少于路数时
    /// （4 路用 AB）也能轮换到<b>当前没在显示的</b>那些路；按格左移只能在已显示的路之间转圈，
    /// 永远看不到第 3、4 路。而 <c>(a+1) % n</c> 对不同 a 仍互不相同 ⇒ 结果恒为合法排列。</para></summary>
    private void OnRotateCompareCells(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_compareActive || CompareOverlayActive) return; // 叠加下不动（同互换）

        var cellCount = _partition.CellCount;
        var routeCount = Math.Max(1, Grid.Count);
        if (cellCount < 2 || routeCount < 2) return;

        var route = NormalizeRoute(cellCount) ?? IdentityRoute(cellCount);
        for (var c = 0; c < route.Length; c++)
            route[c] = (route[c] + 1) % routeCount;

        _compareRoute = route;
        ApplyCompareLayout();
        ScheduleCompareCrop();
        RefreshCompareModeChecks();
        StatusInfo.Text = Loc($"已轮换各格显示的画面（当前 [{(string.Join(", ", route))}]）。",
                              $"Rotated the source shown in each cell (now [{string.Join(", ", route)}]).");
    }

    /// <summary>恒等映射（第 i 格显示第 i 路）。</summary>
    private static int[] IdentityRoute(int cellCount)
    {
        var r = new int[cellCount];
        for (var i = 0; i < cellCount; i++) r[i] = i;
        return r;
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
            $"grid={_partition.Cols}x{_partition.Rows} overlay={CompareOverlayActive}");
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
        if (BenchKeepLayoutOverlayOnExit)
        {
            // 测量档：按住这次 Hide，让"覆盖窗 Hide 是不是激励"能被单变量量出来（默认关 ⇒ 永不进这里）。
            if (!_keepLayoutOverlayWitnessed)
            {
                _keepLayoutOverlayWitnessed = true;
                ComponentLog.Log(Comp.Overlay, "KeepLayoutOverlay", -1,
                    $"skippedHide=True overlay={_layoutOverlay is not null}");
            }
        }
        else
        {
            _layoutOverlay?.HideOverlay();
        }
    }

    /// <summary>覆盖层拖动回调：回灌新分区（覆盖层已做乐观本地更新，这里只同步宿主状态）。
    /// <para>这是"拖动真正改变画面"的关键一环：回灌 ⇒ 重算格表 ⇒ Grid 重排 ⇒ 视频格跟手。
    /// 覆盖层线条本就由它自己的本地乐观值绘制，两者共用同一份
    /// <see cref="GridPartition.ComputeCells"/> 结果，不会漂移。</para>
    /// <para>左右拉动时回灌的是它自己那条揭示线（<see cref="_wipePartition"/>），
    /// 网格的分格宽度不参与 —— 否则退出拉动会发现网格被揭示位置顶掉了。</para></summary>
    private void OnPartitionChanged(GridPartition partition)
    {
        // dragbench 计时入口（非 bench 态立即返回 0，一次布尔判断而已）：
        // 本站点的 16ms 门槛背书的正是下面这三件事的成本，实测档见 MainWindow.DragBench.cs。
        var dragBenchStart = DragBenchEnterCommit();

        if (_compareOverlayActive) _wipePartition = partition;
        else _partition = partition;

        // ⚠ 自由拼接形态下覆盖层报回的仍是"它自己那份兼容分区"，而真正的几何是块表。
        //    若在这里把分区当成真值，就会出现"拖了线、区域没跟着变"（两处各存一份几何）。
        //    故自由拼接下**只刷新区域**，不动块表 —— 块表由 DragWipeLine 按线段语义更新。
        if (_compareOverlayActive && WipeMosaicActive)
        {
            ComponentLog.Log(Comp.Overlay, "SplitChanged", -1,
                $"freeform lines={WipeMosaic!.LogicalLines().Length}");
            ScheduleCompareCrop();
            DragBenchExitCommit(dragBenchStart);
            return;
        }

        // 组件日志：分割变化（拖动中可能连续触发）。它是裁剪重算的直接诱因，
        // 必须与随后的 RegionApply/RegionClear 成对出现才能还原时序。
        ComponentLog.Log(Comp.Overlay, "SplitChanged", -1,
            $"grid={partition.Cols}x{partition.Rows} boundaries={DescribeBoundaries(partition)}");
        _layoutOverlay?.Update(partition);
        ApplyCompareLayout();
        // 分割参数变了 ⇒ 格表变了 ⇒ 裁剪矩形要重算（同样等布局落地后再下发）
        ScheduleCompareCrop();

        DragBenchExitCommit(dragBenchStart);
    }

    /// <summary>路数变化时把分区收敛到新形状（<see cref="GridPartition.Retune"/>）；
    /// 路数掉到 2 以下、或对比区本身不再存在时直接退出。
    /// <para>幂等：分区未变时不做任何事，可安全地每次 UpdateStatus 都调一次。</para>
    /// <para><b>保住权重是这条路径的主要职责</b>：加一路就把用户拖好的分格宽度抹掉，
    /// 读起来像"模式自己跳回去了"。Retune 在形状不变时原样搬运权重，只有形状真的变了
    /// （2 路左右 → 3 路三列）才退回均匀。</para></summary>
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

        // 素材少于 2 路就没有对比对象（lane 数可以更多，那只是空占位格）。
        if (_sync.Count < 2)
        {
            ExitCompareMode();
            return;
        }

        var (pc, pr) = GridLayout.OverrideOf(Grid.Preset);

        // 左右拉动下带表必须跟着 lane 数长 —— 这是"加路不再失效"的另一半：
        // OpenPaths 会把 Grid.Count 扩到能容纳新路（否则 surface 不够、打开被静默跳过），
        // 而带表若不跟着长，新 lane 就落在格表之外、被 ArrangeOverride 隐藏 ⇒ 又是"加了看不到"。
        // 收敛点放这里（与上面那句"UpdateStatus 是全部路数变更路径的共同汇聚点"同源），
        // 而不是只挂在 AddSlotPlaceholder 上 —— 因为**拖放 / 菜单打开 / 会话重载**同样会让路数变。
        //
        // ⚠ 必须用**只改数据、不做布局**的版本：本方法由 UpdateStatus 调用，
        //    而完整的 GrowWipeBandsTo 会调 ApplyCompareLayout + ScheduleCompareCrop
        //    ⇒ 等于在"刷状态栏"里做布局并下发裁剪，重入且时序危险。
        //    真机实测后果：启动 5 路档时进程直接原生崩溃（baseline 3/3 不崩、改后 3/3 崩）。
        //    布局那部分交给本方法末尾既有的 ScheduleCompareCrop（它本就延后一拍）。
        if (_compareOverlayActive && !WipeMosaicActive && Grid.Count > WipeBandCount)
            GrowWipeBandsDataOnly(Grid.Count);

        // 左右拉动期间网格分区不参与呈现（揭示线是另一份），但形状仍要跟 lane 数收敛，
        // 否则退出拉动时会拿到一份"格数与 lane 数不符"的旧分区。
        var retuned = _partition.Retune(Grid.Count, Grid.SingleView, pc, pr);
        if (retuned.SameShape(_partition)) return;
        _partition = retuned;
        _layoutOverlay.Update(ActivePartition());
        // 形状与格数都变了，必须同步重排画面并让裁剪跟着走。
        ApplyCompareLayout();
        ScheduleCompareCrop();
    }

    /// <summary>换分格朝向（快捷键 C）：未进入则进入，已进入则在
    /// <see cref="GridPartition.ShapesFor"/> 的候选里换到下一个（2 路：左右↔上下；
    /// 3 路：三列↔三行↔2×2；4 路：2×2↔四列↔四行；5~6 路：3×2↔2×3；7~9 路只有 3×3）。
    ///
    /// <para><b>不再"循环到末项即退出"</b>：旧实现把退出藏在 C 的末项里，是因为当时
    /// "退出对比 = 回均匀网格"是唯一的退出口。合并后退出由 G 的开关语义负责（键是开关、
    /// 菜单项是进入，与左右拉动同一约定），C 只管换朝向 ⇒ 用户不必靠多按几次来退出，
    /// 也不会"想换朝向却把模式关掉了"。</para>
    ///
    /// <para>换朝向会把权重重置为均匀（形状都换了，旧权重没有对应的列可落），
    /// 这是刻意的一致性取舍，状态栏会把这件事说出来。</para></summary>
    private void CycleCompareShape()
    {
        if (_sync.Count < 2)
        {
            // 0/1 路：没有可对比的对象，给出可见反馈（不静默）。
            StatusInfo.Text = Loc("网格对比需要至少 2 路视频。", "The grid compare view needs at least 2 lanes.");
            return;
        }

        if (!_compareActive)
        {
            EnterCompareMode();
            if (_compareActive) NotifyCompareMode();
            return;
        }

        if (_compareOverlayActive)
        {
            // 左右拉动只有一条揭示线，"朝向"由它的 axis 开关决定，不参与这里的循环。
            NotifyCompareOverlay();
            return;
        }

        _partition = _partition.NextShape();
        _layoutOverlay?.Update(_partition);
        ApplyCompareLayout();
        ScheduleCompareCrop();
        NotifyCompareMode();
    }

    /// <summary>分格形状显示名（中英双份，供状态栏 / 提示复用）。
    ///
    /// <para><b>为什么每个形状都要显式列出</b>：旧的 <c>CompareModeName</c> 用三分支 switch
    /// 兜底，变体一旦漏分支就静默显示成 "AB"（C# 对带 <c>_ =&gt;</c> 的 switch 不报 CS8509）。
    /// 这里同样保留 <c>_ =&gt;</c> 兜底给 (1,1) 这类不该出现的组合，但已知的每种朝向都单独有名，
    /// 新增朝向时不必再担心"名字掉进 default"。</para></summary>
    private string CompareShapeName(GridPartition p) => (p.Cols, p.Rows) switch
    {
        (2, 1) => Loc("左右两格", "2 columns"),
        (1, 2) => Loc("上下两格", "2 rows"),
        (3, 1) => Loc("三列", "3 columns"),
        (1, 3) => Loc("三行", "3 rows"),
        (2, 2) => Loc("四宫格", "2×2 grid"),
        (4, 1) => Loc("四列", "4 columns"),
        (1, 4) => Loc("四行", "4 rows"),
        (3, 2) => Loc("3 列 2 行", "3×2 grid"),
        (2, 3) => Loc("2 列 3 行", "2×3 grid"),
        (3, 3) => Loc("九宫格", "3×3 grid"),
        _ => Loc($"{p.Cols} 列 {p.Rows} 行", $"{p.Cols}x{p.Rows} grid"),
    };

    /// <summary>"哪几路当前不干活"的状态栏后缀，读<b>实测</b>启用集合
    /// （<see cref="SyncController.InactiveRoutes"/>），不是拿格数去推"后几路"。
    ///
    /// <para><b>为什么必须读实测</b>：① 做过左右互换 / 轮换之后，没占格的未必是编号靠后的那几路；
    /// ② <c>Failed</c> 的路被 <c>SetActiveRoutes</c> 跳过、永远保持启用，按格数推会把它们也说成
    /// "已停用"，那是句假话。文案与判据同源，才不会各说各的。</para>
    ///
    /// <para><b>合并态下这个后缀恒为空</b>（一路一格，没有路被挤出去），它只在左右拉动
    /// （两格装不下 N 路）时才有内容 —— 保留它而不是写死"全部显示"，就是因为读的是实测值。</para>
    ///
    /// <para><b>已知的一拍延迟</b>：模式变化时本函数被同步调用，而启用态是延后一拍才下发的
    /// （见 <see cref="ApplyRouteActivity"/>），所以切模式的那一瞬间可能还是旧集合；
    /// <c>ApplyRouteActivity</c> 落地后会再通知一次，最终说的就是实测值。</para></summary>
    private string DescribeDeactivatedRoutes()
    {
        var inactive = _sync.InactiveRoutes();
        if (inactive.Count == 0) return "";
        var names = string.Join("、", inactive.Select(i => (i + 1).ToString()));
        var showing = _sync.Count - inactive.Count;
        return Loc($"，第 {names} 路未启用（当前模式只显示 {showing} 路，未启用的那几路不再解码/呈现）",
                   $" (route {names} deactivated — this view shows {showing}; deactivated routes don't decode/present)");
    }

    private void NotifyCompareMode()
    {
        if (!_compareActive)
        {
            StatusInfo.Text = Loc("已回到均匀网格（无分割线）。按 G 用网格对比。",
                                  "Back to the plain grid (no dividers). Press G for the grid compare view.");
            return;
        }

        var name = CompareShapeName(_partition);
        var scope = DescribeDeactivatedRoutes();   // 读实测集合，理由见该方法注释
        StatusInfo.Text = Loc(
            $"网格对比：{name}，{_partition.CellCount} 路全部显示，拖分割线调整各格占比{scope}（按 C 换分格 / 按 S 左右拉动，再按 G 退出）",
            $"Grid compare: {name}, all {_partition.CellCount} lane(s) visible, drag a divider to resize cells{scope} (C changes the split, S the wipe, G exits)");
    }

    // ══════════ 视图模式入口（合并后只剩两项：网格对比 / 左右拉动） ══════════
    //
    // 两项共用同一套"以真实状态为准"的勾选刷新（RefreshCompareModeChecks），
    // 菜单（视图 → 视图模式）与快捷键（G / V / S）走同一条路径，不会出现两套语义。
    //
    // 旧三项里的"A/B 可拖动对比（V）"与"标准模式（G）"合成一项：基准取网格的渲染覆盖
    // （一路一格、全部路显示），分割线取 A/B 的可拖动能力。V 键保留为同一入口的别名，
    // 老键位不废；G 是<b>开关</b>（再按一次回无分割线的均匀网格），菜单项是<b>进入</b>语义
    // —— 与左右拉动既有的约定一致。

    /// <summary>网格对比的"进入"入口（菜单项 / V 键）。已在态时不重进，
    /// 于是再点一次不会把用户拖好的分格宽度重置成均匀；不足 2 路时给可见反馈而不是静默。</summary>
    private void EnterGridCompare()
    {
        if (_sync.Count < 2)
        {
            StatusInfo.Text = Loc("网格对比需要至少 2 路视频。", "The grid compare view needs at least 2 lanes.");
            return;
        }

        EnterCompareMode();
        // 进入失败（B3 fail-fast 自动降级）时不覆盖状态栏里的失败原因。
        if (_compareActive) NotifyCompareMode();
    }

    /// <summary>回到"无分割线的均匀网格"。<b>不在态时什么都不做</b> ——
    /// 自测与旧 <c>EnterGridMode</c> 要的就是这个语义：G 键是开关（不在态时会进入），
    /// 而"退回均匀网格"这一步不能反过来把模式打开，否则夹具的收尾会变成下一次的前置。</summary>
    internal void ExitGridCompare()
    {
        if (!_compareActive && !_compareOverlayActive) return;
        ExitCompareMode(); // 内部先退出拉动，再撤格表、复位无缝放大与窗口裁剪
        NotifyCompareMode();
    }

    /// <summary>G 键：网格对比的<b>开关</b>。已在该态（且不是左右拉动）则退回均匀网格，
    /// 否则进入。<para>退出口只有这一个：旧实现靠"C 循环到末项"退出，合并后 C 只管换朝向，
    /// 退出必须由一个明确的键承担。</para></summary>
    internal void ToggleGridCompare()
    {
        if (_compareActive && !_compareOverlayActive)
        {
            ExitCompareMode();
            NotifyCompareMode();
            return;
        }
        EnterGridCompare();
    }

    /// <summary>刷新「视图 → 视图模式」两项的勾选与「对比选项」各项的可用状态。
    ///
    /// <para><b>刷新时机</b>：父菜单 <c>SubmenuOpened</c> 时 + 项被点击之后。
    /// 前者保证"用 G / V / S / C 键或路数变化改了模式后，菜单里不会留下陈旧勾选"；
    /// 后者是因为 Avalonia 的菜单交互处理器会<b>先</b>改 <c>IsChecked</c>（Radio 选中自己并取消
    /// 同组其余项）、<b>再</b>触发 <c>Click</c> —— 而"点一下"未必真的改得了状态（不足 2 路时
    /// 进不了对比），不纠正就会与真实状态相反。</para>
    ///
    /// <para>两项互斥，判据只有一份真实状态：<c>_compareOverlayActive</c> = 左右拉动、
    /// <c>_compareActive</c> 且非叠加 = 网格对比。不足 2 路时两项都不可用，
    /// 且此时两处都不会有勾（真实状态就是"均匀网格"）。</para></summary>
    private void RefreshCompareModeChecks()
    {
        var canCompare = _sync.Count >= 2;
        MenuViewStandard.IsEnabled = canCompare;
        MenuViewWipe.IsEnabled = canCompare;

        MenuViewWipe.IsChecked = _compareOverlayActive;
        MenuViewStandard.IsChecked = _compareActive && !_compareOverlayActive;

        // 互换：只在"网格对比 + 非拉动 + 至少两格"时可用。拉动是"两路铺满 + 区域裁半区"，
        // 互换会与它的揭示区打架（两者都靠窗口区域表达"露出哪一路"）。
        MenuModeSwap.IsEnabled = _compareActive && !_compareOverlayActive
            && _partition.CellCount >= 2;
        // 轮换需要"至少两路可换"，否则点它是空转（恒等轮换回自己）
        MenuModeRotate.IsEnabled = MenuModeSwap.IsEnabled && Grid.Count >= 2;
        MenuAutoCompare.IsChecked = _settings.AutoEnterCompare;
    }

    /// <summary>「打开后自动进入对比模式」开关（可勾选偏好，不是模式）。
    /// 与 <see cref="ToggleBottomBar"/> 同结构：翻转 → 落盘 → 以真实状态刷勾。</summary>
    private void OnToggleAutoCompare(object? sender, RoutedEventArgs e)
    {
        _settings.AutoEnterCompare = !_settings.AutoEnterCompare;
        // 自测模式不写用户配置（与 SaveWindowGeometry / 侧栏几何同规）
        if (!_selfTestMode) SettingsStore.Save(_settings);
        RefreshCompareModeChecks();
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

    /// <summary>状态栏主信息条：模式 / 路数 / 步进（帧·秒）/ 失败路数（#19）。
    ///
    /// <para><b>为什么抽成纯静态函数</b>：这条文案原先在 <see cref="UpdateStatusInfo"/> 里
    /// 硬编码中文（<c>"{mode}模式 | 路数 {n}/9 | …帧/…秒"</c>、<c>"{n} 路失败"</c>），
    /// 英文模式下这条信息量最大的状态栏仍是中文。内联写法只能靠"活窗口 + 切英文"人眼验证，
    /// 抽出来后单测可以直接钉住 <b>en 下不含中文、zh 下与旧文案逐字一致</b>
    /// —— 否则"改回硬编码"不会有任何断言判红（同 <see cref="ResolveFidelity"/> 的做法）。</para>
    ///
    /// <para><b>为什么必须用格式串而不是拼接</b>：模式名/路数/步长单位/失败路数在 zh 与 en 里
    /// 语序与量词都不同（"4 路失败" vs "4 route(s) failed"），拼接等于把中文语序钉死在英文界面上。
    /// 分隔符也放进格式串，由各语言自行决定分段写法。</para>
    ///
    /// <para>本方法只负责"主信息条"这三段；其后的帧率告警 / 运行时错误 / RT 诊断
    /// 仍由调用方按原有写法追加（它们本就是"⚠ + 语言表键"或纯技术 token）。</para></summary>
    /// <param name="mode">已本地化的模式名（<c>Status_SingleMode</c> / <c>Status_GridMode</c>）。</param>
    /// <param name="routeCount">当前路数。</param>
    /// <param name="frameStep">按帧步进步长。</param>
    /// <param name="secondsStep">按秒步进步长。</param>
    /// <param name="failedRoutes">失败路数（0 = 不显示该段）。</param>
    internal static string BuildStatusInfoLine(string mode, int routeCount,
        int frameStep, double secondsStep, int failedRoutes)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(LanguageManager.Tf("Status_ModeRoutesFmt", mode, routeCount));
        sb.Append(LanguageManager.Tf("Status_StepsFmt", LanguageManager.T("Status_Steps"),
            frameStep, secondsStep));
        if (failedRoutes > 0) sb.Append(LanguageManager.Tf("Status_FailedRoutesFmt", failedRoutes));
        return sb.ToString();
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
        // #19：状态栏主信息条整段走语言表（见 BuildStatusInfoLine 的注释）。
        var sb = new System.Text.StringBuilder();
        sb.Append(BuildStatusInfoLine(mode, _sync.Count,
            _sync.StepProfile.FrameStep, _sync.StepProfile.SecondsStep, failed));
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
        // 这里原来还有一格 `×{SharedZoom}`，已删（2026-09-29 用户口径对齐）：它是**相对适配框**
        // 的倍率，×1 的含义是"恰好铺满窗口"，与片源尺寸无关 —— 长得像"屏幕:视频"却不是说这件事。
        // 真正那个绝对比值由下面的可信度串给出（RatioX/RatioY = zoom/pan **之后**的 Dest ÷ 源尺寸，
        // 滚轮改倍率时它跟着变，所以缩放反馈没丢）。留两个"×"并列正是"像素和光标对不上"这类
        // 误解的来源；比值不可用时宁可这格空着，也不拿相对倍率冒充。
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

        // 拼合（mosaic）下**不需要放大也一样**：各路窗口恒等于整对比区，按 Bounds 命中会
        // 永远返回第一个可见表面（= 第 0 路），放大镜/滚轮锚点/指针映射全部读到错的那一路。
        // 这是拼合改写排布时最容易漏的一处 —— 窗口重叠了，而"重叠 + 区域裁开"在画面上看不出来。
        if (_compareActive && Grid.MosaicWindows) return HitCompareCellAt(windowPos);

        foreach (var s in Grid.Surfaces)
        {
            if (!s.IsVisible) continue;
            var tl = s.TranslatePoint(new Point(0, 0), this);
            if (tl is not { } origin) continue;
            if (new Rect(origin, new Size(s.Bounds.Width, s.Bounds.Height)).Contains(windowPos)) return s;
        }
        return null;
    }

    /// <summary>按格命中（"对比模式 + 子窗口放大"或"对比模式 + 拼合"时使用）。
    ///
    /// <para><b>为什么必须换</b>：这两种排布下各路子窗口都互相重叠（拼合时每路都铺满整对比区），
    /// 按 <c>Bounds</c> 命中会命中"最后一个覆盖该点的窗口"，与用户实际看到的那一格不一致
    /// （窗口区域已经把越界部分裁掉了，但 Avalonia 侧读的是 <c>Bounds</c> 而不是区域）。</para>
    ///
    /// <para><b>为什么按格就够</b>：格互不重叠且铺满对比区，所以"点在哪个格里"是唯一确定的；
    /// 口径与**裁剪那一路同源**：拼合用不留缝的 <see cref="CompareCropPlanner.MosaicRegionDip"/>
    /// （留 1px 缝的话，缝里的点击谁都归不到 ⇒ 表现为"线正好压在那儿却点不中这一格"），
    /// 其余用内缩 1px 的 <see cref="CompareCropPlanner.CellToContainerDip"/>。
    /// 点落在所有格之外（对比区之外）时返回 null —— 与"没命中任何一路"语义相同。</para></summary>
    private PlayerSurface? HitCompareCellAt(Point windowPos)
    {
        var origin = Grid.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } o) return null;
        var p = new Point(windowPos.X - o.X, windowPos.Y - o.Y);

        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0) return null;

        var cells = _partition.ComputeCells();
        var mosaic = Grid.MosaicWindows;
        // 按格循环，再经"格→路"映射取对应的 PlayerSurface：
        // 互换 / 自选源之后"第 i 格"不一定还是"第 i 路"，按路循环会命中错的那一路。
        for (var cell = 0; cell < cells.Length; cell++)
        {
            var routeIndex = Grid.RouteIndexOfCell(cell);
            if (routeIndex < 0) continue;
            var s = Grid.GetSurface(routeIndex);
            if (s is null || !s.IsVisible) continue;
            var hitDip = mosaic
                ? CompareCropPlanner.MosaicRegionDip(cells[cell], w, h)
                : CompareCropPlanner.CellToContainerDip(cells[cell], w, h);
            if (hitDip.Contains(p)) return s;
        }
        return null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // WM_MOUSEWHEEL 发给焦点窗口，经 Avalonia 视觉树路由。唯一滚轮处理器。
        if (HitSurfaceAt(e.GetPosition(this)) is not null)
        {
            // 对比模式下滚轮改的是"多路同步的无缝放大"（子窗口放大 + 裁剪），
            // 不是单路 _viewZoom —— 后者是内核插值放大，各路也不会同步。
            if (TryHandleCompareWheel((short)(Math.Sign(e.Delta.Y) * 120))) { e.Handled = true; return; }

            ZoomStepKeepingCursorAnchor(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point pt);

    private bool _panDragging;
    private int _panLastX, _panLastY;
    /// <summary>本次拖动命中的那一路。平移量是<b>共享</b>的（一次下发作用于各路），
    /// 但换算 pan 的<b>量程基准</b>必须来自被拖那一路的实机绘制盒 —— 多路时各路盒子远小于窗口，
    /// 黑边时同一路的横/纵量程也不等（见 <see cref="_3FCompare.Platform.ViewPan"/>）。</summary>
    private PlayerSurface? _panSurface;
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
        // 对比模式：滚轮驱动"多路同步无缝放大"（以光标为锚点）。已处理则不再动 _viewZoom ——
        // 两条路径都改"看起来像缩放"的量，同时生效会互相打架（窗口尺寸 vs 内核视口）。
        if (TryHandleCompareWheel(delta)) return;

        ZoomStepKeepingCursorAnchor(delta > 0 ? 1 : -1);
    }

    /// <summary>滚轮一格：改倍率时把<b>光标下那一点钉在光标下</b>，而不是围绕画面中心放缩。
    ///
    /// <para><b>为什么原来"像素和光标固定不上"</b>：旧实现只动 <c>_viewZoom</c>、不动
    /// <c>_viewPan</c> ⇒ 内核始终围绕<b>拟合盒中心</b>放缩（<c>VideoRenderer.cpp:4826-4829</c>），
    /// 光标不在中心时每格滚轮那点都会朝中心方向滑一段，放大十几格就完全找不到原处。
    /// 锚点式与钳位区间的推导见 <see cref="_3FCompare.Platform.ViewPan.AnchorPan"/>。</para>
    ///
    /// <para><b>退化约定</b>：光标落在黑边时改钉"可见区中心当前对着的那一点"（见
    /// <see cref="WheelAnchorFractions"/>）；缩回 1× 时量程塌缩，pan 归零。
    /// 顶到 1 或 32 端点时<b>什么都不动</b> —— 不发"抖一下"的无效变换。</para></summary>
    private void ZoomStepKeepingCursorAnchor(int direction)
    {
        var z0 = _viewZoom;
        var factor = direction > 0 ? 1.15f : 1f / 1.15f;
        var z1 = Math.Clamp(z0 * factor, 1f, 32f);
        if (Math.Abs(z1 - z0) < 1e-6) return;

        var (u, v) = WheelAnchorFractions();
        _viewZoom = z1;
        _viewPanX = ViewPan.AnchorPan(_viewPanX, u, z0, z1);
        _viewPanY = ViewPan.AnchorPan(_viewPanY, v, z0, z1);
        ApplyViewTransform();
    }

    /// <summary>滚轮放缩要钉住的那个<b>源画面归一化坐标</b>（横除源宽、纵除源高）。
    ///
    /// <para>优先光标下那一点。光标落在<b>黑边</b>时（扁格里黑边占大半画面，这是常态而不是意外）
    /// 退到"这一路可见区中心当前正对着的那一点"= 围绕可见画面中心放缩 —— 连续、不跳。</para>
    ///
    /// <para><b>为什么不能拿 0.5 兜底</b>：0.5 是"源画面的中心"，与"当前视口的中心"在已平移的
    /// 状态下是两个不同的点 —— 拿它兜底会把画面猛地拽回源中心，用户会觉得"一滚就飞"。
    /// 只有连表面都命中不到（会话未就绪 / 演示模式，此时内核本就不应用变换）才用 0.5。</para></summary>
    private (double U, double V) WheelAnchorFractions() =>
        _3FCompare.Platform.ViewPan.ResolveAnchorFraction(
            CursorSourceFraction() is { } a ? (a.U, a.V) : null,
            VisibleCenterFractionsAtCursor());

    /// <summary>光标所在那一路的<b>可见区中心当前正对着的源坐标</b>（黑边退化锚）；取不到返回 null。</summary>
    private (double U, double V)? VisibleCenterFractionsAtCursor()
    {
        if (!GetCursorPos(out var pt) || SurfaceAtScreenPoint(pt) is not { } sv) return null;
        return VisibleCenterFractions(sv);
    }

    /// <summary>该路<b>可见区</b>中心正对着的源归一化坐标；会话不可用或取不到点时返回 null。
    /// <para>两条缩放路径（单路 <see cref="WheelAnchorFractions"/> 与对比模式
    /// <c>TryHandleCompareWheel</c> 的相对对齐支）共用这一条，避免"同一个退化各写一份、
    /// 一份跳一份不跳"。</para>
    ///
    /// <para><b>"可见区中心"必须是这一路自己的那一块</b>。原先直接取 <c>surface.Bounds.Center</c>，
    /// 在拼合下等于取"整个对比区的中心"：各路窗口恒等于整对比区 ⇒ 不管光标在哪一格，退化锚都是
    /// 同一个组合画面中心，滚轮一滚每格都朝/离对比区中心滑（就是本方法注释一直警告的那类"一滚就飞"）。
    /// 顺带修掉另一半更早的错：<c>Bounds</c> 是<b>父容器</b>坐标，而 <see cref="MapPointerToVideoFraction"/>
    /// 要的是<b>窗口内</b>坐标，多格网格下前者自带一个格原点偏移。</para></summary>
    internal (double U, double V)? VisibleCenterFractions(PlayerSurface surface)
    {
        var slots = _sync.Slots;
        if (surface.Index < 0 || surface.Index >= slots.Count ||
            slots[surface.Index].Session is not { } session) return null;
        var f = MapPointerToVideoFraction(surface, session, VisibleCenterPointDip(surface));
        return f is { } g ? (g.U, g.V) : null;
    }

    /// <summary>退化锚该用的那个点，<b>以该路窗口自己的客户区为坐标</b>（DIP）。
    /// <para>优先用"这一路屏幕上真正露出的那一块"的中心 —— 那份账由
    /// <see cref="TryCompareVisibleBlockDip"/> 从 <c>SetWindowRgn</c> 的实际下发结果里拿，
    /// 与像素同源，不是第二次几何推算。没有下发过区域（未进对比裁剪、非拼合的窗口==格、
    /// 叠加态分区过窄…）就退回整窗中心，仍是窗口内坐标。</para></summary>
    internal Point VisibleCenterPointDip(PlayerSurface surface)
        => TryCompareVisibleBlockDip(surface.Index, out var block)
            ? new Point(block.X + block.Width / 2, block.Y + block.Height / 2)
            : new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2);

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
        // 对比模式的无缝放大同样可拖动平移（改的是 crop，不是 _viewPan）。
        // 两条路径都用 _panDragging 表达"正在拖"，但提交的目标不同（见 OnSurfaceRelease）。
        var magnifyPan = CompareMagnifyActive;
        // "能不能拖"取决于内核有没有平移量程 —— 现在是 **两侧**：放大时盒子比窗口大、
        // 缩小时盒子比窗口小但能在黑边里走。写死 `zoom > 1.001f` 的那份是旧内核语义
        // （z ≤ 1 整段跳过变换），与内核放开 |z−1|>1e-4 之后不一致 ⇒ 会出现
        // "内核认 pan、app 不给拖"。判据交给同一条 ViewPan.KernelAppliesTransform，
        // 门槛只留一处（诊断行与分支必须读同一个值，否则日志与行为会各自说一套）。
        var hasTravel = _3FCompare.Platform.ViewPan.KernelAppliesTransform(_viewZoom);
        Console.Error.WriteLine($"[Pan] OnSurfacePress zoom={_viewZoom:F3} magnify={magnifyPan} willDrag={hasTravel || magnifyPan}");
        if (hasTravel || magnifyPan)
        {
            _panDragging = true;
            if (magnifyPan) BeginComparePan();
            // 降低但不停止：保持 Failed 状态检测和多路同步
            _pollTimer.Interval = TimeSpan.FromMilliseconds(250);
            GetCursorPos(out var pt);
            _panLastX = pt.X;
            _panLastY = pt.Y;
            // 记住被拖那一路：pan 的量程基准是它的实机绘制盒，不是窗口短边（多路时各路盒子
            // 远小于窗口，黑边时横纵还不等）。中途换路不改 —— 一次拖动的增益应当恒定，
            // 否则光标划过格线就会突然变快/变慢，那正是"手感和倍数对不上"的另一半。
            _panSurface = SurfaceAtScreenPoint(pt);
            Console.Error.WriteLine($"[Pan] Dragging started at screen ({pt.X},{pt.Y}) lane={_panSurface?.Index ?? -1}");
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

        // 放大态：拖动过程中就跟着走（原来只在松手时一次性提交，于是"拖着不动、一松手跳过去"）。
        // docs/26 当年反对"逐帧 SetWindowRgn"是**估算**；2026-09-29 用 --magnifybench 新加的
        // [平移提交] 实测：z=2、2 路下 SetCompareMagnify 单次 p50=0.03ms / p95=0.27ms / max=0.78ms，
        // 连续 34 次/秒提交期间出帧 59.87 fps/路，与同轮基准 58.92 / 放大态 59.14 持平且零 hitch
        // ⇒ 按 ~60Hz 合并提交负担得起。日志：tests/output/magnifybench-pancommit-z2-20260929.log
        if (CompareMagnifyActive)
        {
            AccumulateComparePan(dx, dy);
            var nowMs = Environment.TickCount64;
            if (nowMs - _lastCompareCommitTicks >= _3FCompare.Controls.LayoutOverlayWindow.GestureCommitIntervalMs)
            {
                _lastCompareCommitTicks = nowMs;
                CommitComparePan();   // 提交并清零累积量，松手时只补最后一段残余
            }
            return;
        }

        // 方向、量程基准、以及"分母为什么是被拖那一路的适配盒而不是窗口短边"，都写在 ViewPan 里。
        var (fitW, fitH) = DragFitBoxPhys(_panSurface);
        (_viewPanX, _viewPanY) = ViewPan.Accumulate(_viewPanX, _viewPanY, dx, dy, fitW, fitH, _viewZoom);
        ApplyViewTransform();
    }

    /// <summary>被拖那一路的<b>适配盒</b>（物理像素，即 <c>z=1</c> 时的等比绘制框）。
    ///
    /// <para>首选内核实机上报的 Dest 反推（<c>rt.DestWidth / z</c>）：只有实机数才含变形宽银幕的
    /// 显示宽高比（<c>VideoRenderer.cpp:4804-4806</c> 按 <c>discAspect_</c> 折算源宽），自己按窗口
    /// 尺寸算会得到另一个框。拿不到诊断信息（演示模式 / 旧内核）时退回"画面铺满该路表面"，
    /// 与 <c>VideoPixelMap.MapFallbackFraction</c> 同一假设 —— 注意分母是<b>那一路</b>的 Bounds。</para>
    /// <para>返回 (0,0) 表示无从换算，<see cref="ViewPan.Accumulate"/> 据此本次不动（不猜增益）。</para></summary>
    private (double Width, double Height) DragFitBoxPhys(PlayerSurface? surface)
    {
        if (surface is not { } sv || sv.Bounds.Width <= 0 || sv.Bounds.Height <= 0) return (0, 0);

        var slots = _sync.Slots;
        if (sv.Index < 0 || sv.Index >= slots.Count) return (0, 0);
        var session = slots[sv.Index].Session;
        if (session is null) return (0, 0);

        if (session.ReadRenderTargetInfo(out var rt) && rt.DestWidth > 0 && rt.DestHeight > 0)
            return _3FCompare.Platform.ViewPan.FitBoxFromDest(rt.DestWidth, rt.DestHeight, _viewZoom);

        var scale = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? RenderScaling;
        if (!(scale > 0)) scale = 1.0;
        return (sv.Bounds.Width * scale, sv.Bounds.Height * scale);
    }

    /// <summary>屏幕物理坐标 → 该点命中的那一路表面（与选中/释放同一套命中换算，含 DPI 与外框偏移）。</summary>
    private PlayerSurface? SurfaceAtScreenPoint(System.Drawing.Point screenPt) =>
        HitSurfaceAt(this.PointToClient(new PixelPoint(screenPt.X, screenPt.Y)));

    /// <summary>左键释放（WndProc 转发）：结束平移或触发选中。
    /// 立即发送最终变换值确保松手后精确对齐，恢复轮询频率。</summary>
    private void OnSurfaceRelease(double x, double y)
    {
        if (_panDragging)
        {
            _panDragging = false;

            // 放大态：提交累积的平移（一次 SetCompareMagnify ⇒ 一次 SetWindowRgn）。
            // 走的是"提交即返回"的分支，但轮询频率是按下时降下来的，必须在这里恢复，
            // 否则松手后位置同步会一直停在 250ms（表现为"拖动过一次后播放位置更新变卡"）。
            if (CompareMagnifyActive)
            {
                CommitComparePan();
                RestorePollInterval();
                return;
            }

            // 立即发送最终位置（UI 线程同步调用：原生 SetViewTransform 只写
            // 三个 atomic，毫秒级；不要用 Task.Run 后台调用——会话重建/关闭时
            // UI 线程已释放原生句柄，后台 P/Invoke 访问会 0xC0000005 闪退）。
            var now = Environment.TickCount64;
            _lastPanApplyTicks = now;
            // 已同步下发最终值 ⇒ 取消挂起的补发（否则会用同一组值再发一次）
            _transformFlushTimer.Stop();
            try { SendViewTransform(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Transform] Release FAIL: {ex.Message}"); }
            RestorePollInterval();
            return;
        }
        // 未放大时的点击 → 选中表面
        GetCursorPos(out var pt);
        // P1-18 修复：GetCursorPos 返回的是**物理屏幕像素**，而 HitSurfaceAt（内部用
        // TranslatePoint）用的是**客户区 DIP**。旧的 `pt - Position` 写法错在两处：
        //   ① 没做物理像素 → DIP 换算（150% 缩放偏 1.5 倍、250% 偏 2.5 倍）；
        //   ② Position 是窗口外框左上角（含标题栏与边框），客户区原点在其下方。
        // 两者叠加会让高 DPI 用户点 A 画面却选中 B 画面，且缩放越大偏得越多。
        if (SurfaceAtScreenPoint(pt) is { } clicked)
        {
            Grid.SelectedIndex = clicked.Index;
            UpdatePanelsForSelection();
        }
    }

    /// <summary>把轮询间隔恢复成"拖动前"的常态（拖动期间被降到 250ms 以让出 UI 线程）。
    /// 抽成方法是因为松手有两条出口（内核视口平移 / 放大态 crop 平移），
    /// 两处各写一遍会漂移成"一条忘了恢复"。</summary>
    private void RestorePollInterval()
    {
        if (_isPlaying && _sync.Count > 0)
            _pollTimer.Interval = TimeSpan.FromMilliseconds(83);
        else if (_sync.Count > 0)
            _pollTimer.Interval = TimeSpan.FromMilliseconds(250);
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
            case Key.S when mods.HasFlag(KeyModifiers.Control): OnExportFrame(this, e); break;
            // P1-4：键盘步进同样必须走线程池（内含 50ms 复测等待），
            // 否则每按一次 ←/→ 都会冻结 UI 50ms，连按时手感明显卡顿。
            case Key.Up: StepSecondsAsync(10); break;
            case Key.Down: StepSecondsAsync(-10); break;
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.O when mods == KeyModifiers.None: _ = OpenViaPickerAsync(); break;
            // 裸 B 已随「A-B 滑块视图」一并撤除（该视图只画渐变占位，真机模式下会整块盖住真实画面，
            // 与「视图模式」列里的 A/B 可拖动对比是重复概念）；B 点仍归 TimelineView 自己处理。
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
            // 分格朝向循环（见 CycleCompareShape）：C 只换朝向，退出交给 G 的开关语义。
            // 选快捷键而非菜单：菜单要改 MainWindow.axaml 并新增本地化键，而 Localization/ 不在
            // 本次改动范围（docs/26 §四）；快捷键只动本文件这一处 switch，改动最小且零新依赖。
            case Key.C when mods == KeyModifiers.None: CycleCompareShape(); break;
            // 叠加模式（对标 ICAT Single Screen，docs/31 阶段 2）开关：两路铺满 + B 置顶裁半。
            // 选 S 的依据：① 未被占用（全表只有 Ctrl+S 用于导出帧，见上）；② 语义对得上文档标题
            // "Single Screen"；③ 与 C（模式循环）分属"切模式"与"切呈现"两件事，不会混淆。
            case Key.S when mods == KeyModifiers.None: ToggleCompareOverlay(); break;
            // 视图模式两键（合并后只剩一个可拖网格态）：G = 它的<b>开关</b>（再按一次回无分割线的
            // 均匀网格），V = 同一态的<b>进入</b>别名。
            // 为什么留 V：它已在 README、出货说明与用户肌肉记忆里代表"可拖动分格"，撤掉等于
            // 让老键位凭空失效；而它落到的态与 G 完全相同（EnterGridCompare），不产生第二套语义。
            // 选键依据不变：全表（本 switch + TimelineView 的 A/B + XAML 的 InputGesture）核对过，
            // G / V 两个裸键均未被占用，且带修饰键的 Ctrl+G/Ctrl+V 不受影响（有 None 守卫）。
            case Key.V when mods == KeyModifiers.None: EnterGridCompare(); break;
            case Key.G when mods == KeyModifiers.None: ToggleGridCompare(); break;
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
        // D1..D9 只加不减（WinForms 语义）。锚点同「加路」取网格路数：按会话数判"已达目标"时，
        // 尾部带空 lane 的窗口会被 SetCount(upTo) 静默撤掉几条 lane，恰好与"只加不减"相反。
        var target = Math.Max(Grid.Count, Math.Min(MaxLaneCount, upTo));
        if (target == Grid.Count) return;
        Grid.SetCount(target, _realMode);
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

    /// <summary>会话文件路径过滤（安全闸门）：把外部 <c>.3fcs</c> 里的路径分成"可打开"与"已拒绝"。
    ///
    /// <para><b>为什么必须校验</b>：会话文件可能是别人给的，路径即外部输入。真正的判据只有一条
    /// （见 <see cref="_3FCompare.Core.PathSafety.IsUncPath"/>）：**UNC（<c>\\server\share\…</c>）
    /// 与设备命名空间（<c>\\?\…</c>，<c>//?/…</c> 归一后同形）** —— Windows 访问网络路径时会自动
    /// 发起 NTLM 认证、把本机凭据送到该服务器 ⇒ 凭据外泄。确有需要可手动拖放/选择文件打开，
    /// 那是用户的主动意图，与本路径性质不同。</para>
    ///
    /// <para><b>另两类"拒绝"不是 UNC，文案必须分开给</b>（与 Core 侧 doc 的约定一致，
    /// 见 <c>PathSafety.IsUncPath</c> 的"三类互不相同的原因"）：
    /// ① <b>空 / 空白串</b>：本方法**先行**判掉并给独立文案 <c>(空路径)</c> —— 它是非法输入、
    /// 没有网络语义，说成"不接受 UNC 路径"会把排查方向带偏（IsUncPath 对空白同样返回 <c>true</c>，
    /// 但那只是与 UNC 共用同一个 fail-closed 返回值）；
    /// ② <b>归一化失败的串</b>（非法字符如 <c>\0</c> / 超长 / 无效盘符）：同样按不可信拒绝，
    /// 原因与网络无关。</para>
    ///
    /// <para><b>相对路径刻意放行</b>：IsUncPath 把相对路径归一化成本地绝对路径 ⇒ 返回 <c>false</c>。
    /// "一律拒绝相对路径"**不在**本闸门的判据里（代价论证见下方循环内的注释），
    /// 也刻意不在这里补一条 <c>Path.IsPathRooted</c> 检查 —— 本闸门只管网络/设备这一条线。</para>
    ///
    /// <para><b>为什么不能再用 <c>p.StartsWith(@"\\")</c></b>：<c>//server/share/x</c> 同样被
    /// <see cref="Path.IsPathRooted"/> 视为 rooted，却<b>不</b>以 <c>\\</c> 开头 ⇒ 只查前缀会被
    /// 整体绕过（判据放行 ⇒ 直传 ⇒ 触发 SMB 出网）。判据统一走
    /// <see cref="_3FCompare.Core.PathSafety.IsUncPath"/>：先 <c>GetFullPath</c> 归一化
    /// （<c>//</c> 与 <c>\\</c> 归一后形式唯一），并把归一化失败按不可信拒绝（fail-closed）。</para>
    ///
    /// <para><b>为什么抽成纯静态函数</b>：本方法只做值运算、不碰窗口/内核，单测可直接调用
    /// （UI 工程已对 <c>3FCompare.Platform.Tests</c> 开 InternalsVisibleTo）。
    /// 会话文件是本缺陷真正的攻击面，只有把它变成可断言的生产代码入口，
    /// "改回旧判据"才会判红 —— 否则改坏了也没有任何测试会响。</para>
    ///
    /// <para>返回的 <c>Rejected</c> 带可操作的中文原因（UNC 凭据外泄 / 空路径 / 无法解析），
    /// 而不是笼统一句"未通过校验"：用户要能看懂为什么该路没被打开、以及怎么改。</para></summary>
    /// <param name="paths">按会话原始顺序排列的路径（可能含 null/空白）。</param>
    internal static (System.Collections.Generic.List<string> Accepted,
        System.Collections.Generic.List<int> AcceptedIndex,
        System.Collections.Generic.List<string> Rejected) FilterSessionPaths(
        System.Collections.Generic.IReadOnlyList<string?> paths)
    {
        var accepted = new System.Collections.Generic.List<string>(paths.Count);
        var acceptedIndex = new System.Collections.Generic.List<int>(paths.Count);
        var rejected = new System.Collections.Generic.List<string>();
        for (var i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            if (string.IsNullOrWhiteSpace(p))
            {
                rejected.Add("(空路径)");
                continue;
            }
            // ⚠ 刻意**不**一律拒绝相对路径（docs/41 #18 复核修正）：
            // #18 要堵的是 UNC 绕过导致 NTLM 凭据外泄，**不是**相对路径。一律拒绝相对路径
            // 会静默丢掉用户会话里的整条路（老会话 / 手工编辑过的 .3fcs 里很常见），
            // 用户只会看到"少了一路"而没有可操作的解释——代价远大于收益。
            // 改为**先判定、再归一化**：IsUncPath 内部已经按 GetFullPath 归一后再判，
            // 所以"当前工作目录本身在网络共享上"这种情形展开后仍是 UNC ⇒ 照样被拦下。
            if (_3FCompare.Core.PathSafety.IsUncPath(p))
            {
                rejected.Add($"{p}（UNC/设备路径：访问会触发 NTLM 认证并外泄本机凭据，已拒绝）");
                continue;
            }
            // 判定的串与下游实际使用的串必须是**同一个**：上面判的是归一化后的形式，
            // 这里也交出归一化后的绝对路径，否则"相对路径在别处被按另一个 CWD 解析"仍然存在。
            string full;
            try { full = Path.GetFullPath(p!.Trim()); }
            catch { rejected.Add($"{p}（路径无法解析，已拒绝）"); continue; }
            accepted.Add(full);
            acceptedIndex.Add(i);
        }
        return (accepted, acceptedIndex, rejected);
    }

    /// <summary>按会话快照重开全部路（菜单加载与自测 --sessiontest 共用同一条路径）。
    /// <para><b>P0-1 关键点</b>：顺序必须是 SetCount(0) → SetCount(会话路数) → OpenFiles。
    /// OpenFilesCore 用 _surfaceAt(_sync.Count) 取播放面板，SetCount(0) 之后该处必为 null，
    /// 会直接命中"第 1 路没有可用的播放面板"分支并 return，
    /// 导致 onAllOpened 回调不执行——不 Seek、不恢复循环区间、不播放（表现为加载会话后一片空白）。
    /// 因此重建网格必须发生在打开之前，且要在清空之后。</para></summary>
    /// <summary>按快照重建会话（手动加载与崩溃自愈共用同一条路径，避免两套重建逻辑漂移）。</summary>
    /// <param name="snapshot">会话快照。</param>
    /// <param name="autoPlay">打开后是否自动播放。<b>崩溃自愈传 false</b>：
    /// 崩溃成因是外部钩子的偶发竞态（docs/43），恢复后立刻播放可能立刻再撞上；
    /// 停在原位让用户看清"已恢复"再自行继续，比多闪一次要好。</param>
    internal void LoadSessionSnapshot(SessionSnapshot snapshot, bool autoPlay = true)
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
        // 会话文件里的路径来自外部（.3fcs 可能是别人给的）⇒ 先过安全闸门再打开；
        // 判据与原因见 FilterSessionPaths（含"为什么不能只查 \\ 前缀"）。
        var filtered = FilterSessionPaths(
            snapshot.Items.Select(it => it.Path).ToList());
        var acceptedPaths = filtered.Accepted;
        var acceptedIndex = filtered.AcceptedIndex;
        var rejected = filtered.Rejected;
        if (rejected.Count > 0)
        {
            var msg = $"会话中有 {rejected.Count} 条路径未通过校验已跳过：{string.Join(", ", rejected)}";
            _3FCompare.Core.Diagnostics.AppLog.Warn("Session", msg);
            Console.Error.WriteLine($"[Session] {msg}");
        }

        // 不自动播放时先统一暂停：OpenFiles 与 SeekTo 都不会改播放状态，
        // 不显式 Pause 的话"恢复出来就在跑"，与 autoPlay=false 的语义不符。
        if (!autoPlay)
        {
            _sync.Pause();
            SetPlaying(false);
        }
        _coordinator.OpenFiles(acceptedPaths, autoPlay: autoPlay, onAllOpened: () =>
        {
            // 先恢复偏移，再 SeekTo（SeekTo 内部会叠加偏移）。
            // 注意用 acceptedIndex 对位：跳过若干路径后，槽位索引已不等于原始 Items 索引，
            // 直接按 i 取会把偏移张冠李戴。
            for (var k = 0; k < acceptedIndex.Count && k < _sync.Count; k++)
                // master 偏移恒 0：老会话可能存了非 0 的 off0，加载时归零，
                // 否则一加载就带着"基准被挪走"的状态（docs/15 §3.1）
                //
                // ⚠ 偏移必须钳制：外部 .3fcs 的 Offset100ns 是**未校验的 long**，
                // 极端值（如 long.MinValue）会让 SyncController 的"位置 − 期望"溢出抛
                // OverflowException，而该异常落在轮询里被顶层 catch 吞掉 ⇒ 位置/时间码停更、
                // 每拍重抛、界面形似卡死且看不出原因（docs/45 P1-9）。
                // 钳制到 ±24h，远超任何真实调帧需求。
                _sync.Slots[k].Offset100ns = k == 0
                    ? 0
                    : Math.Clamp(snapshot.Items[acceptedIndex[k]].Offset100ns,
                        -_3FCompare.Core.Settings.SessionSnapshot.MaxOffset100ns,
                        _3FCompare.Core.Settings.SessionSnapshot.MaxOffset100ns);
            // ⚠ 必须用**同步** SeekNow，不能用 SafeSeek（fire-and-forget）：
            // 本回调一返回，批次结算紧接着就执行 _sync.Play()（autoPlay 时）。
            // 异步 Seek 与 Play 之间没有 happens-before ⇒ 实测出现"先播后跳"，
            // 恢复出来从 0 开始跑（docs/45 P2：--sessiontest 偶发假红的成因）。
            SeekNow(snapshot.Position100ns);
            if (snapshot.LoopEnabled && snapshot.LoopEnd100ns > snapshot.LoopStart100ns)
            {
                _sync.LoopStart100ns = snapshot.LoopStart100ns;
                _sync.LoopEnd100ns = snapshot.LoopEnd100ns;
                _sync.LoopEnabled = true;
                _timeline.SetLoopRange(snapshot.LoopStart100ns, snapshot.LoopEnd100ns, true);
            }
        });
    }

    /// <summary>崩溃自愈：把当前会话写进快照文件，供崩溃后重启恢复。
    ///
    /// <para><b>两个调用点，缺一不可</b>：① 5 秒定时轮询——覆盖播放中的位置推进；
    /// ② 「一批文件全部打开完成」回调——覆盖"刚打开就崩"。只留定时轮询的话，
    /// 打开 8 路 4K 的十几秒里若崩溃，快照还是上一批甚至根本不存在，
    /// 而那恰恰是最贵的一次丢失。</para>
    ///
    /// <para><b>自测模式一律不写</b>：自测进程不能往用户的配置目录里留东西，
    /// 否则下一次真实启动会恢复出一屏测试素材。<c>--sessiontest</c> 曾把 800×600 的
    /// 测试窗口几何写进 settings.json，是同一类问题，故这里同样以 <c>_selfTestMode</c> 把门。</para></summary>
    private void TryAutosave()
    {
        if (_selfTestMode) return;
        if (_sync.Count == 0) return;   // 没打开任何素材 ⇒ 没有需要恢复的状态
        // 快照 DTO 的构造要读 UI 状态（播放位置、布局、各路偏移），必须留在 UI 线程。
        // 真正慢的是随后的「临时文件 + fsync」落盘（#6）：磁盘卡顿 / 杀软扫描时会让
        // 5s 定时器在 UI 线程上同步冻结界面——与 #24（Resize 埋点同步落盘）同类，
        // 故只把**写盘**挪进后台线程。SessionAutosave.Write 内部按目标路径串行化
        // （AtomicFile.PathLocks），即便与下一拍重叠也不会互相踩踏。
        SessionSnapshot snapshot;
        try
        {
            snapshot = BuildSessionSnapshot();
        }
        catch (Exception ex)
        {
            _3FCompare.Core.Diagnostics.AppLog.Warn("Autosave", $"会话快照构造失败：{ex.Message}");
            return;
        }
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                _3FCompare.Core.Settings.SessionAutosave.Write(snapshot);
            }
            catch (Exception ex)
            {
                // 写不进去（只读目录 / 磁盘满）绝不能打断播放：自愈是尽力而为的增强，
                // 它的失败只应表现为"没能恢复"，不应表现为"程序报错"。
                _3FCompare.Core.Diagnostics.AppLog.Warn("Autosave", $"会话快照写入失败：{ex.Message}");
            }
        });
    }

    /// <summary>崩溃自愈：若本次是被守护进程拉起来的，就恢复上次会话（暂停态）。
    ///
    /// <para><b>为什么只在带恢复标记时才做</b>：正常启动必须保持干净的空白界面。
    /// 无条件恢复会让每次启动都弹出上次的素材——那不是自愈，是骚扰。</para>
    ///
    /// <para><b>失败一律静默降级为普通启动</b>：快照不存在 / 已过期 / 路径未通过安全校验，
    /// 都属于"没能恢复"而不是"出错"。此时不该弹框，只落一条日志即可。</para></summary>
    private void MaybeRecoverCrashedSession()
    {
        if (_selfTestMode) return;

        var restoreRequested = false;
        foreach (var a in Environment.GetCommandLineArgs())
        {
            if (a == _3FCompare.Diagnostics.CrashGuard.RestoreArg) { restoreRequested = true; break; }
        }
        if (!restoreRequested) return;

        var snapshot = _3FCompare.Core.Settings.SessionAutosave.Read();
        if (snapshot is null)
        {
            _3FCompare.Core.Diagnostics.AppLog.Warn("Recover",
                $"收到恢复标记但快照不可用（{_3FCompare.Core.Settings.SessionAutosave.Describe()}），按普通启动处理");
            return;
        }

        ComponentLog.Log(Comp.Engine, "CrashRecover", -1,
            $"routes={snapshot.Items.Count} pos={snapshot.Position100ns}");
        LoadSessionSnapshot(snapshot, autoPlay: false);
        StatusInfo.Text = Loc(
            $"已恢复上次会话（{snapshot.Items.Count} 路，已暂停）：按空格继续播放。",
            $"Last session restored ({snapshot.Items.Count} lanes, paused): press Space to resume.");
    }

    /// <summary>探针坐标：表面 DIP → 物理后台缓冲像素 → destination 矩形内 → 源视频像素。
    /// 3FCompare M3：旧实现直接用源分辨率等比映射，既漏了 RenderScaling 也漏了 letterbox。
    /// <para>整数像素是 <see cref="MapPointerToVideoFraction"/> 的截断出口 —— 两者必须是同一个点，
    /// 否则"放大镜钉住的像素"与"界面报的第几列"会各说一套。</para></summary>
    private (int X, int Y)? MapPointerToVideoPixel(PlayerSurface surface, IPlayerSession session, Point local)
    {
        var f = MapPointerToVideoFraction(surface, session, local);
        if (f is not { } v) return null;
        return (_3FCompare.Core.Display.VideoPixelMap.FractionToPixel(v.U, v.VideoWidth),
                _3FCompare.Core.Display.VideoPixelMap.FractionToPixel(v.V, v.VideoHeight));
    }

    /// <summary>指针 → 光标下那一点的<b>源画面归一化坐标</b>（<c>U=u/videoW</c>、<c>V=v/videoH</c>）
    /// 与源尺寸；光标落在黑边 / 表面未布局 / 会话不可用时返回 null。
    ///
    /// <para>放大锚点用这一条（要亚像素），探针读值用整数出口。<b>不做截断再乘回</b>：那会把
    /// 像素内的亚像素位置丢掉，而锚点误差是同向累加的（每格滚轮最多偏一个显示后的像素）。</para></summary>
    private (double U, double V, int VideoWidth, int VideoHeight)? MapPointerToVideoFraction(
        PlayerSurface surface, IPlayerSession session, Point local)
    {
        var media = session.ReadMediaInfo();
        if (media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0 ||
            surface.Bounds.Width <= 0 || surface.Bounds.Height <= 0) return null;

        // 1) DIP → 物理像素（子 HWND client 尺寸 == Bounds × RenderScaling）
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var physX = local.X * scale;
        var physY = local.Y * scale;

        // 2) 表面 → 后台缓冲（ShowInBounds 使 chain 尺寸 == client 尺寸，直接可用）
        // 3) 落在 destination 之外（letterbox 黑边）→ null（光标下没有源画面）
        // 4) destination 内 → 源画面的归一化位置
        //    ⚠ destination 是内核 zoom/pan **之后**的绘制矩形（见 PixelReadback A11 注释），
        //      所以这里已经是 zoom-aware 的，绝不能再叠一次倍率。
        if (!session.ReadRenderTargetInfo(out var rt) || rt.SwapWidth == 0 || rt.SwapHeight == 0)
        {
            // 无诊断信息（演示模式 ReadRenderTargetInfo 恒返回 false、旧内核同样不支持）：
            // 退回整面等比映射（漏 letterbox，但不越界）。
            var frac = _3FCompare.Core.Display.VideoPixelMap.MapFallbackFraction(
                physX, physY, surface.Bounds.Width * scale, surface.Bounds.Height * scale);
            if (frac is not { } fb) return null;
            return (fb.U, fb.V, media.VideoWidth, media.VideoHeight);
        }

        var mapped = _3FCompare.Core.Display.VideoPixelMap.MapToSourceFraction(
            physX, physY, rt.DestX, rt.DestY, rt.DestWidth, rt.DestHeight);
        if (mapped is not { } m) return null;
        return (m.U, m.V, media.VideoWidth, media.VideoHeight);
    }

    private static int To8(float v) => Math.Clamp((int)Math.Round(v * 255f), 0, 255);

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleSingleMulti(object? sender, RoutedEventArgs e)
    {
        Grid.SingleView = !Grid.SingleView;
        UpdateStatus();
    }

    // ══════════ 菜单：视图（M3 面板切换） ══════════

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
        if (sender is not MenuItem { Tag: string preset }) return;
        Grid.SetGridLayout(preset);

        // 合并态的格表来自分区，**不**走 Grid 自己的均匀逻辑 —— 只改预设的话，
        // "视图 → 网格布局"在这个态里就是一条看起来没反应的死菜单（旧标准模式下它是生效的）。
        if (!_compareActive || _compareOverlayActive) return;

        var (pc, pr) = GridLayout.OverrideOf(preset);
        var (cols, rows) = GridLayout.ResolveGrid(Grid.Count, Grid.SingleView, pc, pr);
        // 形状没变就什么都不做：预设从 auto 换成"恰好同形"的 3x3 不该把用户拖好的列宽抹掉。
        if (cols == _partition.Cols && rows == _partition.Rows) return;
        _partition = GridPartition.Uniform(Grid.Count, cols, rows);
        _layoutOverlay?.Update(_partition);
        ApplyCompareLayout();
        ScheduleCompareCrop();
    }

    /// <summary>「视图 → 视图模式」两项的统一入口。写法与 <see cref="OnGridPreset"/> 一致
    ///（<c>Click</c> 事件 + <c>Tag</c> 字符串，不用 Command），保持本菜单风格统一。
    /// <para>菜单是<b>进入</b>语义（网格项不会把已激活的态关掉），G 键才是开关。</para></summary>
    private void OnCompareModePreset(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string preset }) return;

        switch (preset)
        {
            case "overlay": EnterCompareOverlay(); break; // 内部自带"需 ≥2 路"提示与状态栏通知
            case "grid": EnterGridCompare(); break;       // 合并态（一路一格 + 可拖分割线）
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

        // 失焦（切走别的程序）：不因指针恰好停在底边而让浮条常驻。
        // （旧版在此前先判"光标停在浮条上"为真，用来兜底 ComboBox 下拉的独立 Popup 抢走激活；
        //  底栏已无会产生 Popup 的控件，浮条自身矩形本就落在热区内，无需单独兜底。）
        if (!IsActive) return false;

        return IsCursorInTransportHotZone(pt.X, pt.Y);
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

    // 「显示 对比网格」一项已撤：它的实际动作是折叠/展开侧栏，与串名无关，
    // 且与「视图 → 侧栏」三态（含"图标栏"）重复。

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
            TransportKeys.CopyInto(result.KeyBindings, _settings.KeyBindings);   // 逐槽拷贝，不共享实例
            RebuildKeyMap();
            _transport.RefreshTips();   // 改了绑定 ⇒ 底栏 tooltip 立刻跟着变
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
            // 小地图是 PlayerSurface 的静态开关（GDI 绘制路径读它），内存 _settings
            // 更新不会自动传导 —— 必须在这里显式回写，否则改设置后本次会话无反应。
            PlayerSurface.SharedMinimapEnabled = _settings.MinimapEnabled;
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
        _autosaveTimer.Stop();
        try { _3FCompare.Core.Diagnostics.AppLog.Shutdown(); } catch { }
    }

    private void CopySettings(AppSettings s)
    {
        _settings.HardwareDecode = s.HardwareDecode;
        _settings.PreferredAdapterIndex = s.PreferredAdapterIndex;
        _settings.FfmpegDirectory = s.FfmpegDirectory;
        _settings.ColorMode = s.ColorMode;
        // SDR scRGB 模式：会话级配置，这里只同步到内存中的 _settings（新会话读取它），
        // 不对已开会话做热切换 —— 内核该字段在 CreateSession 时装配，运行中不重载。
        _settings.SdrScRgbMode = s.SdrScRgbMode;
        // 对齐模式：对比放大的锚点/crop 换算在运行时直接读 _settings.CompareAlign
        // （MainWindow.CompareCrop 的 _compareAlign），漏拷 = 改设置后本次会话静默不生效
        //（已落盘、重启才生效，且无任何报错）。
        _settings.CompareAlign = s.CompareAlign;
        _settings.FrameStep = s.FrameStep;
        _settings.SecondsStep = s.SecondsStep;
        _settings.StartFullscreen = s.StartFullscreen;
        _settings.HideChromeInFullscreen = s.HideChromeInFullscreen;
        _settings.DefaultGridCols = s.DefaultGridCols;
        _settings.DefaultGridRows = s.DefaultGridRows;
        _settings.VrrTearingPresent = s.VrrTearingPresent;
        // RTX VSR：会话级配置，同 VrrTearingPresent —— 只同步到内存中的 _settings，
        // 由新会话建会话时读取；已开会话不热切换（内核该请求在 CreateSession 里登记）。
        _settings.RtxVideoSuperResolution = s.RtxVideoSuperResolution;
        _settings.ScrubPreviewEnabled = s.ScrubPreviewEnabled;
        _settings.MinimapEnabled = s.MinimapEnabled;
        _settings.Language = s.Language;
    }

    // ══════════ M3：面板联动 ══════════

    /// <summary>放大镜呈现驱动（P1-4）：放大镜本体已搬进独立顶层窗 <see cref="_magnifierWindow"/>，
    /// 本方法把它摆到 <see cref="MagnifierOverlay.DesiredPosition"/>（CenterPanel DIP 坐标）
    /// 对应的屏幕位置并显隐。必须在 UI 线程同步执行（UpdateAt 的调用点都在 UI 线程）。</summary>
    private void OnMagnifierPresentationChanged(object? sender, EventArgs e)
    {
        if (!Magnifier.IsVisible)
        {
            _magnifierWindow.HideOverlay();
            return;
        }
        var topLeft = CenterPanel.PointToScreen(new Point(0, 0));
        var scaling = TopLevel.GetTopLevel(CenterPanel)?.RenderScaling ?? 1.0;
        _magnifierWindow.Position = new PixelPoint(
            topLeft.X + (int)Math.Round(Magnifier.DesiredPosition.X * scaling),
            topLeft.Y + (int)Math.Round(Magnifier.DesiredPosition.Y * scaling));
        _magnifierWindow.ShowOverlay(this);
    }

    private void UpdatePanelsForSelection()
    {
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        var session = slot?.Session;
        _probe.AttachSession(session);
        _audioPanel.AttachSession(session, session?.ReadMediaInfo());
        // 第二参数是内核的动态元数据来源读数（null = 内核无此导出，面板会显示"不支持"）。
        _mediaPanel.ShowMediaInfo(session?.ReadMediaInfo(), ReadDynamicMetadataSource(session));
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

    /// <summary>指针这一次该算在<b>哪一路</b>头上（放大镜绑定与探针读点共用）。
    ///
    /// <para><b>为什么以位置为准、不以 <c>e.Source</c> 为准</b>：拼合下各路 <c>PlayerSurface</c>
    /// 的 <c>Bounds</c> 恒等于整个对比区且互相重叠（越界部分靠 <c>SetWindowRgn</c> 裁掉），
    /// 视觉树命中给出的只是"这一叠里最顶的那个"，与光标实际落在哪一格无关 ⇒
    /// 放大镜会一直绑同一路、探针也一直读同一路。位置这条路走的是
    /// <see cref="HitSurfaceAt"/>，而那正是判据 <c>AssertMosaicTiling</c> 第④条逐格钉住的那条
    /// （也是滚轮锚点、平移、指针→源像素共用的那条），口径不再多一套。</para>
    ///
    /// <para><c>source</c> 仍然参与：非对比态（单路 / 均匀网格）下各路互不重叠，视觉树命中是准的，
    /// 而位置此时可能落在对比区之外（面板留白）—— 两者取"位置命中优先、否则退回视觉树"，
    /// 不会比原来少认一路。</para>
    ///
    /// <para>⚠ 这条改动只在"事件真的能到这里"时才有后果。子 HWND 自己吃掉
    /// <c>WM_MOUSEMOVE</c>（<c>PlayerSurface</c> 的分支 <c>return 0</c> 不透传），所以视频区上方的
    /// 移动能否到达 Avalonia 顶层<b>没有本机读数</b>；若根本到不了，症状是"放大镜在对比模式下不跟手"
    /// 而不是"绑错路"，那是先于拼合的另一件事。两种解释的区分读数见
    /// <c>tests/output/lane-b-bounds-audit.md</c> 的"没能确定 A"。</para></summary>
    internal PlayerSurface? LaneForPointerOverlay(Visual? source, Point windowPos)
    {
        if (HitSurfaceAt(windowPos) is { } byPos) return byPos;
        for (var v = source; v is not null; v = v.GetVisualParent())
            if (v is PlayerSurface ps) return ps;
        return null;
    }

    /// <summary>中央区指针移动（隧道）：放大镜跟随 + 探针读点（选中表面）。</summary>
    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        var surface = LaneForPointerOverlay(e.Source as Visual, e.GetPosition(this));
        if (surface is null) return;
        var local = e.GetPosition(surface);

        if (_sidebar.MagnifierOn)
        {
            // 采样点必须用**该路 surface 内**的坐标并乘 DPI 缩放；原先传的是
            // e.GetPosition(CenterPanel)（面板全局坐标）且不乘 RenderScaling，
            // 于是多格布局 / DPI 非 100% 时显示的完全不是光标下的内容（docs/15 §2.2）。
            // session 也要绑定到**指针命中的那一路**，而不是当前选中路。
            // 浮窗位置由 UpdateAt 内部用 TranslatePoint 从 surface 局部坐标换算
            // （#22：此前漏了这一步，多格布局下浮窗整体平移一个格原点）。
            var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            if (_sync.Slots.ElementAtOrDefault(surface.Index)?.Session is { } ms)
                Magnifier.AttachSession(ms);
            Magnifier.UpdateAt(surface, local, scaling);
        }
        if (ReferenceEquals(_sidebar.Active, _probe) && surface.Selected)
        {
            var slot = _sync.Slots.ElementAtOrDefault(surface.Index);
            // 只有"会话在 **且** 这一点真的落在画面上"才可能拿到源坐标；其余一律哨兵 -1。
            // 无会话与"光标落在黑边"合成同一条路径：原先这里是两个分支各写一份 -1，
            // 而"报一个看似合法的坐标"这个缺陷恰好只可能从"另开一条分支"回来（#15 一直
            // 判不了就是因为要伪造 Avalonia 指针事件才能跑到那条分支）—— 合成一条之后
            // 非哨兵值只能来自 MapPointerToVideoPixel，它本身要求有会话。
            var mapped = slot?.Session is { } session
                ? MapPointerToVideoPixel(surface, session, local)
                : null;
            var (px, py) = ProbePointOrSentinel(mapped);
            _probe.UpdatePoint(px, py);
        }
    }

    /// <summary>探针该显示的<b>源像素</b>坐标；拿不到就报哨兵 <c>(-1,-1)</c>。
    /// <para>为什么单独抽这一条：这是"探针读数是不是编出来的"唯一的分岔点。
    /// 历史缺陷形状是把表面内的 DIP 坐标当像素下标显示 —— 数字随窗口尺寸变、与片源无关，
    /// 看着完全合法却是错的（P1-5，2026-09-29）。哨兵与"光标在黑边"同一口径，
    /// 面板会显示占位文案而不是坐标。</para></summary>
    internal static (int X, int Y) ProbePointOrSentinel((int X, int Y)? mapped) => mapped ?? (-1, -1);

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
        RefreshAllPositionsAsync();
        UpdatePanelsForSelection();
    }

    private void OnOffsetNudge(long delta100ns)
    {
        if (IsMasterSelected) return;
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        if (slot is null) return;
        slot.Offset100ns += delta100ns;
        RefreshAllPositionsAsync();
        UpdatePanelsForSelection();
    }

    private void OnOffsetReset()
    {
        var slot = _sync.Slots.ElementAtOrDefault(Grid.SelectedIndex);
        if (slot is null) return;
        slot.Offset100ns = 0;
        RefreshAllPositionsAsync();
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

        // 自测模式先钉位再记录 _lastNormal，否则记进去的是系统随手摆的位置。
        PinWindowPositionForSelfTest();

        if (WindowState == WindowState.Normal)
            _lastNormal = (Position, new Size(Width, Height));
        MaybeExitDemoMode();
        // 崩溃自愈：守护进程拉起来的这一代在这里读回上次会话（普通启动不做任何事）
        MaybeRecoverCrashedSession();
        base.OnOpened(e);
    }

    /// <summary>自测模式把主窗钉到主屏工作区原点。</summary>
    /// <remarks>
    /// 便携设置里没有 WindowX/WindowY 时 <see cref="RestoreWindowGeometry"/> 会在第一行整段返回，
    /// 主窗位置就交给系统每次摆放 ⇒ 偶发崩溃的 A/B 两臂不在同一几何起点上，读数不可比。
    /// 尺寸不需要钉：16 轮自测的布局读数（时间轴/传输栏/状态栏）本来就完全一致，只有位置漂。
    /// 落位后回读写日志，不假定 Avalonia 一定生效（本仓有"写尺寸会异步夹走位置"的前科）。
    /// 普通启动路径不受影响。
    /// </remarks>
    private void PinWindowPositionForSelfTest()
    {
        if (!_selfTestMode || Screens.Primary is not { } screen) return;

        var wa = screen.WorkingArea;
        Position = new PixelPoint(wa.X, wa.Y);
        _3FCompare.Core.Diagnostics.AppLog.Info("SelfTest",
            $"主窗钉位 目标=({wa.X},{wa.Y}) 回读=({Position.X},{Position.Y}) 工作区={wa.Width}x{wa.Height}");
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
            // 必须用同步 SeekNow（与 LoadSessionSnapshot 的修复同型）：会话就绪回调里
            // "fire-and-forget Seek + 立即 Play" 没有 happens-before，实测 Play 先落地、
            // Seek 后到，恢复出来从 0 开始跑。
            SeekNow(pos);
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
        _autosaveTimer.Stop();
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
        // 放大镜覆盖窗同理：Hide() 只隐藏不销毁，必须显式 Close（同上）。
        _magnifierWindow.CloseAndDispose();
        base.OnClosed(e);
    }

    // ══════════ 自动化 selftest（走真实打开管线；WinForms RunSelfTest 断言移植） ══════════

    private static volatile string _step = "启动";

    /// <summary>把"当前自测步骤"贴到组件日志 detail 末尾，让事后的崩溃点能**换算成步骤名**
    /// （账本 §十九：死亡钉在步序而不是墙钟，可当时只能靠"第几次 `RegionApply`"这种剂量去猜步骤）。
    /// <para>产品态 <see cref="_step"/> 恒为"启动"，多出来的这一段是无害噪声；不加开关是故意的 ——
    /// 一旦开关决定"有没有步骤名"，崩溃轮就可能恰好没有那半句。</para></summary>
    internal static string StepTag() => " step=" + _step;

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
