using System;
using System.Collections.Generic;
using System.Linq;
using _3FCompare.Core.Display;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <b>新增 <see cref="CompareMode"/> 枚举值时的守卫测试</b>（AB 竖 / ABC 三列 引入时补）。
///
/// <para><b>为什么必须有这个文件</b>：<see cref="CompareLayout"/> 的四个 switch
/// （<c>AvailableModes</c> 的模式表、<c>CellCount</c>、<c>ComputeCells</c>、<c>HandleAxisAt</c> /
/// <c>HandlePositionAt</c>）都带 <c>_ =&gt;</c> 兜底。C# <b>不会</b>对"带兜底的 switch 未覆盖新枚举值"
/// 报 CS8509 ⇒ 漏改分支是<b>静默</b>的：漏改 <c>ComputeCells</c> 的表现是"竖分被画成左右分"，
/// 看起来像功能实现了、只是方向错，靠肉眼几乎无法归因。既有的两个测试文件全是显式
/// <c>InlineData</c>，新增枚举值一个都不会被覆盖到 —— 必须有遍历式守卫。</para>
///
/// <para><b>反向验证（本文件的用例是怎么被验红的）</b>：实现阶段实际跑过两轮 ——
/// 摘掉 <c>ComputeCells</c> 的 <c>AbVertical</c> 分支 ⇒ 判红 3 条，其中
/// <c>枚举遍历_AbVertical必须是上下两格</c> 与 <c>枚举遍历_AbcColumns必须是三列</c>
/// 是"形状"断言（宽度≠1 / 格数≠3），是真正的判红点；
/// 摘掉 <c>HandleAxisAt</c> 的 <c>AbVertical</c> 分支（改成 Both）⇒ 判红 1 条。
///
/// <b>⚠ 但两条"遍历"用例对变体<b>没有</b>鉴别力，别误以为它们能抓漏改</b>：
/// <list type="bullet">
/// <item><description><c>枚举遍历_格数与手算表一致</c>：变体与它的主形态格数相同
/// （AbVertical=2=Ab、AbcColumns=3=Abc），漏改 <c>ComputeCells</c> 会落进 <c>_ =&gt;</c>
/// 给出<b>同样格数</b>的两/三格 ⇒ 长度断言恒绿。真正抓到它的是上面的"形状"断言。</description></item>
/// <item><description><c>枚举遍历_手柄轴与几何一致</c>：原写的 Both 分支只断言
/// <c>Height &lt; 1</c>，而 AbVertical 是"通宽、按高切"，恰好满足 ⇒ 摘掉该分支时它<b>不红</b>
/// （当时的红来自 <c>CompareLayoutTests</c> 的显式 InlineData）。已补 <c>Width &lt; 1</c>
/// 让它名副其实。</description></item>
/// </list></para>
/// </summary>
public class ModeVariantTests
{
    /// <summary>
    /// 手算表：每个模式的格数与"最小可用路数"。
    ///
    /// <para><b>必须手写、不得由实现推导</b>：写成 <c>CellCount(mode)</c> 就是同源互证 ——
    /// ComputeCells 与 CellCount 一起漏改时断言恒绿。表里没有的模式会让第一个用例判红并
    /// 打印"表需同步更新"，这正是新增枚举值时<b>想要</b>的效果（强制人工确认形状）。</para>
    /// </summary>
    private static readonly Dictionary<CompareMode, (int Cells, int MinRoutes)> Expected = new()
    {
        [CompareMode.Ab] = (2, 2),           // 左右
        [CompareMode.AbVertical] = (2, 2),   // 上下
        [CompareMode.Abc] = (3, 3),          // 左大 + 右上/右下
        [CompareMode.AbcColumns] = (3, 3),   // 三列
        [CompareMode.Abcd] = (4, 4),         // 四宫格
    };

    /// <summary>手柄数手算表。HandleCount 是"新增枚举必须同步改"的四处之一，
    /// 但它只返回 1 或 2 ⇒ <c>count &gt;= 1</c> 这类断言<b>恒真</b>，抓不到漏改
    /// （将来新增双手柄模式却忘了改 HandleCount，循环只会画 1 个手柄而断言仍绿）。
    /// 故此处按模式逐一登记，与格数表同一套"表里没有 ⇒ 判红"的机制。</summary>
    private static readonly Dictionary<CompareMode, int> ExpectedHandles = new()
    {
        [CompareMode.Ab] = 1,
        [CompareMode.AbVertical] = 1,
        [CompareMode.Abc] = 1,
        [CompareMode.AbcColumns] = 2, // 两条竖线各一个
        [CompareMode.Abcd] = 1,
    };

    private static IEnumerable<CompareMode> AllModes => Enum.GetValues<CompareMode>();

    /// <summary>表必须覆盖全部枚举值。新增模式而表没更新 ⇒ 这里先红，且消息直接说明要做什么。</summary>
    [Fact]
    public void 手算表覆盖全部枚举值()
    {
        foreach (var mode in AllModes)
        {
            Assert.True(Expected.ContainsKey(mode),
                $"模式 {mode}({(int)mode}) 不在手算表里 —— 新增枚举值必须在此登记其格数与最小路数，" +
                "并确认 ComputeCells / CellCount / HandleAxisAt / HandlePositionAt 四处分支都已补上。");
        }
    }

    /// <summary>格数：CellCount 与 ComputeCells 各自与手算表比对（不互证）。</summary>
    [Fact]
    public void 枚举遍历_格数与手算表一致()
    {
        foreach (var mode in AllModes)
        {
            var (cells, _) = Expected[mode];

            Assert.Equal(cells, CompareLayout.CellCount(mode));
            Assert.Equal(cells, CompareLayout.ComputeCells(mode, SplitParams.Default(mode)).Length);
            // 换一组分割参数仍应同格数：格数只由模式决定
            Assert.Equal(cells, CompareLayout.ComputeCells(mode, new SplitParams(0.3, 0.6)).Length);
        }
    }

    /// <summary>每个模式都必须<b>真的能在某个路数下被选到</b>：
    /// 定义了却永远不可用的模式等于死代码（且会让 C 键循环多一个空转档）。</summary>
    [Fact]
    public void 枚举遍历_每个模式都有可用的路数()
    {
        foreach (var mode in AllModes)
        {
            var (_, minRoutes) = Expected[mode];

            // 只判 AvailableModes：IsAvailable 的实现就是 `AvailableModes(count).Contains(mode)`，
            // 两条一起断言是逐字重复（零增量覆盖），且会把"实现改成同义写法"误判为回归。
            Assert.Contains(mode, CompareLayout.AvailableModes(minRoutes));
            // 收敛后必须还是它自己（可用 ⇒ 原样返回）
            Assert.Equal(mode, CompareLayout.CoerceMode(mode, minRoutes));
        }
    }

    /// <summary>几何不变量（对<b>每一个</b>模式、多组分割参数）：铺满 [0,1]²、互不重叠、宽高非负。
    /// 这条能抓住"漏改 ComputeCells ⇒ 落进 default（左右两格）"这类静默失败中的大部分 ——
    /// 形状不对时面积和 / 重叠判据会立刻失真。</summary>
    [Fact]
    public void 枚举遍历_几何铺满不重叠无空隙()
    {
        var splits = new[]
        {
            SplitParams.Default(CompareMode.Ab),
            new SplitParams(0.3, 0.6),
            new SplitParams(0.6, 0.3),       // 逆序：三列模式下两条线交换
            new SplitParams(0.5, 0.5),       // 重合：三列模式下会被最小间隔撑开
            new SplitParams(0.05, 0.95),
            new SplitParams(0, 1),           // 非法：贴边
            new SplitParams(-5, 7),          // 非法：越界
            new SplitParams(double.NaN, double.NaN),
        };

        foreach (var mode in AllModes)
        foreach (var split in splits)
        {
            var cells = CompareLayout.ComputeCells(mode, split);
            var label = $"{mode} @ {split.X}/{split.Y}";

            Assert.All(cells, c =>
            {
                Assert.True(c.Width >= 0, $"{label} 出现负宽 {c.Width}");
                Assert.True(c.Height >= 0, $"{label} 出现负高 {c.Height}");
                Assert.InRange(c.X, 0.0, 1.0);
                Assert.InRange(c.Y, 0.0, 1.0);
            });

            for (var i = 0; i < cells.Length; i++)
            for (var j = i + 1; j < cells.Length; j++)
            {
                var ox = Math.Min(cells[i].X + cells[i].Width, cells[j].X + cells[j].Width)
                       - Math.Max(cells[i].X, cells[j].X);
                var oy = Math.Min(cells[i].Y + cells[i].Height, cells[j].Y + cells[j].Height)
                       - Math.Max(cells[i].Y, cells[j].Y);
                Assert.True(Math.Max(0, ox) * Math.Max(0, oy) <= CompareLayout.Epsilon,
                    $"{label} 第 {i}/{j} 格重叠");
            }

            Assert.True(Math.Abs(cells.Sum(c => c.Width * c.Height) - 1.0) <= CompareLayout.Epsilon,
                $"{label} 未铺满");
        }
    }

    /// <summary>手柄数量与位置：每个手柄都必须落在 [0,1]² 内（NaN 分割参数不得把它带出去），
    /// 且不同手柄不得完全重合（重合 ⇒ 用户无法分别抓住它们）。</summary>
    [Fact]
    public void 枚举遍历_手柄合法且互不重合()
    {
        foreach (var mode in AllModes)
        {
            Assert.True(ExpectedHandles.ContainsKey(mode),
                $"模式 {mode} 不在手柄数手算表里 —— 新增枚举值必须在此登记其手柄数");

            var count = CompareLayout.HandleCount(mode);
            // 与手算表比，而不是 `count >= 1`（后者恒真，抓不到漏改）
            Assert.Equal(ExpectedHandles[mode], count);

            var seen = new List<(double X, double Y)>();
            for (var i = 0; i < count; i++)
            {
                var (hx, hy) = CompareLayout.HandlePositionAt(mode, new SplitParams(0.3, 0.6), i);
                Assert.InRange(hx, 0.0, 1.0);
                Assert.InRange(hy, 0.0, 1.0);

                // NaN / 越界分割参数同样要给出可用位置（手柄跑出可视区 = 拖不动）
                var (nx, ny) = CompareLayout.HandlePositionAt(mode, new SplitParams(double.NaN, 5), i);
                Assert.InRange(nx, 0.0, 1.0);
                Assert.InRange(ny, 0.0, 1.0);

                foreach (var (px, py) in seen)
                    Assert.False(Math.Abs(px - hx) < CompareLayout.Epsilon &&
                                 Math.Abs(py - hy) < CompareLayout.Epsilon,
                        $"{mode} 的第 {i} 个手柄与前面的手柄重合");
                seen.Add((hx, hy));
            }
        }
    }

    /// <summary>手柄的<b>可调轴</b>必须与几何自洽：
    /// 只调 X 的模式，其各格必须是"通高"的（否则拖动竖线会有格子跟着变高，那不是竖分）；
    /// 只调 Y 的模式则各格通宽。这条能抓住"HandleAxisAt 漏改分支 ⇒ 竖分模式却拿到 Both"。</summary>
    [Fact]
    public void 枚举遍历_手柄轴与几何一致()
    {
        foreach (var mode in AllModes)
        {
            var axis = CompareLayout.HandleAxisAt(mode, 0);
            var cells = CompareLayout.ComputeCells(mode, new SplitParams(0.3, 0.6));

            switch (axis)
            {
                case SplitAxis.X:
                    Assert.All(cells, c => Assert.Equal(1.0, c.Height, precision: 9));
                    break;
                case SplitAxis.Y:
                    Assert.All(cells, c => Assert.Equal(1.0, c.Width, precision: 9));
                    break;
                case SplitAxis.Both:
                    // 交叉点模式：宽与高都必须随分割参数变化（既不通高也不通宽）。
                    // ⚠ 只判 Height < 1 是不够的：AbVertical 是"通宽、按高切"，
                    // 摘掉它的 HandleAxisAt 分支会落进 Both 且满足 Height<1 ⇒ 断言不红。
                    // 补上 Width<1 后，通宽的竖分模式才会在这条上暴露出来。
                    Assert.Contains(cells, c => c.Height < 1.0 - 1e-6);
                    Assert.Contains(cells, c => c.Width < 1.0 - 1e-6);
                    break;
            }
        }
    }

    /// <summary>AB 竖的定义性断言：两格通宽、上下相接。
    /// 这条是"漏改 ComputeCells ⇒ 落进 default 画成左右分"的<b>直接</b>判红点
    /// （default 分支给的是 (x,1) 与 (w,1)，宽度不是 1）。</summary>
    [Fact]
    public void 枚举遍历_AbVertical必须是上下两格()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.AbVertical, new SplitParams(0.3, 0.6));

        Assert.Equal(2, cells.Length);
        Assert.All(cells, c => Assert.Equal(1.0, c.Width, precision: 12));  // 通宽
        Assert.Equal(0.0, cells[0].Y, precision: 12);
        Assert.Equal(cells[0].Height, cells[1].Y, precision: 12);           // 上下相接
        Assert.Equal(1.0, cells[1].Y + cells[1].Height, precision: 12);
    }

    /// <summary>ABC 三列的定义性断言：三格通高、左右相接。
    /// 同样是"漏改 ⇒ 落进 default（两格）"的直接判红点。</summary>
    [Fact]
    public void 枚举遍历_AbcColumns必须是三列()
    {
        var cells = CompareLayout.ComputeCells(CompareMode.AbcColumns, new SplitParams(0.3, 0.6));

        Assert.Equal(3, cells.Length);
        Assert.All(cells, c => Assert.Equal(1.0, c.Height, precision: 12)); // 通高
        Assert.Equal(0.0, cells[0].X, precision: 12);
        Assert.Equal(cells[0].X + cells[0].Width, cells[1].X, precision: 12);
        Assert.Equal(cells[1].X + cells[1].Width, cells[2].X, precision: 12);
        Assert.Equal(1.0, cells[2].X + cells[2].Width, precision: 12);
    }

    /// <summary>ABC 三列：第 i 个手柄对应 <see cref="SplitParams"/> 的哪个分量（0=X / 1=Y）。
    /// 期望值独立推导："手柄 0 恒为<b>靠左</b>那条线" ⇒ 它对应当前两分量里较小的那个。
    ///
    /// <para><b>反向验证</b>：实现阶段曾写成固定映射（<c>handleIndex &lt;= 0 ? 0 : 1</c>），
    /// 下面的 (0.7, 0.6) 两例与"交叉后拖动的仍是抓住的那条线"立刻判红；
    /// 改为按 <c>X &lt;= Y</c> 判断后转绿。</para></summary>
    [Theory]
    [InlineData(0.3, 0.6, 0, 0)]   // 常态：左线在 X
    [InlineData(0.3, 0.6, 1, 1)]
    [InlineData(0.7, 0.6, 0, 1)]   // 交叉后：左线其实是 Y
    [InlineData(0.7, 0.6, 1, 0)]
    [InlineData(0.5, 0.5, 0, 0)]   // 相等：按 X 视为第一条
    public void HandleComponentAt_手柄零恒对应靠左那条线(double x, double y, int handle, int expected)
    {
        var split = new SplitParams(x, y);
        Assert.Equal(expected, CompareLayout.HandleComponentAt(CompareMode.AbcColumns, split, handle));

        // 与手柄的**实际绘制位置**必须自洽：手柄 0 恒为两条线里靠左的那个
        var p0 = CompareLayout.HandlePositionAt(CompareMode.AbcColumns, split, 0);
        var p1 = CompareLayout.HandlePositionAt(CompareMode.AbcColumns, split, 1);
        Assert.True(p0.X <= p1.X + CompareLayout.Epsilon);
    }

    /// <summary>端到端：抓住左边那条线拖动，<b>动的必须是被抓住的那条</b>。
    ///
    /// <para>这正是固定索引映射会错的场景：<c>(X=0.7, Y=0.6)</c> 时左线 0.6 存在 Y 里，
    /// 若按"手柄 0 → X"写回，被抓住的线不动、右边那条反而跳了 ——
    /// 表现为"拖动 A|B 分隔条却改变了 B|C 的分界"。</para></summary>
    [Fact]
    public void HandleComponentAt_交叉后拖动的仍是抓住的那条线()
    {
        var split = new SplitParams(0.7, 0.6); // 排序后：左线 0.6(Y)、右线 0.7(X)

        // 抓住手柄 0（画在 0.6）并拖到 0.65
        Assert.Equal(0.6, CompareLayout.HandlePositionAt(CompareMode.AbcColumns, split, 0).X, precision: 12);
        var comp = CompareLayout.HandleComponentAt(CompareMode.AbcColumns, split, 0);
        var moved = comp == 0 ? new SplitParams(0.65, split.Y) : new SplitParams(split.X, 0.65);

        // 被抓住的那条真的到了 0.65，另一条停在 0.7 不动
        Assert.Equal(0.65, CompareLayout.HandlePositionAt(CompareMode.AbcColumns, moved, 0).X, precision: 12);
        Assert.Equal(0.7, CompareLayout.HandlePositionAt(CompareMode.AbcColumns, moved, 1).X, precision: 12);
    }

    /// <summary>单手柄模式只有一个分量语义 ⇒ 恒返回 0；越界索引也不得抛异常。</summary>
    [Theory]
    [InlineData(CompareMode.Ab)]
    [InlineData(CompareMode.AbVertical)]
    [InlineData(CompareMode.Abc)]
    [InlineData(CompareMode.Abcd)]
    public void HandleComponentAt_单手柄模式恒为零(CompareMode mode)
    {
        Assert.Equal(0, CompareLayout.HandleComponentAt(mode, new SplitParams(0.3, 0.6), 0));
        Assert.Equal(0, CompareLayout.HandleComponentAt(mode, new SplitParams(0.3, 0.6), 5));
    }
}
