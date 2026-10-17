using _3FCompare.Core.Display;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <see cref="GridPartition"/> 的几何与拖动规则测试。
///
/// <para>与 <c>CompareLayoutTests</c> 同一约定：期望值全部手算独立推导（例：权重 (2,1) ⇒ 边界 2/3），
/// 不引用实现内部常量，以便实现写错时测试仍然报错。<see cref="GridPartition.MinSpanFraction"/>
/// 这类"实现自己用的数"一律在断言里另写成字面量 0.05，避免"实现改了常量、测试跟着一起绿"的
/// 同源锁（<c>docs/41</c> 反复点名的假绿形态）。</para>
/// </summary>
public class GridPartitionTests
{
    private const double Tol = 1e-9;

    private static void AssertRect(CellRect expected, CellRect actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Width, actual.Width, 9);
        Assert.Equal(expected.Height, actual.Height, 9);
    }

    // ══════════ 形状解析：与既有均匀网格同源 ══════════

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(7, 3, 3)]
    [InlineData(9, 3, 3)]
    public void Resolve_默认形状与GridLayout的自动布局逐字一致(int lanes, int cols, int rows)
    {
        // 独立参照：期望值取自 GridLayout.ComputeGrid 的既有语义（合并的前提就是
        // "没拖动过 = 与今天的均匀网格完全同形"，两处不一致会表现成"一进模式画面就跳"）。
        var auto = GridLayout.ComputeGrid(lanes, false);
        Assert.Equal((cols, rows), auto);

        var p = GridPartition.Resolve(lanes);
        Assert.Equal(cols, p.Cols);
        Assert.Equal(rows, p.Rows);
        Assert.Equal(lanes, p.Lanes);
    }

    [Fact]
    public void Resolve_均匀态每格等于既有网格公式()
    {
        // 5 路 ⇒ 3×2。独立按"等分 + 行列主序"手算，与 CompareGridView 均匀路径的
        // col=i/cols、row=i/cols 同构（那里额外减了 1px 缝，属 UI 侧，不在本类型的职责里）。
        var cells = GridPartition.Resolve(5).ComputeCells();

        Assert.Equal(5, cells.Length);
        AssertRect(new CellRect(0.0, 0.0, 1.0 / 3.0, 0.5), cells[0]);
        AssertRect(new CellRect(2.0 / 3.0, 0.0, 1.0 / 3.0, 0.5), cells[2]);
        AssertRect(new CellRect(1.0 / 3.0, 0.5, 1.0 / 3.0, 0.5), cells[4]);
    }

    [Fact]
    public void 单屏态解析成1x1且没有可拖的线()
    {
        var p = GridPartition.Resolve(9, singleView: true);

        Assert.Equal((1, 1), (p.Cols, p.Rows));
        Assert.Empty(p.HandlePositions());
        Assert.Equal(0, p.HandleCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void 路数下溢不得产出负数格(int lanes)
    {
        Assert.Empty(GridPartition.Resolve(lanes).ComputeCells());
    }

    [Fact]
    public void 单路占满整区()
    {
        var cells = GridPartition.Resolve(1).ComputeCells();

        Assert.Single(cells);
        AssertRect(new CellRect(0, 0, 1, 1), cells[0]);
    }

    // ══════════ 格表不变量：无缝、不重叠、宽恒为正 ══════════

    public static IEnumerable<object[]> 形状与权重()
    {
        // 每例都是"手工构造的不均匀权重"，比现实更容易被用户拖出来（含贴近 5% 下界的极端值），
        // 目的是让"补集写死末边界"这条性质在任何形状上都被扫到。
        // ⚠ 权重必须本身合法（每一列/行占比 ≥5%），否则夹具拖不到目标位置、用例会在
        //   构造阶段就假红（见 Build 的说明）。
        yield return new object[] { 2, 2, 1, new[] { 3.0, 1.0 }, new[] { 1.0 } };
        yield return new object[] { 3, 3, 1, new[] { 0.2, 2.0, 0.31 }, new[] { 1.0 } };
        yield return new object[] { 4, 2, 2, new[] { 1.7, 0.9 }, new[] { 0.2, 2.4 } };
        yield return new object[] { 6, 3, 2, new[] { 5.0, 0.5, 1.2 }, new[] { 0.7, 0.7 } };
        yield return new object[] { 9, 3, 3, new[] { 0.5, 3.3, 0.9 }, new[] { 1.4, 0.2, 2.1 } };
        yield return new object[] { 5, 3, 2, new[] { 1.0, 1.0, 1.0 }, new[] { 4.0, 0.3 } };
    }

    [Theory]
    [MemberData(nameof(形状与权重))]
    public void 格表铺满单位正方形且互不重叠(int lanes, int cols, int rows,
        double[] columnWeights, double[] rowWeights)
    {
        var p = Build(lanes, cols, rows, columnWeights, rowWeights);
        var cells = p.ComputeCells();

        // 格数与 CellCount 恒等（既有仓库对 CompareLayout 也钉了同一条"CellCount ≡ ComputeCells.Length"）
        Assert.Equal(p.CellCount, cells.Length);
        Assert.Equal(Math.Min(lanes, cols * rows), cells.Length);
        foreach (var c in cells)
        {
            Assert.True(c.Width > 0, $"宽必须为正（负宽会让子 HWND 被 Win32 当 16 位有符号解释）：{c}");
            Assert.True(c.Height > 0, $"高必须为正：{c}");
            Assert.InRange(c.X, -Tol, 1.0 + Tol);
            Assert.InRange(c.Y, -Tol, 1.0 + Tol);
            Assert.True(c.X + c.Width <= 1.0 + 1e-12, $"右边界不得越出容器：{c}");
            Assert.True(c.Y + c.Height <= 1.0 + 1e-12, $"下边界不得越出容器：{c}");
        }

        // 面积和：格数少于容量时只应等于"前 n 格"的面积，故这里按被占用的格逐一核对补集，
        // 而不是要求总面积为 1（那对 5 路 3×2 这种留空格的形状必然假红）。
        var colEdges = EdgesOf(columnWeights);
        var rowEdges = EdgesOf(rowWeights);
        for (var i = 0; i < cells.Length; i++)
        {
            var col = i % cols;
            var row = i / cols;
            AssertRect(new CellRect(colEdges[col], rowEdges[row],
                    colEdges[col + 1] - colEdges[col], rowEdges[row + 1] - rowEdges[row]),
                cells[i]);
        }
    }

    /// <summary>测试侧独立实现的边界换算（补集写法），作为对 <see cref="GridPartition"/> 的交叉参照。</summary>
    private static double[] EdgesOf(double[] weights)
    {
        var sum = 0.0;
        foreach (var w in weights) sum += w;
        var edges = new double[weights.Length + 1];
        for (var i = 0; i < weights.Length; i++) edges[i + 1] = edges[i] + weights[i] / sum;
        edges[^1] = 1.0;
        return edges;
    }

    /// <summary>累加浮点误差必须集中在最后一条边界上（实现把末边界写死为 1.0），
    /// 于是"最后一格贴右/贴下"是<b>逐位精确</b>的，不是"在容差内"。
    ///
    /// <para>为什么要单独一条精确等式：其余用例都带 1e-9 或 1e-12 容差，把实现里的
    /// <c>edges[^1] = 1.0</c> 删掉后，3 列的累加值 0.9999999999999999 照样落在容差内
    /// ⇒ 那条写死变成了没有牙齿的保护。而下游 <c>CompareCropPlanner</c> 的裁剪区域与格
    /// 必须"完全重合不打洞"，右侧差 1 个浮点 ULP 在 4K 下取整后就是 1px 空洞，
    /// 所以这条要求按精确来钉。</para></summary>
    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(9, 3, 3)]
    public void 末边界逐位精确等于1(int lanes, int cols, int rows)
    {
        var cells = GridPartition.Uniform(lanes, cols, rows).ComputeCells();

        var lastCol = cells[cols - 1];                      // 第一行最右那一格
        Assert.Equal(1.0, lastCol.X + lastCol.Width);        // 刻意不给容差：要的就是逐位相等
        var lastRow = cells[(rows - 1) * cols];              // 第一列最下那一格
        Assert.Equal(1.0, lastRow.Y + lastRow.Height);
    }

    /// <summary>上一条用例在"等分"上没有牙齿：均匀权重 (1,1,1) 的累加值恰好就是 1.0，
    /// 把实现里写死末边界的那行删掉它照样绿。本用例把权重推到累加会少 1 个 ULP 的位置
    /// （(0.15, 1.11, 1.74)/3 → 0.99999999999999989），于是"写死 1.0"这件事必须被观测到：
    /// 末格宽度必须<b>逐位</b>等于 <c>1 - 左边界</c>。
    /// 之所以要求逐位而不是"在容差内"：下游 <c>CompareCropPlanner</c> 的裁剪区域与格必须
    /// 完全重合才不打洞，4K 下差一个 ULP 取整后就是 1px 空洞。</summary>
    [Fact]
    public void 权重累加少一个ULP时末格仍与容器右边贴齐()
    {
        var last = GridPartition.Uniform(3, 3, 1)
            .WithHandleDragged(1, 0.42, 0.5)
            .WithHandleDragged(0, 0.0025, 0.5)   // 越界的一半被钳到下界，权重就此变成不可整除的组合
            .ComputeCells()[^1];

        Assert.Equal(1.0 - last.X, last.Width);
    }

    // ══════════ 手柄表 ══════════

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(2, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(3, 1, 2)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 2, 3)]
    [InlineData(3, 3, 4)]
    public void 手柄数等于内部竖线加横线(int cols, int rows, int expected)
    {
        var p = GridPartition.Uniform(9, cols, rows);

        Assert.Equal(expected, p.HandleCount);
        Assert.Equal(expected, p.HandlePositions().Length);
    }

    [Theory]
    [MemberData(nameof(形状与权重))]
    public void 手柄位置互不重合且都落在自己的分割线上(int lanes, int cols, int rows,
        double[] columnWeights, double[] rowWeights)
    {
        var p = Build(lanes, cols, rows, columnWeights, rowWeights);
        var positions = p.HandlePositions();
        var edges = EdgesOf(columnWeights);
        var rowEdges = EdgesOf(rowWeights);

        for (var k = 0; k < positions.Length; k++)
        {
            if (k < cols - 1)
            {
                Assert.Equal(SplitAxis.X, p.HandleAxisAt(k));
                Assert.Equal(edges[k + 1], positions[k].X, 9);
                Assert.Equal(0.5, positions[k].Y, 9);   // 另一轴取线中点，不给 0（会把手柄推出可视区）
            }
            else
            {
                var row = k - (cols - 1);
                Assert.Equal(SplitAxis.Y, p.HandleAxisAt(k));
                Assert.Equal(0.5, positions[k].X, 9);
                Assert.Equal(rowEdges[row + 1], positions[k].Y, 9);
            }
        }

        // 任意两个手柄不得重合：重合时"抓住哪个"没有确定答案，命中测试会恒选前者
        for (var a = 0; a < positions.Length; a++)
            for (var b = a + 1; b < positions.Length; b++)
                Assert.True(Math.Abs(positions[a].X - positions[b].X) > 1e-6
                            || Math.Abs(positions[a].Y - positions[b].Y) > 1e-6,
                    $"手柄 {a} 与 {b} 重合：{positions[a]}");
    }

    // ══════════ 拖动：只动相邻两列 ══════════

    [Fact]
    public void 拖竖线只改相邻两列_右侧的线不动()
    {
        // 权重 (1,1,1) ⇒ 边界 1/3、2/3。把第一条线拖到 0.4：第一列 1/3→0.4（宽 +0.0667），
        // 第二列相应等量变窄，第二条线（0.6667）必须原地不动。
        var p = GridPartition.Uniform(3, 3, 1);
        var dragged = p.WithHandleDragged(0, 0.4, 0.5);

        Assert.Equal(0.4, dragged.HandlePositions()[0].X, 9);
        Assert.Equal(2.0 / 3.0, dragged.HandlePositions()[1].X, 9);

        var cells = dragged.ComputeCells();
        AssertRect(new CellRect(0.0, 0.0, 0.4, 1.0), cells[0]);
        AssertRect(new CellRect(0.4, 0.0, 4.0 / 15.0, 1.0), cells[1]);   // 0.4 → 2/3
        AssertRect(new CellRect(2.0 / 3.0, 0.0, 1.0 / 3.0, 1.0), cells[2]);
    }

    [Fact]
    public void 拖横线只改相邻两行_下方的线不动()
    {
        // 3×3 ⇒ 横线两条在 1/3、2/3。拖第一条到 0.25，第二条必须还在 2/3。
        var p = GridPartition.Uniform(9, 3, 3);
        var dragged = p.WithHandleDragged(2, 0.5, 0.25);   // 索引 2 = 第一条横线（Cols-1=2 之后）

        Assert.Equal(SplitAxis.Y, p.HandleAxisAt(2));
        Assert.Equal(0.25, dragged.HandlePositions()[2].Y, 9);
        Assert.Equal(2.0 / 3.0, dragged.HandlePositions()[3].Y, 9);
        Assert.Equal(0.5, dragged.HandlePositions()[2].X, 9);
    }

    [Fact]
    public void 拖到原位是幂等的()
    {
        var p = GridPartition.Uniform(2, 2, 1);

        // 在权重 (1,1) 下边界是 0.5；再"拖到 0.5"不得产生任何变化
        // （覆盖层的热路径靠这个判据拦住 pointer move 抖动引发的事件风暴）。
        Assert.True(p.SameShape(p.WithHandleDragged(0, 0.5, 0.5)));
        Assert.False(p.SameShape(p.WithHandleDragged(0, 0.6, 0.5)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void 极端或脏指针被钳住_不产生负宽也不改变总形状(double pointer)
    {
        var p = GridPartition.Uniform(2, 2, 1);
        var dragged = p.WithHandleDragged(0, pointer, 0.5);
        var cells = dragged.ComputeCells();

        // 下界：两列各自至少占 5%（写成字面量，不引用 GridPartition.MinSpanFraction）
        foreach (var c in cells)
        {
            Assert.True(c.Width >= 0.05 - 1e-12, $"列宽跌破 5% 下界：{c}");
            Assert.False(double.IsNaN(c.Width) || double.IsNaN(c.X), $"脏指针把 NaN 带进了格表：{c}");
        }

        if (double.IsNaN(pointer) || double.IsInfinity(pointer))
            Assert.True(p.SameShape(dragged));   // 非有限入参必须整次不动，而不是"钳到 0"
    }

    [Fact]
    public void 越界手柄索引直接返回自身而不是崩()
    {
        var p = GridPartition.Uniform(3, 3, 1);

        Assert.True(p.SameShape(p.WithHandleDragged(-1, 0.2, 0.5)));
        Assert.True(p.SameShape(p.WithHandleDragged(99, 0.2, 0.5)));
    }

    [Fact]
    public void 连续两次把两条线推向同一侧只压到下界不重叠()
    {
        // 权重 (1,1,1)：先把第一条线拖到 0.05 左侧？不 —— 先把第二条线拖到 0.52（第一、二条线之间
        // 只剩一点余量），再把第一条线拖到 0.9（越到第二条右侧）。结果必须仍然单调、两两 ≥5%。
        var p = GridPartition.Uniform(3, 3, 1);
        var step1 = p.WithHandleDragged(1, 0.52, 0.5);
        var step2 = step1.WithHandleDragged(0, 0.9, 0.5);
        var edges = EdgesOf(step2.ColumnWeights.ToArray());

        for (var i = 0; i < edges.Length - 1; i++)
            Assert.True(edges[i + 1] - edges[i] >= 0.05 - 1e-12,
                $"第 {i} 列宽度跌破下界：{edges[i]}→{edges[i + 1]}");
        Assert.Equal(1.0, edges[^1], 12);
    }

    // ══════════ 形状切换 / 路数变化 ══════════

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public void 形状候选首项必等于自动布局(int lanes)
    {
        var shapes = GridPartition.ShapesFor(lanes);

        Assert.NotEmpty(shapes);
        Assert.Equal(GridLayout.ComputeGrid(lanes, false), (shapes[0].Cols, shapes[0].Rows));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void 形状候选每一个都装得下全部路(int lanes)
    {
        // ⭐合并的立身之本："一路一格"。旧模块靠 ABCD 只显 4 路，容量不足是设计内的；
        // 现在任何一个可选朝向都必须容量 ≥ lane 数，否则 C 键循环会把路挤出去（= 回归到旧行为）。
        foreach (var (cols, rows) in GridPartition.ShapesFor(lanes))
            Assert.True(cols * rows >= lanes,
                $"{lanes} 路的候选 {cols}x{rows} 容量 {cols * rows} 不足，会有路没有格");
    }

    [Fact]
    public void 两路的形状候选必须含上下分格()
    {
        // 这条断言是"撤掉 A/B 模式不撤能力"的锚：旧 CompareMode.AbVertical（上下两格）
        // 在网格侧唯一的等价物就是 1×2；旧的网格预设表里没有它（docs/31 §2.2 记的缺口）。
        Assert.Contains((2, 1), GridPartition.ShapesFor(2));
        Assert.Contains((1, 2), GridPartition.ShapesFor(2));
    }

    [Fact]
    public void 三路形状候选必须含三列()
    {
        Assert.Contains((3, 1), GridPartition.ShapesFor(3));
        Assert.Contains((1, 3), GridPartition.ShapesFor(3));
    }

    [Fact]
    public void C键循环回到首项()
    {
        var p = GridPartition.Uniform(2, 2, 1);

        var next = p.NextShape();
        Assert.Equal((1, 2), (next.Cols, next.Rows));
        Assert.Equal((2, 1), (next.NextShape().Cols, next.NextShape().Rows));
    }

    [Fact]
    public void 路数变化时形状未变则保住拖好的权重()
    {
        // 用户在 3×3（9 路）里拖宽了第一列，再减到 5 路 —— 只要显式预设让形状仍是 3×3，
        // 拖好的列宽必须原样保留；否则"减一路"会把用户全部调整悄悄抹掉。
        var dragged = GridPartition.Uniform(9, 3, 3).WithHandleDragged(0, 0.45, 0.5);
        var retuned = dragged.Retune(5, presetCols: 3, presetRows: 3);

        Assert.Equal((3, 3), (retuned.Cols, retuned.Rows));
        Assert.Equal(5, retuned.Lanes);
        Assert.Equal(dragged.ColumnWeights.ToArray(), retuned.ColumnWeights.ToArray());
        Assert.Equal(0.45, retuned.HandlePositions()[0].X, 9);

        // 形状真的变了（2×1 的自动布局在 3 路时是 3×1）就不得不重来，
        // 但必须是确定性的均匀态，而不是残留一份长度都不对的旧权重。
        var reshaped = GridPartition.Uniform(2, 2, 1).Retune(3);
        Assert.Equal((3, 1), (reshaped.Cols, reshaped.Rows));
        Assert.Equal(new[] { 1.0, 1.0, 1.0 }, reshaped.ColumnWeights.ToArray());
    }

    // ══════════ 供"左右拉动"读取的单一边界 ══════════

    [Theory]
    [InlineData(2, 1, 0.5)]
    [InlineData(3, 1, 1.0 / 3.0)]
    [InlineData(1, 1, 0.5)]     // 没有内部边界时给确定值，调用方此时本就不该画线
    public void PrimaryBoundary取第一条内部边界(int cols, int rows, double expected)
    {
        Assert.Equal(expected, GridPartition.Uniform(9, cols, rows).PrimaryBoundary(), 9);
    }

    [Fact]
    public void PrimaryBoundary跟随拖动()
    {
        var p = GridPartition.Uniform(2, 2, 1).WithHandleDragged(0, 0.3, 0.5);

        Assert.Equal(0.3, p.PrimaryBoundary(), 9);
    }

    /// <summary>负样本：形状的轴长越过 <see cref="GridPartition.MaxCellsPerAxis"/> 时必须被钳住，
    /// 并且钳住之后"每格 ≥5%"与"线跟着指针走"两条都要成立。
    ///
    /// <para><b>为什么要单独一条</b>：整套拖动规则的前提是"每个权重 ≥ 5% × 权重和"。均匀构造给
    /// "每格 1.0、和 = 格数"，于是前提等价于"格数 ≤ 20"。24 列时起点每格只有 4.17%，
    /// 两次钳制互相抵消——实测把第一条线拖到 pointer=0 / 0.2 / 0.5 三个位置，落点<b>恒为 3.33%</b>
    /// （线不动）。这条不是负宽事故，而是"下界与拖动响应一起失效"，只能靠构造上界挡。</para></summary>
    [Fact]
    public void 形状超上界被钳住_每格仍不小于百分之五()
    {
        // lane 数取满 20，才看得到 20 个格（ComputeCells 的长度是 min(lane, 容量)，
        // 第一版我把断言写成"格数=20"而 lane 只给 2 ⇒ 期望 20 实得 2，是我算错不是实现错）。
        var p = GridPartition.Uniform(GridPartition.MaxCellsPerAxis, GridPartition.MaxCellsPerAxis + 4, 1);
        Assert.Equal(GridPartition.MaxCellsPerAxis, p.Cols);   // 24 → 钳到 20

        var cells = p.ComputeCells();
        Assert.Equal(GridPartition.MaxCellsPerAxis, cells.Length);
        var sum = 0.0;
        foreach (var c in cells)
        {
            Assert.True(c.Width >= 0.05 - 1e-12,
                $"钳制后仍有格宽 {c.Width:0.####} 低于 5% 下界");
            sum += c.Width;
        }
        Assert.Equal(1.0, sum, 12);

        // 恰好 20 列时每格就是 5% 下界本身 ⇒ 无处可拖（再宽一格就破下界），
        // "拖不动"在这里是**正确**结果，不是缺陷。真正的响应性由下面两条验。
        var pinned = p.WithHandleDragged(0, 0.25, 0.5);
        Assert.True(pinned.SameShape(p), "20 列满界形状本应无处可拖，却动了");
    }

    /// <summary>响应性两档，都要按 5% 下界算准（第一版我把 10 列的期望写成"精确到 0.2"，
    /// 实测 <b>0.15</b> —— 邻格只能让出 5%，所以第一条线最多走到 15%；那 my 断言才是错的）：
    /// ① 有余量的形状（3 列，邻格可让 28.3%）⇒ 精确落到指针位置；
    /// ② 贴着下界的形状（10 列）⇒ 只能走到 15% 就停，且每格仍 ≥5%。</summary>
    [Fact]
    public void 拖动在有余量时精确到位_贴下界时被邻格截住()
    {
        var three = GridPartition.Uniform(3, 3, 1);
        Assert.Equal(1.0 / 3.0, three.HandlePositions()[0].X, 9);
        var exact = three.WithHandleDragged(0, 0.4, 0.5);
        Assert.Equal(0.4, exact.HandlePositions()[0].X, 9);            // 邻格够让 ⇒ 到位

        var ten = GridPartition.Uniform(10, 10, 1);
        Assert.Equal(0.1, ten.HandlePositions()[0].X, 9);              // 10 等分：第一条线在 10%
        var clamped = ten.WithHandleDragged(0, 0.9, 0.5);
        Assert.Equal(0.15, clamped.HandlePositions()[0].X, 9);         // 只能再吃掉邻格的 5%
        foreach (var c in clamped.ComputeCells())
            Assert.True(c.Width >= 0.05 - 1e-12, $"截断后出现低于 5% 的格：{c.Width:0.####}");

        // 方向性：往回拖也必须动（24 列那种"指针怎么拖都不动"的失效形态要能被抓住）
        Assert.Equal(0.15, ten.WithHandleDragged(0, 0.2, 0.5).HandlePositions()[0].X, 9);
        Assert.Equal(0.05, ten.WithHandleDragged(0, 0.0, 0.5).HandlePositions()[0].X, 9);
    }

    // ══════════ 夹具 ══════════

    private static GridPartition Build(int lanes, int cols, int rows,
        double[] columnWeights, double[] rowWeights)
    {
        // GridPartition 的构造是私有的（权重不变量只能由内部维持），夹具走公开 API：
        // 先取均匀态，再按目标权重给出的边界逐条拖过去。拖动是"在相邻两列之间搬权重"，
        // 逐条拖之后各边界恰好落进目标位置（后面的线不受前面的拖动影响）。
        // 若某列权重本身低于 5% 下界就拖不到 ⇒ 夹具直接判红，不允许"构造不出来当没这例"。
        var p = GridPartition.Uniform(lanes, cols, rows);
        var edges = EdgesOf(columnWeights);
        // 从右往左拖：每条线只能与"紧邻的下一列"搬权重，若先拖左边那条，
        // 它右边那列的右边界还停在均匀位置 ⇒ 目标位置会瞬时不可达而被下界钳住
        // （夹具就此拖不到，用例假红）。自右向左时右边界已就位，逐条都可达。
        for (var k = cols - 2; k >= 0; k--) p = p.WithHandleDragged(k, edges[k + 1], 0.5);

        var rowEdges = EdgesOf(rowWeights);
        for (var j = rows - 2; j >= 0; j--) p = p.WithHandleDragged(cols - 1 + j, 0.5, rowEdges[j + 1]);

        Assert.Equal(cols, p.Cols);
        Assert.Equal(rows, p.Rows);
        Assert.Equal(lanes, p.Lanes);
        for (var k = 0; k < cols - 1; k++)
            Assert.Equal(edges[k + 1], p.HandlePositions()[k].X, 6);
        for (var j = 0; j < rows - 1; j++)
            Assert.Equal(rowEdges[j + 1], p.HandlePositions()[cols - 1 + j].Y, 6);
        return p;
    }
}
