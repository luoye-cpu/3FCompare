using System.Collections.Immutable;

namespace _3FCompare.Core.Display;

/// <summary>多路对比布局模式。取值与 UI 侧的"AB / ABC / ABCD"按钮一一对应，序列化时按整数保存。</summary>
public enum CompareMode
{
    /// <summary>2 路：左右两格。</summary>
    Ab = 0,

    /// <summary>3 路：左大图 + 右侧上下两小图。</summary>
    Abc = 1,

    /// <summary>4 路：十字四宫格。</summary>
    Abcd = 2,
}

/// <summary>拖拽手柄可调整的分割轴。</summary>
public enum SplitAxis
{
    /// <summary>仅竖分割（左右分栏）。</summary>
    X,

    /// <summary>仅横分割（上下分栏）。</summary>
    Y,

    /// <summary>十字交叉点：同时调整竖分割与横分割。</summary>
    Both,
}

/// <summary>
/// 归一化分割参数，X / Y 表示分割线在容器内的相对位置。
/// 与 <see cref="CompareLayout"/> 同为纯值类型，UI 侧拖拽时逐帧重建，不产生堆分配。
/// </summary>
/// <param name="X">竖分割线位置（0=最左，1=最右）。</param>
/// <param name="Y">横分割线位置（0=最上，1=最下）；<see cref="CompareMode.Ab"/> 不使用。</param>
public readonly record struct SplitParams(double X, double Y)
{
    /// <summary>各模式的视觉合理初值：一律居中。
    /// <para>AB 只用到 X，但 Y 仍给 0.5 以免出现 0 这种"未初始化"语义。</para>
    /// <para><paramref name="mode"/> 当前不改变结果（三种模式都取 0.5/0.5），保留该参数是为了让
    /// 调用点显式表达"这是哪个模式的初值"，也为将来某个模式需要不同初值留出签名；
    /// 原先按模式分三个分支但三分支完全相同，属于会被误读为"分支有区别"的冗余。</para></summary>
    public static SplitParams Default(CompareMode mode) => new(0.5, 0.5);

    /// <summary>把 X / Y 收敛到 <see cref="CompareLayout.MinFraction"/>~<see cref="CompareLayout.MaxFraction"/>。
    /// NaN（拖拽中除零、未初始化的绑定值）按 0.5 处理，避免把 NaN 传染进 <see cref="CompareLayout.ComputeCells"/>。</summary>
    public SplitParams Clamp() => new(CompareLayout.ClampFraction(X), CompareLayout.ClampFraction(Y));
}

/// <summary>
/// 归一化单元格：X / Y 为左上角，Width / Height 为尺寸，全部以容器为单位（0~1）。
/// 与像素、DIP 无关，UI 侧只需各自乘以容器宽高即可落位——这是"形状无关"的关键。
/// </summary>
public readonly record struct CellRect(double X, double Y, double Width, double Height);

/// <summary>
/// 多路对比的非均匀布局计算（纯逻辑，可单测）。
///
/// <para>与 <see cref="GridLayout"/> 的分工：后者只做<b>均匀</b> N×M 网格（每格等大），
/// 本类负责 AB / ABC / ABCD 这类<b>非均匀</b>布局与"按路数决定可用模式"的规则。
/// 两者互不依赖，<see cref="GridLayout"/> 的历史行为（含 <c>CodeFor</c> 回归钉）保持不变。</para>
///
/// <para>所有方法均为无状态纯函数：同样的入参必得同样的出参，方便单测与 UI 复算。</para>
/// </summary>
public static class CompareLayout
{
    /// <summary>分割线允许的最小归一化位置。取 0.05 的理由：4K 宽度下约 192px，
    /// 既是肉眼可辨的最小单元格，又给手柄本身留出命中区域；再小则拖动会与"贴边"难以区分。</summary>
    public const double MinFraction = 0.05;

    /// <summary>分割线允许的最大归一化位置（= 1 - <see cref="MinFraction"/>），保证右侧 / 下侧单元格不为零宽高。</summary>
    public const double MaxFraction = 1.0 - MinFraction;

    /// <summary>容差：比较归一化坐标时用（浮点累加误差量级远小于此值）。</summary>
    public const double Epsilon = 1e-9;

    // ══════════ 模式可用性规则 ══════════

    // 用 ImmutableList 而不是普通数组：这些表是全局共享的静态缓存，
    // 若以 IReadOnlyList<CompareMode> 暴露，调用方一次 (CompareMode[]) 强转即可就地改写，
    // 污染后续所有调用（且是跨会话的静默错误）。ImmutableList 从类型上堵死这条路。
    // （不用 ImmutableArray 是因为它只有 Length 没有 Count，而现有调用方读的是 .Count。）
    private static readonly ImmutableList<CompareMode> ModesNone = ImmutableList<CompareMode>.Empty;
    private static readonly ImmutableList<CompareMode> ModesAb =
        ImmutableList.Create(CompareMode.Ab);
    private static readonly ImmutableList<CompareMode> ModesAbAbc =
        ImmutableList.Create(CompareMode.Ab, CompareMode.Abc);
    private static readonly ImmutableList<CompareMode> ModesAll =
        ImmutableList.Create(CompareMode.Ab, CompareMode.Abc, CompareMode.Abcd);

    /// <summary>按路数给出可用模式（需求明确指定的规则，顺序即 UI 按钮顺序）：
    /// 2 路 → AB；3 路 → AB / ABC；≥4 路 → AB / ABC / ABCD；&lt;2 路 → 空集合
    ///（对比功能由多路导入触发，0/1 路时不应暴露任何对比模式）。
    /// 返回的是缓存的<b>不可变</b>列表，调用方无法改写其内容。</summary>
    public static ImmutableList<CompareMode> AvailableModes(int count) => count switch
    {
        < 2 => ModesNone,
        2 => ModesAb,
        3 => ModesAbAbc,
        _ => ModesAll, // >= 4
    };

    /// <summary><paramref name="mode"/> 在 <paramref name="count"/> 路下是否可用。</summary>
    public static bool IsAvailable(CompareMode mode, int count)
    {
        // 用序数比较代替 Contains：模式取值连续且有序，"可用集合必为前缀"是该规则的固有性质。
        // 若将来规则变成非前缀集合，这里需同步改写。
        var max = MaxAvailableOrdinal(count);
        return max >= 0 && (int)mode >= 0 && (int)mode <= max;
    }

    /// <summary>把 <paramref name="mode"/> 收敛为当前路数下可用的模式：
    /// 可用则原样返回；不可用则退到<b>可用集合中的最大模式</b>
    ///（4 路选 ABCD 后掉到 3 路 → ABC，保留用户尽可能多的对比意图）。
    /// <para><b>下界</b>：负数取值（反序列化出的脏数据 / 强转出来的非法枚举）不在任何可用集合里，
    /// 必须与"超过上界"同样收敛，否则 <c>-1</c> 会原样穿过 <c>&lt;= max</c> 传出去。</para>
    /// <para><b>兜底</b>：集合为空（count &lt; 2，即对比功能根本未启动）时返回 <see cref="CompareMode.Ab"/>，
    /// 使返回值始终是确定值，调用方无需再处理 null / 异常；此时该模式也不应被真正渲染。</para></summary>
    public static CompareMode CoerceMode(CompareMode mode, int count)
    {
        var max = MaxAvailableOrdinal(count);
        if (max < 0) return CompareMode.Ab;
        return (int)mode >= 0 && (int)mode <= max ? mode : (CompareMode)max;
    }

    /// <summary>该模式的单元格数量：AB=2、ABC=3、ABCD=4。未知取值按 2 处理（与 <see cref="CompareMode.Ab"/> 一致）。</summary>
    public static int CellCount(CompareMode mode) => mode switch
    {
        CompareMode.Abc => 3,
        CompareMode.Abcd => 4,
        _ => 2,
    };

    // ══════════ 几何计算 ══════════

    /// <summary>按模式与分割参数计算归一化单元格，索引即 A/B/C/D 顺序。
    ///
    /// <para>形状：AB 左右两格；ABC 左大图 + 右侧上下两小图；ABCD 十字四宫格。
    /// 结果保证：无缝铺满 [0,1]²、互不重叠、宽高非负——右侧 / 下侧的尺寸一律写成
    /// <c>1 - X</c> / <c>1 - Y</c> 的补集，而不是另算一个端点，从构造上消除缝隙与重叠。</para>
    ///
    /// <para>入参会先经 <see cref="SplitParams.Clamp"/>，因此即便调用方直接传入 0、1、负数或 NaN
    /// 也不会产出零宽 / 越界单元格。</para></summary>
    public static CellRect[] ComputeCells(CompareMode mode, SplitParams split)
    {
        var (x, y) = split.Clamp();
        var w = 1.0 - x;
        var h = 1.0 - y;

        return mode switch
        {
            CompareMode.Abc =>
            [
                new CellRect(0, 0, x, 1),  // A：左大图，通高
                new CellRect(x, 0, w, y),  // B：右上
                new CellRect(x, y, w, h),  // C：右下
            ],
            CompareMode.Abcd =>
            [
                new CellRect(0, 0, x, y),  // A：左上
                new CellRect(x, 0, w, y),  // B：右上
                new CellRect(0, y, x, h),  // C：左下
                new CellRect(x, y, w, h),  // D：右下
            ],
            _ =>
            [
                new CellRect(0, 0, x, 1),  // A：左半
                new CellRect(x, 0, w, 1),  // B：右半
            ],
        };
    }

    /// <summary>该模式的手柄可调轴：AB 只调竖分割；ABC 的交叉点在 (X,Y)，同时调竖分割与右侧横分割；ABCD 同理。</summary>
    public static SplitAxis HandleAxis(CompareMode mode) => mode switch
    {
        CompareMode.Ab => SplitAxis.X,
        _ => SplitAxis.Both, // Abc / Abcd
    };

    /// <summary>手柄应绘制 / 命中的归一化位置。AB 无横分割，Y 固定取 0.5（竖线中点），
    /// 以免把 0 当作"贴顶"传给 UI 造成手柄跑出可视区。</summary>
    public static (double X, double Y) HandlePosition(CompareMode mode, SplitParams split)
    {
        var (x, y) = split.Clamp();
        return mode switch
        {
            CompareMode.Ab => (x, 0.5),
            _ => (x, y), // Abc / Abcd
        };
    }

    // ══════════ 内部工具 ══════════

    /// <summary>可用集合中最大模式的序数；集合为空时返回 -1。</summary>
    private static int MaxAvailableOrdinal(int count) => count switch
    {
        < 2 => -1,
        2 => (int)CompareMode.Ab,
        3 => (int)CompareMode.Abc,
        _ => (int)CompareMode.Abcd,
    };

    /// <summary>把归一化分量钳到 [<see cref="MinFraction"/>, <see cref="MaxFraction"/>]；NaN → 0.5。
    /// 注意不能直接用 <c>Math.Clamp</c>：它对 NaN 原样返回，会把 NaN 漏进几何计算。</summary>
    internal static double ClampFraction(double v)
    {
        if (double.IsNaN(v)) return 0.5;
        if (v < MinFraction) return MinFraction;
        if (v > MaxFraction) return MaxFraction;
        return v;
    }
}
