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
    [InlineData(2, new[] { CompareMode.Ab })]
    [InlineData(3, new[] { CompareMode.Ab, CompareMode.Abc })]
    [InlineData(4, new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })]
    [InlineData(5, new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })] // ≥4 一律三模式全开
    [InlineData(9, new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })]
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
    [InlineData(CompareMode.Abc, 2, false)]
    [InlineData(CompareMode.Abcd, 2, false)]
    [InlineData(CompareMode.Ab, 3, true)]
    [InlineData(CompareMode.Abc, 3, true)]
    [InlineData(CompareMode.Abcd, 3, false)]
    [InlineData(CompareMode.Ab, 4, true)]
    [InlineData(CompareMode.Abc, 4, true)]
    [InlineData(CompareMode.Abcd, 4, true)]
    [InlineData(CompareMode.Abcd, 9, true)]
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
    [InlineData(CompareMode.Abc, 3)]
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

    [Theory]
    [InlineData(CompareMode.Ab)]
    [InlineData(CompareMode.Abc)]
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
    [InlineData(CompareMode.Abc)]
    [InlineData(CompareMode.Abcd)]
    public void Default_居中且本身合法(CompareMode mode)
    {
        var d = SplitParams.Default(mode);
        Assert.Equal(0.5, d.X, precision: 12);
        Assert.Equal(0.5, d.Y, precision: 12);
        Assert.Equal(d, d.Clamp()); // 初值必须已满足约束，否则 UI 首帧会跳变
    }

    [Theory]
    [InlineData(CompareMode.Ab, SplitAxis.X)]     // 只调竖分割
    [InlineData(CompareMode.Abc, SplitAxis.Both)] // 交叉点同时调两轴
    [InlineData(CompareMode.Abcd, SplitAxis.Both)]
    public void HandleAxis_按模式返回可调轴(CompareMode mode, SplitAxis expected)
    {
        Assert.Equal(expected, CompareLayout.HandleAxis(mode));
    }

    [Fact]
    public void HandlePosition_Ab_竖线中点()
    {
        var (x, y) = CompareLayout.HandlePosition(CompareMode.Ab, new SplitParams(0.3, 0.6));

        Assert.Equal(0.3, x, precision: 12);
        Assert.Equal(0.5, y, precision: 12); // 忽略传入 Y，固定中线
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
