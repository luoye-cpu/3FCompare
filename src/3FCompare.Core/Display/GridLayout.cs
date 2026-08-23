namespace _3FCompare.Core.Display;

/// <summary>
/// 对比网格布局计算（纯逻辑，可单测）。
/// 负责根据路数、单屏状态与布局预设解析最终网格（列 × 行），
/// App 层 <c>CompareGridView</c> 复用它做控件布局。
/// </summary>
public static class GridLayout
{
    /// <summary>单屏模式始终为 1×1。</summary>
    public const int SingleViewCols = 1;
    public const int SingleViewRows = 1;

    /// <summary>根据路数与单屏状态计算默认网格（自动布局）。</summary>
    public static (int Cols, int Rows) ComputeGrid(int count, bool singleView)
    {
        if (singleView) return (SingleViewCols, SingleViewRows);
        return count switch
        {
            <= 1 => (1, 1),
            2 => (2, 1),
            3 => (3, 1),
            4 => (2, 2),
            5 => (3, 2),
            6 => (3, 2),
            _ => (3, 3), // 7,8,9
        };
    }

    /// <summary>
    /// 解析最终布局：单屏 (1,1)；预设（overrideCols×overrideRows）容量足够时用预设；
    /// 否则回退 <see cref="ComputeGrid"/> 自动布局。
    /// </summary>
    public static (int Cols, int Rows) ResolveGrid(int count, bool singleView, int overrideCols, int overrideRows)
    {
        if (singleView) return (SingleViewCols, SingleViewRows);
        if (overrideCols > 0 && overrideRows > 0 && count <= overrideCols * overrideRows)
            return (overrideCols, overrideRows);
        return ComputeGrid(count, singleView);
    }
}
