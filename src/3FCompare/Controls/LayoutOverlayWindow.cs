using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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
/// （真机体验缺陷，本类曾如此）。改用 <see cref="Window.Owner"/> 后：owned 窗口恒定在 owner
/// 之上（视频子 HWND 属于 owner，被一并盖住 ⇒ airspace 不退化）、不占任务栏、随 owner 最小化
/// 隐藏，且与其他应用同处普通 Z 序带 —— 被别的程序盖住、被 owner 带回，都是"引用 owner"的自然
/// 结果，无需自己管 Z 序。</para>
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
/// 在 <c>WM_NCHITTEST</c> 中<b>只在手柄命中半径内</b>返回 <c>HTCLIENT</c>，
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

    /// <summary>诊断：覆盖层是否真的排在主窗口<b>之上</b>（顶层 Z 序里更靠前）。
    /// <para>这是 airspace 不退化（分割线/手柄不被视频画面盖住）的可自动判定形式：视频子 HWND
    /// 属于主窗口，只要本窗口高于 owner 就必然高于它们。Win32 保证 owned 窗口恒定在 owner 之上、
    /// 且不会有第三个窗口插在两者之间，故该判据是确定的，不是"碰巧"。</para></summary>
    public bool IsAboveHostInZOrder
    {
        get
        {
            if (_hwnd == nint.Zero) return false;
            var hostHwnd = _host?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
            if (hostHwnd == nint.Zero) return false;

            // 自顶向下走顶层窗口链：先遇到自己 ⇒ 在宿主之上。
            for (var h = GetTopWindow(nint.Zero); h != nint.Zero; h = GetWindow(h, GW_HWNDNEXT))
            {
                if (h == _hwnd) return true;
                if (h == hostHwnd) return false;
            }
            return false;
        }
    }

    /// <summary>诊断：是否仍带 <c>WS_EX_TOPMOST</c>。
    /// <para>这是自动化唯一能拿到的 Z 序证据 —— 真机 Z 序（"有没有盖住别的应用"）无法在进程内
    /// 断言，但"还在不在置顶带"可以。修掉"浮在所有窗口之上"后此值必须为 <c>false</c>。</para></summary>
    public bool HasTopmostStyle =>
        _hwnd != nint.Zero && (GetWindowLongPtr(_hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    // ═══════════════════════ 状态 ═══════════════════════

    private Window? _host;
    private Visual? _anchor;

    private CompareMode _mode = CompareMode.Ab;
    private SplitParams _split = SplitParams.Default(CompareMode.Ab);

    private bool _dragging;
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
    }

    /// <summary>更新模式 / 分割参数并重绘（宿主在自身布局变化后调用）。
    /// 拖动中宿主回灌同一值时是幂等的。</summary>
    public void Update(CompareMode mode, SplitParams split)
    {
        ApplyState(mode, split);
        InvalidateVisual();
    }

    /// <summary>隐藏覆盖层并解除对宿主的跟随订阅（可再次 <see cref="ShowOverlay"/> 复用实例）。
    ///
    /// <para><b>B6：刻意不叫 <c>Hide</c></b>。若本方法以 <c>new void Hide()</c> 遮蔽基类
    /// <see cref="Window.Hide"/>，则任何持有 <see cref="Window"/> 引用的路径（框架内部、
    /// 宿主泛型代码、将来新增的调用点）调 <c>Hide()</c> 都会走基类实现而<b>不</b>
    /// <see cref="DetachHost"/> —— 本窗口会继续强订阅主窗口的
    /// Position/Size/Scaling 与 anchor 的 PropertyChanged ⇒ 覆盖层被隐藏却仍被主窗口
    /// 强引用，且每次宿主布局变化都白跑一次 <see cref="SyncGeometry"/>。
    /// 改名让"必须解除订阅"这件事在调用点就显式可见，不再依赖调用者的静态类型。</para></summary>
    public void HideOverlay()
    {
        DetachHost();
        _dragging = false;
        base.Hide();
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
        _cursor = CursorFor(CompareLayout.HandleAxis(mode));
        Cursor = _cursor;
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
        if (!IsOnHandle(e.GetPosition(this))) return;

        _dragging = true;
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

    /// <summary>把指针位置（窗口内 DIP）换算成归一化坐标，按模式决定更新 X / Y / 两者，
    /// Clamp 后本地更新并抛出事件。</summary>
    private void ApplyDrag(Point p)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var nx = p.X / w;
        var ny = p.Y / h;

        var next = CompareLayout.HandleAxis(_mode) switch
        {
            SplitAxis.X => new SplitParams(nx, _split.Y),
            SplitAxis.Y => new SplitParams(_split.X, ny),
            _ => new SplitParams(nx, ny),
        };
        next = next.Clamp();
        if (next == _split) return; // record struct 值相等：原地抖动不产生事件风暴

        _split = next; // 乐观本地更新：不必等宿主回灌即可跟手
        InvalidateVisual();
        SplitChanged?.Invoke(next);
    }

    /// <summary>指针（窗口内 DIP）是否落在手柄命中圈内。</summary>
    private bool IsOnHandle(Point p)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return false;
        var (hx, hy) = CompareLayout.HandlePosition(_mode, _split);
        var dx = p.X - hx * w;
        var dy = p.Y - hy * h;
        return dx * dx + dy * dy <= HitRadius * HitRadius;
    }

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

        // 手柄：位置由 Core 统一给出（AB 的 Y 固定 0.5，竖线中点）。
        var (hx, hy) = CompareLayout.HandlePosition(_mode, _split);
        context.DrawEllipse(_handleFill, _handleRingPen, new Point(hx * w, hy * h), HandleRadius, HandleRadius);
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

    /// <summary>与 <see cref="IsOnHandle"/> 同一判据，但输入是**物理客户区像素**
    /// （WM_NCHITTEST 拿到的是物理坐标，不能直接与 DIP 比较）。</summary>
    private bool IsOnHandlePhysical(int px, int py)
    {
        if (!GetClientRect(_hwnd, out var rc)) return false;
        var cw = rc.Right - rc.Left;
        var ch = rc.Bottom - rc.Top;
        if (cw <= 0 || ch <= 0) return false;

        var (hx, hy) = CompareLayout.HandlePosition(_mode, _split);
        var dx = px - hx * cw;
        var dy = py - hy * ch;

        var scaling = RenderScaling;
        if (!(scaling > 0)) scaling = 1.0;
        var r = HitRadius * scaling;
        return dx * dx + dy * dy <= r * r;
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
