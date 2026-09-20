using _3FCompare.Core.Display;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 阶段 3「模式体系统一（2~9 路）」的纯计算单测：叠加（AB）/ 分屏（ABC）/ 网格（N×M）。
///
/// <para>期望值全部按语义规则<b>独立手算</b>，不引用被测实现的内部常量与表；实现若与语义冲突，
/// 本文件保留实现行为、在注释中标明「实现如此」，不改生产代码。</para>
/// </summary>
public class ModeSystemTests
{
    // ══════════ 1. AvailableModes：全路数收敛 ══════════

    /// <summary>规则：n&lt;2 → 空；n=2 → {AB}；n=3 → {AB,ABC}；n≥4 → {AB,ABC,ABCD}（全集）。</summary>
    [Theory]
    [InlineData(0, new CompareMode[0])]                       // 0 路：对比功能未启动
    [InlineData(1, new CompareMode[0])]                       // 1 路：仍不启动
    [InlineData(2, new[] { CompareMode.Ab })]
    [InlineData(3, new[] { CompareMode.Ab, CompareMode.Abc })]
    [InlineData(4, new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })]
    [InlineData(5, new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })]
    [InlineData(7, new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })]
    [InlineData(9, new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd })]
    public void AvailableModes_按路数返回规定集合(int count, CompareMode[] expected)
    {
        // 顺序即 UI 按钮顺序，逐元素比较同时钉住顺序与内容
        Assert.Equal(expected, CompareLayout.AvailableModes(count).ToArray());
    }

    /// <summary>关键行为：n≥4 一律返回<b>同一个全集</b>，不随路数再分档（4 路与 9 路完全等价）。</summary>
    [Fact]
    public void AvailableModes_四路及以上一律返回全集()
    {
        var full = new[] { CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd };

        for (var count = 4; count <= 20; count++)
        {
            Assert.Equal(full, CompareLayout.AvailableModes(count).ToArray());
        }
    }

    // ══════════ 2. CoerceMode：收敛到当前路数可用的最大模式 ══════════

    /// <summary>语义：可用则原样返回（含「已可用不被提升」）；超上限则退到可用集合中的最大模式。</summary>
    [Theory]
    // 2 路：上限 AB
    [InlineData(2, CompareMode.Ab, CompareMode.Ab)]
    [InlineData(2, CompareMode.Abc, CompareMode.Ab)]
    [InlineData(2, CompareMode.Abcd, CompareMode.Ab)]
    // 3 路：上限 ABC
    [InlineData(3, CompareMode.Ab, CompareMode.Ab)]
    [InlineData(3, CompareMode.Abc, CompareMode.Abc)]
    [InlineData(3, CompareMode.Abcd, CompareMode.Abc)]
    // 5~9 路：上限 ABCD；已可用的小模式保持原样，只有超上限的取值收敛到 ABCD
    [InlineData(5, CompareMode.Ab, CompareMode.Ab)]
    [InlineData(5, CompareMode.Abc, CompareMode.Abc)]
    [InlineData(5, CompareMode.Abcd, CompareMode.Abcd)]
    [InlineData(7, CompareMode.Ab, CompareMode.Ab)]
    [InlineData(7, CompareMode.Abc, CompareMode.Abc)]
    [InlineData(7, CompareMode.Abcd, CompareMode.Abcd)]
    [InlineData(9, CompareMode.Ab, CompareMode.Ab)]
    [InlineData(9, CompareMode.Abc, CompareMode.Abc)]
    [InlineData(9, CompareMode.Abcd, CompareMode.Abcd)]
    public void CoerceMode_可用原样_超上限退到最大可用(int count, CompareMode mode, CompareMode expected)
    {
        Assert.Equal(expected, CompareLayout.CoerceMode(mode, count));
    }

    /// <summary>5~9 路的上限就是 ABCD：超出枚举范围的取值（脏反序列化 / 非法强转）一律收敛到 ABCD，
    /// 且收敛结果必然可用（不会漏出 -1 或 7）。</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void CoerceMode_五路及以上_超上限取值收敛到Abcd(int count)
    {
        foreach (var dirty in new[] { (CompareMode)(-1), (CompareMode)int.MinValue, (CompareMode)3, (CompareMode)7 })
        {
            var coerced = CompareLayout.CoerceMode(dirty, count);

            Assert.Equal(CompareMode.Abcd, coerced);
            Assert.True(CompareLayout.IsAvailable(coerced, count));
        }
    }

    /// <summary>路数 &lt;2 时集合为空，实现固定返回 AB 作确定性兜底。
    /// 注意「实现如此」的语义边界：返回值只保证<b>确定</b>（不抛异常 / 不是非法枚举），
    /// <b>不保证可用</b>——此时 IsAvailable(AB, count) 为 false，该模式不应被真正渲染。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void CoerceMode_路数不足二时兜底Ab_但该模式并不可用(int count)
    {
        Assert.Equal(CompareMode.Ab, CompareLayout.CoerceMode(CompareMode.Abcd, count));
        Assert.Equal(CompareMode.Ab, CompareLayout.CoerceMode(CompareMode.Ab, count));

        Assert.Empty(CompareLayout.AvailableModes(count));
        Assert.False(CompareLayout.IsAvailable(CompareMode.Ab, count));
    }

    // ══════════ 3. CellCount 与 ComputeCells 长度一致 ══════════

    /// <summary>格数语义：AB=2（左右）、ABC=3（左大 + 右上/右下）、ABCD=4（四宫格）。</summary>
    [Theory]
    [InlineData(CompareMode.Ab, 2)]
    [InlineData(CompareMode.Abc, 3)]
    [InlineData(CompareMode.Abcd, 4)]
    public void CellCount_与ComputeCells长度一致(CompareMode mode, int expected)
    {
        Assert.Equal(expected, CompareLayout.CellCount(mode));

        // 两种分割位置都应给出同样格数（格数只由模式决定，与分割参数无关）
        Assert.Equal(expected, CompareLayout.ComputeCells(mode, new SplitParams(0.5, 0.5)).Length);
        Assert.Equal(expected, CompareLayout.ComputeCells(mode, new SplitParams(0.3, 0.6)).Length);
    }

    /// <summary>把两个方法绑在一起：对 2~9 路下<b>每一个可用模式</b>，格数 == 实际算出的单元格数。</summary>
    [Fact]
    public void CellCount_与ComputeCells长度一致_覆盖2至9路的全部可用模式()
    {
        for (var count = 2; count <= 9; count++)
        {
            var modes = CompareLayout.AvailableModes(count);
            Assert.NotEmpty(modes);

            foreach (var mode in modes)
            {
                var cells = CompareLayout.ComputeCells(mode, new SplitParams(0.5, 0.5));
                Assert.Equal(CompareLayout.CellCount(mode), cells.Length);
            }
        }
    }

    // ══════════ 4. ComputeGrid：全路数行列 ══════════

    /// <summary>自动网格手算：1→1x1、2→2x1、3→3x1、4→2x2、5/6→3x2、7/8/9→3x3。</summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(7, 3, 3)]
    [InlineData(8, 3, 3)]
    [InlineData(9, 3, 3)]
    public void ComputeGrid_全路数行列(int count, int expectedCols, int expectedRows)
    {
        Assert.Equal((expectedCols, expectedRows), GridLayout.ComputeGrid(count, singleView: false));
    }

    // ══════════ 5. ⭐ 网格不丢路：容量 ≥ 路数 ══════════

    /// <summary>「5~9 路走均匀网格」能成立的前提：每路都必须分到一个格子，
    /// 即 cols*rows ≥ n。n=6 在 3x2 下是<b>满容</b>（6 == 6），一格都不能少。</summary>
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
    public void ComputeGrid_网格容量不小于路数_不丢路(int count)
    {
        var (cols, rows) = GridLayout.ComputeGrid(count, singleView: false);

        Assert.True(cols >= 1 && rows >= 1, $"{count} 路网格出现空维度 {cols}x{rows}");
        Assert.True(cols * rows >= count,
            $"{count} 路在 {cols}x{rows}（容量 {cols * rows}）网格中丢了 {count - cols * rows} 路");
    }

    /// <summary>5 路与 6 路同为 3x2：容量 6 恰好容纳 6 路（≥5 与 ≥6 都成立），
    /// 这是「不加行/列就能覆盖 6 路」的临界点。</summary>
    [Fact]
    public void ComputeGrid_五路与六路同为三乘二_恰好容纳()
    {
        var (cols5, rows5) = GridLayout.ComputeGrid(5, singleView: false);
        var (cols6, rows6) = GridLayout.ComputeGrid(6, singleView: false);

        Assert.Equal((3, 2), (cols5, rows5));
        Assert.Equal((3, 2), (cols6, rows6));

        Assert.True(cols5 * rows5 >= 5); // 6 ≥ 5：5 路时有 1 个空格
        Assert.True(cols6 * rows6 >= 6); // 6 ≥ 6：6 路时满容，再少一格就丢路
    }

    // ══════════ 6. OverrideOf：预设解析 ══════════

    /// <summary>预设名 → 覆盖值；无法识别的预设（含 "auto"、null、空串）一律 (0,0) 表示不覆盖，
    /// 交由 ComputeGrid 自动布局。</summary>
    [Theory]
    [InlineData("2x1", 2, 1)]
    [InlineData("2x2", 2, 2)]
    [InlineData("3x3", 3, 3)]
    [InlineData("auto", 0, 0)]
    [InlineData(null, 0, 0)]
    [InlineData("", 0, 0)]
    [InlineData("2X1", 0, 0)]      // 大小写敏感：非精确匹配即视为未识别
    [InlineData("2x1 ", 0, 0)]     // 带尾随空格同样不识别
    public void OverrideOf_预设解析(string? preset, int expectedCols, int expectedRows)
    {
        Assert.Equal((expectedCols, expectedRows), GridLayout.OverrideOf(preset));
    }

    /// <summary>覆盖值为 (0,0) 时必须真的回落到自动布局（不覆盖），而不是产出 0x0 网格。</summary>
    [Theory]
    [InlineData("auto")]
    [InlineData(null)]
    public void OverrideOf_未识别预设回落自动布局(string? preset)
    {
        var (cols, rows) = GridLayout.OverrideOf(preset);

        Assert.Equal((0, 0), (cols, rows));
        Assert.Equal(GridLayout.ComputeGrid(5, singleView: false),
                     GridLayout.ResolveGrid(5, singleView: false, cols, rows));
    }
}
