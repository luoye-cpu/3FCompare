using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Core.Display;

namespace _3FCompare.Controls;

/// <summary>
/// 多路对比（AB / ABC / ABCD）的<b>分割线 + 可拖动手柄</b>覆盖窗口。
///
/// <para><b>为什么必须是独立窗口</b>：每路视频由 <see cref="PlayerSurface"/> 的
/// <c>NativeControlHost</c> 子 HWND 渲染，子 HWND 永远盖在父窗口之上 —— 任何放进
/// <c>CenterPanel</c> 的普通 Avalonia 控件（例如 <see cref="MagnifierOverlay"/>）
/// 在真机模式下都会被画面遮住（docs/10 §A8）。故这里走 <see cref="ThumbnailPopup"/>
/// 同款方案：独立无边框透明 <see cref="Window"/>。</para>
///
/// <para><b>Z 序：靠 Owner，<u>不</u>用 Topmost</b>。真正的需求是「高于主窗口（及其视频子
/// HWND），但<b>不</b>高于其他应用」。<c>Topmost</c> 做不到后半句 —— 它的语义是"高于<b>所有</b>
/// 非 topmost 窗口"，Win32 置顶带与 owner 关系无关，于是切到别的程序时分割线/手柄仍浮在最上面
/// （真机体验缺陷，本类曾如此）。改用 <see cref="Window.Owner"/> 换来的是：不占任务栏、随 owner
/// 最小化隐藏、且与其他应用同处普通 Z 序带（不在置顶带，判据见 <see cref="HasTopmostStyle"/>）。</para>
///
/// <para>⚠ <b>但 owner 只是"盖得住画面"的必要条件，<u>不保证</u>次序</b>："owned 窗口恒定在
/// owner 之上"这条说法已被真机读数否证 —— 同一个 owner 方案、同一条走链判据，放大镜覆盖窗上线前
/// 可确证的 22 次读数里 <b>3 次（14%）排在主窗之下</b>，其中留了重试链的 2 例在 180ms×6 次采样里
/// 恒为 Below（未观测到自愈），定案见 <c>docs/48 §3.7</c>。本站点是它的同构站点，所以
/// <see cref="IsAboveHostInZOrder"/> 读的是<b>真机现状</b>而不是"结构上必然"；本窗口目前只做
/// 持续观测（<see cref="ZOrderSamples"/> / <see cref="ZOrderBelowCount"/>），<b>未</b>装
/// <c>HWND_TOP</c> 补插 —— 要不要装由这轮量出来的 rate 决定。</para>
///
/// <para><b>Owner 必须显式设</b>（见 <see cref="ShowOverlay"/>）：不能只依赖 <c>Show(host)</c> ——
/// 模式循环会在窗口<b>已可见</b>时再次调用 <see cref="ShowOverlay"/>，那条路径不会走
/// <c>Show(owner)</c>。是否真的挂上 owner 见 <see cref="OwnerHwndAttached"/>，
/// 是否还残留置顶见 <see cref="HasTopmostStyle"/>（两者都是可自动断言的回归闸门）。</para>
///
/// <para><b>透明</b>：<c>TransparencyLevelHint = Transparent</c> + 全透明 <c>Background</c>。
/// Avalonia 12 的 Win32 后端在 DirectComposition 可用时走
/// <c>WS_EX_NOREDIRECTIONBITMAP</c> + DComp 合成，逐像素 alpha 有效；
/// 实际拿到的级别见 <see cref="AchievedTransparency"/>，宿主可据此断言而不是"假设能透明"。</para>
///
/// <para><b>鼠标穿透（关键）</b>：覆盖窗口铺满整个对比区，若不处理会吞掉全部鼠标消息，
/// 使视频表面的选中 / 滚轮缩放 / 拖动平移统统失效。因此本窗口子类化自身 HWND，
/// 在 <c>WM_NCHITTEST</c> 中<b>只在分割线 / 手柄的命中区内</b>返回 <c>HTCLIENT</c>
/// （单轴手柄沿轴放开到整条线，交叉点型只放一个圆点，见 <see cref="HandleIndexAt(double,double,double,double,double)"/>），
/// 其余位置一律 <c>HTTRANSPARENT</c>，把消息让给下层的视频子 HWND
/// （视频 HWND 与本窗口同属 UI 线程，<c>HTTRANSPARENT</c> 的同线程转发语义成立）。
/// 钩子是否安装成功见 <see cref="HitTestHookInstalled"/>。</para>
///
/// <para><b>职责边界</b>：本窗口只做「呈现 + 输入」，<b>不</b>自己改布局。
/// 拖动时先本地乐观更新（跟手），再经 <see cref="SplitChanged"/> 通知宿主。</para>
/// </summary>
public sealed class LayoutOverlayWindow : Window
{
    // ═══════════════════════ 公开 API ═══════════════════════

    /// <summary>手柄被拖动、分割参数变化时触发（值已 <see cref="SplitParams.Clamp"/>）。
    /// 宿主在此更新自身布局；本窗口不直接改布局。</summary>
    public event Action<SplitParams>? SplitChanged;

    /// <summary>当前布局模式（只读，由 <see cref="ShowOverlay"/> / <see cref="Update"/> 设置）。</summary>
    public CompareMode Mode => _mode;

    /// <summary>当前分割参数（只读）。拖动期间为本地乐观值，与宿主最终值一致。</summary>
    public SplitParams Split => _split;

    /// <summary>手柄是否正在被拖动。</summary>
    public bool IsDragging => _dragging;

    /// <summary>当前各手柄的归一化位置 —— <b>就是绘制与命中测试所用的那一份</b>。
    ///
    /// <para>自测据此断言"多手柄模式真的画了 / 判了 N 个手柄"，而不是只查 Core 的
    /// <see cref="CompareLayout.HandleCount"/>：后者只能证明规则正确，证明不了覆盖层
    /// 真的按它去画与判命中（漏改覆盖层的表现是"第二条线看不见、也抓不住"）。</para></summary>
    public (double X, double Y)[] HandlePositions()
    {
        var count = CompareLayout.HandleCount(_mode);
        var result = new (double X, double Y)[count];
        for (var i = 0; i < count; i++)
            result[i] = CompareLayout.HandlePositionAt(_mode, _split, i);
        return result;
    }

    /// <summary><c>WM_NCHITTEST</c> 钩子是否安装成功。
    /// <b>为 false 时本窗口会吞掉对比区内的全部鼠标消息</b>，宿主应据此禁用对比模式或降级。</summary>
    public bool HitTestHookInstalled => _hitTestHookInstalled;

    /// <summary>平台实际给到的透明级别（Windows 上期望 <see cref="WindowTransparencyLevel.Transparent"/>）。</summary>
    public WindowTransparencyLevel AchievedTransparency => ActualTransparencyLevel;

    /// <summary>覆盖窗口当前是否已显示。</summary>
    public bool IsOverlayVisible => IsVisible;

    /// <summary>诊断：平台层是否真的把宿主挂成了 owner（<c>GetWindow(hwnd, GW_OWNER)</c> 等于宿主 HWND）。
    /// <para><see cref="Window.Owner"/> 只是 Avalonia 侧状态。只判"非 0"不够：owner 为空且
    /// <c>ShowInTaskbar=false</c> 时 Avalonia 会把窗口挂到离屏父窗口上，那同样非 0，但 Z 序关系
    /// 完全不是我们要的（覆盖层会盖不住视频）。故必须与宿主 HWND 逐一比对。</para></summary>
    public bool OwnerHwndAttached
    {
        get
        {
            if (_hwnd == nint.Zero) return false;
            var owner = GetWindow(_hwnd, GW_OWNER);
            return owner != nint.Zero && owner == _host?.TryGetPlatformHandle()?.Handle;
        }
    }

    /// <summary>诊断：走一次顶层链，报本覆盖层<b>此刻</b>是否排在主窗口之上（顶层 Z 序里更靠前）。
    /// <para>这是 airspace 不退化（分割线/手柄不被视频画面盖住）的可自动判定形式：视频子 HWND
    /// 属于主窗口，只要本窗口高于 owner 就必然高于它们。</para>
    ///
    /// <para>⚠ <b>它读的是真机现状，不是"结构上必然成立"的事</b>：owner 只是必要条件 —— Win32
    /// <b>不保证</b> owned 窗口恒定在 owner 之上，也<b>不保证</b>两者之间不会插进第三个窗口。
    /// 同一套 owner 方案 + 同一条走链判据在放大镜覆盖窗上实测：上线前可确证的 22 次读数里
    /// <b>3 次（14%）为 Below</b>，其中 2 例在 180ms×6 次采样里恒为 Below（未观测到自愈），
    /// 定案见 <c>docs/48 §3.7</c>。⇒ 本属性为 <c>true</c> 只代表"这一拍在上面"，为 <c>false</c>
    /// 则是真出事了（用户看不见分割线）；长期发生率见 <see cref="ZOrderSamples"/> /
    /// <see cref="ZOrderBelowCount"/>。</para>
    ///
    /// <para>⚠ 取证<b>不能</b>换成 <c>WindowFromPoint</c> 一类命中探针：本窗口对
    /// <c>WM_NCHITTEST</c> 在手柄命中圈之外一律返回 <c>HTTRANSPARENT</c>（见
    /// <see cref="HitTestWndProc"/>），命中测试按设计落到下层窗口 ⇒ 该探针对"在不在上面"
    /// 没有分辨力，反而会在覆盖层正常工作时报"被遮挡"。</para></summary>
    public bool IsAboveHostInZOrder => QueryChainPosition() == ChainPos.Above;

    /// <summary>诊断：是否仍带 <c>WS_EX_TOPMOST</c>。
    /// <para>这是自动化唯一能拿到的 Z 序证据 —— 真机 Z 序（"有没有盖住别的应用"）无法在进程内
    /// 断言，但"还在不在置顶带"可以。修掉"浮在所有窗口之上"后此值必须为 <c>false</c>。</para></summary>
    public bool HasTopmostStyle =>
        _hwnd != nint.Zero && (GetWindowLongPtr(_hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    // ═══════════════ Z 序持续观测（只读数，不动手） ═══════════════
    //
    // 为什么要这一段：上面的判据原先只在自测进入某一步时被读一次，而放大镜站点实测到的翻转出现在
    // **后续的呈现 / 位置回调**上 ⇒ 一次性采样在统计上等于没测。下面这几个计数只走链、只累加，
    // 不调 SetWindowPos、不写样式、不动位置与焦点（纯观测）；是否给本站点装 HWND_TOP 补插，
    // 由这一轮量出来的 rate 决定。

    /// <summary>顶层走链的累计次数（rate 的分母）。只在窗口<b>已显示</b>且自身与宿主 HWND 都拿得到
    /// 时才增加 —— 隐藏窗口仍在顶层链里，那时读到的 Below 与"用户看不见分割线"无关，是假样本。</summary>
    public int ZOrderSamples { get; private set; }

    /// <summary>其中判为 <b>Below</b>（本窗口排在主窗之下 ⇒ 分割线/手柄被视频画面盖住）的次数。</summary>
    public int ZOrderBelowCount { get; private set; }

    /// <summary>其中走完全链都没见到本窗口的次数。它与 Below 是<b>两种病</b>（Z 序 vs show/hide
    /// 竞态或句柄取错），必须分开数，否则会把后者读成 Z 序缺陷。</summary>
    public int ZOrderNotInChainCount { get; private set; }

    /// <summary>其中<b>进入 Below 的边缘</b>次数（上一次读数不是 Below、这次是）。与
    /// <see cref="ZOrderBelowCount"/> 一比就能把"被压下去一次后长期待着（不自愈 —— 放大镜实测
    /// 正是这种）"和"反复抖动"分开：前者 below 很大而 flips=1，后者两者相近。</summary>
    public int ZOrderFlipsToBelow { get; private set; }

    /// <summary>诊断：<b>宿主</b>是否带 <c>WS_EX_TOPMOST</c>（参照
    /// <see cref="MagnifierOverlayWindow.HostHasTopmostStyle"/> 的同名读数）。置顶带整体高于普通带：
    /// 若主窗在置顶带而本窗口不在，那 <c>HWND_TOP</c> 也补不动 —— 这条读数用来区分"补插无效"与
    /// "补插没被执行"，故一并写进翻转日志。</summary>
    public bool HostHasTopmostStyle
    {
        get
        {
            var hostHwnd = _host?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
            return hostHwnd != nint.Zero
                   && (GetWindowLongPtr(hostHwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        }
    }

    /// <summary>走链的三态（内部判据）。分开"被压到主窗之下"与"压根不在链里"，理由见
    /// <see cref="ZOrderNotInChainCount"/>。</summary>
    private enum ChainPos { Above, Below, NotInChain }

    /// <summary>上一次观测到的状态，<c>null</c> = 还没采过。只用来把日志压到翻转那一拍。</summary>
    private ChainPos? _lastZOrder;

    /// <summary>自顶向下走一次顶层链。<b>纯查询</b>：不计数、不写日志、不改任何窗口状态。
    /// <para>链首必须用 <c>GetTopWindow(nint.Zero)</c>：<c>GetWindow(0, GW_HWNDFIRST)</c> 返回 0
    /// ⇒ 整条走链一次都不执行 ⇒ 恒判 NotInChain（放大镜站点实测踩过，<c>docs/48 §3.7 ①</c>）。</para></summary>
    private ChainPos QueryChainPosition()
    {
        if (_hwnd == nint.Zero) return ChainPos.NotInChain;
        var hostHwnd = _host?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hostHwnd == nint.Zero) return ChainPos.NotInChain;

        for (var h = GetTopWindow(nint.Zero); h != nint.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (h == _hwnd) return ChainPos.Above;
            if (h == hostHwnd) return ChainPos.Below;
        }
        return ChainPos.NotInChain;   // 走完全链都没见到本窗口
    }

    /// <summary>累计一次走链读数，并<b>只在"进入 Below"的那一拍</b>落一条日志
    /// （上一次读数不是 Below 而这次是 —— 连续 Below 不重复写；首个读数即 Below 时没有前置状态，
    /// 也写一条并标 <c>无前置→Below</c>：放大镜实测的失效形态正是"一进去就一直 Below"，
    /// 若只认 Above→Below 这一条边，那种情况反而一条证据都不留）。
    ///
    /// <para><b>为什么日志不能无条件写</b>：采样点挂在产品热路径上（拖动分割线、主窗移动/缩放都会
    /// 走到），日志条数必须与"异常回合数"同阶而不能与"调用次数"同阶 —— 本项目有过"组件日志节流是
    /// 全局共享"把判据读成假红的前车之鉴，无条件写日志只会制造噪声、甚至灌满落盘队列。</para>
    ///
    /// <para><b>副作用清单：无</b>。只有 Win32 只读查询（GetTopWindow / GetWindow /
    /// GetWindowLongPtr）与自身计数，不触碰位置、尺寸、Z 序、焦点、激活状态。</para></summary>
    private void ObserveZOrder()
    {
        if (_closed || !IsVisible || _hwnd == nint.Zero) return;
        if ((_host?.TryGetPlatformHandle()?.Handle ?? nint.Zero) == nint.Zero) return;

        var previous = _lastZOrder;
        var pos = QueryChainPosition();
        _lastZOrder = pos;
        ZOrderSamples++;

        if (pos == ChainPos.Below)
        {
            ZOrderBelowCount++;
            if (previous == ChainPos.Below) return;   // 连续 Below：只在进入的那一拍写
            ZOrderFlipsToBelow++;
            var from = previous?.ToString() ?? "无前置";
            AppLog.Warn("ZOrder",
                $"[LayoutOverlayWindow] {from}→Below" +
                $" owner={OwnerHwndAttached} topmost={HasTopmostStyle}" +
                $" hostTopmost={HostHasTopmostStyle}" +
                $" samples={ZOrderSamples} below={ZOrderBelowCount}");
        }
        else if (pos == ChainPos.NotInChain)
        {
            ZOrderNotInChainCount++;
        }
    }

    /// <summary>落一条累计汇总（只在"这一轮观测到此结束"的位置调用，与热路径无关）。
    /// 一次都没采到就不写 —— 那说明这轮压根没进过对比模式，写出来只是噪声。
    ///
    /// <para><b>为什么要 <c>where</c>：本站点的"窗口关闭"那条多半落不了盘</b> —— 宿主的
    /// <c>OnClosing</c> 先调 <c>AppLog.Shutdown()</c>（MainWindow.axaml.cs:3034），之后才在
    /// <c>OnClosed</c> 里 <c>CloseAndDispose()</c>（同文件 3114），writer 已关 ⇒ 那一条被丢弃。
    /// 故 <see cref="HideOverlay"/>（退出对比模式，writer 仍在）是本站点唯一稳定能落盘的汇总点，
    /// 两处都打、用 <c>where</c> 区分。</para></summary>
    private void LogZOrderSummary(string where)
    {
        if (ZOrderSamples <= 0) return;
        AppLog.Info("ZOrder",
            $"[LayoutOverlayWindow] 汇总({where}) samples={ZOrderSamples} below={ZOrderBelowCount}" +
            $" notInChain={ZOrderNotInChainCount} flips={ZOrderFlipsToBelow} last={_lastZOrder}");
    }

    // ═══════════════════════ 状态 ═══════════════════════

    private Window? _host;
    private Visual? _anchor;

    private CompareMode _mode = CompareMode.Ab;
    private SplitParams _split = SplitParams.Default(CompareMode.Ab);

    private bool _dragging;
    /// <summary>正在被拖的是第几个手柄（<see cref="CompareLayout.HandleCount"/> 之内的索引）。
    /// 单手柄模式恒为 0；<see cref="CompareMode.AbcColumns"/> 有两条竖线 ⇒ 0 / 1 各管一条。
    /// 拖动结束时<b>不</b>复位到 0：模式切换的那一拍若还拿着旧索引，取轴只会拿到"最后一个手柄"，
    /// 而复位成 0 会让"拖动中切模式"把第一条线拽走 —— 留着旧索引是更保守的选择
    /// （<see cref="CompareLayout.HandleAxisAt"/> 对越界索引按最后一个手柄处理）。</summary>
    private int _dragHandle;
    private bool _syncing;
    private bool _closed;

    /// <summary>手柄命中半径（DIP）。比视觉半径大一圈，手指/鼠标更容易抓到。</summary>
    private const double HitRadius = 14.0;

    /// <summary>手柄视觉半径（DIP）。</summary>
    private const double HandleRadius = 9.0;

    private Cursor _cursor = CursorAll;

    // 复用的线段缓冲：Render 每帧调用，避免每帧分配（本项目对热路径分配敏感）。
    private readonly List<(Point A, Point B)> _segments = new(8);

    // U3：Cursor 是 IDisposable，原先 CursorFor 在每次 ApplyState（拖拽移动中宿主会回灌
    // 分割参数 ⇒ 每个 pointer move 一次）都 new 一个且从不释放，与"热路径零分配"自述相悖。
    // 三个轴向各缓存一份，仅轴变化时换引用即可。静态字段在首次实例化（Avalonia 已启动）时才初始化。
    private static readonly Cursor CursorX = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CursorY = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor CursorAll = new(StandardCursorType.SizeAll);

    // ═══════════════════════ 构造 ═══════════════════════

    public LayoutOverlayWindow()
    {
        // Avalonia 12：WindowDecorations 是新 API；旧 SystemDecorations 已废弃（CS0618）。
        SetCurrentValue(WindowDecorationsProperty, global::Avalonia.Controls.WindowDecorations.None);
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 逐像素透明：让下方视频透出。命中测试需要非 null 的 Background
        // （Avalonia 侧靠它参与 InputHitTest），而 alpha=0 的纯透明刷在 DComp 合成下不可见。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = new SolidColorBrush(Colors.Transparent);

        // 刻意**不**置顶（显式写 false 以固化意图）：置顶带高于所有非 topmost 窗口、与 owner 无关，
        // 会让分割线在切到别的程序后仍浮在最上面。Z 序改由 Owner 提供（见类注释与 ShowOverlay）。
        Topmost = false;
        ShowActivated = false;      // 显示/点击都不抢主窗口激活（Space 等快捷键保持在主窗口）
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Focusable = false;
        // 分割线/手柄必须跟随主题（视频区本身不参与，但覆盖层要跟）。
        // 自绘窗口不会自动失效，需显式重绘。
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    // ═══════════════════════ 对外方法 ═══════════════════════

    /// <summary>在 <paramref name="anchor"/>（对比区容器，通常是 <c>CompareGridView</c>）
    /// 之上显示覆盖层，并开始跟随宿主窗口的移动 / 缩放 / DPI 变化。
    /// <para>单元格几何由本窗口用 <see cref="CompareLayout.ComputeCells"/> 自行推导，
    /// 调用方无需（也不能）传 cells —— 避免"宿主算一份、覆盖层再算一份"的两份几何漂移。</para></summary>
    /// <param name="host">宿主主窗口，用于订阅位置/尺寸变化并作为 owner。</param>
    /// <param name="anchor">对比区容器，覆盖窗口精确覆盖它的屏幕矩形。</param>
    /// <param name="mode">布局模式。</param>
    /// <param name="split">初始分割参数（内部会 Clamp）。</param>
    public void ShowOverlay(Window host, Visual anchor, CompareMode mode, SplitParams split)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(anchor);
        if (_closed) throw new InvalidOperationException("窗口已关闭，不能再 ShowOverlay；请新建实例。");

        DetachHost();
        _host = host;
        // Owner 是本窗口 Z 序的唯一来源（见类注释）。必须显式设，不能只靠下面的 Show(host)：
        // ShowOverlay 会在窗口已可见时被再次调用（C 键模式循环），那条路径跳过 Show(owner)。
        Owner = host;
        _anchor = anchor;
        ApplyState(mode, split);
        AttachHost();

        // 先定位再显示，避免在默认位置闪一帧。
        SyncGeometry();
        if (!IsVisible) Show(host);
        SyncGeometry();
        InvalidateVisual();
        // 尾部观测一次：本方法是"进入 / 切换对比模式"的呈现入口（宿主每次 EnterCompareMode 都调，
        // 含 C 键模式循环），语义对齐 MagnifierOverlayWindow 挂在 ShowOverlay 尾部的同款读数。
        ObserveZOrder();
    }

    /// <summary>更新模式 / 分割参数并重绘（宿主在自身布局变化后调用）。
    /// 拖动中宿主回灌同一值时是幂等的。顺带做一次 Z 序走链观测（只累加计数，见
    /// <see cref="ZOrderSamples"/>；日志只在"进入 Below"的那一拍写）。</summary>
    public void Update(CompareMode mode, SplitParams split)
    {
        ApplyState(mode, split);
        InvalidateVisual();
        // 热路径观测点：宿主每次回灌分割参数都走这里（拖动中每个 pointer move 一次）。
        ObserveZOrder();
    }

    /// <summary>隐藏覆盖层并解除对宿主的跟随订阅（可再次 <see cref="ShowOverlay"/> 复用实例）。
    ///
    /// <para><b>B6：刻意不叫 <c>Hide</c></b>。若本方法以 <c>new void Hide()</c> 遮蔽基类
    /// <see cref="Window.Hide"/>，则任何持有 <see cref="Window"/> 引用的路径（框架内部、
    /// 宿主泛型代码、将来新增的调用点）调 <c>Hide()</c> 都会走基类实现而<b>不</b>
    /// <see cref="DetachHost"/> —— 本窗口会继续强订阅主窗口的
    /// Position/Size/Scaling 与 anchor 的 PropertyChanged ⇒ 覆盖层被隐藏却仍被主窗口
    /// 强引用，且每次宿主布局变化都白跑一次 <see cref="SyncGeometry"/>。
    /// 改名让"必须解除订阅"这件事在调用点就显式可见，不再依赖调用者的静态类型。</para>
    ///
    /// <para>末尾落一条 Z 序观测汇总（累计快照）：这是本站点稳定能落盘的汇总点 —— 本窗口的
    /// <c>OnClosed</c> 只发生在应用退出链上，那时宿主的 <c>AppLog.Shutdown()</c> 已经跑过，
    /// 理由见 <see cref="LogZOrderSummary"/>。</para></summary>
    public void HideOverlay()
    {
        DetachHost();
        _dragging = false;
        base.Hide();
        LogZOrderSummary("HideOverlay");
    }

    /// <summary>彻底关闭并释放。宿主窗口关闭时必须调用，否则进程里会残留一个顶层窗口，
    /// 且 Avalonia 会因仍有存活 Window 而不退出消息循环（同 <see cref="ThumbnailPopup.CloseAndDispose"/>）。</summary>
    public void CloseAndDispose()
    {
        if (_closed) return;
        DetachHost();
        _dragging = false;
        _segments.Clear();
        if (IsVisible) base.Hide();
        Close();
    }

    // ═══════════════════════ 状态 / 跟随 ═══════════════════════

    private void ApplyState(CompareMode mode, SplitParams split)
    {
        _mode = mode;
        _split = split.Clamp();
        SetCursorForHandle(_dragging ? _dragHandle : 0);
    }

    /// <summary>把光标设成第 <paramref name="handleIndex"/> 个手柄的轴向光标。
    /// 拖动中必须用<b>当前手柄</b>的轴 —— 三列模式下两个手柄都是竖线（光标相同），
    /// 但将来若出现"一条竖线 + 一条横线"的模式，静止时按第一个手柄给光标会给出错误提示。
    /// 只在轴真的变化时换引用（U3：拖拽热路径不得 new Cursor）。</summary>
    private void SetCursorForHandle(int handleIndex)
    {
        var next = CursorFor(CompareLayout.HandleAxisAt(_mode, handleIndex));
        if (ReferenceEquals(next, _cursor)) return;
        _cursor = next;
        Cursor = next;
    }

    private void AttachHost()
    {
        if (_host is null) return;
        _host.PositionChanged += OnHostPositionChanged;
        _host.SizeChanged += OnHostSizeChanged;
        _host.ScalingChanged += OnHostScalingChanged;
        // U2：自身被拖到另一块显示器时 RenderScaling 变化，必须重算 DIP 尺寸（见 EffectiveScaling）。
        ScalingChanged += OnSelfScalingChanged;
        if (_anchor is not null) _anchor.PropertyChanged += OnAnchorPropertyChanged;
    }

    private void DetachHost()
    {
        if (_host is not null)
        {
            _host.PositionChanged -= OnHostPositionChanged;
            _host.SizeChanged -= OnHostSizeChanged;
            _host.ScalingChanged -= OnHostScalingChanged;
        }
        ScalingChanged -= OnSelfScalingChanged;
        if (_anchor is not null) _anchor.PropertyChanged -= OnAnchorPropertyChanged;
    }

    private void OnHostPositionChanged(object? sender, PixelPointEventArgs e) => SyncGeometry();

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => SyncGeometry();

    private void OnHostScalingChanged(object? sender, EventArgs e) => SyncGeometry();

    private void OnSelfScalingChanged(object? sender, EventArgs e) => SyncGeometry();

    private void OnAnchorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 侧栏拖动 / 全屏切换 / 网格预设变化都会改对比区的 Bounds；只认这一条即可覆盖。
        if (e.Property == Visual.BoundsProperty) SyncGeometry();
    }

    /// <summary>把对比区的屏幕矩形同步给本窗口：位置用物理像素，尺寸换算回 DIP。</summary>
    private void SyncGeometry()
    {
        if (_syncing || _closed) return;
        if (_host is null || _anchor is null) return;
        if (TopLevel.GetTopLevel(_anchor) is null) return; // 尚未挂上视觉树 / 已摘除

        var bounds = _anchor.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        PixelPoint topLeft, bottomRight;
        try
        {
            topLeft = _anchor.PointToScreen(new Point(0, 0));
            bottomRight = _anchor.PointToScreen(new Point(bounds.Width, bounds.Height));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[LayoutOverlayWindow] PointToScreen 失败: {ex.Message}");
            return;
        }

        var pxW = bottomRight.X - topLeft.X;
        var pxH = bottomRight.Y - topLeft.Y;
        if (pxW <= 0 || pxH <= 0) return;

        // 尺寸按**对比区所在显示器**的缩放换算（Position 已定位到对比区左上角，两者必然同屏）。
        //
        // U2：这里刻意**不先取本窗口的 RenderScaling**。ShowOverlay 在 Show() 之前就先调一次
        // SyncGeometry（避免在默认位置闪一帧），而此时本窗口还没被挂到任何显示器上，
        // RenderScaling 会返回 1.0 —— 它不是"未知"，只是"尚未确定"，且 1.0 本身是合法缩放，
        // 无法靠数值区分。原先的 `!(scaling > 0)` 判断因此拦不住它：150% DPI 下首帧会按 1.0
        // 折算，窗口比对比区大 50%，直到下一次布局事件才纠正。
        // anchor 已在视觉树里，其 TopLevel 的 RenderScaling 从第一次调用起就是可信的，故优先用它。
        var scaling = EffectiveScaling();

        var dipW = pxW / scaling;
        var dipH = pxH / scaling;

        _syncing = true;
        try
        {
            if (Position != topLeft) Position = topLeft;
            // 浮点抖动不触发重排：差 < 0.5 DIP 视为同值。
            if (Math.Abs(Width - dipW) > 0.5) Width = dipW;
            if (Math.Abs(Height - dipH) > 0.5) Height = dipH;
        }
        finally
        {
            _syncing = false;
        }

        // 落位之后再观测一次（前面那些守卫 return 都不计数，采的是"真的重摆了一遍"）：
        // 主窗移动 / 缩放 / DPI 变化 / 侧栏拖动都走这条位置同步路径 ⇒ 覆盖层每次被重新摆一遍都会
        // 留下一个读数 —— 放大镜那次翻转正是出现在这类后续位置/呈现回调上。
        ObserveZOrder();
    }

    /// <summary>对比区所在显示器的缩放（物理像素 / DIP），保证为正。
    ///
    /// <para>优先级：anchor 所在 <see cref="TopLevel"/> 的 <c>RenderScaling</c> → 宿主 →
    /// 本窗口自身 → 1.0。首选 anchor 的理由见 <see cref="SyncGeometry"/> 的 U2 注释：
    /// 本窗口在 <see cref="ShowOverlay"/> 的首次 <see cref="SyncGeometry"/> 时尚未挂到显示器上，
    /// 其 <c>RenderScaling</c> 恒为 1.0（合法的默认值，不是"未知"），据此折算会算错高 DPI 首帧。</para>
    /// <para>自身项保留作兜底：anchor 已被摘出视觉树时（<c>GetTopLevel</c> 为 null）它至少是 1.0。</para></summary>
    private double EffectiveScaling()
    {
        var scaling = TopLevel.GetTopLevel(_anchor)?.RenderScaling ?? 0d;
        if (!(scaling > 0) && _host is not null) scaling = _host.RenderScaling;
        if (!(scaling > 0)) scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        return scaling;
    }

    // ═══════════════════════ 输入（拖动） ═══════════════════════

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var handle = HandleIndexAt(e.GetPosition(this));
        if (handle < 0) return;

        _dragging = true;
        _dragHandle = handle; // 记住抓的是哪一个：三列模式下两条线的拖动目标不同
        SetCursorForHandle(handle);
        e.Pointer.Capture(this); // 拖出窗口仍要收到 Move/Released
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        ApplyDrag(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    /// <summary>把指针位置（窗口内 DIP）换算成归一化坐标，按<b>当前手柄</b>的轴决定更新 X / Y / 两者，
    /// Clamp 后本地更新并抛出事件。
    ///
    /// <para><b>多手柄模式下"轴"不足以决定改哪个分量</b>：<see cref="CompareMode.AbcColumns"/> 的
    /// 两个手柄轴都是 <see cref="SplitAxis.X"/>，但它们分别管<b>第一条</b>与<b>第二条</b>竖线
    /// （X 与 Y 两个分量）。故轴为 X / Y 时还要按手柄索引挑分量：索引 0 改 X、索引 1 改 Y。
    /// 单手柄模式索引恒为 0，落到"改 X"/"改 Y"，与改动前<b>逐字一致</b>。</para></summary>
    private void ApplyDrag(Point p)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var nx = p.X / w;
        var ny = p.Y / h;

        // 分量身份必须问 Core：多手柄模式（ABC 三列）的两条线存在 X / Y 两个分量里，
        // 而手柄画在**排序后**的位置上 —— 两条线交叉后"手柄 0 ↔ X"的固定映射就反了，
        // 表现为"拖动左边那条却改变了右边的分界"。见 CompareLayout.HandleComponentAt。
        var component = CompareLayout.HandleComponentAt(_mode, _split, _dragHandle);
        var next = CompareLayout.HandleAxisAt(_mode, _dragHandle) switch
        {
            SplitAxis.X => component == 0 ? new SplitParams(nx, _split.Y) : new SplitParams(_split.X, nx),
            SplitAxis.Y => component == 0 ? new SplitParams(_split.X, ny) : new SplitParams(ny, _split.Y),
            _ => new SplitParams(nx, ny), // Both：交叉点
        };
        next = next.Clamp();
        if (next == _split) return; // record struct 值相等：原地抖动不产生事件风暴

        _split = next; // 乐观本地更新：不必等宿主回灌即可跟手
        InvalidateVisual();
        SplitChanged?.Invoke(next);
    }

    /// <summary>指针（窗口内 DIP）落在第几个手柄的命中区内；未命中返回 -1。
    /// 手柄数量由 <see cref="CompareLayout.HandleCount"/> 给出，位置由
    /// <see cref="HandleIndexAt(double,double,double,double,double)"/> 的共用判据给出 —— 本窗口不自己推导几何（见类注释）。</summary>
    private int HandleIndexAt(Point p) => HandleIndexAt(p.X, p.Y, Bounds.Width, Bounds.Height, HitRadius);

    /// <summary>自测探针口：把一次 DIP 坐标按<b>与真实点击完全同一条</b>判据换算成手柄序号。
    /// <para>为什么要暴露它：命中区从"线中间的圆点"改成"整条线"之后，肉眼看不出差别，
    /// 而判据错了的表现是"抓线拖不动"或"哪儿都能抓、误触吞掉画面上的滚轮"。
    /// 这里只读不写状态，与 <see cref="HandlePositions"/> 同性质。</para></summary>
    internal int HitHandleIndexAt(Point dip) => HandleIndexAt(dip);

    /// <summary>命中判据的<b>唯一实现</b>：DIP 路径（Avalonia 事件）与物理路径
    /// （<c>WM_NCHITTEST</c>）都换调到它，只是传入各自的坐标与半径 —— 同一条规则出现两份实现，
    /// 就是本项目反复踩到的"其中一份是错的"（真机表现：光标显示能拖、点下去却让给视频层）。
    ///
    /// <para><b>单轴手柄沿轴放开到整条线</b>：<see cref="SplitAxis.X"/> 的竖线不论高低都能抓，
    /// <see cref="SplitAxis.Y"/> 的横线不论左右都能抓。"左右拉动对比"就建立在这条线上，
    /// 用户的说法是"中间的线可以拉动"，而不是"线中间那个圆点可以拉动"。
    /// <b>交叉点手柄（<see cref="SplitAxis.Both"/>，ABC / ABCD）刻意不放开</b>：那种手柄一次改两个分量，
    /// 沿整条线抓会把另一轴一起拽到指针高度，用户看到的是"没抓的那条线跳了"。</para></summary>
    private int HandleIndexAt(double px, double py, double w, double h, double radius)
    {
        if (w <= 0 || h <= 0) return -1;

        var count = CompareLayout.HandleCount(_mode);
        for (var i = 0; i < count; i++)
        {
            var (hx, hy) = CompareLayout.HandlePositionAt(_mode, _split, i);
            var dx = px - hx * w;
            var dy = py - hy * h;
            var hit = CompareLayout.HandleAxisAt(_mode, i) switch
            {
                SplitAxis.X => System.Math.Abs(dx) <= radius,
                SplitAxis.Y => System.Math.Abs(dy) <= radius,
                _ => dx * dx + dy * dy <= radius * radius, // 交叉点型：只放一个圆点
            };
            if (hit) return i;
        }
        return -1;
    }

    /// <summary>指针（窗口内 DIP）是否落在任一手柄的命中区内。</summary>
    private bool IsOnHandle(Point p) => HandleIndexAt(p) >= 0;

    /// <summary>轴向 → 光标。返回**静态缓存**实例（U3：不得在拖拽热路径上 new，见字段注释）。</summary>
    private static Cursor CursorFor(SplitAxis axis) => axis switch
    {
        SplitAxis.X => CursorX,
        SplitAxis.Y => CursorY,
        _ => CursorAll,
    };

    // ═══════════════════════ 绘制 ═══════════════════════

    // 分割线/手柄配色取主题令牌：强调色随主题（浅色下金黄压暗到琥珀），
    // 光晕与描边环是"压在视频上"的对比度辅助，深浅同值（见 ThemeTokens）。
    // 非 static readonly：主题切换后要能重取。
    // 字段初值取令牌表的深色值：首次 EnsureTheme 会按当前变体覆盖，运行时不生效，
    // 只为消除硬编码副本（单一真源见 ThemeTokens）。
    private Pen _haloPen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayHaloBrush").Dark)), 4);
    private Pen _linePen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark)), 1.5);
    private IBrush _handleFill = new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark));
    private Pen _handleRingPen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayRingBrush").Dark)), 2);
    private Avalonia.Styling.ThemeVariant? _themeTag;

    private void EnsureTheme()
    {
        if (_themeTag == ActualThemeVariant) return;
        _themeTag = ActualThemeVariant;
        _haloPen = new Pen(ThemePalette.Brush(this, "OverlayHaloBrush"), 4);
        _linePen = new Pen(ThemePalette.Brush(this, "AccentBrush"), 1.5);
        _handleFill = ThemePalette.Brush(this, "AccentBrush");
        _handleRingPen = new Pen(ThemePalette.Brush(this, "OverlayRingBrush"), 2);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        EnsureTheme();

        _segments.Clear();
        foreach (var c in CompareLayout.ComputeCells(_mode, _split))
        {
            var x0 = c.X * w;
            var y0 = c.Y * h;
            var x1 = (c.X + c.Width) * w;
            var y1 = (c.Y + c.Height) * h;

            // 只取每条单元格的「右边界」与「下边界」，且仅当它落在 (0,1) 开区间内。
            // 理由：ComputeCells 保证无缝铺满 [0,1]²，于是任意一条内部竖分割线
            // 恰好等于「其左侧所有单元格的右边界」的并集 —— 只发右/下边即可无重无漏地
            // 覆盖全部内部边界，也自然避免了同一条线被画两遍（半透明光晕叠加会变黑）。
            // 贴容器外沿的边不画：视频格自身已有边框，再描一圈只会显脏。
            if (c.X + c.Width < 1.0 - CompareLayout.Epsilon)
                _segments.Add((new Point(x1, y0), new Point(x1, y1)));
            if (c.Y + c.Height < 1.0 - CompareLayout.Epsilon)
                _segments.Add((new Point(x0, y1), new Point(x1, y1)));
        }

        // 两趟绘制：先铺完所有深色光晕，再画亮线。
        // 若逐段「晕+线」，后一段的光晕会盖住前一段的亮线，交叉处出现断口。
        foreach (var (a, b) in _segments) context.DrawLine(_haloPen, a, b);
        foreach (var (a, b) in _segments) context.DrawLine(_linePen, a, b);

        // 手柄：位置与数量都由 Core 统一给出（AB 的 Y 固定 0.5，竖线中点；
        // ABC 三列有两条竖线 ⇒ 两个手柄）。逐个画，不能只画第一个 —— 否则三列模式下
        // 第二条线可拖但看不见抓手，用户无从下手。
        var handles = CompareLayout.HandleCount(_mode);
        for (var i = 0; i < handles; i++)
        {
            var (hx, hy) = CompareLayout.HandlePositionAt(_mode, _split, i);
            context.DrawEllipse(_handleFill, _handleRingPen, new Point(hx * w, hy * h), HandleRadius, HandleRadius);
        }
    }

    // ═══════════════════════ Win32：命中测试钩子 ═══════════════════════

    private nint _hwnd;
    private nint _origWndProc;
    private bool _hitTestHookInstalled;
    private WndProcDelegate? _hookProc; // 必须字段持有：委托被 GC 后原生回调会踩空指针

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private const int GWLP_WNDPROC = -4;
    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_NOACTIVATE = 0x08000000;
    private const nint WS_EX_TOPMOST = 0x00000008;
    /// <summary><c>GetWindow</c> 命令：取 owner 窗口（对顶层窗口即 <c>GWL_HWNDPARENT</c> 所指）。</summary>
    private const uint GW_OWNER = 4;
    /// <summary><c>GetWindow</c> 命令：Z 序里下一个（更靠下）的窗口。</summary>
    private const uint GW_HWNDNEXT = 2;
    private const uint WM_NCHITTEST = 0x0084;
    private const nint HTCLIENT = 1;
    private const nint HTTRANSPARENT = -1;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        InstallHitTestHook();

        // 不假装透明一定成功：拿不到逐像素 alpha 时窗口会以不透明底覆盖视频，
        // 这是必须在真机上暴露而不是静默降级的失效模式，故显式记到 stderr。
        if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            Console.Error.WriteLine(
                $"[LayoutOverlayWindow] 未取得逐像素透明（实际 {ActualTransparencyLevel}），" +
                "覆盖层可能遮挡视频画面；宿主应据 AchievedTransparency 降级或改用 Win32 分层窗口。");
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭链最上游落一条汇总（此时计数已定）。⚠ 应用退出路径上宿主已在 OnClosing 里
        // AppLog.Shutdown()，这一条多半被丢弃 ⇒ 稳定的汇总见 HideOverlay（见该方法注释）。
        LogZOrderSummary("OnClosed");
        _closed = true;
        UninstallHitTestHook();
        base.OnClosed(e);
    }

    private void InstallHitTestHook()
    {
        if (_hwnd != nint.Zero) return;

        var handle = TryGetPlatformHandle();
        if (handle is null || handle.Handle == nint.Zero)
        {
            Console.Error.WriteLine("[LayoutOverlayWindow] 取不到平台 HWND，命中测试钩子未安装（对比区鼠标消息将被吞掉）。");
            return;
        }

        _hwnd = handle.Handle;

        // 点击手柄不激活本窗口：主窗口的键盘快捷键（Space/方向键）不被打断。
        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);

        _hookProc = HitTestWndProc;
        var prev = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hookProc));
        if (prev == nint.Zero)
        {
            Console.Error.WriteLine($"[LayoutOverlayWindow] 子类化失败（Win32 错误 {Marshal.GetLastWin32Error()}），命中测试钩子未安装。");
            _hookProc = null;
            return;
        }

        _origWndProc = prev;
        _hitTestHookInstalled = true;
    }

    private void UninstallHitTestHook()
    {
        if (_hwnd != nint.Zero && _origWndProc != nint.Zero)
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _origWndProc);
        _origWndProc = nint.Zero;
        _hwnd = nint.Zero;
        _hookProc = null;
        _hitTestHookInstalled = false;
    }

    /// <summary>子类化窗口过程：只截 <c>WM_NCHITTEST</c>，其余原样转发给 Avalonia 的窗口过程。
    /// 手柄命中圈内返回 <c>HTCLIENT</c>（消息正常进来），其余位置返回 <c>HTTRANSPARENT</c>
    /// 把消息让给下层的视频子 HWND —— 这是"覆盖窗口不吞输入"的唯一关键点。</summary>
    private nint HitTestWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_NCHITTEST && _hitTestHookInstalled)
        {
            // lParam 是**屏幕坐标**，低/高 16 位各为有符号 short。
            var sx = (short)(lParam & 0xFFFF);
            var sy = (short)((lParam >> 16) & 0xFFFF);
            var pt = new POINT { X = sx, Y = sy };
            if (ScreenToClient(hwnd, ref pt) && IsOnHandlePhysical(pt.X, pt.Y))
                return HTCLIENT;
            return HTTRANSPARENT;
        }
        return CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>与 <see cref="HandleIndexAt(Point)"/> <b>同一条</b>判据，只是输入是**物理客户区像素**
    /// （<c>WM_NCHITTEST</c> 拿到的是物理坐标，不能直接与 DIP 比较）：半径按 <see cref="RenderScaling"/>
    /// 放大后交给共用实现，两条路径不再各写一份几何。
    /// 必须逐个手柄判：<see cref="CompareMode.AbcColumns"/> 的第二条竖线不命中也会
    /// 吞掉该处的鼠标消息（表现为"第二条线附近无法拖动画面"）。</summary>
    private bool IsOnHandlePhysical(int px, int py)
    {
        if (!GetClientRect(_hwnd, out var rc)) return false;
        var cw = rc.Right - rc.Left;
        var ch = rc.Bottom - rc.Top;
        if (cw <= 0 || ch <= 0) return false;

        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        return HandleIndexAt(px, py, cw, ch, HitRadius * scaling) >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProcW(nint prevProc, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);
}
