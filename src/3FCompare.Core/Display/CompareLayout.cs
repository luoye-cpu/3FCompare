using System.Collections.Immutable;

namespace _3FCompare.Core.Display;

/// <summary>多路对比布局模式。取值与 UI 侧的"AB / ABC / ABCD"按钮一一对应，序列化时按整数保存。
///
/// <para><b>变体的取值位置</b>：<see cref="AbVertical"/>(3) 与 <see cref="AbcColumns"/>(4) 追加在
/// 原有三个值之后 —— 它们从未被持久化（<c>SessionSnapshot</c> / <c>AppSettings</c> 都没有
/// CompareMode 字段），故不需要任何迁移代码；未知值由 <see cref="CompareLayout.CoerceMode"/> 收敛。</para>
///
/// <para><b>⚠ 新增值必须同步改四处</b>（C# 对带 <c>_ =&gt;</c> 兜底的 switch 不报 CS8509，
/// 漏改分支会静默落进 default，表现为"竖分被画成左右分"）：
/// <see cref="CompareLayout.AvailableModes"/> 的模式表、<see cref="CompareLayout.CellCount"/>、
/// <see cref="CompareLayout.ComputeCells"/>、<see cref="CompareLayout.HandleAxisAt"/> 与
/// <see cref="CompareLayout.HandlePositionAt"/>。单测 <c>ModeVariantTests</c> 用
/// <c>Enum.GetValues</c> 遍历钉住这四处，漏改会判红。</para></summary>
public enum CompareMode
{
    /// <summary>2 路：左右两格。</summary>
    Ab = 0,

    /// <summary>3 路：左大图 + 右侧上下两小图。</summary>
    Abc = 1,

    /// <summary>4 路：十字四宫格。</summary>
    Abcd = 2,

    /// <summary>2 路：<b>上下</b>两格（<see cref="Ab"/> 的横分割变体）。
    /// 格数与 <see cref="Ab"/> 相同，只换分割轴：拖动手柄调 <see cref="SplitAxis.Y"/>。</summary>
    AbVertical = 3,

    /// <summary>3 路：<b>三列</b>等宽分栏（<see cref="Abc"/> 的变体）。
    /// 需要<b>两条</b>竖分割线 ⇒ 复用 <see cref="SplitParams"/> 的两个自由度：
    /// X = 第一条线、Y = 第二条线（顺序由 <see cref="CompareLayout.OrderPair"/> 归一化，
    /// 于是把 X 拖到 Y 右侧只是"两条线换了身份"，不会产出负宽的中间列）。</summary>
    AbcColumns = 4,
}

/// <summary>多路对比的<b>分辨率对齐模式</b>：决定"各路露出源画面的哪一块"。
///
/// <para><b>为什么是两个模式而不是一个</b>：笔记里"把所有路分辨率拉到一致"有两种语义，
/// 现有实现只满足其中一种，且两种无法同时成立（各路源分辨率不同时，"同一相对位置"与
/// "同一像素尺寸"必然是两块不同的区域）。故做成可切换的模式，而不是静默选一个。</para>
///
/// <para>序列化为 int（<c>AppSettings.CompareAlign</c>），故<b>不要</b>在中间插入新成员 ——
/// 那会改变已有数字的含义（与 <c>SidebarMode</c> 同一约定）。</para></summary>
public enum CompareAlign
{
    /// <summary><b>相对对齐</b>（默认，既有行为）：各路共用同一个归一化裁剪区间 ⇒
    /// 露出的是<b>各自画面中相同的相对位置</b>。1080p 与 4K 各露出自己画面的同一块比例，
    /// 但露出的源像素尺寸不同（4K 那一路的像素数是 1080p 的 4 倍）。</summary>
    Relative = 0,

    /// <summary><b>像素级对齐</b>：各路露出<b>相同源像素尺寸</b>的区域（以最小的那一路为基准）
    /// ⇒ 放大后各路画面上的"一个像素"对应相同的源像素数，亮度/细节才真正可比。
    /// 代价：各路露出的相对位置不同（4K 只露出中心一小块），且基准之外的路被<b>放大更多</b>。</summary>
    Pixel = 1,
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
/// <param name="X">竖分割线位置（0=最左，1=最右）。
/// <see cref="CompareMode.AbcColumns"/> 下它是<b>第一条</b>竖线（另见 <paramref name="Y"/>）。</param>
/// <param name="Y">横分割线位置（0=最上，1=最下）；<see cref="CompareMode.Ab"/> 不使用。
/// <para><b>例外</b>：<see cref="CompareMode.AbcColumns"/> 没有横分割线，此时 Y 是
/// <b>第二条</b>竖线 —— 该模式需要两条竖线而本结构只有两个自由度，复用是最小的改动
/// （另选方案是给 <c>ComputeCells</c> 加"变体"参数，那会让 ~8 处调用点全部改签名）。</para></param>
public readonly record struct SplitParams(double X, double Y)
{
    /// <summary>各模式的视觉合理初值。
    /// <para>单线模式一律居中（0.5 / 0.5）；AB 只用到 X，但 Y 仍给 0.5 以免出现 0 这种
    /// "未初始化"语义。</para>
    /// <para><b><see cref="CompareMode.AbcColumns"/> 取 (1/3, 2/3)</b>：三列等宽的初值。
    /// 若也取 0.5，两条线重合 ⇒ <see cref="CompareLayout.OrderPair"/> 会把第二条推到
    /// 0.5+0.05，首帧就是"窄—宽—宽"的畸形分栏，看起来像布局算错了。</para></summary>
    public static SplitParams Default(CompareMode mode) => mode switch
    {
        CompareMode.AbcColumns => new(1.0 / 3.0, 2.0 / 3.0),
        _ => new(0.5, 0.5),
    };

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
        ImmutableList.Create(CompareMode.Ab, CompareMode.AbVertical);
    private static readonly ImmutableList<CompareMode> ModesAbAbc =
        ImmutableList.Create(CompareMode.Ab, CompareMode.AbVertical, CompareMode.Abc, CompareMode.AbcColumns);
    private static readonly ImmutableList<CompareMode> ModesAll =
        ImmutableList.Create(CompareMode.Ab, CompareMode.AbVertical, CompareMode.Abc,
            CompareMode.AbcColumns, CompareMode.Abcd);

    /// <summary>按路数给出可用模式（顺序即 C 键循环顺序）：
    /// 2 路 → AB / AB 竖；3 路 → AB / AB 竖 / ABC / ABC 三列；
    /// ≥4 路 → 上述 + ABCD；&lt;2 路 → 空集合（对比功能由多路导入触发，0/1 路时不应暴露任何对比模式）。
    /// <para><b>顺序约定</b>：主形态在前、变体紧跟其后（<see cref="CompareMode.Ab"/> 后是
    /// <see cref="CompareMode.AbVertical"/>，<see cref="CompareMode.Abc"/> 后是
    /// <see cref="CompareMode.AbcColumns"/>）—— 于是"格数最多"的 <see cref="CompareMode.Abcd"/>
    /// 仍在最后，与既有"循环到末项即退出"的行为一致。</para>
    /// 返回的是缓存的<b>不可变</b>列表，调用方无法改写其内容。</summary>
    public static ImmutableList<CompareMode> AvailableModes(int count) => count switch
    {
        < 2 => ModesNone,
        2 => ModesAb,
        3 => ModesAbAbc,
        _ => ModesAll, // >= 4
    };

    /// <summary><paramref name="mode"/> 在 <paramref name="count"/> 路下是否可用。
    ///
    /// <para><b>为什么改成集合判定</b>：原先是"序数 &lt;= 上界"，它成立的唯一前提是
    /// "可用集合恰好是枚举的前缀"。加入变体后 <see cref="CompareMode.AbVertical"/>(3) 的序数大于
    /// <see cref="CompareMode.Abc"/>(1)，3 路集合 {Ab, Abc} 不再是前缀 ⇒ 序数判定会把
    /// AbVertical 误判为可用（而它确实可用，但 <see cref="CompareMode.AbcColumns"/>(4) 在 3 路下
    /// 也会被判成"&gt;上界 Abc"而不可用，尽管它在集合里）。这类"看起来对、换个数就错"的
    /// 判定必须以集合为准 —— 规则变了，判定方式必须跟着变（原注释里已写明这条前置条件）。</para></summary>
    public static bool IsAvailable(CompareMode mode, int count)
        => AvailableModes(count).Contains(mode);

    /// <summary>把 <paramref name="mode"/> 收敛为当前路数下可用的模式：
    /// 可用则原样返回；不可用则退到 <see cref="FallbackMode"/>（<b>标准形态里格数最多的那个</b>）。
    /// <para><b>为什么回退不取"集合末项"</b>：集合末项在 ≥4 路是 <see cref="CompareMode.Abcd"/>，
    /// 但 2 路的末项是 <see cref="CompareMode.AbVertical"/> —— 那样"4 路 ABCD 掉到 2 路"会变成
    /// 上下分栏，而既有行为是左右分栏（<c>docs</c> 里"保留用户尽可能多的对比意图"指的是格数，
    /// 不是变体）。故回退只在 Ab / Abc / Abcd 三条标准形态里挑，变体永不作为回退目标；
    /// 标准形态都不可用（count&lt;2）时才取集合末项作为确定性兜底。</para>
    /// <para><b>下界</b>：负数取值（反序列化出的脏数据 / 强转出来的非法枚举）不在任何可用集合里，
    /// 与"超过上界"走同一条收敛路径。集合判定天然挡住它们 —— <c>Contains(-1)</c> 恒为 false，
    /// 不需要再单独写 <c>&gt;= 0</c> 判断（那是序数判定才需要的补丁）。</para>
    /// <para><b>兜底</b>：集合为空（count &lt; 2，即对比功能根本未启动）时返回 <see cref="CompareMode.Ab"/>，
    /// 使返回值始终是确定值，调用方无需再处理 null / 异常；此时该模式也不应被真正渲染。</para></summary>
    public static CompareMode CoerceMode(CompareMode mode, int count)
    {
        var modes = AvailableModes(count);
        if (modes.Count == 0) return CompareMode.Ab;
        return modes.Contains(mode) ? mode : FallbackMode(count);
    }

    /// <summary>该模式的单元格数量：AB / AB 竖 = 2、ABC / ABC 三列 = 3、ABCD = 4。
    /// 未知取值按 2 处理（与 <see cref="CompareMode.Ab"/> 一致）。</summary>
    public static int CellCount(CompareMode mode) => mode switch
    {
        CompareMode.Abc => 3,
        CompareMode.AbcColumns => 3,
        CompareMode.Abcd => 4,
        _ => 2, // Ab / AbVertical
    };

    // ══════════ 几何计算 ══════════

    /// <summary>按模式与分割参数计算归一化单元格，索引即 A/B/C/D 顺序。
    ///
    /// <para>形状：AB 左右两格；<b>AB 竖</b> 上下两格；ABC 左大图 + 右侧上下两小图；
    /// <b>ABC 三列</b> 三列等分（可拖动两条竖线分别调整）；ABCD 十字四宫格。
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
            // AB 竖：与 Ab 同样的两格，只把分割轴从 X 换成 Y。
            CompareMode.AbVertical =>
            [
                new CellRect(0, 0, 1, y),  // A：上半（通宽）
                new CellRect(0, y, 1, h),  // B：下半
            ],
            // ABC 三列：X / Y 是两条竖线，经 OrderPair 排序并保证中间列不为零宽。
            CompareMode.AbcColumns => ComputeColumns(),
            _ =>
            [
                new CellRect(0, 0, x, 1),  // A：左半
                new CellRect(x, 0, w, 1),  // B：右半
            ],
        };

        CellRect[] ComputeColumns()
        {
            var (x1, x2) = OrderPair(split);
            return
            [
                new CellRect(0, 0, x1, 1),            // A：第一列
                new CellRect(x1, 0, x2 - x1, 1),      // B：第二列
                new CellRect(x2, 0, 1.0 - x2, 1),     // C：第三列
            ];
        }
    }

    /// <summary>三列模式的两条竖分割线：<b>排序后</b>的 (第一条, 第二条)，并保证中间列
    /// 至少有 <see cref="MinFraction"/> 宽。
    ///
    /// <para><b>为什么要排序</b>：X / Y 是两个独立自由度，用户完全可以把第一条线拖到第二条
    /// 右侧。若不排序直接写 <c>B.Width = Y - X</c>，中间列宽度会变成负数 —— 负宽矩形会让
    /// 子 HWND 定位到非法尺寸（Win32 只认 16 位有符号，负数被解释成巨大的正数），布局直接错乱。
    /// 排序后"拖动越过"表现为两条线交换身份，是连续且可预测的行为。</para>
    ///
    /// <para><b>为什么要最小间隔</b>：两条线重合时中间列宽为 0，那一格完全不可见且手柄重叠
    /// （用户再也无法把它们分开 ⇒ 卡死）。先尝试把第二条右推，右推不动就把第一条左推 ——
    /// 两个方向都保证结果仍落在 [<see cref="MinFraction"/>, <see cref="MaxFraction"/>]。</para></summary>
    internal static (double First, double Second) OrderPair(SplitParams split)
    {
        var (x, y) = split.Clamp();
        var lo = Math.Min(x, y);
        var hi = Math.Max(x, y);
        if (hi - lo >= MinFraction) return (lo, hi);

        hi = Math.Min(MaxFraction, lo + MinFraction);
        if (hi - lo < MinFraction) lo = Math.Max(MinFraction, hi - MinFraction);
        return (lo, hi);
    }

    /// <summary>该模式的<b>手柄数量</b>。绝大多数模式是「一个交叉点」= 1；
    /// <see cref="CompareMode.AbcColumns"/> 有两条独立的竖分割线 ⇒ 2 个手柄（见 <see cref="HandleAxisAt"/>）。
    /// <para>覆盖层据此决定画几个手柄、命中测试要判几个圈 —— 它是"多手柄"能力的唯一入口，
    /// 单手柄模式调用它拿到 1，行为与改动前完全一致。</para></summary>
    public static int HandleCount(CompareMode mode) => mode switch
    {
        CompareMode.AbcColumns => 2,
        _ => 1,
    };

    /// <summary>第 <paramref name="handleIndex"/> 个手柄的可调轴：AB 只调竖分割、<b>AB 竖</b>只调横分割；
    /// ABC / ABCD 的交叉点同时调两轴；<b>ABC 三列</b>的两个手柄各自只调一条竖线。
    /// <para><b>当前实现不看 <paramref name="handleIndex"/></b>：今天唯一的多手柄模式
    /// （三列）两个手柄同轴。这条对将来"一条竖线 + 一条横线"的模式<b>不成立</b> ——
    /// 届时本方法必须按索引分支，而 <see cref="LayoutOverlayWindow"/> 正是靠它决定写 X 还是写 Y。
    /// 参数先保留，是为了让调用点不必改动，也让"多手柄"这件事在签名上可见。</para></summary>
    public static SplitAxis HandleAxisAt(CompareMode mode, int handleIndex) => mode switch
    {
        CompareMode.Ab => SplitAxis.X,
        CompareMode.AbVertical => SplitAxis.Y,
        CompareMode.AbcColumns => SplitAxis.X, // 两个手柄都是竖线，只是各管一条
        _ => SplitAxis.Both, // Abc / Abcd
    };

    /// <summary>第 <paramref name="handleIndex"/> 个手柄应绘制 / 命中的归一化位置。
    /// 单轴模式的另一轴固定取 0.5（线中点），以免把 0 当作"贴边"传给 UI 造成手柄跑出可视区。
    /// <para>ABC 三列的两个手柄分别落在经 <see cref="OrderPair"/> 排序后的两条线上 ——
    /// 手柄 0 恒为靠左那条、"手柄 0 改 X、手柄 1 改 Y"的映射在两条线交换身份后仍然成立
    /// （用户看到的是"我抓住的那条线跟着走"，而不是"编号 0 的那条"）。</para></summary>
    public static (double X, double Y) HandlePositionAt(CompareMode mode, SplitParams split, int handleIndex)
    {
        var (x, y) = split.Clamp();
        return mode switch
        {
            CompareMode.Ab => (x, 0.5),
            CompareMode.AbVertical => (0.5, y),
            CompareMode.AbcColumns =>
                handleIndex <= 0 ? (OrderPair(split).First, 0.5) : (OrderPair(split).Second, 0.5),
            _ => (x, y), // Abc / Abcd
        };
    }

    /// <summary>第 <paramref name="handleIndex"/> 个手柄当前对应 <see cref="SplitParams"/> 的
    /// 哪个分量（0 = X，1 = Y）。只对"多个手柄分管不同分量"的模式有意义（今天是
    /// <see cref="CompareMode.AbcColumns"/>：两条竖线分别存在 X 与 Y 里）。
    ///
    /// <para><b>为什么必须问 Core，而不能由调用方按索引硬猜</b>：手柄画在<b>排序后</b>的位置上
    /// （手柄 0 恒为靠左那条），但两条线存在哪个分量里取决于当前 <c>X ≤ Y</c> 与否。
    /// 固定写"手柄 0 → X"在两条线交叉后就反了：实例 —— <c>(X=0.7, Y=0.6)</c> 时手柄 0 画在
    /// 0.6（那是 Y），用户把它拖到 0.65 却去改 X ⇒ <b>被抓住的那条不动、另一条跳了</b>，
    /// 表现为"拖动 A|B 分隔条却改变了 B|C 的分界"。故分量身份必须由本方法给出。</para>
    ///
    /// <para>当两线相等（<c>X == Y</c>）时 <c>X</c> 视为"第一条"：这是 <see cref="OrderPair"/>
    /// 在重合情形下也采用的取向（先固定前者、再推后者），两处同向才不会互相打架。</para></summary>
    public static int HandleComponentAt(CompareMode mode, SplitParams split, int handleIndex)
    {
        if (HandleCount(mode) < 2) return 0; // 单手柄模式只有"竖分割"这一个分量语义
        var (x, y) = split.Clamp();
        var firstIsX = x <= y;
        return handleIndex <= 0 ? (firstIsX ? 0 : 1) : (firstIsX ? 1 : 0);
    }

    /// <summary>该模式的<b>第一个</b>手柄的可调轴（<see cref="HandleAxisAt"/> 在索引 0 上的特化）。
    /// 保留它是为了不打断既有调用点与单测；新代码请用带索引的版本。</summary>
    public static SplitAxis HandleAxis(CompareMode mode) => HandleAxisAt(mode, 0);

    /// <summary>该模式的<b>第一个</b>手柄的位置（<see cref="HandlePositionAt"/> 在索引 0 上的特化）。
    /// 保留它是为了不打断既有调用点与单测；多手柄模式请用带索引的版本。</summary>
    public static (double X, double Y) HandlePosition(CompareMode mode, SplitParams split)
        => HandlePositionAt(mode, split, 0);

    // ══════════ 内部工具 ══════════

    /// <summary>不可用时的回退目标：<b>标准形态</b>（Ab / Abc / Abcd）里格数最多的那个 ——
    /// 4 路选 ABCD 后掉到 3 路 → ABC、掉到 2 路 → AB，保留用户尽可能多的对比意图。
    /// <para>变体（<see cref="CompareMode.AbVertical"/> / <see cref="CompareMode.AbcColumns"/>）
    /// 刻意不参与回退：它们是"同一个格数的另一种排布"，作为回退目标会把"路数变少"变成
    /// "排布也变了"，用户拖好的分割位置与视觉预期一起丢失。
    /// 集合为空（count &lt; 2）时返回 <see cref="CompareMode.Ab"/> 作确定性兜底。</para></summary>
    private static CompareMode FallbackMode(int count)
    {
        var modes = AvailableModes(count);
        if (modes.Count == 0) return CompareMode.Ab;
        // 三个可用集合都含 Ab ⇒ 上面三条必命中其一，末尾没有"集合非空但不含 Ab"的分支。
        // 刻意不写 `return modes[^1]`：那是不可达代码，且一旦将来出现不含 Ab 的集合，
        // 静默返回末项会把"变体"当成回退目标（正是本方法要避免的）。
        if (modes.Contains(CompareMode.Abcd)) return CompareMode.Abcd;
        if (modes.Contains(CompareMode.Abc)) return CompareMode.Abc;
        return CompareMode.Ab;
    }

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
