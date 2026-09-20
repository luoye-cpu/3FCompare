using System;
using Avalonia;
using _3FCompare.Controls;
using _3FCompare.Core.Display;
using _3FCompare.Platform;

namespace _3FCompare;

/// <summary>叠加模式（对标 NVIDIA ICAT <i>Single Screen</i>，见 docs/31 §2.1「阶段 2」）。
///
/// <para><b>是什么</b>：<see cref="CompareMode.Ab"/> 的一种<b>呈现变体</b> —— 不改 Core 的
/// <see cref="CompareLayout"/>（它有自己的单测与 <c>AvailableModes</c>/C 键循环语义），
/// 只在 UI 侧加一个开关：两路子 HWND <b>都铺满整个对比区</b>，再各由
/// <see cref="WindowRegionClipper"/> 裁成<b>互补且互不重叠</b>的两半 ——
/// B（第 1 路）露靠近原点的一侧 <c>[0, split]</c>、A（第 0 路）露另一侧 <c>[split, 1]</c>，
/// 于是拖动分割线即可"揭示 / 擦除"。这就是"两路真实视频叠加"（此前的 <c>AbSliderView</c>
/// 只是渐变占位，因为真实子 HWND 无法在托管层合成）。</para>
///
/// <para><b>为什么两路都裁（而不是"只裁 B、靠 Z 序盖住 A"）</b>：Avalonia 给每个
/// <c>NativeControlHost</c> 子窗口又套了一层容器 HWND，实测两路视频窗口的<b>父窗口不同</b>、
/// 彼此是顶层窗口下的兄弟 ⇒ "谁在上"由<b>容器之间</b>的 Z 序决定，而 <c>SetWindowPos</c>
/// 只作用于容器内部、对两路的相对可见性无效，靠创建顺序纯属偶然。既然两路区域互补且
/// 不重叠（并集 = 整窗），可见结果就与 Z 序<b>完全无关</b> —— 把一个隐性、会静默失效的
/// 依赖整个消掉。</para>
///
/// <para><b>为什么可行</b>：<c>SetWindowRgn</c> 对内核 D3D11 flip-model 呈现已验证生效
/// （docs/26 §10.1：区域外 5/5 露出下层）。</para>
///
/// <para><b>为什么单独一个 partial 文件</b>：与 <c>MainWindow.CompareCrop.cs</c> 同样的理由 ——
/// <c>MainWindow.axaml.cs</c> 混有未提交改动，新逻辑集中在这里，原文件只加几处单行挂钩。</para>
///
/// <para><b>退出必须清除区域</b>：两路的区域若残留，窗口会被<b>永久裁剪</b>（只有重启才恢复）。
/// 故退出走 <see cref="ClearCompareCrop"/>，与放大路径共用同一份 <c>_compareCropApplied</c> 状态。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>叠加揭示方向。两种方向只差一个矩形，共用同一条换算 / 下发 / 清除链路。</summary>
    internal enum OverlayAxis
    {
        /// <summary>水平：B 只露左侧 <c>[0, split]</c>、A 露右侧（分割线竖直，左右拖动）。</summary>
        Horizontal,

        /// <summary>垂直：B 只露上方 <c>[0, split]</c>、A 露下方（分割线水平）。</summary>
        Vertical,
    }

    /// <summary>叠加模式下两路共用的格表：都铺满整个对比区。
    ///
    /// <para>静态只读且复用同一实例：<see cref="CompareGridView.CellOverride"/> 只读该数组、从不改写它，
    /// 而分割线拖动会逐帧调用 <c>ApplyCompareLayout</c> —— 每帧新建两个 <see cref="CellRect"/> 是没必要的分配。</para>
    ///
    /// <para>长度恒为 2 ⇒ 路数 &gt; 2 时其余路被 <c>CompareGridView.ArrangeOverride</c> 隐藏
    /// （与"AB 只看前 2 路"的既有规则一致）。</para></summary>
    private static readonly CellRect[] CompareOverlayCells =
        [new CellRect(0, 0, 1, 1), new CellRect(0, 0, 1, 1)];

    /// <summary>上层路（B）在格表中的索引；下层路（A）恒为 0。</summary>
    private const int CompareOverlayTopIndex = 1;

    /// <summary>叠加模式是否激活。<b>只有它决定</b> <c>ApplyCompareLayout</c> 用"两路铺满"格表。</summary>
    private bool _compareOverlayActive;

    /// <summary>当前揭示方向。默认水平（分割线竖直，与 <see cref="CompareMode.Ab"/> 的手柄轴一致）。</summary>
    private OverlayAxis _compareOverlayAxis = OverlayAxis.Horizontal;

    /// <summary>叠加模式是否激活（供 <c>ApplyCompareLayout</c> / 命中测试 / 自测读取）。</summary>
    internal bool CompareOverlayActive => _compareOverlayActive;

    /// <summary>当前揭示方向（供自测读取）。</summary>
    internal OverlayAxis CompareOverlayAxis => _compareOverlayAxis;

    /// <summary>叠加模式的两路铺满格表（由 <c>ApplyCompareLayout</c> 在叠加激活时使用）。</summary>
    private static CellRect[] CompareOverlayLayout() => CompareOverlayCells;

    /// <summary>叠加模式入口（快捷键 <c>S</c>）：开 → 关 → 开。
    /// <para>关闭时<b>不退出对比模式</b>，只回到 AB 左右分栏 —— 叠加本就是 AB 的一种呈现变体。</para></summary>
    internal void ToggleCompareOverlay()
    {
        if (_compareOverlayActive)
        {
            ExitCompareOverlay();
            NotifyCompareOverlay();
            return;
        }
        EnterCompareOverlay();
    }

    /// <summary>进入叠加模式。路数 &lt; 2 时给状态栏提示且不进入（不静默）。
    ///
    /// <para><b>进入顺序</b>：先复位无缝放大（两者语义互斥：放大是"窗口大于格 + 区域裁出格"，
    /// 叠加是"两窗铺满 + 区域裁出半区"，同时生效会互相破坏），再确保处于 <see cref="CompareMode.Ab"/>
    /// （已在该模式时<b>不</b>重进，以保留用户已拖好的分割位置），最后换格表并排一拍下发区域。</para></summary>
    /// <returns>真的进入为 true；路数不足或对比模式降级为 false。</returns>
    internal bool EnterCompareOverlay()
    {
        if (_sync.Count < 2)
        {
            StatusInfo.Text = Loc("叠加模式需要至少 2 路视频。", "Overlay mode requires at least 2 routes.");
            return false;
        }
        if (_compareOverlayActive) return true;

        ResetCompareMagnify();

        // 非 AB 或未进入对比模式时才走 EnterCompareMode —— 后者会把分割参数重置为默认值，
        // 用户已调好的位置不该因为"打开叠加"被抹掉。
        if (!_compareActive || _compareMode != CompareMode.Ab)
            EnterCompareMode(CompareMode.Ab);
        if (!_compareActive) return false; // 覆盖层 fail-fast 降级（见 VerifyOverlayDegradation）

        _compareOverlayActive = true;
        ApplyCompareLayout();               // 换成"两路铺满"格表
        ScheduleCompareCrop();              // 区域依赖布局落地，延后一拍下发
        NotifyCompareOverlay();
        return true;
    }

    /// <summary>退出叠加模式：清状态 + <b>同步</b>清除区域 + 回到 AB 左右分栏。
    ///
    /// <para><b>必须同步清除</b>：延后一拍的话这一拍内两路仍被裁着，而任何异常路径
    /// （含 <c>OnClosing</c>）都不会再来补一次 ⇒ 窗口被永久裁剪。</para>
    ///
    /// <para>退出后两路各占半屏、不再重叠，区域也一并清掉，故不需要任何 Z 序补偿。</para></summary>
    internal void ExitCompareOverlay()
    {
        if (!_compareOverlayActive) return;
        _compareOverlayActive = false;
        ClearCompareCrop();
        if (_compareActive)
        {
            ApplyCompareLayout(); // 回到 AB 左右分栏
            ScheduleCompareCrop(); // 重新下发 AB 的裁剪（Avalonia 取整产生的 ≤1px 外扩）
        }
    }

    /// <summary>切换揭示方向（水平 / 垂直）。本阶段是<b>内部开关</b>：用户入口只有"开关叠加"的
    /// <c>S</c> 键，垂直方向由自测覆盖；方向只影响矩形，换算与下发完全共用。</summary>
    internal void SetCompareOverlayAxis(OverlayAxis axis)
    {
        if (_compareOverlayAxis == axis) return;
        _compareOverlayAxis = axis;
        if (_compareOverlayActive) ScheduleCompareCrop();
    }

    /// <summary>状态栏提示（与 <see cref="NotifyCompareMode"/> 同一风格：说明当前状态 + 下一步怎么按）。
    /// 退出叠加时直接复用 <see cref="NotifyCompareMode"/>，因为退出后回到的正是"对比模式 AB"。</summary>
    private void NotifyCompareOverlay()
    {
        if (!_compareOverlayActive)
        {
            NotifyCompareMode();
            return;
        }

        var axis = _compareOverlayAxis == OverlayAxis.Horizontal
            ? Loc("水平揭示", "horizontal reveal")
            : Loc("垂直揭示", "vertical reveal");
        var cells = CompareLayout.CellCount(CompareMode.Ab);
        var scope = _sync.Count > cells
            ? Loc($"，显示前 {cells} / 共 {_sync.Count} 路", $" (showing first {cells} of {_sync.Count} routes)")
            : "";
        StatusInfo.Text = Loc(
            $"叠加模式（{axis}）：两路铺满，拖动分割线揭示 B{scope}（按 S 退出 / 按 C 切换模式）",
            $"Overlay mode ({axis}): both routes full-area, drag the split line to reveal B{scope} (press S to exit / C to cycle)");
    }

    /// <summary>叠加模式下两路的可见区域（<b>互补且互不重叠</b>，并集恒等于整窗）。
    ///
    /// <para><b>坐标</b>：窗口相对、物理像素（<see cref="WindowRegionClipper"/> 的入参语义）。
    /// 区域恒从窗口原点起算 ⇒ 水平方向 B 露窗口左侧 <c>split</c> 比例、A 露右侧；垂直方向同理
    /// （B 露上、A 露下）。因为两半不重叠且铺满，可见结果与容器 Z 序无关。</para>
    ///
    /// <para><b>边界（为什么把分界点钳进 [1, span-1]）</b>：
    /// <list type="bullet">
    /// <item><description><see cref="Rect32"/> 要求宽高 &gt; 0 —— 0 尺寸区域会被
    /// <c>CreateRectRgn</c> 解释为空区域、<c>SetWindowRgn</c> 直接失败；而"隐藏子窗口"
    /// 会与 <c>CompareGridView.ArrangeOverride</c> 每拍重设 <c>IsVisible=true</c> 打架。</description></item>
    /// <item><description>故 <c>split ≤ 0</c> ⇒ 分界点取 1：B 只余 1px（视觉上等价于不可见）、
    /// A 占其余全部；<c>split ≥ 1</c> 反之，<b>A 只余 1px</b>。</description></item>
    /// <item><description><b>特别是 split ≥ 1 时不能对 A 用 <c>Clear</c></b>：那会让 A 整窗可见、
    /// 盖住 B，又把可见性交回给 Z 序（本方案的整个前提就是不依赖 Z 序）。</description></item>
    /// </list>
    /// 实践上 <c>split</c> 来自 <see cref="SplitParams.Clamp"/>，恒在 <c>[0.05, 0.95]</c>，
    /// 上面两条是防御性分支（NaN / 脏数据不会让窗口整块消失或整块盖住）。</para></summary>
    /// <param name="windowPx">该路子 HWND 在容器坐标下的物理像素矩形。</param>
    /// <param name="split">揭示比例（0~1，NaN 按 0.5）。</param>
    /// <param name="axis">揭示方向。</param>
    /// <param name="regionA">第 0 路（A）的可见区域 —— 远离原点的一侧（水平 = 右，垂直 = 下）。</param>
    /// <param name="regionB">第 1 路（B）的可见区域 —— 靠近原点的一侧（水平 = 左，垂直 = 上）。</param>
    /// <returns>true = 需要下发区域；false = 无法表达互补（对比区窄于 2px）⇒ 调用方不裁剪。</returns>
    private static bool OverlayRevealPlan(Rect32 windowPx, double split, OverlayAxis axis,
        out Rect32 regionA, out Rect32 regionB)
    {
        regionA = regionB = default;
        if (!windowPx.IsValid) return false;

        var span = axis == OverlayAxis.Horizontal ? windowPx.Width : windowPx.Height;
        var ortho = axis == OverlayAxis.Horizontal ? windowPx.Height : windowPx.Width;
        if (span < 2 || ortho < 1) return false; // 1px 级的对比区：无法让两半都 ≥1px

        var frac = double.IsNaN(split) ? 0.5 : split;
        var px = (int)Math.Round(frac * span, MidpointRounding.AwayFromZero);
        if (px < 1) px = 1;               // split ≤ 0：B 只余 1px
        if (px > span - 1) px = span - 1; // split ≥ 1：A 只余 1px

        regionB = axis == OverlayAxis.Horizontal
            ? new Rect32(0, 0, px, ortho)
            : new Rect32(0, 0, ortho, px);
        regionA = axis == OverlayAxis.Horizontal
            ? new Rect32(px, 0, span - px, ortho)
            : new Rect32(0, px, ortho, span - px);
        return true;
    }

    /// <summary>叠加模式下的命中测试：点落在 B 的揭示区内 → B，否则 A。
    ///
    /// <para><b>为什么必须单独判</b>：两路窗口都铺满且互相重叠，<c>HitSurfaceAt</c> 的通用循环按
    /// <c>Bounds</c> 命中会恒选第 0 路（A），与用户实际看到的内容不符（探针 / 滚轮缩放 /
    /// 部分选中路径会读错路）。区域已经把"看不见的部分"裁掉了，所以判定只需比较"点是否落在揭示区内"。</para>
    ///
    /// <para>两种方向都由 <c>_compareSplit.X</c> 驱动：叠加建立在 <see cref="CompareMode.Ab"/> 之上，
    /// 而 AB 的手柄只调 X（<see cref="CompareLayout.HandleAxis"/>）—— 用 Y 会让垂直方向失去拖动入口。</para></summary>
    private PlayerSurface? HitOverlayAt(Point windowPos)
    {
        var origin = Grid.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } o) return null;
        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0) return null;

        var p = new Point(windowPos.X - o.X, windowPos.Y - o.Y);
        if (p.X < 0 || p.Y < 0 || p.X > w || p.Y > h) return null; // 点在对比区之外

        var split = _compareSplit.X;
        var inReveal = _compareOverlayAxis == OverlayAxis.Horizontal ? p.X <= split * w : p.Y <= split * h;
        return Grid.GetSurface(inReveal ? CompareOverlayTopIndex : 0);
    }
}
