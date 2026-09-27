using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using _3FCompare.Core.Diagnostics;

namespace _3FCompare.Controls;

/// <summary>悬浮传输栏（docs/31 阶段 4.3「传输控制改悬浮自动隐藏」）。
///
/// <para><b>为什么必须是独立顶层窗</b>：每路视频由 <see cref="PlayerSurface"/> 的
/// <c>NativeControlHost</c> 子 HWND 渲染，子 HWND 永远盖在父窗口之上 —— 任何放进
/// <c>MainWindow</c> 视觉树、想"浮在视频之上"的 Avalonia 控件都会被画面遮住（airspace，
/// 同 <see cref="LayoutOverlayWindow"/> 与 docs/10 §A8）。故复用同一套方案：无边框、
/// 逐像素透明的 owned 顶层窗。</para>
///
/// <para><b>Z 序：靠 Owner，<u>不</u>用 Topmost</b>（与 <see cref="LayoutOverlayWindow"/>
/// 完全同因）：需求是「高于主窗口（及其视频子 HWND），但<b>不</b>高于其他应用」。
/// <c>Topmost</c> 做不到后半句 —— 它高于<b>所有</b>非 topmost 窗口、与 owner 无关，
/// 切到别的程序时传输栏会仍浮在最上面。改用 <see cref="Window.Owner"/> 换来的是：不占任务栏、
/// 随 owner 最小化隐藏、与其他应用同处普通 Z 序带。</para>
///
/// <para>⚠ <b>但 owner 只是"盖得住画面"的必要条件，<u>不保证</u>次序</b>："owned 窗口恒定在
/// owner 之上"已被真机读数否证 —— 同一个 owner 方案、同一条走链判据，放大镜覆盖窗上线前可确证的
/// 22 次读数里 <b>3 次（14%）排在主窗之下</b>，其中留了重试链的 2 例在 180ms×6 次采样里恒为 Below
/// （未观测到自愈），定案见 <c>docs/48 §3.7</c>。所以 <see cref="IsAboveHostInZOrder"/> 读的是
/// <b>真机现状</b>而非"结构上必然"；本窗口目前只做持续观测（<see cref="ZOrderSamples"/> /
/// <see cref="ZOrderBelowCount"/>），<b>未</b>装 <c>HWND_TOP</c> 补插 —— 要不要装由这轮量出的
/// rate 决定。</para>
///
/// <para><b>鼠标穿透（关键）</b>：窗口 = 栏体 + 四周 <see cref="Pad"/> 的透明留白（给圆角呼吸空间）。
/// 若不处理，那圈留白会吞掉视频边缘的拖拽/滚轮。故子类化自身 HWND，在 <c>WM_NCHITTEST</c> 中
/// <b>只有栏体矩形内</b>返回 <c>HTCLIENT</c>（按钮 / chip 可交互），留白一律
/// <c>HTTRANSPARENT</c> 让给下层视频子 HWND（同线程转发语义成立，同 LayoutOverlayWindow）。
/// 钩子是否装成见 <see cref="HitTestHookInstalled"/>。</para>
///
/// <para><b>不抢焦点</b>：<c>ShowActivated=false</c> + <c>WS_EX_NOACTIVATE</c> ——
/// <c>Show</c> 与点击都不激活本窗口，主窗口的 Space / 方向键等快捷键不被打断
/// （栏内控件已换成无独立 Popup 的 chip/候选行；旧的 ComboBox 下拉自带 Popup 窗，才不受这条约束）。</para>
///
/// <para><b>职责边界</b>：本窗口只做「呈现 + 定位 + 命中测试」，<b>不</b>决定何时浮现 ——
/// 何时 <see cref="ShowBar"/> / <see cref="HideBar"/> 由宿主（MainWindow）按光标热区决定。</para>
/// </summary>
public sealed class FloatingTransportWindow : Window
{
    /// <summary>栏体高度（DIP）。与常驻 <c>TransportHost</c> 的 54 保持一致，避免切换时控件跳动。</summary>
    public const double BarHeight = 54.0;

    /// <summary>栏体四周的透明留白（DIP）：圆角与描边的呼吸空间，也是命中测试的穿透环带。</summary>
    public const double Pad = 6.0;

    /// <summary>栏体与视频区左 / 右 / 下边缘的间距（DIP）。</summary>
    public const double EdgeMargin = 8.0;

    // ═══════════════════════ 诊断（可自动断言，供自测消费） ═══════════════════════

    /// <summary><c>WM_NCHITTEST</c> 钩子是否安装成功。
    /// <b>为 false 时留白环带会吞掉视频边缘的鼠标消息</b>（栏体本身仍可交互）。</summary>
    public bool HitTestHookInstalled => _hitTestHookInstalled;

    /// <summary>平台实际给到的透明级别（期望 <see cref="WindowTransparencyLevel.Transparent"/>）。</summary>
    public WindowTransparencyLevel AchievedTransparency => ActualTransparencyLevel;

    /// <summary>覆盖层当前是否已显示。</summary>
    public bool IsBarVisible => IsVisible;

    /// <summary>诊断：平台层是否真的把宿主挂成了 owner（同 LayoutOverlayWindow，只判"非 0"不够）。</summary>
    public bool OwnerHwndAttached
    {
        get
        {
            if (_hwnd == nint.Zero) return false;
            var owner = GetWindow(_hwnd, GW_OWNER);
            return owner != nint.Zero && owner == _host?.TryGetPlatformHandle()?.Handle;
        }
    }

    /// <summary>诊断：走一次顶层链，报本浮条<b>此刻</b>是否排在主窗口之上。
    /// <para>视频子 HWND 属于主窗口，只要本窗口高于 owner 就必然也高于它们 ⇒ 浮条看得见；
    /// 反过来（Below）就是真被画面盖住了。</para>
    /// <para>⚠ 它读的是<b>真机现状，不是"结构上必然成立"</b>：owner 只是必要条件，Win32
    /// <b>不保证</b> owned 窗口恒定在 owner 之上 —— 同一套方案 + 同一条走链判据在放大镜覆盖窗上
    /// 实测 22 次读数里 <b>3 次（14%）为 Below</b>，且 180ms×6 次采样内不自愈（<c>docs/48 §3.7</c>）。
    /// 长期发生率见 <see cref="ZOrderSamples"/> / <see cref="ZOrderBelowCount"/>。</para>
    /// <para>⚠ 取证不能用 <c>WindowFromPoint</c> 一类命中探针：本窗口对 <c>WM_NCHITTEST</c>
    /// 按设计返回 <c>HTTRANSPARENT</c>（留白环带，见 <see cref="HitTestWndProc"/>），
    /// 命中测试对它没有分辨力。</para></summary>
    public bool IsAboveHostInZOrder => QueryChainPosition() == ChainPos.Above;

    /// <summary>诊断：是否仍带 <c>WS_EX_TOPMOST</c>。修掉"浮在所有应用之上"后必须为 <c>false</c>。</summary>
    public bool HasTopmostStyle =>
        _hwnd != nint.Zero && (GetWindowLongPtr(_hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    // ═══════════════ Z 序持续观测（只读数，不动手） ═══════════════
    //
    // 为什么要这一段：上面的判据原先只在自测进入某一步时被读一次，而放大镜站点实测到的翻转出现在
    // **后续的呈现 / 位置回调**上 ⇒ 一次性采样在统计上等于没测。下面只走链、只累加计数，
    // 不调 SetWindowPos、不写样式、不动位置与焦点（纯观测）；是否给本站点装 HWND_TOP 补插，
    // 由这一轮量出来的 rate 决定。

    /// <summary>顶层走链的累计次数（rate 的分母）。只在浮条<b>已显示</b>且自身与宿主 HWND 都拿得到
    /// 时才增加 —— 浮条平时自动隐藏，隐藏窗口仍在顶层链里，那时读到的 Below 与"用户看不见浮条"
    /// 无关，是假样本。</summary>
    public int ZOrderSamples { get; private set; }

    /// <summary>其中判为 <b>Below</b>（本窗口排在主窗之下 ⇒ 浮条被视频画面盖住）的次数。</summary>
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
    /// <para><b>为什么日志不能无条件写</b>：采样点挂在产品热路径上（100ms 的浮现轮询、主窗移动
    /// 与缩放都会走到），日志条数必须与"异常回合数"同阶而不能与"调用次数"同阶 —— 本项目有过
    /// "组件日志节流是全局共享"把判据读成假红的前车之鉴，无条件写日志只会制造噪声、
    /// 甚至灌满落盘队列。</para>
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
                $"[FloatingTransportWindow] {from}→Below" +
                $" owner={OwnerHwndAttached} topmost={HasTopmostStyle}" +
                $" hostTopmost={HostHasTopmostStyle}" +
                $" samples={ZOrderSamples} below={ZOrderBelowCount}");
        }
        else if (pos == ChainPos.NotInChain)
        {
            ZOrderNotInChainCount++;
        }
    }

    /// <summary>落一条累计汇总（只在"窗口真的在销毁"的位置调用，与热路径无关 ——
    /// 自动隐藏 <see cref="HideBar"/> 不打，那是每次离开热区都会走的路径）。
    /// 一次都没采到就不写。
    ///
    /// <para>宿主在 <c>OnClosing</c> 里的顺序是「先 <c>DestroyFloatingTransport</c>
    /// （MainWindow.axaml.cs:3032）、后 <c>AppLog.Shutdown()</c>（同文件 3034）」⇒ 退出路径上本条
    /// 有落盘窗口。⚠ 但本窗口的 <see cref="Window.OnClosed"/> 是否在该顺序内被同步派发到，
    /// 取决于 Avalonia 的 Close 实现，<b>未取证</b> —— 别把它当唯一证据：计数本身在进程内可读，
    /// 翻转日志行也已带上累计值。</para></summary>
    private void LogZOrderSummary(string where)
    {
        if (ZOrderSamples <= 0) return;
        AppLog.Info("ZOrder",
            $"[FloatingTransportWindow] 汇总({where}) samples={ZOrderSamples} below={ZOrderBelowCount}" +
            $" notInChain={ZOrderNotInChainCount} flips={ZOrderFlipsToBelow} last={_lastZOrder}");
    }

    // ═══════════════════════ 状态 ═══════════════════════

    /// <summary>栏体本体（内容控件挂在这里）。</summary>
    private readonly Border _root;

    private Window? _host;
    private Visual? _anchor;

    private bool _syncing;

    /// <summary>已完成的几何重算次数（只数真正落位的那些，守卫提前返回的不计）。
    /// 自测用它把"还没轮到重算"和"已算完并稳定"分开。</summary>
    internal int GeometrySyncs { get; private set; }
    private bool _closed;
    private bool _opened;

    /// <summary>最近一次 SyncGeometry 算出的期望落位（物理像素）。</summary>
    private PixelPoint _wantedPos;

    /// <summary>「位置被夹走就补写一次」的额度：每轮同步刷新为 1，补写一次后归 0。
    /// 一次性额度是硬约束 —— 绝不与平台的夹取拉锯，每轮最多多发一次 SetWindowPos。</summary>
    private int _reassertBudget;

    private bool _reasserting;

    public FloatingTransportWindow()
    {
        // Avalonia 12：WindowDecorations 是新 API；旧 SystemDecorations 已废弃（CS0618）。
        SetCurrentValue(WindowDecorationsProperty, global::Avalonia.Controls.WindowDecorations.None);
        WindowStartupLocation = WindowStartupLocation.Manual;

        // 逐像素透明：让下方视频透出。命中测试需要非 null 的 Background（Avalonia 侧靠它参与
        // InputHitTest），而 alpha=0 的纯透明刷在 DComp 合成下不可见。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = new SolidColorBrush(Colors.Transparent);

        // 刻意**不**置顶（显式写 false 以固化意图，见类注释）。
        Topmost = false;
        ShowActivated = false;   // Show / 点击都不抢主窗口激活（Space 等快捷键留在主窗口）
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Focusable = false;

        Width = 640;
        Height = BarHeight + Pad * 2;

        _root = new Border
        {
            Margin = new Thickness(Pad),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        };
        // 用与常驻 TransportHost 同一套令牌，主题切换自动跟随（DynamicResource 绑定）。
        ThemePalette.SetBrush(_root, Border.BackgroundProperty, "PanelBrush");
        ThemePalette.SetBrush(_root, Border.BorderBrushProperty, "BorderBrush");
        Content = _root;
    }

    // ═══════════════════════ 对外方法 ═══════════════════════

    /// <summary>挂上宿主与锚点，并把 <paramref name="content"/>（传输栏本体）装进本窗口。
    ///
    /// <para>⚠ <b>控件单亲约束</b>：调用方必须先把这个控件从原父（<c>TransportHost</c>）
    /// 上摘下来（<c>TransportHost.Child = null</c>），否则 Avalonia 会因"已有父"抛异常。</para>
    ///
    /// <para>⚠ <b>销毁约定</b>：关闭本窗口前必须先用 <see cref="DetachContent"/> 把控件摘出。
    /// 传输栏是宿主的常驻控件，随窗口一起销毁会导致"关掉悬浮后再也看不到传输栏"。</para></summary>
    public void Attach(Window host, Visual anchor, Control content)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(content);
        if (_closed) throw new InvalidOperationException("窗口已关闭，不能再 Attach；请新建实例。");

        DetachHost();
        _host = host;
        // Owner 是本窗口 Z 序的唯一来源（见类注释）。必须显式设，不能只靠 Show(host)。
        Owner = host;
        _anchor = anchor;
        _root.Child = content;
        AttachHost();
        SyncGeometry();
    }

    /// <summary>把内容控件摘出并返回（<b>不</b>销毁，也不置 null 语义之外的任何状态）。
    /// 见 <see cref="Attach"/> 的销毁约定。</summary>
    public Control? DetachContent()
    {
        var content = _root.Child;
        _root.Child = null;
        return content;
    }

    /// <summary>浮现（不激活）。重复调用幂等。
    ///
    /// <para>尾部做一次 Z 序走链观测（只累加 <see cref="ZOrderSamples"/> 等计数，见其注释；
    /// 日志只在"进入 Below"的那一拍写）。本方法是本站点的"呈现入口"：指针停在底部热区期间，
    /// 宿主的热区轮询每 <c>TransportHoverPollMs=100ms</c> 就会调它一次（MainWindow.axaml.cs:2346
    /// 由 <c>OnTransportHoverTick</c> 驱动），语义对齐 <see cref="MagnifierOverlayWindow.ShowOverlay"/>
    /// 尾部那个同款读数 —— 放大镜的翻转正是出现在这类后续呈现回调上，一次性采样看不见。</para></summary>
    public void ShowBar()
    {
        if (_closed) return;
        // 先定位再显示，避免在默认位置闪一帧。
        SyncGeometry();
        if (!IsVisible) Show(_host!);
        SyncGeometry();
        ObserveZOrder();
    }

    /// <summary>隐藏（窗口与内容都保留，可再次 <see cref="ShowBar"/>）。</summary>
    public void HideBar()
    {
        if (_closed || !IsVisible) return;
        Hide();
    }

    /// <summary>彻底关闭并释放。宿主窗口关闭 / 退出悬浮模式时必须调用，否则进程里会残留一个
    /// 顶层窗口，且 Avalonia 会因仍有存活 Window 而不退出消息循环。</summary>
    public void CloseAndDispose()
    {
        if (_closed) return;
        _closed = true;
        DetachHost();
        if (IsVisible) Hide();
        if (_root.Child is not null)
            // 显式报警而不是静默：控件被留在已关闭的窗口上，宿主再想把它挂回常驻位会因
            // "已有父"抛异常 —— 这正是"关掉悬浮后传输栏消失"的根因，必须在日志里看得见。
            Console.Error.WriteLine(
                "[FloatingTransportWindow] 关闭时仍持有内容控件；宿主应先 DetachContent() 再 CloseAndDispose()。");
        if (_opened)
        {
            try { Close(); } catch (Exception ex) { Console.Error.WriteLine($"[FloatingTransportWindow] Close 失败: {ex.Message}"); }
        }
    }

    /// <summary>屏幕物理坐标点是否落在本窗口矩形内（宿主判定"指针停在浮条上"用）。</summary>
    public bool ContainsScreenPoint(PixelPoint screenPoint)
    {
        if (_closed || !IsVisible) return false;
        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        var size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return false;
        return new PixelRect(Position, PixelSize.FromSize(size, scaling)).Contains(screenPoint);
    }

    // ═══════════════════════ 跟随（位置 / 尺寸 / DPI） ═══════════════════════

    private void AttachHost()
    {
        if (_host is null) return;
        _host.PositionChanged += OnHostPositionChanged;
        _host.SizeChanged += OnHostSizeChanged;
        _host.ScalingChanged += OnHostScalingChanged;
        // 自身被拖到另一块显示器时 RenderScaling 变化，必须重算 DIP 尺寸（同 LayoutOverlayWindow U2）。
        ScalingChanged += OnSelfScalingChanged;
        // 尺寸写入引发的"夹回工作区"是以一次位置变化回来的，这是唯一能补救它的时机（见 SyncGeometry）。
        PositionChanged += OnSelfPositionChanged;
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
        PositionChanged -= OnSelfPositionChanged;
        if (_anchor is not null) _anchor.PropertyChanged -= OnAnchorPropertyChanged;
    }

    private void OnHostPositionChanged(object? sender, PixelPointEventArgs e) => SyncGeometry();

    private void OnHostSizeChanged(object? sender, SizeChangedEventArgs e) => SyncGeometry();

    private void OnHostScalingChanged(object? sender, EventArgs e) => SyncGeometry();

    private void OnSelfScalingChanged(object? sender, EventArgs e) => SyncGeometry();

    private void OnAnchorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 侧栏拖动 / 全屏切换 / 网格预设 / 底栏折叠都会改视频区的 Bounds；只认这一条即可覆盖。
        if (e.Property == Visual.BoundsProperty) SyncGeometry();
    }

    /// <summary>把栏体摆到视频区（<see cref="_anchor"/>）底部：水平居中留 <see cref="EdgeMargin"/> 边距，
    /// 垂直贴底。位置用物理像素，尺寸换算回 DIP（同 LayoutOverlayWindow）。</summary>
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
            Console.Error.WriteLine($"[FloatingTransportWindow] PointToScreen 失败: {ex.Message}");
            return;
        }

        var pxW = bottomRight.X - topLeft.X;
        var pxH = bottomRight.Y - topLeft.Y;
        if (pxW <= 0 || pxH <= 0) return;

        var scaling = EffectiveScaling();
        var marginPx = EdgeMargin * scaling;
        var padPx = Pad * scaling;
        var barHpx = BarHeight * scaling;

        var winWpx = pxW - 2 * marginPx + 2 * padPx;
        var winHpx = barHpx + 2 * padPx;
        if (winWpx < 200) winWpx = 200; // 视频区极窄时的兜底，避免出现负/零宽窗口

        var x = topLeft.X + marginPx - padPx;
        var y = bottomRight.Y - marginPx - barHpx - padPx;

        // 自测取证用：每真正走到"落位"这一步就 +1。前面的守卫 return 都不计数，
        // 因此它表示"重算确实发生过"，而不是"被调用过"。
        // 自测靠它区分"还没轮到重算"与"已算完并稳定"——只看数值稳定会在前者上过早收敛
        // （实测 resize 还原后 558ms 那轮，稳定采到的仍是上一次的 1448）。
        GeometrySyncs++;
        _syncing = true;
        try
        {
            // 写入顺序固定为「尺寸 → 位置」：平台在写尺寸时会把窗口夹回工作区（实测工作区 2560×1528，
            // 请求 (760,1538)+2172×99 → 落位 (388,1429)，右 / 底恰好贴在工作区边界上），
            // 而纯位置写入不会被夹（初始态就稳稳停在越界的 y=1539）⇒ 位置必须最后写。
            // 浮点抖动不触发重排：差 < 0.5 DIP 视为同值。
            var pos = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
            var dipW = winWpx / scaling;
            var dipH = winHpx / scaling;
            var sizeChanged = false;
            if (Math.Abs(Width - dipW) > 0.5) { Width = dipW; sizeChanged = true; }
            if (Math.Abs(Height - dipH) > 0.5) { Height = dipH; sizeChanged = true; }

            _wantedPos = pos;
            // 每轮同步都留一张"被夹走就补写"的牌（额度 1，谁先观测到夹取谁消费）。
            // 不能只在尺寸变化的那轮挂：夹取是排在尺寸写入之后才异步回来的，中间常常夹着几轮
            // "尺寸/位置读起来都没变"的同步，那时撤销额度就等于把补救窗口关掉了。
            // 实测（钉主窗到 (342,342)，同机同素材）：只改写入顺序 → 8 次里 1 次绿；
            // 加补救但只在尺寸变化的那轮挂额度 → 3 次里 0 次绿；每轮都挂 → 连跑全绿。
            _reassertBudget = 1;
            // sizeChanged 时无条件重写一次位置：万一夹取是同步发生的、而 Position 读起来仍是
            // 夹取前的旧值（恰等于期望值），只靠 Position != pos 判断会把错值永久留下。
            if (sizeChanged || Position != pos) Position = pos;
        }
        finally
        {
            _syncing = false;
        }

        // 落位之后观测一次（上面那些守卫 return 都不计数，与 GeometrySyncs 同口径）：
        // 主窗移动 / 缩放 / DPI 变化 / anchor Bounds 变化都走这条位置同步路径。
        // 隐藏态下 ObserveZOrder 自己会短路，所以隐藏期间被驱动的这几十次不会污染 rate。
        ObserveZOrder();
    }

    /// <summary>平台把窗口夹回工作区之后补一次纯位置写入 —— 这是"位置写在最后"仍不够的那一半：
    /// 夹取是异步回来的，且回来时没有任何宿主/anchor 事件会再驱动一次 SyncGeometry。
    /// 本窗口无装饰、不可拖动，位置只可能来自 <see cref="SyncGeometry"/> 或被平台夹走，
    /// 故"和期望落位不符"即可判定为被夹走，无需别的判据。</summary>
    private void OnSelfPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_reasserting || _closed || _reassertBudget <= 0) return;
        if (Position == _wantedPos) return; // 我们自己那次写到位了：额度留着，等随后可能到来的夹取

        var want = _wantedPos;
        _reassertBudget = 0; // 每轮同步最多补一次，绝不与平台的夹取拉锯
        _reasserting = true;
        try
        {
            Position = want;
        }
        finally
        {
            _reasserting = false;
        }
    }

    /// <summary>视频区所在显示器的缩放（物理像素 / DIP）。优先级同 LayoutOverlayWindow：
    /// anchor 的 TopLevel（首帧尚未挂显示器时本窗口 RenderScaling 恒为 1.0，是合法值而非"未知"，
    /// 据此折算会在高 DPI 下算错）→ 宿主 → 自身 → 1.0。</summary>
    private double EffectiveScaling()
    {
        var scaling = TopLevel.GetTopLevel(_anchor)?.RenderScaling ?? 0d;
        if (!(scaling > 0) && _host is not null) scaling = _host.RenderScaling;
        if (!(scaling > 0)) scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        return scaling;
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
    private const uint GW_OWNER = 4;
    private const uint GW_HWNDNEXT = 2;
    private const uint WM_NCHITTEST = 0x0084;
    private const nint HTCLIENT = 1;
    private const nint HTTRANSPARENT = -1;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _opened = true;
        InstallHitTestHook();

        // 不假装透明一定成功：拿不到逐像素 alpha 时窗口会以不透明底盖住视频，
        // 这是必须在真机上暴露而不是静默降级的失效模式，故显式记到 stderr。
        if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            Console.Error.WriteLine(
                $"[FloatingTransportWindow] 未取得逐像素透明（实际 {ActualTransparencyLevel}），" +
                "浮条会遮挡视频画面；宿主应据 AchievedTransparency 降级回常驻布局。");
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭链最上游落一条汇总（此时计数已定，卸载钩子与 _hwnd 归零都在它之后）。
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
            Console.Error.WriteLine("[FloatingTransportWindow] 取不到平台 HWND，命中测试钩子未安装（留白环带会吞掉鼠标消息）。");
            return;
        }

        _hwnd = handle.Handle;

        // 点击浮条不激活本窗口：主窗口的键盘快捷键（Space/方向键）不被打断。
        var exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);

        _hookProc = HitTestWndProc;
        var prev = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hookProc));
        if (prev == nint.Zero)
        {
            Console.Error.WriteLine($"[FloatingTransportWindow] 子类化失败（Win32 错误 {Marshal.GetLastWin32Error()}），命中测试钩子未安装。");
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
    /// 栏体矩形内返回 <c>HTCLIENT</c>（按钮 / chip 正常收到消息），留白环带返回
    /// <c>HTTRANSPARENT</c> 把消息让给下层的视频子 HWND —— 这是"浮条不吞视频边缘输入"的关键点。</summary>
    private nint HitTestWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_NCHITTEST && _hitTestHookInstalled)
        {
            // lParam 是**屏幕坐标**，低/高 16 位各为有符号 short。
            var sx = (short)(lParam & 0xFFFF);
            var sy = (short)((lParam >> 16) & 0xFFFF);
            var pt = new POINT { X = sx, Y = sy };
            if (ScreenToClient(hwnd, ref pt) && IsOnBarPhysical(pt.X, pt.Y))
                return HTCLIENT;
            return HTTRANSPARENT;
        }
        return CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>输入是**物理客户区像素**（WM_NCHITTEST 拿到的是物理坐标，不能直接与 DIP 比较），
    /// 判据是"落在栏体内侧（留白环带之外）"。</summary>
    private bool IsOnBarPhysical(int px, int py)
    {
        if (!GetClientRect(_hwnd, out var rc)) return false;
        var cw = rc.Right - rc.Left;
        var ch = rc.Bottom - rc.Top;
        if (cw <= 0 || ch <= 0) return false;

        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        var pad = Pad * scaling;
        return px >= pad && py >= pad && px <= cw - pad && py <= ch - pad;
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
