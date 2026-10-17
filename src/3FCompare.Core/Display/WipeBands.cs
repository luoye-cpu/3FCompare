namespace _3FCompare.Core.Display;

/// <summary>「左右拉动对比」的几何真源（纯逻辑，可单测）。
///
/// <para><b>它解决什么</b>：拉动此前被硬编码成"恒两格"——<c>_CompareOverlayCells</c> 是长度 2 的
/// <c>static readonly</c>，<c>OverlayRevealPlan</c> 的签名是 <c>out regionA, out regionB</c>。
/// 于是路数 &gt; 2 时多出来的路被 <c>CompareGridView.ArrangeOverride</c> 静默隐藏，
/// 用户点「加路」看不到任何画面变化。本类型把"N 条带"变成一等公民：
/// 带数 = 参与对比的路数，每条带露出一路，n-1 条线各可独立拖。</para>
///
/// <para><b>为什么放在 Core 而不是 UI</b>：带表与揭示区都是纯几何，与 Avalonia / Win32 无关。
/// 放在这里才能被单元测试逐条钉住（尤其是"两两不重叠"与"并集恰好铺满"这两条不变式，
/// 它们一旦破了，后果是画面缺块或两路互相盖住，而这两种表现都很难从截图定位到根因）。</para>
///
/// <para><b>与 <see cref="GridPartition"/> 的关系</b>：本类型<b>不重新实现</b>权重与拖动，
/// 只负责"按轴向构造 n 带分区"和"把分区换算成窗口区域"。拖动仍走
/// <see cref="GridPartition.WithHandleDragged"/>，于是拉动与网格对比共用同一份拖动语义，
/// 两条路径不会各自漂移。</para>
/// </summary>
public static class WipeBands
{
    /// <summary>单轴最多几条带。
    /// <para>取 <see cref="GridPartition.MaxCellsPerAxis"/>（= 20）而不是 9：本上限是
    /// <b>几何</b>约束（每个权重 ≥ 5% 才保证拖动有效，见该常量注释），与"素材路数上限"是两件事。
    /// 素材路数由 UI 的 <c>MaxLaneCount</c> 管，这里只管"这个形状在数学上是否成立"。</para></summary>
    public const int MaxBands = GridPartition.MaxCellsPerAxis;

    /// <summary>按带数构造拉动分区：水平揭示 = <c>n×1</c>（n-1 条竖线），
    /// 垂直揭示 = <c>1×n</c>（n-1 条横线）。
    ///
    /// <para>带数被钳到 <c>[1, MaxBands]</c>：0 或负数没有意义（没有任何带可露），
    /// 超过 <see cref="MaxBands"/> 会让"每带至少 5%"失效、拖动失灵（见该常量的实测记录）。
    /// 钳制而非抛异常：调用方（UI）在路数变化的中途会经过一些过渡态，抛异常会把一次
    /// 布局抖动放大成崩溃。</para></summary>
    public static GridPartition Partition(int bandCount, WipeAxis axis)
    {
        var n = Math.Clamp(bandCount, 1, MaxBands);
        return axis == WipeAxis.Horizontal
            ? GridPartition.Uniform(n, n, 1)   // n 列 1 行 ⇒ n-1 条竖线，左右拖动
            : GridPartition.Uniform(n, 1, n);  // 1 列 n 行 ⇒ n-1 条横线，上下拖动
    }

    /// <summary>第 <paramref name="bandIndex"/> 条带占的归一化矩形（走
    /// <see cref="GridPartition.ComputeCells"/>，与网格对比同一份几何）。</summary>
    public static CellRect BandOf(GridPartition partition, int bandIndex)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var cells = partition.ComputeCells();
        if (bandIndex < 0 || bandIndex >= cells.Length)
            throw new ArgumentOutOfRangeException(nameof(bandIndex),
                $"带号越界：{bandIndex}（本分区共 {cells.Length} 条带）");
        return cells[bandIndex];
    }

    /// <summary>把 n 条带换算成<b>窗口相对、物理像素</b>的互补区域集合。
    ///
    /// <para><b>三条不变式</b>（全部由构造保证，单测逐条钉住）：
    /// <list type="number">
    /// <item><description><b>两两不重叠</b>：相邻带的交界处不共享像素（前一段右边界 == 后一段左边界，
    /// 半开区间）。重叠会让两路画面在同一像素上打架，胜负由 Z 序决定 —— 而本方案的整个前提
    /// 就是"不依赖 Z 序"。</description></item>
    /// <item><description><b>并集恰好铺满整窗</b>：首段从 0 起、末段到 span 止，中间无缝。
    /// 差一个像素就会在带之间留一条父窗底色的缝。</description></item>
    /// <item><description><b>每段宽高恒 &gt; 0</b>：<c>Rect32</c> 的宽高为 0 时
    /// <c>CreateRectRgn</c> 得到空区域、<c>SetWindowRgn</c> 直接失败（窗口整块不可见）。
    /// 故每段至少 1px —— 这与旧 <c>OverlayRevealPlan</c> 的钳制口径一致。</description></item>
    /// </list></para>
    ///
    /// <para><b>与旧两路实现的关系</b>：n = 2 时本方法的结果与旧 <c>OverlayRevealPlan</c>
    /// <b>逐字段相同</b>（含 1px 钳制），这是回归锚点 —— 单测里对着旧算法的期望值钉死，
    /// 保证"把手动写的两路逻辑换成通用 N 路"没有顺带改掉既有行为。</para>
    ///
    /// <para><b>边界钳制</b>：界标先乘 span 取整（<c>AwayFromZero</c>，与旧实现同口径），
    /// 再保证相邻界标至少差 1px：若因取整导致某段塌成 0 宽，则把该界标依次推开。
    /// 带数 &gt; span 像素时（窗口窄于带数）无法让每段都 ≥1px，此时返回 <c>null</c> ——
    /// 调用方不裁剪（宁可整窗可见，也不产生一条 0 宽区域让 Win32 失败）。</para></summary>
    /// <param name="windowPx">该路窗口在容器坐标下的物理像素矩形（宽高须 &gt; 0）。</param>
    /// <param name="boundaries">界标比例，长度 = 带数 + 1，首项 0、末项 1（由
    /// <see cref="PartitionBoundaries"/> 产出）。</param>
    /// <param name="axis">揭示方向。</param>
    /// <returns>长度 = 带数 的区域数组；无法表达时 <c>null</c>。</returns>
    public static Rect32[]? PlanRegions(Rect32 windowPx, IReadOnlyList<double> boundaries, WipeAxis axis)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        if (!windowPx.IsValid) return null;

        var bandCount = boundaries.Count - 1;
        if (bandCount < 1) return null;

        var span = axis == WipeAxis.Horizontal ? windowPx.Width : windowPx.Height;
        var ortho = axis == WipeAxis.Horizontal ? windowPx.Height : windowPx.Width;
        if (ortho < 1) return null;
        // 每段至少 1px：带数 > 像素跨度时无解，返回 null 让调用方退回"不裁剪"。
        if (span < bandCount) return null;

        // 界标 → 像素。首尾写死 0 / span，中间取整后再单调化。
        var cuts = new int[bandCount + 1];
        cuts[0] = 0;
        cuts[bandCount] = span;
        for (var i = 1; i < bandCount; i++)
        {
            var frac = boundaries[i];
            if (!double.IsFinite(frac)) frac = (double)i / bandCount;   // NaN/Inf：退回均匀
            var px = (int)Math.Round(Math.Clamp(frac, 0.0, 1.0) * span, MidpointRounding.AwayFromZero);
            cuts[i] = px;
        }

        // 单调化 + 每段 ≥1px。从左往右推：第 i 个界标至少比前一个多 1、最多给右侧留够余量。
        for (var i = 1; i < bandCount; i++)
        {
            var min = cuts[i - 1] + 1;
            var max = cuts[bandCount] - (bandCount - i);   // 右侧每段也要 ≥1px
            if (cuts[i] < min) cuts[i] = min;
            if (cuts[i] > max) cuts[i] = max;
        }

        // 单调化可能把某个界标顶到 max 之上（带数接近 span 时），最后反查一次确保严格递增。
        for (var i = 1; i <= bandCount; i++)
            if (cuts[i] <= cuts[i - 1]) return null;

        var regions = new Rect32[bandCount];
        for (var i = 0; i < bandCount; i++)
        {
            var start = cuts[i];
            var len = cuts[i + 1] - cuts[i];
            if (len < 1) return null;
            // ⚠ 区域是**窗口相对**坐标（SetWindowRgn 语义，见 WindowRegionClipper 类型注释），
            //    故**不加** windowPx.X/Y —— 加了就会整体偏移窗口原点（实测偏 2,2），
            //    表现是"露出一条父窗底色 / 裁错位置"。这与 CompareCropPlanner.Plan 的
            //    `region = cellPx - windowPx` 是同一套口径。
            regions[i] = axis == WipeAxis.Horizontal
                ? new Rect32(start, 0, len, ortho)
                : new Rect32(0, start, ortho, len);
        }
        return regions;
    }

    /// <summary>把分区换算成界标比例数组（长度 = 带数 + 1，首项 0、末项 1）。
    /// 拉动与网格共用 <see cref="GridPartition"/> 的权重，故拖动一条线之后
    /// 这里的界标自动跟着变，不需要另一份状态。</summary>
    public static double[] PartitionBoundaries(GridPartition partition, WipeAxis axis)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var cells = partition.ComputeCells();
        var result = new double[cells.Length + 1];
        result[0] = 0.0;
        for (var i = 0; i < cells.Length; i++)
            result[i + 1] = axis == WipeAxis.Horizontal
                ? cells[i].X + cells[i].Width
                : cells[i].Y + cells[i].Height;
        // 末项写死 1.0：与 GridPartition.Edges 同一口径，把浮点误差关在最后一条边界上。
        result[^1] = 1.0;
        return result;
    }
}

/// <summary>拉动揭示的方向。
/// <para><b>为什么不在 Core 里复用 UI 的 <c>MainWindow.OverlayAxis</c></b>：那是 <c>MainWindow</c>
/// 的嵌套类型，Core 不能引用 UI。两者语义一一对应，UI 侧负责在边界处换算一次
/// （见 <c>MainWindow.CompareOverlay</c>），而不是让 Core 去猜 UI 的枚举。</para></summary>
public enum WipeAxis
{
    /// <summary>水平揭示：竖线，左右拖动。第一条带在左。</summary>
    Horizontal,

    /// <summary>垂直揭示：横线，上下拖动。第一条带在上。</summary>
    Vertical,
}

/// <summary>窗口相对、物理像素矩形 —— 本项目 <c>Rect32</c> 的<b>唯一真源</b>。
///
/// <para><b>为什么住在 Core</b>：原先它定义在 UI 工程的 <c>Platform/WindowRegionClipper.cs</c>。
/// N 带拉动的区域换算是<b>纯几何</b>，而"两两不重叠 / 并集铺满 / 每段为正"这三条不变式
/// 一旦破了，后果是画面缺块或两路互相盖住 —— 两者都极难从截图反推到根因。
/// 要让它进 Core 单测，类型本身就必须能被 Core 引用。UI 侧通过
/// <c>Platform/WindowRegionClipper.cs</c> 顶部的 <c>using Rect32 = ...Core.Display.Rect32;</c>
/// 继续用同一类型，不存在两份实现漂移。</para>
///
/// <para><b>坐标语义</b>：X/Y 相对<b>窗口左上角</b>的物理像素（<c>SetWindowRgn</c> 的坐标语义，
/// 见 Win32 文档），可为负。宽高必须 &gt; 0：0 尺寸会被 <c>CreateRectRgn</c> 解释为空区域，
/// <c>SetWindowRgn</c> 直接失败（窗口整块不可见）；负宽会被 Win32 的 16 位有符号解释成巨大正数。</para>
/// </summary>
public readonly record struct Rect32(int X, int Y, int Width, int Height)
{
    /// <summary>右下角 X（独占边界）。可能溢出 int，需精确比较请用 <see cref="TryGetEdges"/>。</summary>
    public int Right => X + Width;

    /// <summary>右下角 Y（独占边界）。可能溢出 int，需精确比较请用 <see cref="TryGetEdges"/>。</summary>
    public int Bottom => Y + Height;

    /// <summary>宽和高是否均为正（空矩形/负尺寸会被 CreateRectRgn 解释为空区域）。</summary>
    public bool IsValid => Width > 0 && Height > 0;

    /// <summary>是否完全覆盖 <paramref name="other"/>（用 long 运算，避免极端坐标下的溢出误判）。</summary>
    public bool Contains(Rect32 other)
        => IsValid && other.IsValid
           && other.X >= X && other.Y >= Y
           && (long)other.X + other.Width <= (long)X + Width
           && (long)other.Y + other.Height <= (long)Y + Height;

    /// <summary>
    /// 纯逻辑：校验尺寸为正、且换算成 right/bottom 后不溢出 int 范围，成功则输出四条边。
    /// 溢出会让 <c>CreateRectRgn</c> 收到翻转的矩形（被当成空区域），表现为"调用成功但窗口不可见"，
    /// 因此宁可在这里明确拒绝。
    /// </summary>
    public bool TryGetEdges(out int left, out int top, out int right, out int bottom)
    {
        left = X;
        top = Y;
        right = 0;
        bottom = 0;

        if (!IsValid) return false;

        var r = (long)X + Width;
        var b = (long)Y + Height;
        if (r > int.MaxValue || r < int.MinValue) return false;
        if (b > int.MaxValue || b < int.MinValue) return false;

        right = (int)r;
        bottom = (int)b;
        return true;
    }
}
