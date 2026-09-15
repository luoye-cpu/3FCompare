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
    /// <summary>2×1 网格。新增码（旧快照不会产生），与 UI 的第四个预设 "2x1" 对齐——
    /// 原先 PresetOf 只能映射 2x2/3x3/auto，导致用户选 2x1 时无处可存。</summary>
    public const int Code2x1 = 4;

    /// <summary>当前视图状态 → 布局代码（保存会话时用）。
    /// 与 <see cref="IsSingleView"/> / <see cref="PresetOf"/> 三件套成对使用——
    /// 保存侧与还原侧各写一份 switch 是"存了却不还原"这类缺陷的温床。</summary>
    public static int CodeFor(bool singleView, int count)
        => singleView ? CodeSingle : (count <= 4 ? Code2x2 : Code3x3);

    /// <summary>该布局代码是否表示单屏。</summary>
    public static bool IsSingleView(int code) => code == CodeSingle;

    /// <summary>布局代码 → 网格预设名（与 <c>CompareGridView.SetGridLayout</c> 的取值一致）。
    /// 注意 UI 侧共四个预设（auto/2x1/2x2/3x3），这里必须全部覆盖，漏一个就是"存了却还原成别的"。</summary>
    public static string PresetOf(int code) => code switch
    {
        Code2x1 => "2x1",
        Code2x2 => "2x2",
        Code3x3 => "3x3",
        _ => "auto",
    };

    /// <summary>预设名 → 网格覆盖值；(0,0) 表示不覆盖，交由 <see cref="ComputeGrid"/> 自动布局。
    /// 与 <see cref="PresetOf"/> 同为上/下行映射，UI 的 SetGridLayout 应复用本函数而非再写一份。</summary>
    public static (int Cols, int Rows) OverrideOf(string? preset) => preset switch
    {
        "2x1" => (2, 1),
        "2x2" => (2, 2),
        "3x3" => (3, 3),
        _ => (0, 0),
    };

    /// <summary>用户实际选择的预设 → 布局代码（保存会话时用），与 <see cref="PresetOf"/> 互逆。
    ///
    /// 为什么不能用 <see cref="CodeFor"/>：它按"路数"推导，会丢弃用户显式选择的预设。
    /// 例：2 路时用户选了 3x3，CodeFor 仍返回 Code2x2，重载后变成 2x2——
    /// 1/2/3/5/6 路都会因此改变布局（详见 docs/14 §1.1）。
    /// </summary>
    public static int CodeFromPreset(string? preset, bool singleView)
        => singleView ? CodeSingle : preset switch
        {
            "2x1" => Code2x1,
            "2x2" => Code2x2,
            "3x3" => Code3x3,
            _ => CodeAuto,
        };
}
