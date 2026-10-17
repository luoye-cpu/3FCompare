using _3FCompare.Core.Display;

namespace _3FCompare.Core.Tests;

/// <summary><see cref="WipeBands"/> 的判据：N 带拉动几何。
///
/// <para><b>为什么这些判据必须存在</b>：拉动的区域一旦算错，表现是"画面缺一块"
/// 或"两路在同一像素上打架（胜负交给 Z 序）"——两者都很难从截图反推到根因。
/// 而三条不变式（两两不重叠 / 并集铺满 / 每段 &gt; 0）都是<b>纯几何</b>，
/// 在这里能逐条硬断言，不必上真机。</para>
///
/// <para><b>N=2 回归锚点</b>：<c>PlanRegions</c> 取代了旧 <c>OverlayRevealPlan</c>
/// （签名只有 <c>out regionA, out regionB</c>）。旧算法在
/// <c>MainWindow.CompareOverlay.cs:210-232</c>，其规则是：界标乘 span 后
/// <c>AwayFromZero</c> 取整，钳到 <c>[1, span-1]</c>；A = 远离原点一侧、B = 靠近原点一侧。
/// 下面 <c>N2_与旧实现逐字段一致</c> 把那份算式在此<b>独立重写一遍</b>做对照 ——
/// 这样"把手写两路换成通用 N 路"就不可能在无人察觉的情况下改掉既有行为。</para></summary>
public class WipeBandsTests
{
    // ────────── 构造 ──────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public void 分区带数与形状正确(int n)
    {
        var h = WipeBands.Partition(n, WipeAxis.Horizontal);
        Assert.Equal(n, h.Cols);
        Assert.Equal(1, h.Rows);
        Assert.Equal(n, h.CellCount);
        // n-1 条可拖竖线：这正是"每条线独立调比例"的载体
        Assert.Equal(n - 1, h.HandleCount);

        var v = WipeBands.Partition(n, WipeAxis.Vertical);
        Assert.Equal(1, v.Cols);
        Assert.Equal(n, v.Rows);
        Assert.Equal(n, v.CellCount);
        Assert.Equal(n - 1, v.HandleCount);
    }

    [Fact]
    public void 带数被钳到合法范围而不是抛异常()
    {
        // 过渡态（路数从 0 变到 1）会经过 0 带；抛异常会把一次布局抖动放大成崩溃
        Assert.Equal(1, WipeBands.Partition(0, WipeAxis.Horizontal).CellCount);
        Assert.Equal(1, WipeBands.Partition(-5, WipeAxis.Horizontal).CellCount);
        // 上限来自 GridPartition（几何约束：每权重 ≥5% 才保证拖动有效）
        var tooMany = WipeBands.Partition(WipeBands.MaxBands + 10, WipeAxis.Horizontal);
        Assert.Equal(WipeBands.MaxBands, tooMany.CellCount);
    }

    // ────────── 三条不变式（N = 1..9 全扫） ──────────

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    public void 三条不变式_不重叠_铺满_每段为正(int n)
    {
        var win = new Rect32(12, 34, 1600, 900);
        foreach (var axis in new[] { WipeAxis.Horizontal, WipeAxis.Vertical })
        {
            var part = WipeBands.Partition(n, axis);
            var regions = WipeBands.PlanRegions(win, WipeBands.PartitionBoundaries(part, axis), axis);
            Assert.NotNull(regions);
            Assert.Equal(n, regions!.Length);

            // ③ 每段宽高恒 > 0（0 尺寸会被 CreateRectRgn 当空区域、SetWindowRgn 失败）
            foreach (var r in regions)
            {
                Assert.True(r.IsValid, $"{axis} n={n}: 出现非正尺寸区域 {r}");
                Assert.True(r.Width >= 1 && r.Height >= 1);
            }

            var span = axis == WipeAxis.Horizontal ? win.Width : win.Height;
            var ortho = axis == WipeAxis.Horizontal ? win.Height : win.Width;

            // ② 并集恰好铺满**窗口**：区域是窗口相对坐标（SetWindowRgn 语义），
            //    故首段从 0 起、末段到 span 止 —— 与窗口在容器里的位置（win.X/Y）无关。
            //    ⚠ 这条判据同时钉住一个真机抓出的缺陷：把 win.X/Y 加进区域会让整体偏移窗口原点。
            var firstStart = axis == WipeAxis.Horizontal ? regions[0].X : regions[0].Y;
            Assert.Equal(0, firstStart);
            var last = regions[^1];
            var lastEnd = axis == WipeAxis.Horizontal ? last.X + last.Width : last.Y + last.Height;
            Assert.Equal(span, lastEnd);
            var total = 0;
            foreach (var r in regions) total += axis == WipeAxis.Horizontal ? r.Width : r.Height;
            Assert.Equal(span, total);

            // ① 两两不重叠（半开区间：前段 end == 后段 start 不算重叠）+ 正交方向一致
            for (var i = 1; i < regions.Length; i++)
            {
                var prev = regions[i - 1];
                var prevEnd = axis == WipeAxis.Horizontal ? prev.X + prev.Width : prev.Y + prev.Height;
                var curStart = axis == WipeAxis.Horizontal ? regions[i].X : regions[i].Y;
                Assert.Equal(prevEnd, curStart);
            }
            foreach (var r in regions)
            {
                var rCross = axis == WipeAxis.Horizontal ? r.Y : r.X;
                var rCrossLen = axis == WipeAxis.Horizontal ? r.Height : r.Width;
                Assert.Equal(0, rCross);
                Assert.Equal(ortho, rCrossLen);
            }
        }
    }

    [Fact]
    public void 拖动一条线只改相邻两条带_其余带不动()
    {
        // 语义（GridPartition.WithHandleDragged）：第 k 条线位于"第 k 条带的右边界"，
        // 即界标下标 k+1。拖动把它移到 pointer，代价是**只从相邻那条带搬宽度**：
        //   · 界标 ≤ k 不动（由更早的线决定）
        //   · 界标 k+1 移向 pointer（可能被 MinSpanFraction 钳住，见下一个用例）
        //   · 界标 > k+1 全部不动  ← 这条才是"其余带不动"的判据，也是用户直觉
        // 实测 k=1、pointer=0.6（5 等分，min=0.25）：权重 1,1.75,0.25,1,1 ⇒ 界标 0,0.2,0.55,0.6,0.8,1
        //   注意 0.55 ≠ 0.6：右侧那条带已到 5% 下界（0.25/5），故被钳住 —— 这是正确行为。
        const int n = 5;
        var part = WipeBands.Partition(n, WipeAxis.Horizontal);

        var before = WipeBands.PartitionBoundaries(part, WipeAxis.Horizontal);
        var dragged = part.WithHandleDragged(1, 0.6, 0.5);
        var after = WipeBands.PartitionBoundaries(dragged, WipeAxis.Horizontal);

        // 界标 ≤ k 不动
        Assert.Equal(before[0], after[0], 12);
        Assert.Equal(before[1], after[1], 12);
        // 界标 k+1 变了（朝指针方向移动）
        Assert.NotEqual(before[2], after[2]);
        Assert.True(after[2] > before[2], $"界标应变大：{before[2]} → {after[2]}");
        // 界标 > k+1 纹丝不动 —— 拖一条线不许把它右边的线带走
        for (var i = 3; i <= n; i++)
            Assert.Equal(before[i], after[i], 12);
        // 首末界标恒为 0 / 1（铺满整窗的前提）
        Assert.Equal(0.0, after[0], 12);
        Assert.Equal(1.0, after[n], 12);
    }

    [Fact]
    public void 拖到极端时相邻带被钳在下界_不会塌成零宽()
    {
        // 拖到极端位置时，被搬运的那两条带各自仍须 ≥ MinSpanFraction（5%）——
        // 这是"带不会塌成 0 宽"的保证（0 宽区域会让 SetWindowRgn 失败、窗口整块不可见）。
        // 用权重直接断言更贴近机制：WithHandleDragged 就是在权重上钳的。
        const int n = 4;
        var part = WipeBands.Partition(n, WipeAxis.Horizontal);
        var total = 0.0;
        foreach (var w in part.ColumnWeights) total += w;
        var minWeight = GridPartition.MinSpanFraction * total;

        foreach (var pointer in new[] { 0.0, 1.0, -5.0, 5.0 })
        {
            var after = part.WithHandleDragged(1, pointer, 0.5);
            for (var i = 0; i < after.ColumnWeights.Count; i++)
                Assert.True(after.ColumnWeights[i] >= minWeight - 1e-9,
                    $"pointer={pointer} 时第 {i} 列权重 {after.ColumnWeights[i]} 低于下界 {minWeight}");

            // 界标严格递增且铺满 0..1 ⇒ 每条带宽度为正
            var b = WipeBands.PartitionBoundaries(after, WipeAxis.Horizontal);
            Assert.Equal(0.0, b[0], 12);
            Assert.Equal(1.0, b[n], 12);
            for (var i = 1; i <= n; i++)
                Assert.True(b[i] > b[i - 1], $"pointer={pointer} 时界标非严格递增：{string.Join(",", b)}");
        }
    }

    // ────────── 回归锚点：N=2 与旧 OverlayRevealPlan 逐字段一致 ──────────

    // 覆盖生产可达的界标范围：PrimaryBoundary 来自权重，而权重恒 ≥ MinSpanFraction（5%），
    // 故 2 带时界标实际落在 [0.05, 0.95]。0 / 1 是不可达值（旧实现里那两个钳制分支属防御性代码），
    // 它们由下面 NaN/越界那组用例覆盖，不混进本锚点。
    [Theory]
    [InlineData(1600, 900, 0.5)]
    [InlineData(1600, 900, 0.05)]
    [InlineData(1600, 900, 0.95)]
    [InlineData(1600, 900, 0.333333)]
    [InlineData(1600, 900, 0.666667)]
    [InlineData(3, 7, 0.05)]      // 极窄窗口：考验钳制
    [InlineData(2, 1, 0.5)]
    [InlineData(101, 1, 0.5)]
    [InlineData(1920, 1080, 0.512345)]
    public void N2_与旧实现逐字段一致(int w, int h, double split)
    {
        var win = new Rect32(0, 0, w, h);
        foreach (var axis in new[] { WipeAxis.Horizontal, WipeAxis.Vertical })
        {
            // 旧实现的输入是 _wipePartition.PrimaryBoundary()：用旧构造方式把 split 灌进去
            var oldStyle = axis == WipeAxis.Horizontal
                ? GridPartition.Uniform(2, 2, 1).WithHandleDragged(0, split, 0.5)
                : GridPartition.Uniform(2, 1, 2).WithHandleDragged(0, 0.5, split);
            var realSplit = oldStyle.PrimaryBoundary();
            var bounds = WipeBands.PartitionBoundaries(oldStyle, axis);
            var got = WipeBands.PlanRegions(win, bounds, axis);

            // 旧算式独立重写一遍（MainWindow.CompareOverlay.cs:210-232）
            var span = axis == WipeAxis.Horizontal ? w : h;
            var ortho = axis == WipeAxis.Horizontal ? h : w;
            if (span < 2 || ortho < 1) { Assert.Null(got); continue; }

            var frac = double.IsNaN(realSplit) ? 0.5 : realSplit;
            var px = (int)Math.Round(frac * span, MidpointRounding.AwayFromZero);
            if (px < 1) px = 1;
            if (px > span - 1) px = span - 1;

            var expectB = axis == WipeAxis.Horizontal
                ? new Rect32(0, 0, px, ortho)
                : new Rect32(0, 0, ortho, px);
            var expectA = axis == WipeAxis.Horizontal
                ? new Rect32(px, 0, span - px, ortho)
                : new Rect32(0, px, ortho, span - px);

            Assert.NotNull(got);
            // 旧实现里 B = 第 0 路（靠近原点）、A = 第 1 路；新实现 regions[i] 是第 i 条带
            Assert.Equal(expectB, got![0]);
            Assert.Equal(expectA, got![1]);
        }
    }

    // ────────── 退化与防御 ──────────

    [Fact]
    public void 无法表达互补时返回null而不是产生坏区域()
    {
        // 窗口窄于带数：不能让每段都 ≥1px ⇒ 返回 null，调用方退回"不裁剪"
        // （宁可整窗可见，也不产生 0 宽区域让 SetWindowRgn 失败、窗口整块不可见）
        var bounds = WipeBands.PartitionBoundaries(WipeBands.Partition(5, WipeAxis.Horizontal), WipeAxis.Horizontal);
        Assert.Null(WipeBands.PlanRegions(new Rect32(0, 0, 3, 10), bounds, WipeAxis.Horizontal));

        // 非法窗口矩形（0/负尺寸）同样返回 null
        Assert.Null(WipeBands.PlanRegions(new Rect32(0, 0, 0, 10), bounds, WipeAxis.Horizontal));
        Assert.Null(WipeBands.PlanRegions(new Rect32(0, 0, 100, 0), bounds, WipeAxis.Horizontal));
    }

    [Fact]
    public void NaN或Inf界标退回均匀而不是产生NaN区域()
    {
        // NaN 一旦乘进坐标会让 CreateRectRgn 收到翻转矩形（窗口整块不可见）。
        // 界标来自拖动中的绑定值，确有拿到 NaN 的路径，故必须防御。
        var win = new Rect32(0, 0, 900, 600);
        var bad = new[] { 0.0, double.NaN, double.PositiveInfinity, 1.0 };
        var got = WipeBands.PlanRegions(win, bad, WipeAxis.Horizontal);
        Assert.NotNull(got);
        Assert.Equal(3, got!.Length);
        foreach (var r in got) Assert.True(r.IsValid);
        // 退回均匀 ⇒ 三条带应各占约 1/3
        var total = 0;
        foreach (var r in got) total += r.Width;
        Assert.Equal(900, total);
    }

    [Fact]
    public void 带号越界抛出而不是静默返回坏值()
    {
        var part = WipeBands.Partition(3, WipeAxis.Horizontal);
        Assert.Throws<ArgumentOutOfRangeException>(() => WipeBands.BandOf(part, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WipeBands.BandOf(part, 3));
        Assert.Equal(0.0, WipeBands.BandOf(part, 0).X);
    }

    [Fact]
    public void 单带铺满整窗()
    {
        // n=1 时没有内部线、没有手柄，那一条带必须铺满整窗。
        // 区域是**窗口相对**坐标 ⇒ (0,0,宽,高)，与窗口在容器里的位置无关。
        var win = new Rect32(5, 7, 800, 600);
        var part = WipeBands.Partition(1, WipeAxis.Horizontal);
        Assert.Equal(0, part.HandleCount);
        var regions = WipeBands.PlanRegions(win, WipeBands.PartitionBoundaries(part, WipeAxis.Horizontal), WipeAxis.Horizontal);
        Assert.NotNull(regions);
        Assert.Single(regions!);
        Assert.Equal(new Rect32(0, 0, win.Width, win.Height), regions![0]);
    }
}
