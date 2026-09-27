using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using _3FCompare.Controls;
using _3FCompare.Core.Display;
using _3FCompare.Diagnostics;
using _3FCompare.Platform;

namespace _3FCompare;

/// <summary>对比模式的 Win32 窗口裁剪接线（阶段 3.2 落地，见 docs/26 §九）。
///
/// <para><b>做什么</b>：把「格表 + 当前放大参数」换算成各路子 HWND 的裁剪矩形，经
/// <see cref="WindowRegionClipper"/> 下发；退出对比模式时同步清除。</para>
///
/// <para><b>为什么单独一个 partial 文件</b>：<c>MainWindow.axaml.cs</c> 混有 v0.2.5 的未提交改动，
/// 本次只允许"新增式最小改动"。故全部新逻辑落在这里，原文件只加 3 处单行挂钩
/// （构造末尾 <see cref="WireCompareCrop"/>、进入/退出/分割变化各一行）。</para>
///
/// <para><b>三条路径</b>：
/// <list type="number">
/// <item><description><b>未启用放大（默认，z = 1）</b>：<c>CompareGridView.ArrangeOverride</c> 把每路
/// 安排成恰好等于自己的格，只有 Avalonia 的向外取整会让窗口比格大 ≤1px ⇒
/// <see cref="CompareCropPlanner.Plan"/> 把这 ≤1px 外扩裁掉（区域与格在容器坐标下完全重合，不打洞）。
/// 这条路径与引入放大之前<b>逐字一致</b>。</description></item>
/// <item><description><b>启用放大（z &gt; 1，默认关闭）</b>：子窗口被排成"格的 z 倍并偏移到让目标
/// 画面块对齐到格"，区域由 <see cref="CompareCropPlanner.MagnifyPlan"/> 按<b>源画面比例</b>换算
/// （含 letterbox 修正）。窗口变大 ⇒ 内核按更高分辨率真实重渲染（<c>PrepareScaledVideo</c> 的
/// 目标尺寸就是 destination 尺寸，不是插值放大）；平移由窗口位置表达 ⇒ 不受内核
/// <c>SetViewTransform</c> 的 <c>max(0,·)</c> 钳制影响。详见
/// <see cref="SetCompareMagnify"/> 与 <c>CompareCropPlanner</c>。</description></item>
/// <item><description><b>叠加模式（ICAT Single Screen，默认关闭）</b>：格表被换成"两路铺满"，
/// 裁剪语义不再是"裁出格"而是"裁出互补的半区"—— B 路由
/// <c>MainWindow.CompareOverlay.OverlayRevealPlan</c> 裁成 <c>[0, split]</c>、A 路裁成
/// <c>[split, 1]</c>，两者互不重叠且并集为整窗 ⇒ 可见结果与容器 Z 序无关。
/// 与放大互斥（见 <see cref="BuildCellMagnify"/> 的守卫）。</description></item>
/// </list></para>
///
/// <para><b>命中测试副作用</b>：区域会同时裁掉命中测试（区域外点击落到父窗口），这正是"越界部分
/// 不抢鼠标"的保证；但 Avalonia 侧的 <c>HitSurfaceAt</c> 读的是 <c>Bounds</c>（放大后各路窗口互相
/// 重叠）⇒ 已一并改为"按格命中"，见 <c>MainWindow.HitSurfaceAt</c>。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>每路最近一次真正下发成功的裁剪（路号 → 句柄 + 区域）。
    /// 作用有二：① 避免同一方案反复调用 <c>SetWindowRgn</c>（它在 <c>bRedraw=true</c> 下会触发重绘）；
    /// ② 句柄被重建时能发现"换了 HWND"，从而对旧句柄做清理、对新句柄重设。</summary>
    private readonly Dictionary<int, (nint Hwnd, Rect32 Region)> _compareCropApplied = new();

    // ══════════ 无缝放大：子窗口放大 + 偏移 + 裁剪 ══════════
    //
    // 与内核 SetViewTransform 的区别（这是本方案存在的理由）：
    //   · 内核 zoom 的 destination 有 max(0,·) 钳制 ⇒ 不能任意平移；
    //   · 内核 zoom 恒为双线性插值 ⇒ 放大后像素是插出来的。
    // 本方案把"放大"交给子窗口物理尺寸：窗口 = 格的 z 倍 ⇒ 内核按更高分辨率**真实重渲染**
    // （PrepareScaledVideo 的目标尺寸就是 destination 尺寸，不是插值放大），
    // 平移由 MoveWindow 表达 ⇒ 不受 max(0,·) 影响。只有一个放大源 ⇒ 不存在 z² 叠加。

    /// <summary>放大倍数硬上限。理由：swapchain 面积按 <c>z²</c> 增长，<c>z=4</c> 已是 16 倍面积；
    /// 4K 对比区在 z=4 时子窗口总像素达 132 Mpx（≈530 MB/缓冲 @BGRA8），再高就不是"性能下降"
    /// 而是直接撞显存上限。上限与像素预算<b>同时</b>生效，取更严的那个。</summary>
    internal const double MaxCompareZoom = 4.0;

    /// <summary>所有路子窗口的总像素预算 ≈ 8 路 4K（<c>8 × 3840 × 2160</c> ≈ 66.4 Mpx）。
    ///
    /// <para><b>实测依据（docs/26 §十三，RTX 5080 + 4 路真实 4K60）</b>：
    /// 旧值 33.2 Mpx（≈4 路 4K）是按"BGRA8 单缓冲 127 MB × 2~3 缓冲 ⇒ 250~380 MB"估算的，
    /// <b>实测偏保守约 3 倍</b>——100.1 Mpx（3× 旧预算）下仍稳定 58 fps，显存仅 +594 MB；
    /// 边际成本约 4~8 MB/Mpx。</para>
    ///
    /// <para><b>为何取 2 倍而非 3 倍</b>：实测时本机 GPU 已被其它进程占用约 83%，
    /// 帧率数据受环境噪声影响；且需为显存更小的低端 GPU 保留余量。
    /// 2 倍既释放了 z=3/z=4 的可用空间，又不过分逼近实测上限。</para>
    ///
    /// <para>超过就<b>拒绝启用</b>并写明原因，绝不静默分配。
    /// 注意：真正的高倍瓶颈是<b>填充率</b>（内核按整个 z² 窗口重渲染）而非显存。</para></summary>
    internal const double CompareMagnifyPixelBudget = 8.0 * 3840 * 2160;

    private double _compareZoom = 1.0;
    private double _compareCropX, _compareCropY;
    /// <summary>本次放大生效的对齐模式（进入放大时从 <c>AppSettings.CompareAlign</c> 取一次）。
    /// 不在中途跟随设置变化：改设置后重新缩放才会生效，避免"正在放大时窗口尺寸突然跳变"。</summary>
    private CompareAlign _compareAlign = CompareAlign.Relative;
    /// <summary>每路源画面尺寸（索引 = 路号）；(0,0) = 未知（演示模式）⇒ 该路按"画面铺满窗口"换算。</summary>
    private PixelSize[] _compareMagnifySources = Array.Empty<PixelSize>();
    /// <summary>本次启用生效的像素预算。默认 <see cref="CompareMagnifyPixelBudget"/>；
    /// 仅自测会传一个小值以便真正走到"超预算拒绝"这条分支（否则按真实窗口尺寸永远撞不到闸门）。</summary>
    private double _compareMagnifyBudget = CompareMagnifyPixelBudget;
    /// <summary>放大参数（含预算复核）交给 Grid 后，等待子 HWND 被 Avalonia 搬到位时的重试计数。
    /// 有界：<c>ShowInBounds</c> 排在 <c>AfterRender</c>，正常一两拍就位；若某路始终不匹配
    /// （窗口被隐藏/尺寸为 0/平台实现变化），无界重试会变成 Dispatcher 上的死循环 —— 宁可放弃放大。</summary>
    private int _compareMagnifyHwndRetries;
    private const int MaxCompareMagnifyHwndRetries = 8;
    /// <summary>最近一次因超预算而拒绝放大时打的日志指纹，避免结构性变化时刷屏。</summary>
    private string _compareMagnifyBudgetNote = string.Empty;
    /// <summary>像素级对齐退化（读不到任何一路源尺寸）的提示指纹，同上用途。
    /// 与上面分开：两者指纹形态不同，共用一个字段会互相覆盖 ⇒ 去重失效。</summary>
    private string _compareAlignNote = string.Empty;

    /// <summary>当前是否处于"子窗口放大"状态。</summary>
    internal bool CompareMagnifyActive => _compareZoom > 1.0;

    // ══════════ 滚轮 / 拖动：无缝放大的交互入口 ══════════
    //
    // 内核完整、但此前只有自测 --magnifybench 会调 SetCompareMagnify（生产 UI 没有任何触发路径）。
    // 这里补上两条：滚轮以光标为锚点缩放、放大态拖动平移。两者都**不**走内核
    // SetViewTransform —— 那条路径是单路视口变换，且 destination 有 max(0,·) 钳制不能任意平移。

    /// <summary>最近一次被闸门拒绝的倍率（0 = 没有待跳过的拒绝）。
    /// 滚轮每格都会调进 <see cref="TryHandleCompareWheel"/>，若每次都把拒绝原因写状态栏，
    /// 连续滚动就是一串刷屏 —— 记下被拒的倍率，同一倍率的重复请求静默跳过。</summary>
    private double _compareMagnifyRejectedZoom;
    /// <summary>放大态平移：本次拖动累积的屏幕位移（物理像素）。松手时才一次性提交 ——
    /// 逐帧改 crop 会连带逐帧 <c>SetWindowRgn</c>，那是 docs/26 明确反对的高频路径。</summary>
    private double _comparePanAccumX, _comparePanAccumY;

    /// <summary>滚轮 → 多路同步无缝放大（光标锚点）。
    ///
    /// <para><b>分流</b>：对比模式下滚轮改的是"所有路共同的放大倍率 + 裁切位置"
    /// （<see cref="SetCompareMagnify"/>），而不是单路 <c>_viewZoom</c>。
    /// 非对比模式返回 false，调用方照旧走原路径 —— 既有行为零变化。</para>
    ///
    /// <para><b>锚点公式</b>：设当前倍率 <c>z0</c>、露出区间左上角 <c>u0</c>、光标下的源坐标 <c>u</c>，
    /// 目标倍率 <c>z1</c>。要求"光标下的那一点在屏幕上不动"：
    /// <c>(u-u0)·z0 == (u-u1)·z1</c> ⇒ <c>u1 = u - (u-u0)·z0/z1</c>。
    /// z0=1（未放大）时退化为 <c>u1 = u - u/z1</c>，与 docs/47 §4.2 给出的式子一致。</para>
    ///
    /// <para><b>为什么不能复用 <c>_viewZoom</c></b>：它是内核视口变换，恒为双线性插值 ⇒
    /// "放大后看到的是插出来的像素"，而本方案放大子窗口物理尺寸 ⇒ 内核按更高分辨率真实重渲染。
    /// 这正是"无缝放大"存在的理由。</para></summary>
    /// <returns>true = 已由对比放大处理（调用方不要再动 <c>_viewZoom</c>）。</returns>
    internal bool TryHandleCompareWheel(short delta)
    {
        // 叠加模式下放大被禁用（两者都改子窗口矩形 + 区域，语义互斥）：不接管滚轮，
        // 让单路 _viewZoom 路径继续工作，而不是"看似处理了其实什么都没发生"。
        if (!_compareActive || _compareOverlayActive) return false;

        var z0 = _compareZoom > 1.0 ? _compareZoom : 1.0;
        var factor = delta > 0 ? 1.15 : 1.0 / 1.15;
        var z1 = Math.Clamp(z0 * factor, 1.0, MaxCompareZoom);

        // 缩到底：显式退出放大（ResetCompareMagnify 是幂等的，未放大时调用无副作用）。
        if (z1 <= 1.0 + 1e-9)
        {
            _compareMagnifyRejectedZoom = 0;
            ResetCompareMagnify();
            ScheduleCompareCrop();
            NotifyCompareZoomChanged(1.0);
            return true;
        }

        // 已经到上限还在往同方向滚：不再调用（否则每格写一次"超过上限"提示 ⇒ 刷屏）
        if (Math.Abs(z1 - z0) < 1e-9) return true;
        if (Math.Abs(z1 - _compareMagnifyRejectedZoom) < 1e-9) return true;

        var anchor = CursorSourceFraction(); // 取不到（黑边/演示模式）就按画面中心

        // ⚠ 两种对齐模式下 crop 的**坐标域不同**，锚点换算必须分开，不能共用一套公式：
        //   · 相对对齐：crop 就是源画面归一化坐标，AnchorCrop 直接适用；
        //   · 像素级对齐：crop 是"视口在可平移范围内的相对位置"，其与坐标的换算是
        //     p = view·minW·(1-1/z)（p = 各路相同的源像素起点）。若把 view 当归一化坐标
        //     直接喂给 AnchorCrop，两种域的数值被相减 ⇒ 锚点偏移（从 1× 起第一次
        //     滚轮就偏，z=2 时只有正确值的一半）。
        double cropX, cropY;
        if (_compareAlign == CompareAlign.Pixel)
        {
            // 取不到锚点时也必须留在 Pixel 分支里：else 分支把"视口位置"当归一化坐标喂给
            // AnchorCrop 并按 [0,1-1/z] 钳位，等于把本轮刚修的"坐标域混用 + 右/下侧拖不到底"
            // 两个缺陷在光标落在黑边时原样复发（而扁格/瘦格里黑边占大半画面，光标几乎必落在黑边）。
            // 传 sourceWidth = 0 让 AnchorPixelAligned 走它自己的退化分支 = 视口位置不动。
            var a = anchor;
            cropX = AnchorPixelAligned(a?.U ?? 0, a?.SourceWidth ?? 0, _compareCropX, z0, z1);
            cropY = AnchorPixelAligned(a?.V ?? 0, a?.SourceHeight ?? 0, _compareCropY, z0, z1);
        }
        else
        {
            var u = anchor?.U ?? 0.5;
            var v = anchor?.V ?? 0.5;
            var maxCrop = CompareCropPlanner.MaxCropFraction(z1);
            cropX = ClampToCrop(CompareCropPlanner.AnchorCrop(u, _compareCropX, z0, z1), maxCrop);
            cropY = ClampToCrop(CompareCropPlanner.AnchorCrop(v, _compareCropY, z0, z1), maxCrop);
        }

        if (!SetCompareMagnify(z1, cropX, cropY))
        {
            _compareMagnifyRejectedZoom = z1; // 同一倍率的重复请求静默跳过
            return true;
        }

        _compareMagnifyRejectedZoom = 0;
        NotifyCompareZoomChanged(z1);
        return true;
    }

    /// <summary>像素级对齐下的锚点缩放：求新倍率对应的"视口位置"（0~1）。
    ///
    /// <para><b>推导</b>：各路露出的源像素区间恒为 <c>[p, p + minW/z]</c>（<c>p</c> = 各路相同的
    /// 像素起点）。锚点条件是"光标在格内的相对位置不变"：
    /// <c>(q-p0)/(minW/z0) == (q-p1)/(minW/z1)</c> ⇒ <c>p1 = q - (q-p0)·z0/z1</c>，
    /// 其中 <c>q = u·W_r</c> 是光标下的源像素坐标（<c>W_r</c> = 光标所在路的源宽）。
    /// 再按 <c>view = p1 / (minW·(1-1/z1))</c> 反算回视口位置。</para>
    ///
    /// <para><b>为什么不能直接套 <see cref="CompareCropPlanner.AnchorCrop"/></b>：那个公式的
    /// 输入/输出都是"源画面归一化坐标"，而像素级对齐下本方法的输入/输出都是"视口位置"。
    /// 两个域的数值被相减会得到错误结果（z=2 时约为正确值的一半）。</para>
    ///
    /// <para><b>退化</b>：<c>z1 = 1</c> 时可平移范围为 0 ⇒ 返回 0（与"贴左上"一致）；
    /// 源尺寸未知时返回当前视口位置（不动）。</para></summary>
    private double AnchorPixelAligned(double u, int sourceWidth, double currentView, double z0, double z1)
    {
        var min = CompareCropPlanner.MinSourceSize(_compareMagnifySources);
        if (min.Width <= 0 || sourceWidth <= 0) return currentView;

        var span = min.Width * (1.0 - 1.0 / z1);
        if (span <= 0) return 0; // z1 == 1：没有可平移的余地

        var q = u * sourceWidth;
        var p0 = currentView * min.Width * (1.0 - 1.0 / z0);
        var p1 = q - (q - p0) * (z0 / z1);
        return ClampUnit(p1 / span);
    }

    /// <summary>光标下的<b>源画面归一化坐标</b>（u, v）与该路的源尺寸；取不到返回 null
    ///（落在黑边、演示模式、会话已释放）。复用既有的 <c>MapPointerToVideoPixel</c>
    ///（已处理 letterbox 与 DPI）。
    ///
    /// <para>像素级对齐的锚点换算需要<b>源宽高</b>而不只是归一化坐标：归一化坐标要乘上
    /// 该路的源尺寸才能得到"像素坐标"，才能与"各路相同的像素起点 p"在同一域里比较。</para></summary>
    private (double U, double V, int SourceWidth, int SourceHeight)? CursorSourceFraction()
    {
        if (!GetCursorPos(out var cursor)) return null;
        var windowPos = this.PointToClient(new PixelPoint(cursor.X, cursor.Y));

        var surface = HitSurfaceAt(windowPos);
        if (surface is null) return null;

        var slots = _sync.Slots;
        if (surface.Index < 0 || surface.Index >= slots.Count) return null;

        var origin = surface.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } o) return null;
        var local = new Point(windowPos.X - o.X, windowPos.Y - o.Y);

        var session = slots[surface.Index].Session;
        var pixel = MapPointerToVideoPixel(surface, session, local);
        if (pixel is null) return null;

        var media = session.ReadMediaInfo();
        if (media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0) return null;

        return (pixel.Value.X / (double)media.VideoWidth, pixel.Value.Y / (double)media.VideoHeight,
                media.VideoWidth, media.VideoHeight);
    }

    /// <summary>把"视口位置"钳到 [0,1]（NaN → 0）。像素级对齐下 CropX / CropY 用这个区间，
    /// 与相对对齐的 [0, 1-1/z] 不同（见 <see cref="SetCompareMagnify"/>）。</summary>
    private static double ClampUnit(double v)
    {
        if (double.IsNaN(v)) return 0;
        if (v < 0) return 0;
        return v > 1 ? 1 : v;
    }

    /// <summary>把裁剪分量钳到 [0, <paramref name="maxCrop"/>]（NaN → 0）。
    /// 与 <c>CompareCropPlanner.ClampCrop</c> 同规则，只是后者是私有的；
    /// <c>Math.Clamp</c> 会原样放行 NaN，而 NaN 进入矩形会让 <c>SetWindowRgn</c> 收到翻转矩形。</summary>
    private static double ClampToCrop(double v, double maxCrop)
    {
        if (double.IsNaN(v)) return 0;
        if (v < 0) return 0;
        return v > maxCrop ? maxCrop : v;
    }

    /// <summary>放大态的拖动平移：按下时清零累积量。</summary>
    internal void BeginComparePan()
    {
        _comparePanAccumX = _comparePanAccumY = 0;
    }

    /// <summary>放大态的拖动平移：累积一次屏幕位移（物理像素）。<b>不</b>在这里提交 ——
    /// 逐帧改 crop 会连带逐帧 <c>SetWindowRgn</c>（docs/26：不要逐帧调用）。</summary>
    internal void AccumulateComparePan(double dxPx, double dyPx)
    {
        _comparePanAccumX += dxPx;
        _comparePanAccumY += dyPx;
    }

    /// <summary>放大态的拖动平移：松手时提交一次。
    ///
    /// <para><b>换算</b>：<see cref="CompareCropPlanner.Magnify"/> 的构造保证"格宽 Wc 恰好露出
    /// 源区间的 1/z" ⇒ 源区间整体（归一化 1.0）在屏幕上占 <c>Wc·z</c>。故位移 <c>d</c> 像素
    /// 对应的归一化位移为 <c>d / (Wc·z)</c>。画面跟手右移 ⇒ 露出的源区间左移 ⇒ 取负号。</para></summary>
    internal void CommitComparePan()
    {
        if (!CompareMagnifyActive) return;
        if (_comparePanAccumX == 0 && _comparePanAccumY == 0) return;

        var scaling = CompareCropScaling();
        if (!(scaling > 0)) scaling = 1.0;
        var dxDip = _comparePanAccumX / scaling;
        var dyDip = _comparePanAccumY / scaling;
        _comparePanAccumX = _comparePanAccumY = 0;

        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        if (cells.Length == 0) return;
        var wDip = Grid.Bounds.Width;
        var hDip = Grid.Bounds.Height;
        if (wDip <= 0 || hDip <= 0) return;

        // 各格尺寸不同（ABC 的 A 通高、B/C 半高）⇒ 用**光标所在格**的尺寸换算，
        // 否则在窄格里拖动的灵敏度会与宽格差一倍（表现为"上下拖不动"）。
        var cellIndex = CompareCellAtCursor();
        if (cellIndex < 0 || cellIndex >= cells.Length) cellIndex = 0;
        var cell = cells[cellIndex];

        var cellWDip = Math.Max(1, cell.Width * wDip);
        var cellHDip = Math.Max(1, cell.Height * hDip);

        // 换算系数随对齐模式而变，且**与钳位上界同源**（下面两者必须一起换，否则"拖不到底"
        // 或"拖过头"）：
        //   · 相对对齐：格宽 Wc 显示源画面的 1/z ⇒ 源归一化 1.0 在屏幕上占 Wc·z，
        //     crop 的合法上界是 1-1/z；
        //   · 像素级对齐：格宽 Wc 显示 minW/z 个源像素，而可平移总量是 minW·(1-1/z)
        //     ⇒ 拖满整个量程只需 Wc·(z-1) DIP（不是 Wc·z），view 的合法上界是 1。
        //     用 Wc·z 会让拖动慢 z/(z-1) 倍（z=2 时整整慢一倍）。
        var pixelAligned = _compareAlign == CompareAlign.Pixel;
        var maxCrop = pixelAligned ? 1.0 : CompareCropPlanner.MaxCropFraction(_compareZoom);
        var divisorX = cellWDip * (pixelAligned ? _compareZoom - 1.0 : _compareZoom);
        var divisorY = cellHDip * (pixelAligned ? _compareZoom - 1.0 : _compareZoom);
        if (divisorX <= 0) divisorX = cellWDip; // z→1 时量程趋于 0，避免除零
        if (divisorY <= 0) divisorY = cellHDip;

        var cropX = ClampToCrop(_compareCropX - dxDip / divisorX, maxCrop);
        var cropY = ClampToCrop(_compareCropY - dyDip / divisorY, maxCrop);

        SetCompareMagnify(_compareZoom, cropX, cropY);
    }

    /// <summary>光标落在第几格（放大态命中测试用）；不在对比区内返回 -1。</summary>
    private int CompareCellAtCursor()
    {
        if (!GetCursorPos(out var cursor)) return -1;
        var windowPos = this.PointToClient(new PixelPoint(cursor.X, cursor.Y));
        var origin = Grid.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } o) return -1;

        var p = new Point(windowPos.X - o.X, windowPos.Y - o.Y);
        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0) return -1;

        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        for (var i = 0; i < cells.Length; i++)
            if (CompareCropPlanner.CellToContainerDip(cells[i], w, h).Contains(p)) return i;
        return -1;
    }

    /// <summary>放大倍率变化后给用户可见反馈（状态栏）。
    /// 只在对比模式下写 —— 否则会把常规状态冲掉。</summary>
    private void NotifyCompareZoomChanged(double zoom)
    {
        if (!_compareActive) return;
        StatusInfo.Text = zoom <= 1.0
            ? Loc("已退出无缝放大（1:1）。", "Seamless zoom off (1:1).")
            : Loc($"无缝放大 {zoom:0.##}×（滚轮缩放 / 拖动平移）。",
                  $"Seamless zoom {zoom:0.##}× (wheel to zoom / drag to pan).");
    }

    /// <summary>
    /// 启用/更新"子窗口放大 + 裁剪"。<b>默认关闭</b>（<c>zoom = 1</c>，等价于现状）；
    /// 关闭状态下本方法以外的所有路径与改动前逐字一致。
    ///
    /// <para>所有路共用同一个 <paramref name="zoom"/>/<paramref name="cropX"/>/<paramref name="cropY"/>
    /// ⇒ 各路露出的是各自画面中相同的相对位置（无缝）。</para>
    ///
    /// <para><b>两道性能闸门</b>：① <see cref="MaxCompareZoom"/>；② <see cref="CompareMagnifyPixelBudget"/>
    /// 总像素预算。任一不满足即<b>拒绝并写明原因</b>（返回 false + 日志），不做静默降级 ——
    /// 静默降级会让用户以为"已经放大到 4 倍"而实际没有。</para>
    /// </summary>
    /// <param name="zoom">放大倍数，≥1。=1 表示关闭。</param>
    /// <param name="cropX">要露出的画面块左上角，<b>源画面归一化 x</b>（0~1）。自动钳到 <c>1-1/zoom</c>。</param>
    /// <param name="cropY">同上，y。</param>
    /// <param name="pixelBudget">像素预算覆盖值，≤0 表示用默认 <see cref="CompareMagnifyPixelBudget"/>。
    /// 仅供自测触发"超预算拒绝"分支（真实窗口尺寸下 z ≤ 4 撞不到该闸门）。</param>
    /// <returns>已接受为 true；被拒绝（倍数非法/超上限/超像素预算）为 false。</returns>
    internal bool SetCompareMagnify(double zoom, double cropX, double cropY, double pixelBudget = 0)
    {
        if (double.IsNaN(zoom) || zoom < 1.0)
        {
            RejectCompareMagnify($"放大倍数非法：{zoom}（要求 ≥ 1）");
            return false;
        }
        if (zoom <= 1.0)
        {
            ResetCompareMagnify();
            return true;
        }
        if (zoom > MaxCompareZoom)
        {
            RejectCompareMagnify(
                $"放大倍数 {zoom:0.##} 超过上限 {MaxCompareZoom:0.#}：子窗口面积按 z² 增长，" +
                $"{MaxCompareZoom:0.#} 倍已是 {MaxCompareZoom * MaxCompareZoom:0.#} 倍面积，再高会以平方速度吃显存");
            return false;
        }

        // ⚠ 对齐模式与源尺寸必须在**估算像素之前**就位：EstimateCompareMagnifyPixels 逐路按
        // 有效倍率 z_i = z·W_i/minW 累加，而 minW 来自源尺寸、只在像素级对齐下才启用。
        // 先估算再赋值的话，首次滚轮（或用户刚切成像素级）用的是上一次的 align 与空的
        // sources ⇒ minW=0 ⇒ 逐路退化成共用 z ⇒ 低估到 (minW/W_i)²（4K 与 1080p 同场、
        // z=2 时 4K 路实为 8 倍，按 2 倍估算会让显存闸门形同虚设）。
        _compareAlign = (CompareAlign)_settings.CompareAlign; // AppSettings.Normalize 已钳到 [0,1]
        _compareMagnifySources = ReadCompareMagnifySources();

        if (_compareAlign == CompareAlign.Pixel)
        {
            // 像素级对齐下 CropX / CropY 的语义是"视口在可平移范围内的相对位置"，
            // 合法区间是 [0,1]，不是相对对齐的 [0, 1-1/z] —— 按后者钳会把右/下侧
            // 一大块变成永远看不到（表现为"拖不到底"）。
            cropX = ClampUnit(cropX);
            cropY = ClampUnit(cropY);
        }

        var budget = pixelBudget > 0 ? pixelBudget : CompareMagnifyPixelBudget;
        var pixels = EstimateCompareMagnifyPixels(zoom);
        if (pixels > budget)
        {
            RejectCompareMagnify(
                $"放大 {zoom:0.##} 倍后子窗口总像素 {pixels / 1e6:0.#} Mpx 超过预算 " +
                $"{budget / 1e6:0.#} Mpx（≈4 路 4K，BGRA8 单缓冲约 127 MB × 2~3 缓冲）");
            return false;
        }

        _compareMagnifyBudget = budget;
        _compareZoom = zoom;
        _compareCropX = cropX;
        _compareCropY = cropY;
        _compareMagnifyBudgetNote = string.Empty;
        _compareMagnifyHwndRetries = 0;
        PushCompareMagnifyToGrid();
        // 窗口尺寸变了 ⇒ 布局要重跑；区域必须等新布局落地后再下发。
        ScheduleCompareCrop();
        return true;
    }

    /// <summary>关闭放大，回到"窗口 == 格"（等价于现状）。退出对比模式时必须调用 ——
    /// 否则子窗口会保持放大尺寸，而对比模式的格表已经撤掉，窗口会盖住相邻路。</summary>
    internal void ResetCompareMagnify()
    {
        _compareZoom = 1.0;
        _compareCropX = _compareCropY = 0;
        // 对齐模式同样要复位：它决定下一次放大的倍率换算，留着旧值会让"复位后第一次
        // 滚轮"用上一次的模式做预算估算（配合 SetCompareMagnify 里已提前的赋值顺序，
        // 这里复位能保证"未放大"这一状态在语义上是完整的）。
        _compareAlign = (CompareAlign)_settings.CompareAlign;
        _compareMagnifySources = Array.Empty<PixelSize>();
        _compareMagnifyBudget = CompareMagnifyPixelBudget;
        _compareMagnifyBudgetNote = string.Empty;
        _compareAlignNote = string.Empty;
        _compareMagnifyHwndRetries = 0;
        PushCompareMagnifyToGrid();
    }

    /// <summary>把当前放大参数（含预算复核）交给 <see cref="CompareGridView"/>；不生效时置回 null。</summary>
    /// <returns>排版参数**真的变了**为 true（调用方据此知道需要等一次新布局）。</returns>
    private bool PushCompareMagnifyToGrid()
    {
        var next = BuildCellMagnify();
        if (SameMagnify(Grid.CellMagnify, next)) return false;
        Grid.CellMagnify = next;
        return true;
    }

    /// <summary>放大参数的值相等判定。必须按值比：<see cref="CellMagnify"/> 是 record，但
    /// <c>Sources</c> 是 <c>IReadOnlyList</c>，record 生成的相等性对它是<b>引用</b>比较 ⇒
    /// 每次新建实例都会被判为"变了"，于是每次结构性变化都白跑一次布局。</summary>
    private static bool SameMagnify(CellMagnify? a, CellMagnify? b)
    {
        if (a is null || b is null) return ReferenceEquals(a, b);
        // Align 必须参与比较：切换对齐模式后各路的有效倍率 / 裁剪都变了，
        // 漏比它会让 Grid 认为"参数没变"而不重排（表现为切了模式画面不动）。
        if (a.Align != b.Align) return false;
        if (a.Zoom != b.Zoom || a.CropX != b.CropX || a.CropY != b.CropY) return false;
        if (a.Sources.Count != b.Sources.Count) return false;
        for (var i = 0; i < a.Sources.Count; i++)
            if (a.Sources[i] != b.Sources[i]) return false;
        return true;
    }

    /// <summary>构造格表用的放大参数；不满足条件（未启用 / 非对比模式 / 超预算）时返回 null。
    ///
    /// <para><b>预算在这里复核一次</b>：<see cref="SetCompareMagnify"/> 的估算发生在启用那一刻，
    /// 之后用户仍可能把窗口拉大、或把分割线拖成更大的格。若不复核，一次拉窗就能绕过闸门 ——
    /// 闸门必须在<b>真正分配之前</b>生效，而不是只在入口处检查一次。</para></summary>
    private CellMagnify? BuildCellMagnify()
    {
        // 叠加模式下禁用无缝放大：两者都要改子窗口矩形与区域，语义互斥（放大是"窗口大于格 +
        // 裁出格"，叠加是"两窗铺满 + 裁出半区"）。见 MainWindow.CompareOverlay.cs。
        if (!_compareActive || _compareZoom <= 1.0 || _compareOverlayActive) return null;

        // R7（docs/41:119）：源尺寸原本只在"启用放大那一刻"读一次，之后增删路数不会刷新
        // ⇒ 数组长度与当前路数脱节，新增的路取到 (0,0)（按"画面铺满窗口"换算 ⇒ 露出
        // **不同**的源区间，"无缝"名不副实）。路↔格映射（互换 / 自选源）会让路序动态变化，
        // 直接放大这个缺陷，故在这里按"长度不符即重读"兜住 —— 只在真的增删过路时触发，
        // 不会成为热路径（ReadCompareMagnifySources 是 P/Invoke）。
        if (_compareMagnifySources.Length != _sync.Slots.Count)
            _compareMagnifySources = ReadCompareMagnifySources();

        var pixels = EstimateCompareMagnifyPixels(_compareZoom);
        if (pixels > _compareMagnifyBudget)
        {
            var note = $"{_compareZoom:0.##}@{pixels / 1e6:0.#}Mpx";
            if (note != _compareMagnifyBudgetNote)
            {
                _compareMagnifyBudgetNote = note;
                RejectCompareMagnify(
                    $"布局变化后放大 {_compareZoom:0.##} 倍的总像素 {pixels / 1e6:0.#} Mpx 超过预算 " +
                    $"{_compareMagnifyBudget / 1e6:0.#} Mpx ⇒ 本次不放大（画面回到 1:1，不会爆显存）");
            }
            return null;
        }
        _compareMagnifyBudgetNote = string.Empty;

        // 像素级对齐但没有一路知道源尺寸（演示模式 / 纯音频）⇒ 没有基准，无从换算。
        // 这里**不**静默退回相对对齐：那会让用户以为"已经像素对齐了"。写一条提示说明退化。
        if (_compareAlign == CompareAlign.Pixel &&
            CompareCropPlanner.MinSourceSize(_compareMagnifySources).Width <= 0)
        {
            // 独立字段而不是复用 _compareMagnifyBudgetNote：两者指纹不同（"2.0@70.0Mpx"
            // vs 固定串），共用一个字段会互相覆盖 ⇒ 交替出现时去重双双失效、提示刷屏。
            var note = "pixel-nosource";
            if (note != _compareAlignNote)
            {
                _compareAlignNote = note;
                _3FCompare.Core.Diagnostics.AppLog.Warn("CompareMagnify",
                    "像素级对齐退化：没有任何一路能读出源尺寸（演示模式 / 纯音频），本次按相对对齐处理");
                if (_compareActive)
                    StatusInfo.Text = Loc("像素级对齐不可用：读不到源分辨率，本次按相对对齐处理。",
                                          "Pixel alignment unavailable: no source resolution known; using relative alignment.");
            }
        }

        return new CellMagnify(_compareZoom, _compareCropX, _compareCropY, _compareMagnifySources, _compareAlign);
    }

    /// <summary>估算放大后所有路子窗口的总物理像素数。
    /// <para>上界推导：各格互不重叠且铺满对比区 ⇒ <c>Σ(格面积) ≤ 对比区面积</c>，
    /// 于是 <c>Σ(格面积 × z²) ≤ 对比区物理面积 × z²</c>。这里按<b>实际格表逐格累加</b>，
    /// 比"对比区 × z²"更紧（格数 &lt; 路数时多余的路不计）。</para>
    /// <para>布局尚未落地（尺寸为 0）时返回 0 —— 此时无从估算，交由 <see cref="BuildCellMagnify"/>
    /// 在布局落地后复核。</para></summary>
    private double EstimateCompareMagnifyPixels(double zoom)
    {
        var wDip = Grid.Bounds.Width;
        var hDip = Grid.Bounds.Height;
        if (wDip <= 0 || hDip <= 0) return 0;

        var scaling = CompareCropScaling();
        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        // 像素级对齐下各路的**有效**倍率不同（z_i = z·W_i/minW），高分辨率路的窗口按平方更大。
        // 必须逐路按 z_i 累加：沿用共用 z 会低估到 (minW/W_i)² —— 4K 与 1080p 同场、
        // z=2 时 4K 路实为 8 倍（64 倍面积），按 2 倍估算等于把显存闸门当成摆设。
        var sources = _compareMagnifySources;
        var align = _compareAlign;
        var minW = align == CompareAlign.Pixel
            ? CompareCropPlanner.MinSourceSize(sources).Width
            : 0;

        double total = 0;
        for (var cell = 0; cell < cells.Length && cell < Grid.Count; cell++)
        {
            var cellPx = CompareCropPlanner.ToPhysicalRect(
                CompareCropPlanner.CellToContainerDip(cells[cell], wDip, hDip), scaling);

            // 必须按"格 → 路"映射配对：cells[i] 是**格**、sources[i] 是**路**，
            // 互换 / 轮换之后两者不再同序（4 路用 AB 轮换到 [2,3] 时，循环里的
            // sources[0]/[1] 是根本没在显示的两路）⇒ 会低估到让显存闸门失效。
            var route = Grid.RouteIndexOfCell(cell);
            if (route < 0) route = cell;
            var zi = route < sources.Length && sources[route].Width > 0 && minW > 0
                ? CompareCropPlanner.AlignedZoom(zoom, sources[route].Width, minW)
                : zoom;
            total += (double)cellPx.Width * zi * cellPx.Height * zi;
        }
        return total;
    }

    /// <summary>读每路源画面尺寸（放大换算需要它才能算 letterbox 适配，见
    /// <see cref="CompareCropPlanner.FitDestination"/>）。读不到（演示模式 / 纯音频 / 会话已释放）
    /// 记 (0,0)，换算时按"画面铺满窗口"处理 —— 这是唯一安全的假设。
    /// <para>只在启用/更新放大时读一次：<c>ReadMediaInfo</c> 在引擎内已缓存，但仍是 P/Invoke，
    /// 不该出现在结构性变化的热路径上。</para></summary>
    private PixelSize[] ReadCompareMagnifySources()
    {
        var slots = _sync.Slots;
        var result = new PixelSize[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            try
            {
                var media = slots[i].Session.ReadMediaInfo();
                var w = media?.VideoWidth ?? 0;
                var h = media?.VideoHeight ?? 0;
                result[i] = w > 0 && h > 0 ? new PixelSize(w, h) : default;
            }
            catch (Exception ex)
            {
                result[i] = default;
                _3FCompare.Core.Diagnostics.AppLog.Debug(
                    "CompareMagnify", $"第 {i} 路源尺寸读取失败（按未知处理）：{ex.GetType().Name}");
            }
        }
        return result;
    }

    /// <summary>拒绝放大：写日志 + 写状态栏（用户可见，不静默）。</summary>
    private void RejectCompareMagnify(string reason)
    {
        // 组件日志：放大闸门拒绝（像素预算 / 倍数非法 / 布局变化后超预算）。
        // 用 Render 组件归类 —— 它决定的是"呈现是否进入放大路径"。
        ComponentLog.Log(Comp.Render, "MagnifyRejected", -1, reason);
        _3FCompare.Core.Diagnostics.AppLog.Warn("CompareMagnify", "拒绝启用无缝放大：" + reason);
        if (_compareActive) StatusInfo.Text = Loc($"未启用无缝放大：{reason}", $"Seamless zoom not enabled: {reason}");
    }

    /// <summary>已排队一次延迟下发（合并同一轮内的多次请求，避免重复计算与重复 Win32 调用）。</summary>
    private bool _compareCropScheduled;

    /// <summary>挂接裁剪重设的时机。由构造函数在最后调用一次。</summary>
    /// <remarks>
    /// <b>为什么是这几个时机</b>：裁剪矩形只依赖"结构几何"——格表、对比区尺寸、DPI、子 HWND 是否还在。
    /// 因此：
    /// <list type="bullet">
    /// <item><description>对比区 <c>Bounds</c> 变化：窗口缩放、侧栏折叠、全屏切换都会改它，
    /// 且**不一定会**触发宿主 <c>SizeChanged</c>（侧栏折叠就是典型），故必须单独盯它；</description></item>
    /// <item><description>宿主 <c>ScalingChanged</c>：跨屏拖动导致 DPI 变化，区域是物理像素 ⇒ 必须重算；</description></item>
    /// <item><description>新表面 / 表面重新挂到视觉树：子 HWND 在这两处（重）创建，旧区域随旧句柄一起消失，
    /// 新句柄必须重设 —— 这就是"子 HWND 重建时重设"。</description></item>
    /// </list>
    /// <b>为什么盯 <c>Bounds</c> 而不是 <c>LayoutUpdated</c></b>：后者每次布局都触发（窗口拖动缩放时逐帧），
    /// 会把本文件变成一条新热路径；<c>Bounds</c> 只在几何真的变了才触发，等价但零空转。
    /// </remarks>
    private void WireCompareCrop()
    {
        Grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.BoundsProperty) ScheduleCompareCrop();
        };
        SizeChanged += (_, _) => ScheduleCompareCrop();
        ScalingChanged += (_, _) => ScheduleCompareCrop();
        // 已存在的表面也要挂：构造期创建的初始表面早于本方法，且窗口 Show 之前
        // AttachedToVisualTree 尚未触发过，只挂 SurfaceCreated 会漏掉它们。
        foreach (var surface in Grid.Surfaces)
            surface.AttachedToVisualTree += (_, _) => ScheduleCompareCrop();
        Grid.SurfaceCreated += surface =>
            surface.AttachedToVisualTree += (_, _) => ScheduleCompareCrop();
    }

    /// <summary>排队一次裁剪下发。合并同一轮内的多次请求；真正的计算在 UI 线程的下一拍进行，
    /// 那时布局已经落地（子 HWND 已按新格位摆放），不会拿到过期几何。</summary>
    private void ScheduleCompareCrop()
    {
        // 非对比模式且没有遗留区域 ⇒ 无事可做。这一步让"挂时机"在常规使用下**零成本**
        // （否则窗口缩放/侧栏折叠每次都会往 Dispatcher 排一个空任务）。
        if (!_compareActive && _compareCropApplied.Count == 0) return;
        if (_compareCropScheduled) return;
        _compareCropScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _compareCropScheduled = false;
            ApplyCompareCrop();
        }, DispatcherPriority.Background);
    }

    /// <summary>唯一的裁剪下发入口：按当前格表/放大参数重算每一路的区域并应用（或清除）。
    /// 必须在 UI 线程调用（读 Avalonia 布局状态 + 对子 HWND 做 Win32 调用）。</summary>
    /// <remarks>
    /// <b>放大/平移参数如何进入</b>：它们不进本方法，而是通过"子窗口矩形"进入 —— 窗口被排成
    /// "格的 z 倍并偏移"，格内露出的就是目标画面块（换算见
    /// <see cref="CompareCropPlanner.Magnify"/>）。未启用放大时窗口 ≈ 格，区域只裁掉
    /// Avalonia 向外取整的 ≤1px 外扩。
    ///
    /// <b>为什么不在平移/裁剪拖动过程中逐帧重算</b>：任务明确要求"不要逐帧调用（会引入新热路径）"，
    /// 且 <c>SetWindowRgn</c> 在 <c>bRedraw=true</c> 下会触发重绘 —— 60Hz 改区域对 flip-model
    /// 呈现的影响尚未实测（探针只验过"改区域不重建 swapchain"）。故裁剪只在<b>结构变化</b>时重算：
    /// 进入/退出、分割变化、对比区 Bounds、DPI、表面重挂载，以及放大参数被显式设置时。
    /// 平移/裁剪参数本身是"显式设置一次"的量（<see cref="SetCompareMagnify"/>），不是拖动量。
    /// </remarks>
    internal void ApplyCompareCrop()
    {
        if (!_compareActive)
        {
            // 退出对比模式：放大参数必须一并复位，否则子窗口会保持放大尺寸而格表已撤掉。
            // ExitCompareMode 已经复位过，这里是兜底（幂等）。
            ResetCompareMagnify();
            ClearCompareCrop();
            return;
        }

        var wDip = Grid.Bounds.Width;
        var hDip = Grid.Bounds.Height;
        if (wDip <= 0 || hDip <= 0)
        {
            ClearCompareCrop();
            return;
        }

        // 排版参数可能与 Grid 当前排版不一致（首次启用放大、或布局变大后预算被打破）。
        // 此时**本拍不下发区域**：窗口还停在旧位置，按新参数裁会把画面裁到错误的块上。
        // 先同步排版，再排一拍等布局落地后重来 —— 下一拍 BuildCellMagnify 稳定，必然收敛。
        if (PushCompareMagnifyToGrid())
        {
            ScheduleCompareCrop();
            return;
        }

        var cells = CompareLayout.ComputeCells(_compareMode, _compareSplit);
        var scaling = CompareCropScaling();
        // 放大参数（含预算复核）。null = 未启用 ⇒ 走既有路径，行为与改动前逐字一致。
        var magnify = BuildCellMagnify();
        // 放大路径下"子 HWND 还没被 Avalonia 搬到放大位置"的路：本拍不下发区域，末尾再排一拍。
        var waitingForHwnd = false;

        for (var i = 0; i < Grid.Count; i++)
        {
            var surface = Grid.GetSurface(i);
            if (surface is null) continue;

            var hwnd = surface.Hwnd;
            if (hwnd == nint.Zero)
            {
                // 子 HWND 尚未创建（或已被销毁）：忘掉旧状态，等 AttachedToVisualTree 再来一次。
                // 这里刻意不 Clear —— 对一个已经不存在的句柄调 SetWindowRgn 没有意义。
                _compareCropApplied.Remove(i);
                continue;
            }

            // 格数 < 路数时多余的路被隐藏（见 CompareGridView.ArrangeOverride）：它们不该有区域。
            // 该路显示在哪一格由"格→路"映射决定（互换 / 自选源后不是第 i 格）——
            // 按路号取格会裁到别的路的格上，表现为"画面缺一块、且裁错了位置"。
            var cellIndex = Grid.CellIndexOfRoute(i);
            if (!surface.IsVisible || cellIndex < 0 || cellIndex >= cells.Length)
            {
                ClearOne(i, hwnd);
                continue;
            }

            // ── 第三条分支：叠加模式（ICAT Single Screen，见 MainWindow.CompareOverlay.cs）──
            // 与下面两条的区别：格表已被换成"两路铺满"，裁剪语义不是"裁出格"而是"裁出互补的半区"
            // —— B（第 1 路）露 [0, split]、A（第 0 路）露 [split, 1]，两者互不重叠且并集为整窗，
            // 故可见结果与容器 Z 序完全无关（不需要任何 SetWindowPos）。这里**永远不 Clear**：
            // 任何一侧"不裁"都会变成整窗可见，重新把可见性交回给 Z 序（见 OverlayRevealPlan）。
            if (_compareOverlayActive)
            {
                var overlayWinPx = CompareCropPlanner.ToPhysicalRect(surface.Bounds, scaling);
                if (!OverlayRevealPlan(overlayWinPx, _compareSplit.X, _compareOverlayAxis,
                        out var regionA, out var regionB))
                {
                    ClearOne(i, hwnd); // 对比区窄于 2px：无法表达互补，不裁剪
                    continue;
                }

                ApplyCropRegion(i, hwnd, i == CompareOverlayTopIndex ? regionB : regionA);
                continue;
            }

            var cellDip = CompareCropPlanner.CellToContainerDip(cells[cellIndex], wDip, hDip);
            var cellPx = CompareCropPlanner.ToPhysicalRect(cellDip, scaling);

            CompareCropPlanner.CropPlan plan;
            if (magnify is null)
            {
                // 未放大：窗口矩形取**实际排布结果**（Bounds 相对父容器），而不是再算一遍格表 ——
                // 两者不一致时（未来布局放大窗口）以实际为准，格表只用来定"要露出哪一块"。
                var windowPx = CompareCropPlanner.ToPhysicalRect(surface.Bounds, scaling);
                plan = CompareCropPlanner.Plan(windowPx, cellPx);
            }
            else
            {
                // 放大：窗口已被 Grid 排成"格的 z 倍并偏移"，区域由**源画面比例**换算
                // （含 letterbox 修正，见 CompareCropPlanner.Magnify）。
                // 像素级对齐下每路裁剪区间不同 ⇒ 逐路取 CropOf（与 ArrangeOverride 同源），
                // 否则窗口按 A 路的偏移摆、区域却按共用 crop 裁 ⇒ 露出错误的块。
                var src = magnify.SourceOf(i);
                var crop = magnify.CropOf(i);
                var zoom = magnify.ZoomOf(i);
                var geom = CompareCropPlanner.Magnify(
                    cellDip, zoom, crop.CropX, crop.CropY, src.Width, src.Height);
                var expectedWinPx = CompareCropPlanner.ToPhysicalRect(geom.WindowDip, scaling);

                // 两重"还没落地"的判定，任一不满足就本拍不下发：
                //  ① Avalonia 的排版（Bounds）—— 排布矩形与换算出的窗口不符；
                //  ② **OS 窗口本身**（GetWindowRect）—— Avalonia 的 NativeControlHost 把
                //     ShowInBounds 排在 AfterRender 优先级，比本方法（Background）晚一拍执行。
                //     只看 Bounds 会漏掉这一拍：区域已按放大窗口算好，窗口却还是格的尺寸，
                //     区域大半落在窗口之外 ⇒ 该路画面瞬间缺一块。
                if (!RectApproximately(surface.Bounds, geom.WindowDip) ||
                    !HwndMatchesPixels(hwnd, expectedWinPx))
                {
                    waitingForHwnd = true;
                    continue;
                }

                plan = CompareCropPlanner.MagnifyPlan(
                    cellDip, zoom, crop.CropX, crop.CropY, src.Width, src.Height, scaling);
            }

            if (!plan.ShouldApply)
            {
                ClearOne(i, hwnd);
                continue;
            }

            ApplyCropRegion(i, hwnd, plan.Region);
        }

        // 有路在等子 HWND 搬到放大位置 ⇒ 再排一拍（AfterRender 的 ShowInBounds 早于本优先级执行，
        // 故下一拍通常已就位）。没有这一步，那些路会永远停在"窗口已放大但没有区域"的状态：
        // Bounds 不变 ⇒ 不会再触发 ScheduleCompareCrop，画面会一直缺一块。
        // 重试有界（见 MaxCompareMagnifyHwndRetries）：始终搬不到位时宁可放弃放大并写日志，
        // 也不要在 Dispatcher 上死循环。
        if (waitingForHwnd)
        {
            if (++_compareMagnifyHwndRetries <= MaxCompareMagnifyHwndRetries)
            {
                ScheduleCompareCrop();
            }
            else
            {
                _compareMagnifyHwndRetries = 0;
                _3FCompare.Core.Diagnostics.AppLog.Warn("CompareMagnify",
                    $"子窗口连续 {MaxCompareMagnifyHwndRetries} 拍未按放大方案就位 ⇒ 本次放弃无缝放大");
                ResetCompareMagnify();
                ScheduleCompareCrop();
            }
        }
        else
        {
            _compareMagnifyHwndRetries = 0;
        }
    }

    /// <summary>子 HWND 的实际物理尺寸是否等于 <paramref name="expectedPx"/>（容差 2px，吸收
    /// Avalonia 在 DIP→物理时按 <c>(int)</c> 截断产生的偏差）。
    /// <para>只看尺寸不看位置：区域是<b>窗口相对坐标</b>，其有效性只取决于窗口尺寸；
    /// 而位置与尺寸由同一次 <c>SetWindowPos</c>/<c>MoveWindow</c> 落地，尺寸对上即说明这一拍已搬完。</para></summary>
    private static bool HwndMatchesPixels(nint hwnd, Rect32 expectedPx)
    {
        var actual = ReadWindowRectPx(hwnd);
        return actual.IsValid &&
               Math.Abs(actual.Width - expectedPx.Width) <= 2 &&
               Math.Abs(actual.Height - expectedPx.Height) <= 2;
    }

    /// <summary>读子 HWND 的屏幕矩形（物理像素）。读失败返回 <c>default</c>（<c>IsValid == false</c>）。</summary>
    private static Rect32 ReadWindowRectPx(nint hwnd)
    {
        if (hwnd == nint.Zero || !GetWindowRect(hwnd, out var r)) return default;
        return new Rect32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>清除全部裁剪，回到"整窗可见"。退出对比模式、以及对比模式被自动降级时都必须走这里 ——
    /// 漏掉任何一条路径都会把窗口永久裁着（用户看到的是"画面缺了一块"，且重启才恢复）。</summary>
    internal void ClearCompareCrop()
    {
        // 组件日志：全量清除裁剪（退出对比模式 / 被自动降级的必经路径）。
        // 与 RegionApply 配对读，可确认"窗口被永久裁着"这类事故是否发生。
        ComponentLog.Log(Comp.Overlay, "RegionClearAll", -1, $"count={_compareCropApplied.Count}");
        foreach (var state in _compareCropApplied.Values)
        {
            if (WindowRegionClipper.Clear(state.Hwnd)) InvalidateChildParent(state.Hwnd);
        }
        _compareCropApplied.Clear();
    }

    /// <summary>清除某一路的裁剪。句柄已变（HWND 被重建）时只丢状态 —— 旧句柄的区域随窗口销毁，
    /// 对新句柄调用 Clear 反而会误清一个刚建好、还没设过区域的窗口。</summary>
    private void ClearOne(int index, nint hwnd)
    {
        if (!_compareCropApplied.TryGetValue(index, out var prev)) return;
        _compareCropApplied.Remove(index);
        if (prev.Hwnd != hwnd) return;
        // 组件日志：单路清除裁剪（格数 < 路数时多余路被隐藏、叠加区过窄等分支）
        ComponentLog.Log(Comp.Overlay, "RegionClear", index, $"hwnd=0x{hwnd:X}");
        if (WindowRegionClipper.Clear(hwnd)) InvalidateChildParent(hwnd);
    }

    /// <summary>下发某一路的区域并记账（去重 + 失效重绘）。
    /// <para>三条分支（未放大 / 放大 / 叠加）共用同一份 <c>_compareCropApplied</c> 状态 ——
    /// 这是"退出时一定能清除"的前提：清除只认这一份账，任何一条路径漏记都会让窗口被永久裁剪。</para></summary>
    private void ApplyCropRegion(int index, nint hwnd, Rect32 region)
    {
        if (_compareCropApplied.TryGetValue(index, out var prev) &&
            prev.Hwnd == hwnd && prev.Region == region)
            return; // 方案未变：不重复调用（bRedraw=true 会触发一次重绘）

        if (!WindowRegionClipper.ApplyRect(hwnd, region.X, region.Y, region.Width, region.Height)) return;
        _compareCropApplied[index] = (hwnd, region);
        InvalidateChildParent(hwnd);
        // 组件日志：SetWindowRgn 真的下发成功（去重之后，即"确实调了 Win32"的那一次）。
        // 窗口区域与 D3D11 flip-model 交换链的交互是本项目已知的高风险面，必须留痕。
        ComponentLog.Log(Comp.Overlay, "RegionApply", index,
            $"hwnd=0x{hwnd:X} rect={region.X},{region.Y},{region.Width},{region.Height}");
    }

    /// <summary>区域变化后让父窗口重绘被"露出/收回"的那条带。
    /// <para>依据：探针记录里列为"待确认"的一条 —— 父窗口带 <c>WS_CLIPCHILDREN</c> 时，
    /// 子窗口区域变化露出的父窗口像素不保证自动重绘。本应用的顶层窗口正是 <c>WS_CLIPCHILDREN</c>
    /// （见 <c>ToggleFullscreen</c> 一带的样式处理），故显式失效一次。</para>
    /// <para>只在区域**真的变化**时调用（正常路径一天也走不到几次），不会成为热路径。</para></summary>
    private static void InvalidateChildParent(nint child)
    {
        var parent = GetParent(child);
        if (parent != nint.Zero) InvalidateRect(parent, nint.Zero, false);
    }

    /// <summary>取物理像素换算用的缩放。优先用对比区所在 TopLevel 的 <c>RenderScaling</c>
    /// （与 <c>LayoutOverlayWindow.EffectiveScaling</c> 同一理由：宿主尚未 <c>Show</c> 时自身
    /// <c>RenderScaling</c> 会返回 1.0，那不是"未知"而是"尚未确定"，据此折算会算错高 DPI 首帧）。</summary>
    private double CompareCropScaling()
    {
        var scaling = TopLevel.GetTopLevel(Grid)?.RenderScaling ?? 0d;
        if (!(scaling > 0)) scaling = RenderScaling;
        return scaling > 0 ? scaling : 1.0;
    }

    /// <summary>两个矩形是否在 1.5 DIP 容差内相等（位置与尺寸）。
    ///
    /// <para><b>为什么需要容差</b>：放大路径要求"窗口已被排到换算出的位置"才下发区域。
    /// 期望值由 <see cref="CompareCropPlanner.Magnify"/> 从格表算出，实际值来自
    /// <c>PlayerSurface.Bounds</c>；Avalonia 会把排布矩形向外取整到物理像素
    /// （<c>NativeControlHost.GetAbsoluteBounds</c> 的 <c>RoundLayoutValue</c>），
    /// 两者会有亚像素级差异。容差取 1.5 DIP：足以吸收取整，又能在"布局真的没落地"
    /// （Bounds 仍是格的尺寸，差 z 倍）时明确判否。</para></summary>
    private static bool RectApproximately(Rect actual, Rect expected)
    {
        const double tolerance = 1.5;
        return Math.Abs(actual.X - expected.X) <= tolerance &&
               Math.Abs(actual.Y - expected.Y) <= tolerance &&
               Math.Abs(actual.Width - expected.Width) <= tolerance &&
               Math.Abs(actual.Height - expected.Height) <= tolerance;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetParent(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct CropWndRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out CropWndRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);
}
