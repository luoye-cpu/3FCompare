using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

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
/// 切到别的程序时传输栏会仍浮在最上面。owned 窗口则恒定在 owner 之上（视频子 HWND 属于
/// owner，被一并盖住）、不占任务栏、随 owner 最小化隐藏，且与其他应用同处普通 Z 序带。</para>
///
/// <para><b>鼠标穿透（关键）</b>：窗口 = 栏体 + 四周 <see cref="Pad"/> 的透明留白（给圆角呼吸空间）。
/// 若不处理，那圈留白会吞掉视频边缘的拖拽/滚轮。故子类化自身 HWND，在 <c>WM_NCHITTEST</c> 中
/// <b>只有栏体矩形内</b>返回 <c>HTCLIENT</c>（按钮 / ComboBox 可交互），留白一律
/// <c>HTTRANSPARENT</c> 让给下层视频子 HWND（同线程转发语义成立，同 LayoutOverlayWindow）。
/// 钩子是否装成见 <see cref="HitTestHookInstalled"/>。</para>
///
/// <para><b>不抢焦点</b>：<c>ShowActivated=false</c> + <c>WS_EX_NOACTIVATE</c> ——
/// <c>Show</c> 与点击都不激活本窗口，主窗口的 Space / 方向键等快捷键不被打断
/// （ComboBox 下拉是独立 Popup 窗口，不受影响）。</para>
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

    /// <summary>诊断：是否真的排在主窗口<b>之上</b>（视频子 HWND 属于主窗口，故必然也高于它们）。</summary>
    public bool IsAboveHostInZOrder
    {
        get
        {
            if (_hwnd == nint.Zero) return false;
            var hostHwnd = _host?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
            if (hostHwnd == nint.Zero) return false;
            for (var h = GetTopWindow(nint.Zero); h != nint.Zero; h = GetWindow(h, GW_HWNDNEXT))
            {
                if (h == _hwnd) return true;
                if (h == hostHwnd) return false;
            }
            return false;
        }
    }

    /// <summary>诊断：是否仍带 <c>WS_EX_TOPMOST</c>。修掉"浮在所有应用之上"后必须为 <c>false</c>。</summary>
    public bool HasTopmostStyle =>
        _hwnd != nint.Zero && (GetWindowLongPtr(_hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    // ═══════════════════════ 状态 ═══════════════════════

    /// <summary>栏体本体（内容控件挂在这里）。</summary>
    private readonly Border _root;

    private Window? _host;
    private Visual? _anchor;

    private bool _syncing;
    private bool _closed;
    private bool _opened;

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

    /// <summary>浮现（不激活）。重复调用幂等。</summary>
    public void ShowBar()
    {
        if (_closed) return;
        // 先定位再显示，避免在默认位置闪一帧。
        SyncGeometry();
        if (!IsVisible) Show(_host!);
        SyncGeometry();
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

        _syncing = true;
        try
        {
            var pos = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
            if (Position != pos) Position = pos;
            // 浮点抖动不触发重排：差 < 0.5 DIP 视为同值。
            var dipW = winWpx / scaling;
            var dipH = winHpx / scaling;
            if (Math.Abs(Width - dipW) > 0.5) Width = dipW;
            if (Math.Abs(Height - dipH) > 0.5) Height = dipH;
        }
        finally
        {
            _syncing = false;
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
    /// 栏体矩形内返回 <c>HTCLIENT</c>（按钮 / ComboBox 正常收到消息），留白环带返回
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
