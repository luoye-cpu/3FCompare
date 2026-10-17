using System.Collections.Immutable;

namespace _3FCompare.Core.Display;

/// <summary>归一化单元格：X / Y 为左上角，Width / Height 为尺寸，全部以容器为单位（0~1）。
/// 与像素、DIP 无关，UI 侧只需各自乘以容器宽高即可落位——这是"形状无关"的关键。</summary>
public readonly record struct CellRect(double X, double Y, double Width, double Height);

/// <summary>拖拽手柄可调整的分割轴。
/// <para>合并后的网格里每条内部线都是<b>单轴</b>的（一条竖线只改它左右两列的宽度），
/// 所以 <see cref="Both"/> 已不再有产出方：它保留只因为旧交叉点型手柄的语义写在枚举里，
/// 覆盖层的命中判据仍要为它留一支。</para></summary>
public enum SplitAxis
{
    /// <summary>仅竖分割（左右分栏）。</summary>
    X,

    /// <summary>仅横分割（上下分栏）。</summary>
    Y,

    /// <summary>十字交叉点：同时调整竖分割与横分割（合并后的网格分区不再产出此值）。</summary>
    Both,
}

/// <summary>可拖分割线的<b>网格分区</b>——"标准网格 + A/B 可拖动"合并后的几何真源（纯逻辑，可单测）。
///
/// <para><b>为什么用"权重"而不是"分割线绝对位置"</b>：一条线的位置在 N 路网格里不是独立量——
/// 把第 2 条竖线右移会让第 3 条也动，除非同时改写它们之间那一列的宽度。用<b>每列/每行一个正权重</b>
/// （宽度 = 权重 / 权重和）之后，"拖第 k 条线"天然等价于"在第 k 列与第 k+1 列之间搬运一段宽度"，
/// 于是三条硬性质由构造保证而不是靠事后钳制：① 各格无缝铺满 <c>[0,1]²</c> 且互不重叠
/// （末条边界恒写死为 1.0，与旧 <c>CompareLayout.ComputeCells</c> 的补集写法同一思路，
/// 目的是消除浮点累加留下的缝隙）；② 宽度恒为正（两侧各自不小于 <see cref="MinSpanFraction"/>
/// 的权重才允许移动）；③ 被拖的那条线右侧/下方的其它线<b>不动</b>。
/// 负宽矩形在下游是要命的：<c>CompareGridView.ArrangeOverride</c> 的注释写明子 HWND 的宽高
/// 走 Win32 16 位有符号，负数会被解释成巨大的正数、布局当场错乱。</para>
///
/// <para><b>自由度</b>：内部竖线 <c>Cols-1</c> 条 + 横线 <c>Rows-1</c> 条 ⇒ 最多 4 个手柄（3×3）。
/// 这正好是旧 <c>SplitParams</c>（只有两个自由度）装不下的部分（<c>docs/47</c> 记的
/// "三列受 SplitParams 自由度硬约束"就是这件事），也是本类型存在的理由。</para>
///
/// <para><b>与 <see cref="GridLayout"/> 的分工</b>：行列<b>形状</b>（几乘几）仍由
/// <see cref="GridLayout.ResolveGrid"/> 按路数与预设解析，本类型只决定"这些行列各占多宽/多高"。
/// 两条路径共用同一个形状解析，于是"没拖动时的分区"与"既有的均匀网格"逐字同形。</para>
///
/// <para>不可变：拖动产生新实例（每帧一个小数组 + 一个对象，与
/// 旧 <c>CompareLayout.ComputeCells</c> 的既有分配量级一致）。</para></summary>
public sealed class GridPartition
{
    /// <summary>任何一列 / 一行允许收缩到的最小占比（占整个对比区的比例）。
    /// 取 0.05 沿用旧 <c>CompareLayout.MinFraction</c> 的理由：4K 宽度下约 192px，
    /// 既是肉眼可辨的最小格，又给手柄留出命中区；再小就与"贴边"难以区分。</summary>
    public const double MinSpanFraction = 0.05;

    /// <summary>比较归一化坐标时的容差（浮点累加误差量级远小于此值）。</summary>
    public const double Epsilon = 1e-9;

    /// <summary>路数（不是格数：格数 = <c>Cols × Rows</c>，可能多于路数，多的那几格空着）。</summary>
    public int Lanes { get; }

    public int Cols { get; }

    public int Rows { get; }

    /// <summary>各列宽度权重（恒正，和恒为构造时的权重和；对外只读，防止调用方就地改写这份
    /// 被多处消费的不可变状态）。</summary>
    public ImmutableList<double> ColumnWeights { get; }

    /// <summary>各行高度权重。</summary>
    public ImmutableList<double> RowWeights { get; }

    private GridPartition(int lanes, int cols, int rows,
        ImmutableList<double> columnWeights, ImmutableList<double> rowWeights)
    {
        Lanes = lanes;
        Cols = cols;
        Rows = rows;
        ColumnWeights = columnWeights;
        RowWeights = rowWeights;
    }

    // ══════════ 构造 / 解析 ══════════

    /// <summary>按路数、单屏态与网格预设解析<b>均匀</b>分区（形状由 <see cref="GridLayout.ResolveGrid"/> 决定）。
    /// 这是"刚进入合并模式"和"重置分割"两处的入口。</summary>
    public static GridPartition Resolve(int lanes, bool singleView = false,
        int presetCols = 0, int presetRows = 0)
    {
        var (cols, rows) = GridLayout.ResolveGrid(Math.Max(0, lanes), singleView, presetCols, presetRows);
        return Uniform(lanes, cols, rows);
    }

    /// <summary>单轴最多几格 = <c>1 / MinSpanFraction</c>（= 20）。
    ///
    /// <para><b>为什么这是一条硬上界而不是"反正到不了"</b>：整个拖动规则的地基是
    /// "每一个权重 ≥ <see cref="MinSpanFraction"/> × 权重和"（<see cref="WithHandleDragged"/>
    /// 里两次钳制不会互相推翻的前提就靠它）。均匀构造给的是"每格权重 1.0、权重和 = 格数"，
    /// 于是该前提等价于 <c>1.0 ≥ 0.05 × 格数</c> ⇒ 格数 ≤ 20。越过这条界，起点的每个格子就
    /// 已经<b>不到</b> 5%，"每格至少 5%"从承诺变成空话，而且拖动会失灵：24 列时实测
    /// （复刻本方法的算式）起点每格 4.17%，把第一条线拖到 pointer=0 / 0.2 / 0.5 三个不同位置，
    /// 落点<b>恒为 3.33%</b>——线根本不动。宽高仍为正（权重恒正 ⇒ 边界单调），所以这不是
    /// 负宽矩形那一级事故，而是"下界与拖动响应一起失效"——依然必须挡在构造里。</para>
    ///
    /// <para>现实里到不了 20（lane 上限 9 ⇒ 自动形状最多 3×3，朝向候选最多 4 列），
    /// 但 <see cref="Uniform"/> 是 public：调用方给什么形状都必须得到合法几何，
    /// 不能把"只在今天成立"的前提交给运气。</para></summary>
    public const int MaxCellsPerAxis = (int)(1.0 / MinSpanFraction);

    /// <summary>指定形状、各列各行等宽。形状的两轴各钳到 [<c>1</c>, <see cref="MaxCellsPerAxis"/>]，
    /// 理由见该常量注释。</summary>
    public static GridPartition Uniform(int lanes, int cols, int rows)
    {
        cols = Math.Clamp(cols, 1, MaxCellsPerAxis);
        rows = Math.Clamp(rows, 1, MaxCellsPerAxis);
        return new GridPartition(Math.Max(0, lanes), cols, rows,
            EqualWeights(cols), EqualWeights(rows));
    }

    /// <summary>该路数下可选的<b>形状</b>序列（顺序即 <c>C</c> 键循环顺序，首项必须是
    /// <see cref="GridLayout.ComputeGrid"/> 的自动形状，于是"没动过 C 键"时与既有网格逐字一致）。
    ///
    /// <para>合并后 <c>CompareMode</c> 的固定形状就落到这里：<c>Ab</c>≡2×1、
    /// <c>AbcColumns</c>≡3×1、<c>Abcd</c>≡2×2；<c>AbVertical</c>（上下两格）需要 1×2，
    /// 而旧的网格预设表里从来没有过（<c>docs/31 §2.2</c> 点名的缺口），所以 2 路的候选必须含它，
    /// 否则撤 A/B 模式就是撤能力。</para>
    ///
    /// <para><c>Abc</c>（左大图 + 右侧上下两小图）刻意<b>不在</b>候选里：它是 T 形交界的
    /// 非整线形状，整线网格表达不了。3 路退化为 3×1 或 2×2（留空格），是"合并成网格形式"这一
    /// 决定的直接代价，已对用户明示。</para></summary>
    public static ImmutableList<(int Cols, int Rows)> ShapesFor(int lanes) => lanes switch
    {
        <= 1 => ImmutableList.Create((1, 1)),
        2 => ImmutableList.Create((2, 1), (1, 2)),
        3 => ImmutableList.Create((3, 1), (1, 3), (2, 2)),
        4 => ImmutableList.Create((2, 2), (4, 1), (1, 4)),
        5 or 6 => ImmutableList.Create((3, 2), (2, 3)),
        _ => ImmutableList.Create((3, 3)), // 7~9：3×3 是上限，没有第二种形状可换
    };

    /// <summary>换到 <c>ShapesFor</c> 里的下一个形状（到末项回到首项）。
    /// 当前形状不在候选里（例如来自预设菜单的 3×3 用在 2 路上）时，回到该路数的首项。</summary>
    public GridPartition NextShape()
    {
        var shapes = ShapesFor(Lanes);
        var index = -1;
        for (var i = 0; i < shapes.Count; i++)
            if (shapes[i].Cols == Cols && shapes[i].Rows == Rows) { index = i; break; }

        var next = shapes[(index + 1) % shapes.Count];
        return Uniform(Lanes, next.Cols, next.Rows);
    }

    /// <summary>路数变化时的收敛：形状不变则<b>保住用户拖好的权重</b>，形状变了才重置为均匀。
    /// <para>"形状不变"判的是 <see cref="Resolve"/> 在新路数下给出的形状与当前一致——
    /// 于是 2 路拖宽了左列、加到第 3 路时不会因为多出一条线而把之前的调整悄悄抹掉
    /// （3 路的自动形状是 3×1，与 2 路的 2×1 不同，此时确实只能重来）。</para></summary>
    public GridPartition Retune(int lanes, bool singleView = false, int presetCols = 0, int presetRows = 0)
    {
        var resolved = Resolve(lanes, singleView, presetCols, presetRows);
        if (resolved.Cols == Cols && resolved.Rows == Rows)
            return new GridPartition(lanes, Cols, Rows, ColumnWeights, RowWeights);
        return resolved;
    }

    private static ImmutableList<double> EqualWeights(int n)
    {
        var builder = ImmutableList.CreateBuilder<double>();
        for (var i = 0; i < n; i++) builder.Add(1.0);
        return builder.ToImmutable();
    }

    // ══════════ 几何 ══════════

    /// <summary>本分区真正发出去的格数 = <c>min(路数, Cols × Rows)</c>，与
    /// <see cref="ComputeCells"/> 的长度恒等（合并态下它就是路数）。
    /// <para>单独给一个属性而不是让调用方 <c>ComputeCells().Length</c>：格↔路映射、
    /// 互换 / 轮换、状态栏"只显示前 N 路"都要用这个数，为它专门分配一个数组是纯浪费。</para></summary>
    public int CellCount => Math.Min(Math.Max(0, Lanes), Cols * Rows);

    /// <summary>格表：长度 = <c>min(路数, Cols × Rows)</c>，索引即路号（第 i 路落在第 i 格）。
    ///
    /// <para><b>为什么格数按路数而不是按 <c>Cols×Rows</c> 给满</b>：下游
    /// <c>CompareGridView.ArrangeOverride</c> 把"格数 &lt; 路数"当作隐藏多余路的信号，
    /// <c>MainWindow.NormalizeRoute</c> 又按格数归一路↔格映射；若这里给出 6 格而只有 5 路，
    /// 第 6 格会占一个空位并让"轮换各格画面"把第 0 路重复排进去。给出与路数相等的格数，
    /// 合并态下"格数 ≡ 路数"恒成立 ⇒ 既有的隐藏/停用逻辑一次都不会触发（这正是合并要的效果：
    /// 网格基准就是<b>全部路都显示</b>）。</para></summary>
    public CellRect[] ComputeCells()
    {
        var colEdges = Edges(ColumnWeights);
        var rowEdges = Edges(RowWeights);
        var capacity = Cols * Rows;
        var count = Math.Min(Math.Max(0, Lanes), capacity);

        var cells = new CellRect[count];
        for (var i = 0; i < count; i++)
        {
            var col = i % Cols;
            var row = i / Cols;
            cells[i] = new CellRect(
                colEdges[col], rowEdges[row],
                colEdges[col + 1] - colEdges[col],
                rowEdges[row + 1] - rowEdges[row]);
        }
        return cells;
    }

    /// <summary>归一化边界：长度 <c>n+1</c>，首项 0、末项<b>写死 1.0</b>。
    /// 中间项按权重累加；单调性由"每个权重 ≥ <see cref="MinSpanFraction"/> × 权重和"保证，
    /// 末项写死则让浮点误差集中在<b>最后一条</b>边界上（表现为最右/最下一格差 1e-16），
    /// 而不是累积成一整条看不见的缝隙。</summary>
    private static double[] Edges(ImmutableList<double> weights)
    {
        var total = Total(weights);
        var edges = new double[weights.Count + 1];
        var acc = 0.0;
        for (var i = 0; i < weights.Count; i++)
        {
            edges[i] = acc;
            acc += weights[i] / total;
        }
        edges[^1] = 1.0;
        return edges;
    }

    private static double Total(ImmutableList<double> weights)
    {
        var sum = 0.0;
        for (var i = 0; i < weights.Count; i++) sum += weights[i];
        return sum > 0 && !double.IsNaN(sum) ? sum : 1.0;
    }

    // ══════════ 手柄（分割线） ══════════

    /// <summary>可拖动的分割线数量 = 内部竖线 + 内部横线（1×1 为 0 ⇒ 覆盖层不画手柄）。</summary>
    public int HandleCount => Math.Max(0, Cols - 1) + Math.Max(0, Rows - 1);

    /// <summary>第 <paramref name="k"/> 个手柄管的是竖线还是横线：前 <c>Cols-1</c> 个是竖线，
    /// 其后是横线。编号稳定 ⇒ 覆盖层可以据此给光标（<see cref="SplitAxis.X"/> 用左右箭头）。</summary>
    public SplitAxis HandleAxisAt(int k)
        => k < Cols - 1 ? SplitAxis.X : SplitAxis.Y;

    /// <summary>第 <paramref name="k"/> 个手柄的归一化位置。单轴线另一轴取 0.5（线中点），
    /// 与旧 <c>CompareLayout.HandlePositionAt</c> 的既有取向一致：把 0 交给 UI 会让手柄跑出可视区。</summary>
    public (double X, double Y) HandlePositionAt(int k)
    {
        if (k < Cols - 1) return (BoundaryFraction(Edges(ColumnWeights), k), 0.5);
        var row = k - (Cols - 1);
        return (0.5, BoundaryFraction(Edges(RowWeights), row));
    }

    /// <summary>全部手柄位置（覆盖层绘制 / 命中测试与自测探针都读它，避免两处各算一份）。</summary>
    public (double X, double Y)[] HandlePositions()
    {
        var count = HandleCount;
        var result = new (double X, double Y)[count];
        for (var i = 0; i < count; i++) result[i] = HandlePositionAt(i);
        return result;
    }

    private static double BoundaryFraction(double[] edges, int k) => edges[k + 1];

    /// <summary>把指针（0~1 归一化）应用到第 <paramref name="k"/> 条分割线上，返回新分区。
    ///
    /// <para><b>只改相邻两个权重</b>：这条线两侧的列（或行）之间搬运一段权重，总和不变，
    /// 于是该线之后的所有线位置不动——这是用户对"拖动分割线"的直觉（ICAT 的分屏拖动同语义）。</para>
    ///
    /// <para><b>两次钳制的顺序</b>：先按左/上侧的下界钳，再按右/下侧的下界钳。两者不会互相
    /// 推翻：不变量"<b>每个</b>权重 ≥ <see cref="MinSpanFraction"/> × 权重和"意味着这两列之和
    /// ≥ 2×下界，故 <c>min - w[k] ≤ w[k+1] - min</c>，第二次钳不会把第一次钳推回去。
    /// 不写这条顺序的后果是拖到极端时两列同时被压到下界、权重和改变、其它线整体漂移。</para>
    ///
    /// <para>非有限入参（NaN / Inf，来自未初始化的绑定值或除零）按该线当前位置处理 ⇒ 本方法
    /// 幂等，不会把 NaN 传进权重表（那会让 <see cref="ComputeCells"/> 产出 NaN 格，子 HWND
    /// 当场失步）。</para></summary>
    public GridPartition WithHandleDragged(int k, double pointerX, double pointerY)
    {
        if (k < 0 || k >= HandleCount) return this;

        var isColumn = k < Cols - 1;
        var weights = isColumn ? ColumnWeights : RowWeights;
        var pointer = isColumn ? pointerX : pointerY;
        if (!double.IsFinite(pointer)) return this;

        var total = Total(weights);
        var min = MinSpanFraction * total;
        var index = isColumn ? k : k - (Cols - 1);

        var current = BoundaryFraction(Edges(weights), index);
        if (!double.IsFinite(current)) return this;   // 权重已被脏数据污染：不在此处继续放大
        var delta = (Math.Clamp(pointer, 0.0, 1.0) - current) * total;

        var left = weights[index];
        var right = weights[index + 1];
        if (left + delta < min) delta = min - left;
        if (right - delta < min) delta = right - min;

        var next = weights.SetItem(index, left + delta).SetItem(index + 1, right - delta);
        return new GridPartition(Lanes, Cols, Rows,
            isColumn ? next : ColumnWeights,
            isColumn ? RowWeights : next);
    }

    /// <summary>第一条内部边界的位置（0~1）。
    /// <para>"左右拉动对比"是只有一路的两格揭示，它的分割比例就是这一个数；合并态下 2 路默认
    /// 形状 2×1 恰好给出它，于是拉动与拖动分格共用同一份几何，不再需要旧那份只有两个自由度的 <c>SplitParams</c>。</para>
    /// <para>无内部边界（1×1）时返回 0.5：调用方此时本就不该画线，给确定值比抛异常好。</para></summary>
    public double PrimaryBoundary()
    {
        if (Cols > 1) return BoundaryFraction(Edges(ColumnWeights), 0);
        if (Rows > 1) return BoundaryFraction(Edges(RowWeights), 0);
        return 0.5;
    }

    /// <summary>值相等（供"原地抖动不产生事件风暴"的守卫使用：拖动热路径逐帧比较新旧分区）。
    /// 按权重逐项比较，容差 <see cref="Epsilon"/>。</summary>
    public bool SameShape(GridPartition? other)
        => other is not null
        && other.Lanes == Lanes && other.Cols == Cols && other.Rows == Rows
        && SameWeights(other.ColumnWeights, ColumnWeights)
        && SameWeights(other.RowWeights, RowWeights);

    private static bool SameWeights(ImmutableList<double> a, ImmutableList<double> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (Math.Abs(a[i] - b[i]) > Epsilon) return false;
        return true;
    }
}
