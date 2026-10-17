using _3FCompare.Core.Display;
using Xunit;

namespace _3FCompare.Core.Tests;

public class GridLayoutTests
{
    // ---- 自动布局（单屏 / 多屏）----

    [Theory]
    [InlineData(true, 0, 1, 1)]
    [InlineData(true, 9, 1, 1)] // 单屏始终 1x1
    public void ComputeGrid_SingleView_AlwaysOneByOne(bool singleView, int count, int expectedCols, int expectedRows)
    {
        Assert.Equal((expectedCols, expectedRows), GridLayout.ComputeGrid(count, singleView));
    }

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
    public void ComputeGrid_MultiView_AutoLayout(int count, int expectedCols, int expectedRows)
    {
        Assert.Equal((expectedCols, expectedRows), GridLayout.ComputeGrid(count, singleView: false));
    }

    // ---- 布局预设 ----

    [Theory]
    [InlineData(4, 2, 2, 2, 2)] // 容量 4，足够 → 用预设
    [InlineData(5, 2, 2, 3, 2)] // 容量 4 不足(5>4) → 回退自动 (3x2)
    [InlineData(9, 3, 3, 3, 3)] // 容量 9，足够 → 用预设
    [InlineData(2, 3, 3, 3, 3)] // 容量 9 足够(2<=9) → 用预设
    public void ResolveGrid_PresetCapacity_FallsBackWhenOverflow(
        int count, int overrideCols, int overrideRows, int expectedCols, int expectedRows)
    {
        var (cols, rows) = GridLayout.ResolveGrid(count, singleView: false, overrideCols, overrideRows);
        Assert.Equal(expectedCols, cols);
        Assert.Equal(expectedRows, rows);
    }

    [Fact]
    public void ResolveGrid_SingleView_IgnoresPreset()
    {
        // 单屏时忽略预设，始终 1x1
        Assert.Equal((1, 1), GridLayout.ResolveGrid(4, singleView: true, 2, 2));
    }

    // ══════════ 以下四条自 ModeSystemTests 迁入（2026-09-30 旧模式模块删除）══════════
    // 那份文件里只有这四条测的是 GridLayout（活代码），其余全是 CompareMode/CompareLayout。
    // 旧文件里的 ComputeGrid_全路数行列与本文件 ComputeGrid_MultiView_AutoLayout 逐字同表，
    // 故不再重复搬入。

    /// <summary>⭐「5~9 路走均匀网格」能成立的前提：每路都必须分到一个格子，
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
    /// 这是「不加行/列就能覆盖 6 路」的临界点。合并后的朝向候选就以它为下界。</summary>
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

    /// <summary>覆盖值为 (0,0) 时必须真的回落到自动布局（不覆盖），而不是产出 0x0 网格。
    ///
    /// <para><b>期望值必须独立</b>：原先写 <c>Assert.Equal(ComputeGrid(5), ResolveGrid(5, 0, 0))</c>
    /// 是同源互证——ComputeGrid 自己算错（例如 5 路给 2×2）时两边一起错、断言恒绿。
    /// 5 路的手算结果是 <b>3×2</b>（容量 6 ≥ 5，见上面的全路数表）。</para></summary>
    [Theory]
    [InlineData("auto")]
    [InlineData(null)]
    public void OverrideOf_未识别预设回落自动布局(string? preset)
    {
        var (cols, rows) = GridLayout.OverrideOf(preset);

        Assert.Equal((0, 0), (cols, rows));
        Assert.Equal((3, 2), GridLayout.ResolveGrid(5, singleView: false, cols, rows));
    }
}
