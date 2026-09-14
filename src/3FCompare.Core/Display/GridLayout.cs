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

    // ══════════ 会话快照布局代码（保存 / 还原共用同一套映射）══════════

    /// <summary>自动布局（预设不覆盖，按路数自动解析）。</summary>
    public const int CodeAuto = 0;
    /// <summary>单屏（只显示选中路）。</summary>
    public const int CodeSingle = 1;
    /// <summary>2×2 网格。</summary>
    public const int Code2x2 = 2;
    /// <summary>3×3 网格。</summary>
    public const int Code3x3 = 3;

    /// <summary>当前视图状态 → 布局代码（保存会话时用）。
    /// 与 <see cref="IsSingleView"/> / <see cref="PresetOf"/> 三件套成对使用——
    /// 保存侧与还原侧各写一份 switch 是"存了却不还原"这类缺陷的温床。</summary>
    public static int CodeFor(bool singleView, int count)
        => singleView ? CodeSingle : (count <= 4 ? Code2x2 : Code3x3);

    /// <summary>该布局代码是否表示单屏。</summary>
    public static bool IsSingleView(int code) => code == CodeSingle;

    /// <summary>布局代码 → 网格预设名（与 <c>CompareGridView.SetGridLayout</c> 的取值一致）。</summary>
    public static string PresetOf(int code) => code switch
    {
        Code2x2 => "2x2",
        Code3x3 => "3x3",
        _ => "auto",
    };
}
