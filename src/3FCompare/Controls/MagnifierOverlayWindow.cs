using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Runtime.InteropServices;

namespace _3FCompare.Controls;

/// <summary>承载 <see cref="MagnifierOverlay"/> 的独立 owned 顶层窗（P1-4 修复）。
///
/// <para><b>为什么必须独立窗口</b>：视频由 <see cref="PlayerSurface"/> 的 NativeControlHost
/// 子 HWND 渲染，子 HWND 恒在父窗口自绘内容之上（airspace）——放大镜原先挂在
/// CenterPanel 下，坐标算得再对也会被视频整块盖住。
/// 与 <see cref="LayoutOverlayWindow"/> / <see cref="FloatingTransportWindow"/> 同一套方案。</para>
///
/// <para>⚠ <b>取证口径</b>：判"放大镜到底看不看得见"必须用 <see cref="IsAboveHostInZOrder"/>，
/// <b>不能</b>用 <c>WindowFromPoint</c> —— 本窗口带 <c>WS_EX_TRANSPARENT</c>（鼠标穿透是硬需求），
/// 命中测试按设计返回下层窗口，那个探针对"在不在上面"没有分辨力，反而会在修复生效时报告"被遮挡"。</para>
///
/// <para><b>Z 序</b>：靠 <c>Owner=主窗口</c>，<b>不用 Topmost</b> —— Topmost 会高于其它应用，
/// 放大镜不能在切走后仍浮在别的程序上面。但 owner 关系<b>不保证</b>次序：补插上线前可确证的
/// 22 次读数里 <b>3 次</b>（14%）覆盖窗排在主窗<b>之下</b>，其中留了重试链的 2 例在 180ms×6 次采样里
/// 恒为 Below（没有观测到自愈）⇒ 每次呈现回调走 <see cref="ReassertAboveOwner"/> 观测并补插。
/// 兜底是否真接在指针路径上，由自测的注入夹具（把覆盖窗插到主窗之下）验证，
/// 不靠"这一轮碰巧没复现"。上线后 47 次到达该步的读数：终态 <b>0 次 Below</b>，其中 <b>1 次</b>
/// 是补插当场治好的现行（`补插次数=1` 且终态 Above），其余 46 次计数为 0 ⇒ 现场率是否变化不作结论，
/// 只确证"看得见"这一项。数字与口径见 `docs/48` §3.7。</para>
///
/// <para><b>鼠标穿透</b>：放大镜 IsHitTestVisible=false，但顶层窗本身仍会吞鼠标消息；
/// 用 WS_EX_TRANSPARENT 让整窗命中测试穿透，视频的拖拽/滚轮不受影响。</para>
///
/// <para><b>定位契约</b>：本窗口只按 <see cref="MagnifierOverlay.OverlayPosition"/>（容器 DIP 坐标）
/// 的屏幕映射摆放，何时显示/隐藏由 <see cref="MagnifierOverlay.PresentationChanged"/> 通知宿主决定；
/// 宿主（MainWindow）订阅后调 <see cref="ShowOverlay"/>/<see cref="HideOverlay"/> 并设 <c>Position</c>。</para>
/// </summary>
public sealed class MagnifierOverlayWindow : Window
{
    private bool _opened;

    public MagnifierOverlayWindow(MagnifierOverlay overlay)
    {
        // Avalonia 12：WindowDecorations 是新 API（同 FloatingTransportWindow）。
        SetCurrentValue(WindowDecorationsProperty, WindowDecorations.None);
        WindowStartupLocation = WindowStartupLocation.Manual;
        // 逐像素透明：窗口矩形大于放大镜内容（圆角/阴影呼吸空间），须透出下方视频。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = new SolidColorBrush(Colors.Transparent);
        // 刻意不置顶（显式写 false 固化意图）：Z 序唯一来源是 Owner。
        Topmost = false;
        ShowActivated = false;   // 不抢主窗口焦点
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Focusable = false;
        Width = MagnifierOverlay.WidthPx;
        Height = MagnifierOverlay.HeightPx;
        Content = overlay;
    }

    /// <summary>挂 Owner 并显示（不激活）。重复调用幂等。</summary>
    public void ShowOverlay(Window host)
    {
        // Owner 是本窗口 Z 序的唯一来源；Owner 是受保护成员，本类（Window 子类）内可直接赋值。
        Owner = host;
        if (!IsVisible)
        {
            Show(host);
            _opened = true;
        }
        // Show 落位会被同时发生的其它顶层窗 Z 序变动打断 ⇒ 补插上线前 22 次读数里 3 次覆盖窗
        // 被排在主窗**之下**（用户看不见；留了重试链的 2 例 180ms 内不自愈）。
        ReassertAboveOwner();
    }

    /// <summary>隐藏（窗口与内容保留，可再次 ShowOverlay）。</summary>
    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }

    /// <summary>彻底关闭并释放。宿主窗口关闭时必须调用，否则残留顶层窗口，
    /// Avalonia 会因仍有存活 Window 而不退出消息循环（同 LayoutOverlayWindow 约定）。</summary>
    public void CloseAndDispose()
    {
        if (!_opened) return;
        _opened = false;
        try { Close(); } catch (System.Exception ex)
        { System.Console.Error.WriteLine($"[MagnifierOverlayWindow] Close 失败: {ex.Message}"); }
    }

    // ═══════════════════════ Win32：整窗鼠标穿透 + Z 序取证 ═══════════════════════

    private nint _hwnd;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hwnd == nint.Zero) return;
        _hwnd = hwnd;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new nint(exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE));
    }

    /// <summary>自测取证用：本窗口的 Win32 句柄（<see cref="OnOpened"/> 里缓存，未打开为 0）。</summary>
    public nint OverlayHwnd => _hwnd;

    /// <summary>是否真的挂上了 Win32 owner 关系（<c>GetWindow(GW_OWNER)</c> 非空）。
    /// Z 序的唯一来源，owner 没挂上就可能被视频子 HWND 盖住。</summary>
    public bool OwnerHwndAttached =>
        _hwnd != nint.Zero && GetWindow(_hwnd, GW_OWNER) != nint.Zero;

    /// <summary>诊断：是否仍带 <c>WS_EX_TOPMOST</c>（放大镜不该浮在其它应用之上）。</summary>
    public bool HasTopmostStyle =>
        _hwnd != nint.Zero && (GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    /// <summary>诊断：<b>宿主</b>是否带 <c>WS_EX_TOPMOST</c>。置顶带整体高于普通带，
    /// 若主窗在置顶带而覆盖窗不在，则 <see cref="ReassertAboveOwner"/> 用 <c>HWND_TOP</c> 也补不动
    /// ——这条读数用来区分"补插无效"和"补插没被执行"。</summary>
    public bool HostHasTopmostStyle =>
        Owner?.TryGetPlatformHandle()?.Handle is { } h && h != nint.Zero
        && (GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    /// <summary>顶层 Z 序查询的三种结果。把"被压到主窗之下"与"压根不在链里"分开——
    /// 前者是 Z 序问题，后者是 show/hide 竞态或句柄取错，修法完全不同。</summary>
    public enum ZOrderPosition { Above, Below, NotInChain }

    /// <summary>自顶向下走顶层链，报覆盖窗相对主窗的位置。
    /// <para>必须用 <c>GetTopWindow(0)</c> 取链首：<c>GetWindow(0, GW_HWNDFIRST)</c> 返回 0，
    /// 会让整条走链一次都不执行 ⇒ 恒判 NotInChain（实测踩过）。</para></summary>
    public ZOrderPosition QueryZOrderPosition()
    {
        if (_hwnd == nint.Zero) return ZOrderPosition.NotInChain;
        var hostHwnd = Owner?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hostHwnd == nint.Zero) return ZOrderPosition.NotInChain;

        for (var h = GetTopWindow(nint.Zero); h != nint.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (h == _hwnd) return ZOrderPosition.Above;
            if (h == hostHwnd) return ZOrderPosition.Below;
        }
        return ZOrderPosition.NotInChain;   // 走完全链都没见到覆盖窗
    }

    /// <summary>补插次数（自测取证用）：健康轮次应为 0；非 0 说明这一轮确实被压下去过。</summary>
    public int ZOrderReasserts { get; private set; }

    /// <summary>观测到"排在主窗之下"时补一次 Z 序插入。只在确诊 <see cref="ZOrderPosition.Below"/>
    /// 时动手 ⇒ 健康路径上只走一次走链，不打 SetWindowPos。
    ///
    /// <para>⚠ 插入点**必须**是 <c>HWND_TOP</c>，不能把主窗句柄当 <c>hWndInsertAfter</c> 传：
    /// 该参数的语义是"插到谁**之后**（=之下）"，传 owner 等于亲手把自己塞到主窗底下
    /// （实测：那样跑的批次 12 次读数 11 次 Below，见 `zofix_run*.log`；对照基线 3/22）。</para>
    ///
    /// <para>不用 <c>HWND_TOPMOST</c>：那会让放大镜在切到别的应用后仍浮在最上面（样式判据禁止）。</para></summary>
    public void ReassertAboveOwner()
    {
        if (QueryZOrderPosition() != ZOrderPosition.Below) return;
        SetWindowPos(_hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        ZOrderReasserts++;
    }

    /// <summary>Win32 侧真实可见性。Avalonia 的 <c>IsVisible=true</c> 不代表窗口在 z 序表里。</summary>
    public bool Win32WindowVisible => _hwnd != nint.Zero && IsWindowVisible(_hwnd);

    /// <summary>覆盖窗是否排在主窗之上（⇒ 也在主窗的视频子 HWND 之上）。
    /// <para><b>为什么不能用 <c>WindowFromPoint</c> 代替</b>：本窗口带 <c>WS_EX_TRANSPARENT</c>
    /// （鼠标穿透是硬需求），命中测试按设计返回下层窗口 ⇒ 该探针对"在不在上面"没有分辨力。</para></summary>
    public bool IsAboveHostInZOrder => QueryZOrderPosition() == ZOrderPosition.Above;

    /// <summary>纯观测诊断：自顶向下列出**包含该物理点**的前若干个顶层窗口（含各自 owner），
    /// 用来区分"覆盖窗真的排在主窗之下"与"缓存的 _hwnd 不是那个可见窗口"这两种解释。
    /// 不改变任何窗口样式或 Z 序。</summary>
    public string DescribeZOrderAt(int px, int py)
    {
        var sb = new System.Text.StringBuilder();
        var hostHwnd = Owner?.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        var rank = 0;
        for (var h = GetTopWindow(nint.Zero); h != nint.Zero && rank < 6; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(h)) continue;
            if (!GetWindowRect(h, out var r)) continue;
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) continue;
            if (px < r.Left || px >= r.Right || py < r.Top || py >= r.Bottom) continue;
            sb.Append($"[{rank}]0x{h:X}({r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top})" +
                      $"owner=0x{(long)GetWindow(h, GW_OWNER):X}" +
                      $"top={((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0 ? 1 : 0)}" +
                      (h == _hwnd ? "←覆盖窗" : h == hostHwnd ? "←主窗" : "") + " ");
            rank++;
        }
        return sb.Length == 0 ? "无包含该点的顶层窗口" : sb.ToString();
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const uint GW_OWNER = 4;
    private const uint GW_HWNDNEXT = 2;

    private static readonly nint HWND_TOP = nint.Zero;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out WinRect r);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }
}
