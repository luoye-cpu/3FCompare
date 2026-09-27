using _3FCompare.Core.Display;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <see cref="CompareLayout"/> 的几何与规则测试。
/// 期望值全部按需求文档手算独立推导（例：X=0.3、Y=0.6），不引用实现内部常量，
/// 以便实现写错时测试仍能报错。
/// </summary>
public class CompareLayoutTests
{
    // ══════════ AvailableModes ══════════

    [Theory]
    [InlineData(0, new CompareMode[0])]                              // 未导入：对比功能未启动
    [InlineData(1, new CompareMode[0])]                              // 单路：仍不启动
    [InlineData(2, new[] { CompareMode.Ab, CompareMode.AbVertical })]
    [InlineData(3, new[] { CompareMode.Ab, CompareMode.AbVertical, CompareMode.Abc, CompareMode.AbcColumns })]
    [InlineData(4, new[] { CompareMode.Ab, CompareMode.AbVertical, CompareMode.Abc, CompareMode.AbcColumns, CompareMode.Abcd })]
    [InlineData(5, new[] { CompareMode.Ab, CompareMode.AbVertical, CompareMode.Abc, CompareMode.AbcColumns, CompareMode.Abcd })] // ≥4 一律全开
    [InlineData(9, new[] { CompareMode.Ab, CompareMode.AbVertical, CompareMode.Abc, CompareMode.AbcColumns, CompareMode.Abcd })]
    public void AvailableModes_按路数返回规定集合(int count, CompareMode[] expected)
    {
        // 顺序即 UI 按钮顺序：逐元素比较已同时钉住顺序与内容
        Assert.Equal(expected, CompareLayout.AvailableModes(count).ToArray());
    }

    [Theory]
    [InlineData(CompareMode.Ab, 0, false)]
    [InlineData(CompareMode.Abc, 0, false)]
    [InlineData(CompareMode.Abcd, 0, false)]
    [InlineData(CompareMode.Ab, 1, false)]
    [InlineData(CompareMode.Abc, 1, false)]
    [InlineData(CompareMode.Abcd, 1, false)]
    [InlineData(CompareMode.Ab, 2, true)]
    [InlineData(CompareMode.AbVertical, 2, true)]     // 变体：2 路即可用（同为 2 格）
    [InlineData(CompareMode.Abc, 2, false)]
    [InlineData(CompareMode.AbcColumns, 2, false)]    // 3 格 ⇒ 2 路容不下
    [InlineData(CompareMode.Abcd, 2, false)]
    [InlineData(CompareMode.Ab, 3, true)]
    [InlineData(CompareMode.AbVertical, 3, true)]
    [InlineData(CompareMode.Abc, 3, true)]
    [InlineData(CompareMode.AbcColumns, 3, true)]
    [InlineData(CompareMode.Abcd, 3, false)]
    [InlineData(CompareMode.Ab, 4, true)]
    [InlineData(CompareMode.AbVertical, 4, true)]
    [InlineData(CompareMode.Abc, 4, true)]
    [InlineData(CompareMode.AbcColumns, 4, true)]
    [InlineData(CompareMode.Abcd, 4, true)]
    [InlineData(CompareMode.Abcd, 9, true)]
    [InlineData(CompareMode.AbcColumns, 9, true)]
    public void IsAvailable_与AvailableModes一致(CompareMode mode, int count, bool expected)
    {
        Assert.Equal(expected, CompareLayout.IsAvailable(mode, count));
        Assert.Equal(expected, CompareLayout.AvailableModes(count).Contains(mode));
    }

    // ══════════ CoerceMode ══════════

    [Theory]
    [InlineData(CompareMode.Ab, 2, CompareMode.Ab)]     // 可用 → 原样
    [InlineData(CompareMode.Abcd, 4, CompareMode.Abcd)] // 可用 → 原样
    [InlineData(CompareMode.Abc, 2, CompareMode.Ab)]    // 3 路模式掉到 2 路 → 退到最大可用(AB)
    [InlineData(CompareMode.Abcd, 2, CompareMode.Ab)]   // 4 路模式掉到 2 路 → AB
    [InlineData(CompareMode.Abcd, 3, CompareMode.Abc)]  // 4 路模式掉到 3 路 → 保留尽可能多(ABC)
    [InlineData(CompareMode.Abc, 5, CompareMode.Abc)]   // 已可用，不被"提升"
    [InlineData(CompareMode.Ab, 5, CompareMode.Ab)]
    public void CoerceMode_不可用时收敛到最大可用模式(CompareMode mode, int count, CompareMode expected)
    {
        Assert.Equal(expected, CompareLayout.CoerceMode(mode, count));
    }

    [Theory]
    // 负数取值恒满足 "<= max"，若不显式判下界就会原样漏出去（脏反序列化数据 / 非法强转）
    [InlineData((CompareMode)(-1), 4, CompareMode.Abcd)]
    [InlineData((CompareMode)(-1), 3, CompareMode.Abc)]
    [InlineData((CompareMode)(-1), 2, CompareMode.Ab)]
    [InlineData((CompareMode)int.MinValue, 4, CompareMode.Abcd)]
    // 超出上界：同样收敛到最大可用模式
    [InlineData((CompareMode)7, 4, CompareMode.Abcd)]
    [InlineData((CompareMode)7, 2, CompareMode.Ab)]
    public void CoerceMode_非法枚举取值也收敛到可用集合(CompareMode mode, int count, CompareMode expected)
    {
        var coerced = CompareLayout.CoerceMode(mode, count);

        Assert.Equal(expected, coerced);
        // 关键不变量：收敛结果必须真的可用（-1 漏出时会立刻违反）
        Assert.True(CompareLayout.IsAvailable(coerced, count));
        Assert.Contains(coerced, CompareLayout.AvailableModes(count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void CoerceMode_可用集合为空时兜底为Ab(int count)
    {
        // 对比功能未启动：返回值必须确定，调用方无需处理 null / 异常
        Assert.Equal(CompareMode.Ab, CompareLayout.CoerceMode(CompareMode.Abcd, count));
        Assert.Equal(CompareMode.Ab, CompareLayout.CoerceMode(CompareMode.Ab, count));
    }

    [Fact]
    public void CoerceMode_结果必为可用模式()
    {
        foreach (var count in new[] { 0, 1, 2, 3, 4, 9 })
        foreach (var mode in new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })
        {
            var coerced = CompareLayout.CoerceMode(mode, count);
            if (count < 2) continue; // 空集合分支由上一个用例单独钉住
            Assert.True(CompareLayout.IsAvailable(coerced, count));
        }
    }

    // ══════════ CellCount ══════════

    [Theory]
    [InlineData(CompareMode.Ab, 2)]
    [InlineData(CompareMode.AbVertical, 2)]
    [InlineData(CompareMode.Abc, 3)]
    [InlineData(CompareMode.AbcColumns, 3)]
    [InlineData(CompareMode.Abcd, 4)]
    public void CellCount_与模式路数一致(CompareMode mode, int expected)
    {
        Assert.Equal(expected, CompareLayout.CellCount(mode));
    }

    // ══════════ ComputeCells：几何正确性 ══════════

    // 手算基准：X=0.3、Y=0.6 ⇒ 右/下侧宽高分别为 1-0.3=0.7、1-0.6=0.4
    private static readonly SplitParams Split03_06 = new(0.3, 0.6);

    [Fact]
    public void ComputeCells_Ab_左右两格()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.Ab, Split03_06);

        Assert.Equal(2, cells.Length);
        AssertRect(new CellRect(0, 0, 0.3, 1), cells[0]);
        AssertRect(new CellRect(0.3, 0, 0.7, 1), cells[1]);
    }

    [Fact]
    public void ComputeCells_Abc_左大图加右侧上下两小图()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.Abc, Split03_06);

        Assert.Equal(3, cells.Length);
        AssertRect(new CellRect(0, 0, 0.3, 1), cells[0]);    // A 通高
        AssertRect(new CellRect(0.3, 0, 0.7, 0.6), cells[1]); // B 右上
        AssertRect(new CellRect(0.3, 0.6, 0.7, 0.4), cells[2]); // C 右下，高 = 1-0.6
    }

    [Fact]
    public void ComputeCells_Abcd_十字四宫格()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.Abcd, Split03_06);

        Assert.Equal(4, cells.Length);
        AssertRect(new CellRect(0, 0, 0.3, 0.6), cells[0]);
        AssertRect(new CellRect(0.3, 0, 0.7, 0.6), cells[1]);
        AssertRect(new CellRect(0, 0.6, 0.3, 0.4), cells[2]);
        AssertRect(new CellRect(0.3, 0.6, 0.7, 0.4), cells[3]);
    }

    /// <summary>AB 竖：与 Ab 同样的两格，只把分割轴换成 Y。手算基准同前（X=0.3、Y=0.6）
    /// ⇒ 上半高 0.6、下半高 1-0.6=0.4，两格都<b>通宽</b>（Width == 1）。</summary>
    [Fact]
    public void ComputeCells_AbVertical_上下两格()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.AbVertical, Split03_06);

        Assert.Equal(2, cells.Length);
        AssertRect(new CellRect(0, 0, 1, 0.6), cells[0]);    // A：上半，通宽
        AssertRect(new CellRect(0, 0.6, 1, 0.4), cells[1]);  // B：下半，高 = 1-0.6

        // 通宽是"竖分"的定义性特征，且两格必须共用 x 起点
        Assert.Equal(1.0, cells[0].Width, precision: 12);
        Assert.Equal(1.0, cells[1].Width, precision: 12);
        Assert.Equal(cells[0].X, cells[1].X, precision: 12);
        Assert.Equal(1.0, cells[0].Height + cells[1].Height, precision: 12);
    }

    /// <summary>ABC 三列：X / Y 是两条竖线。手算 (0.3, 0.6) ⇒ 三列宽 0.3 / 0.3 / 0.4，都通高。</summary>
    [Fact]
    public void ComputeCells_AbcColumns_三列()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.AbcColumns, Split03_06);

        Assert.Equal(3, cells.Length);
        AssertRect(new CellRect(0, 0, 0.3, 1), cells[0]);
        AssertRect(new CellRect(0.3, 0, 0.3, 1), cells[1]);  // 0.6-0.3
        AssertRect(new CellRect(0.6, 0, 0.4, 1), cells[2]);  // 1-0.6
        Assert.All(cells, c => Assert.Equal(1.0, c.Height, precision: 12));
    }

    /// <summary>两条竖线被拖成"逆序"（X 在 Y 右侧）时<b>不得</b>产出负宽的中间列。
    /// 期望值独立：排序后第一条线 0.3、第二条 0.6 ⇒ 与正序<b>完全相同</b>的三列。
    /// 这条钉住的是"负宽矩形会让子 HWND 定位到非法尺寸（Win32 只认 16 位有符号）"这个崩溃面。</summary>
    [Fact]
    public void ComputeCells_AbcColumns_两条线逆序时仍为正宽三列()
    {
        var forward = CompareLayout.ComputeCells(CompareMode.AbcColumns, new SplitParams(0.3, 0.6));
        var reversed = CompareLayout.ComputeCells(CompareMode.AbcColumns, new SplitParams(0.6, 0.3));

        Assert.Equal(forward, reversed);
        Assert.All(reversed, c => Assert.True(c.Width > 0, $"出现非正宽度 {c.Width}"));
    }

    /// <summary>两条竖线重合（或间距小于 <see cref="CompareLayout.MinFraction"/>）时中间列仍须可见 ——
    /// 否则该格宽为 0，两个手柄重叠 ⇒ 用户再也分不开它们（卡死）。</summary>
    [Theory]
    [InlineData(0.5, 0.5)]      // 完全重合
    [InlineData(0.5, 0.51)]     // 间距远小于 MinFraction
    [InlineData(0.95, 0.95)]    // 贴右上界重合：右推不动，只能左推第一条
    [InlineData(0.05, 0.05)]    // 贴左边界重合
    public void ComputeCells_AbcColumns_两线重合时中间列仍有最小宽度(double x, double y)
    {
        var cells = CompareLayout.ComputeCells(CompareMode.AbcColumns, new SplitParams(x, y));

        Assert.Equal(3, cells.Length);
        Assert.All(cells, c => Assert.True(c.Width > 0, $"出现零宽列 {c.Width}"));
        Assert.True(cells[1].Width >= CompareLayout.MinFraction - CompareLayout.Epsilon,
            $"中间列宽 {cells[1].Width} 小于最小可辨宽度");
        // 仍是无缝铺满：三列首尾相接、总宽恰为 1
        Assert.Equal(0.0, cells[0].X, precision: 9);
        Assert.Equal(cells[0].Width, cells[1].X, precision: 9);
        Assert.Equal(cells[0].Width + cells[1].Width, cells[2].X, precision: 9);
        Assert.Equal(1.0, cells[2].X + cells[2].Width, precision: 9);
    }

    [Theory]
    [InlineData(CompareMode.Ab)]
    [InlineData(CompareMode.AbVertical)]
    [InlineData(CompareMode.Abc)]
    [InlineData(CompareMode.AbcColumns)]
    [InlineData(CompareMode.Abcd)]
    public void ComputeCells_任意模式下_铺满不重叠无空隙(CompareMode mode)
    {
        // 覆盖多个分割位置，含极端与非法值（应被 Clamp 兜住）
        foreach (var split in new[]
                 {
                     new SplitParams(0.5, 0.5),
                     new SplitParams(0.3, 0.6),
                     new SplitParams(0.05, 0.95),
                     new SplitParams(0, 0),       // 非法：贴边
                     new SplitParams(1, 1),       // 非法：贴边
                     new SplitParams(-5, 7),      // 非法：越界
                     new SplitParams(double.NaN, double.NaN), // 非法：NaN
                 })
        {
            var cells = CompareLayout.ComputeCells(mode, split);

            Assert.Equal(CompareLayout.CellCount(mode), cells.Length);

            foreach (var c in cells)
            {
                // 落在 [0,1] 且宽高非负（实现用 Clamp 保证 >0，这里放宽到 ≥0 是更弱的必要条件）
                Assert.InRange(c.X, 0.0, 1.0);
                Assert.InRange(c.Y, 0.0, 1.0);
                Assert.True(c.Width >= 0, $"{mode} 出现负宽度 {c.Width}");
                Assert.True(c.Height >= 0, $"{mode} 出现负高度 {c.Height}");
                Assert.InRange(c.X + c.Width, 0.0, 1.0 + CompareLayout.Epsilon);
                Assert.InRange(c.Y + c.Height, 0.0, 1.0 + CompareLayout.Epsilon);
            }

            // 不重叠：任意两格交集面积为 0
            for (var i = 0; i < cells.Length; i++)
            for (var j = i + 1; j < cells.Length; j++)
            {
                var ox = Math.Min(cells[i].X + cells[i].Width, cells[j].X + cells[j].Width)
                       - Math.Max(cells[i].X, cells[j].X);
                var oy = Math.Min(cells[i].Y + cells[i].Height, cells[j].Y + cells[j].Height)
                       - Math.Max(cells[i].Y, cells[j].Y);
                var overlap = Math.Max(0, ox) * Math.Max(0, oy);
                Assert.True(overlap <= CompareLayout.Epsilon,
                    $"{mode} 第 {i}/{j} 格重叠，面积 {overlap}");
            }

            // 无空隙：面积和恰为 1（配合上面的"不重叠 + 全在 [0,1]² 内"即等于铺满）
            var area = cells.Sum(c => c.Width * c.Height);
            Assert.True(Math.Abs(area - 1.0) <= CompareLayout.Epsilon,
                $"{mode} 面积和 {area}，未铺满");

            // 外接框必须顶满容器四边
            Assert.Equal(0, cells.Min(c => c.X), precision: 9);
            Assert.Equal(0, cells.Min(c => c.Y), precision: 9);
            Assert.Equal(1, cells.Max(c => c.X + c.Width), precision: 9);
            Assert.Equal(1, cells.Max(c => c.Y + c.Height), precision: 9);
        }
    }

    [Fact]
    public void ComputeCells_Abc_右侧两小图宽度相同且左图通高()
    {
        // 独立推导：右侧两格共用 x 起点与宽度（都由 X 决定），A 高度为 1
        var cells = CompareLayout.ComputeCells(CompareMode.Abc, new SplitParams(0.4, 0.25));

        Assert.Equal(cells[1].X, cells[2].X, precision: 12);
        Assert.Equal(cells[1].Width, cells[2].Width, precision: 12);
        Assert.Equal(1.0, cells[0].Height, precision: 12);
        Assert.Equal(1.0, cells[1].Height + cells[2].Height, precision: 12); // 上下两小图总高 = 1
    }

    // ══════════ SplitParams.Clamp ══════════

    [Theory]
    [InlineData(0.3, 0.3)]                 // 区间内原样
    [InlineData(0.5, 0.5)]
    [InlineData(CompareLayout.MinFraction, CompareLayout.MinFraction)]
    [InlineData(CompareLayout.MaxFraction, CompareLayout.MaxFraction)]
    [InlineData(0, CompareLayout.MinFraction)]       // 下界
    [InlineData(1, CompareLayout.MaxFraction)]       // 上界
    [InlineData(-1, CompareLayout.MinFraction)]      // 负数
    [InlineData(-0.0001, CompareLayout.MinFraction)]
    [InlineData(2, CompareLayout.MaxFraction)]
    [InlineData(double.NegativeInfinity, CompareLayout.MinFraction)]
    [InlineData(double.PositiveInfinity, CompareLayout.MaxFraction)]
    [InlineData(double.NaN, 0.5)]                    // NaN → 中性值，不得传染
    public void Clamp_限制到合法区间(double input, double expected)
    {
        var clamped = new SplitParams(input, input).Clamp();

        Assert.Equal(expected, clamped.X, precision: 12);
        Assert.Equal(expected, clamped.Y, precision: 12);
    }

    [Fact]
    public void Clamp_边界值不会导致零宽高单元格()
    {
        // 需求：拖到边缘时最小单元格仍需有尺寸，因此 [0,1] 必须被留出安全边距
        var clamped = new SplitParams(0, 1).Clamp();
        Assert.True(clamped.X > 0 && clamped.X < 1);
        Assert.True(clamped.Y > 0 && clamped.Y < 1);

        var cells = CompareLayout.ComputeCells(CompareMode.Abcd, new SplitParams(0, 1));
        Assert.All(cells, c =>
        {
            Assert.True(c.Width > 0);
            Assert.True(c.Height > 0);
        });
    }

    [Fact]
    public void Clamp_区间对称且留有最小尺寸()
    {
        Assert.Equal(0.05, CompareLayout.MinFraction, precision: 12);
        Assert.Equal(1.0 - CompareLayout.MinFraction, CompareLayout.MaxFraction, precision: 12);
    }

    // ══════════ Default / HandleAxis / HandlePosition ══════════

    [Theory]
    [InlineData(CompareMode.Ab)]
    [InlineData(CompareMode.AbVertical)]
    [InlineData(CompareMode.Abc)]
    [InlineData(CompareMode.Abcd)]
    public void Default_居中且本身合法(CompareMode mode)
    {
        var d = SplitParams.Default(mode);
        Assert.Equal(0.5, d.X, precision: 12);
        Assert.Equal(0.5, d.Y, precision: 12);
        Assert.Equal(d, d.Clamp()); // 初值必须已满足约束，否则 UI 首帧会跳变
    }

    /// <summary>ABC 三列的初值必须是<b>三等分</b>：两条竖线在 1/3 与 2/3。
    /// 若沿用 0.5/0.5，两条线重合 ⇒ 首帧就是"极窄—宽—宽"的畸形分栏，看起来像布局算错了。</summary>
    [Fact]
    public void Default_AbcColumns_三等分()
    {
        var d = SplitParams.Default(CompareMode.AbcColumns);

        Assert.Equal(1.0 / 3.0, d.X, precision: 12);
        Assert.Equal(2.0 / 3.0, d.Y, precision: 12);
        Assert.Equal(d, d.Clamp());

        // 初值落下去就应当是三列等宽
        var cells = CompareLayout.ComputeCells(CompareMode.AbcColumns, d);
        Assert.Equal(3, cells.Length);
        Assert.All(cells, c => Assert.Equal(1.0 / 3.0, c.Width, precision: 9));
    }

    [Theory]
    [InlineData(CompareMode.Ab, SplitAxis.X)]              // 只调竖分割
    [InlineData(CompareMode.AbVertical, SplitAxis.Y)]      // 只调横分割
    [InlineData(CompareMode.AbcColumns, SplitAxis.X)]      // 三列：每个手柄只调一条竖线
    [InlineData(CompareMode.Abc, SplitAxis.Both)]          // 交叉点同时调两轴
    [InlineData(CompareMode.Abcd, SplitAxis.Both)]
    public void HandleAxis_按模式返回可调轴(CompareMode mode, SplitAxis expected)
    {
        Assert.Equal(expected, CompareLayout.HandleAxis(mode));
        Assert.Equal(expected, CompareLayout.HandleAxisAt(mode, 0));
    }

    /// <summary>手柄数量：只有 ABC 三列有两条独立的分割线 ⇒ 2 个手柄，其余均为 1。
    /// 这条同时是"多手柄能力"的入口断言 —— 覆盖层据此决定画几个圈。</summary>
    [Theory]
    [InlineData(CompareMode.Ab, 1)]
    [InlineData(CompareMode.AbVertical, 1)]
    [InlineData(CompareMode.Abc, 1)]
    [InlineData(CompareMode.Abcd, 1)]
    [InlineData(CompareMode.AbcColumns, 2)]
    public void HandleCount_按模式返回手柄数(CompareMode mode, int expected)
    {
        Assert.Equal(expected, CompareLayout.HandleCount(mode));
    }

    /// <summary>ABC 三列的两个手柄必须落在<b>两条不同的线</b>上（排序后的一左一右）。
    /// 期望值独立：X=0.3、Y=0.6 ⇒ 手柄 0 在 0.3、手柄 1 在 0.6。</summary>
    [Fact]
    public void HandlePosition_AbcColumns_两个手柄各占一条竖线()
    {
        var p0 = CompareLayout.HandlePositionAt(CompareMode.AbcColumns, Split03_06, 0);
        var p1 = CompareLayout.HandlePositionAt(CompareMode.AbcColumns, Split03_06, 1);

        Assert.Equal((0.3, 0.5), p0);
        Assert.Equal((0.6, 0.5), p1);
        Assert.True(p0.X < p1.X); // 手柄 0 恒为靠左那条
    }

    /// <summary>两线逆序时手柄位置仍随排序结果走：手柄 0 在左、手柄 1 在右。
    /// 这样"手柄 0 改 X、手柄 1 改 Y"的映射在两条线交换身份后依然成立。</summary>
    [Fact]
    public void HandlePosition_AbcColumns_逆序时手柄随之排序()
    {
        var split = new SplitParams(0.6, 0.3);

        Assert.Equal((0.3, 0.5), CompareLayout.HandlePositionAt(CompareMode.AbcColumns, split, 0));
        Assert.Equal((0.6, 0.5), CompareLayout.HandlePositionAt(CompareMode.AbcColumns, split, 1));
    }

    /// <summary>手柄位置必须与单元格边界<b>同源</b>：手柄 0/1 的 X 恰为第 1/2 列的右边界。
    /// 不同源时（例如一处排序、一处不排序）手柄会与分割线错位，且只在三列模式下暴露。</summary>
    [Fact]
    public void HandlePosition_AbcColumns_与单元格边界同源()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.AbcColumns, Split03_06);

        Assert.Equal(cells[0].X + cells[0].Width,
            CompareLayout.HandlePositionAt(CompareMode.AbcColumns, Split03_06, 0).X, precision: 12);
        Assert.Equal(cells[1].X + cells[1].Width,
            CompareLayout.HandlePositionAt(CompareMode.AbcColumns, Split03_06, 1).X, precision: 12);
    }

    /// <summary>保留的单手柄 API 必须与带索引版本在索引 0 上完全一致 ——
    /// 否则"既有调用点"与"新的多手柄路径"会画出两个不同位置的手柄。</summary>
    [Theory]
    [InlineData(CompareMode.Ab)]
    [InlineData(CompareMode.AbVertical)]
    [InlineData(CompareMode.Abc)]
    [InlineData(CompareMode.AbcColumns)]
    [InlineData(CompareMode.Abcd)]
    public void HandlePosition_单手柄API与索引零一致(CompareMode mode)
    {
        var split = new SplitParams(0.3, 0.6);
        Assert.Equal(CompareLayout.HandlePositionAt(mode, split, 0), CompareLayout.HandlePosition(mode, split));
    }

    [Fact]
    public void HandlePosition_Ab_竖线中点()
    {
        var (x, y) = CompareLayout.HandlePosition(CompareMode.Ab, new SplitParams(0.3, 0.6));

        Assert.Equal(0.3, x, precision: 12);
        Assert.Equal(0.5, y, precision: 12); // 忽略传入 Y，固定中线
    }

    /// <summary>AB 竖：手柄在<b>横分割线</b>上 ⇒ X 固定 0.5（横线中点）、Y 取分割位置。
    /// 与 AB 恰好镜像；写错成 (x, 0.5) 的话手柄会停在最左边，拖动时线不动 ⇒ 必须钉住。</summary>
    [Fact]
    public void HandlePosition_AbVertical_横线中点()
    {
        var (x, y) = CompareLayout.HandlePosition(CompareMode.AbVertical, new SplitParams(0.3, 0.6));

        Assert.Equal(0.5, x, precision: 12); // 忽略传入 X，固定中线
        Assert.Equal(0.6, y, precision: 12);
    }

    [Fact]
    public void HandlePosition_Abc_交叉点在XY()
    {
        var (x, y) = CompareLayout.HandlePosition(CompareMode.Abc, new SplitParams(0.3, 0.6));

        Assert.Equal(0.3, x, precision: 12);
        Assert.Equal(0.6, y, precision: 12);
    }

    [Fact]
    public void HandlePosition_Abcd_交叉点在XY()
    {
        var (x, y) = CompareLayout.HandlePosition(CompareMode.Abcd, new SplitParams(0.25, 0.75));

        Assert.Equal(0.25, x, precision: 12);
        Assert.Equal(0.75, y, precision: 12);
    }

    [Fact]
    public void HandlePosition_非法输入同样被收敛()
    {
        // 手柄位置与单元格必须来自同一套 Clamp，否则拖到边缘时手柄会与分割线错位
        var (x, y) = CompareLayout.HandlePosition(CompareMode.Abcd, new SplitParams(0, 1));
        var cells = CompareLayout.ComputeCells(CompareMode.Abcd, new SplitParams(0, 1));

        Assert.Equal(cells[0].X + cells[0].Width, x, precision: 12);
        Assert.Equal(cells[0].Y + cells[0].Height, y, precision: 12);
    }

    // ══════════ 辅助 ══════════

    private static void AssertRect(CellRect expected, CellRect actual)
    {
        Assert.Equal(expected.X, actual.X, precision: 12);
        Assert.Equal(expected.Y, actual.Y, precision: 12);
        Assert.Equal(expected.Width, actual.Width, precision: 12);
        Assert.Equal(expected.Height, actual.Height, precision: 12);
    }
}
