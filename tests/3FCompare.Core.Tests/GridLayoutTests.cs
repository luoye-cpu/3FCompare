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
}
