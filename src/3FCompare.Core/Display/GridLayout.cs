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

    /// <summary><b>历史遗留，禁止用于保存路径</b>：按"路数"推导布局代码。
    ///
    /// <para>它的映射与 <see cref="ComputeGrid"/> <b>并不一致</b>（2 路时这里给 2×2，
    /// 而 ComputeGrid 给的是 2×1），把两种实现并存正是"存了却还原成别的"这类缺陷的
    /// 温床（docs/14 §1.1）。保存会话一律走 <see cref="CodeFromPreset"/>，
    /// 由用户显式选择的预设决定代码，而不是由路数反推。</para>
    ///
    /// <para>本方法当前仅被回归测试引用，用来钉死"按路数推导会丢布局"这一历史事实
    ///（<c>tests/3FCompare.Core.Tests/PipelineRegressionTests.cs</c> 的
    /// <c>CodeFor_按路数推导会丢布局_故不得用于保存</c>）。要真正删掉它，必须先同步
    /// 调整该测试，属跨工程改动。</para></summary>
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
