using System.Collections.Immutable;

namespace _3FCompare.Core.Display;

/// <summary>拼接里的一块：归一化矩形 + 它显示第几路（槽号）。
/// <para>槽号允许<b>重复</b>：同一路可以占多块（用户决策，见 <c>docs/51 §八</c>）。</para></summary>
public readonly record struct MosaicTile(CellRect Rect, int Route);

/// <summary>一条<b>可拖分割线段</b>。
///
/// <para><b>为什么是"线段"而不是"整线"</b>：这是本模型与 <see cref="GridPartition"/> 的根本区别。
/// 网格模型里一条竖线必然贯穿整个容器（因为每列有统一宽度），于是 3×3 只有 4 条线。
/// 自由拼接允许 <b>T 形交叉</b> —— 例如上半分成两块、下半是一整块，此时中间那条竖线
/// 只存在于上半（<c>From</c>/<c>To</c> 表达它的覆盖区间），下半没有它。
/// 若仍按"整线"表达，拖动上半那条竖线就会把下半一起切开，与用户预期不符。</para>
///
/// <para><b>位置语义</b>：<see cref="Position"/> 是分割轴上的归一化位置（0~1）；
/// <see cref="From"/>/<see cref="To"/> 是<b>另一轴</b>上的覆盖区间（同样是 0~1）。
/// 水平方向（竖线）时 Position 是 X、From/To 是 Y 区间；垂直方向（横线）时反之。</para></summary>
public readonly record struct MosaicSplit(SplitAxis Axis, double Position, double From, double To);

/// <summary>自由拼接布局：若干互不重叠、并集恰好为 <c>[0,1]²</c> 的矩形，每个带一个槽号。
///
/// <para><b>它解决什么</b>：<see cref="GridPartition"/> 是"每列/每行一个权重"的<b>均匀网格</b>
/// 模型，只能表达整行整列贯穿的 m×n。用户要求"横竖自由组合、允许不对称（一块横一块竖混排）、
/// 任意自由拼接（含 T 形交叉）"—— 那超出了权重模型的表达能力，故新增本模型
/// （设计依据见 <c>docs/51 §十</c>）。</para>
///
/// <para><b>什么已经能复用</b>（这是本模型只需管几何的原因）：
/// 绘制层 <c>LayoutOverlayWindow.Render</c> 本来就是"逐块取右/下边当线段"，
/// 天然支持 T 形交叉与断线；区域下发 <c>WindowRegionClipper.ApplyRects</c> 用
/// <c>CombineRgn(OR)</c> 合并任意多个矩形。故本类只需产出<b>块表</b>与<b>线段表</b>。</para>
///
/// <para><b>三条不变式由构造保证</b>（不靠事后钳制，与 <see cref="GridPartition"/> 同一哲学）：
/// <list type="number">
/// <item><description><b>两两不重叠</b>：<see cref="Split"/> 只把一块切成两块互补子块，
/// 其余块原样保留 ⇒ 划分关系恒成立。</description></item>
/// <item><description><b>并集恰好铺满 <c>[0,1]²</c></b>：初始单块即整窗，切分保面积。</description></item>
/// <item><description><b>每块宽高恒 &gt; 0</b>：切分点被钳到 <see cref="MinSpanFraction"/> × 父块尺寸，
/// 两侧都留得住最小宽度。0 宽块会让 <c>CreateRectRgn</c> 得到空区域、<c>SetWindowRgn</c> 失败
/// （窗口整块不可见）。</description></item>
/// </list></para>
///
/// <para>不可变：每次切分/拖动产生新实例。</para></summary>
public sealed class MosaicLayout
{
    /// <summary>任何一块允许收缩到的最小尺寸（占<b>整个容器</b>的比例，不是占父块）。
    ///
    /// <para>取 <see cref="GridPartition.MinSpanFraction"/> 同值（0.05）的理由一致：
    /// 4K 宽度下约 192px，既是肉眼可辨的最小块，又给手柄留出命中区。
    /// 按"整个容器"而不是"父块"来定下界，是为了让<b>嵌得很深的块</b>也不会缩到不可用 ——
    /// 若按父块比例，切三层之后最内层可以小到 0.05³ ≈ 0.01%。</para></summary>
    public const double MinSpanFraction = GridPartition.MinSpanFraction;

    /// <summary>块表（按构造顺序；顺序稳定，便于判据与序列化往返比对）。</summary>
    public ImmutableArray<MosaicTile> Tiles { get; }

    /// <summary>探测用的槽号标记：用于合成一个"每块各占一槽"的初始布局。</summary>
    private MosaicLayout(ImmutableArray<MosaicTile> tiles) => Tiles = tiles;

    /// <summary>整窗单块（槽号 0）。这是自由拼接的起点。</summary>
    public static MosaicLayout Single() =>
        new(ImmutableArray.Create(new MosaicTile(new CellRect(0, 0, 1, 1), 0)));

    /// <summary>按给定块表构造（调用方保证不变式；主要供反序列化与 <see cref="FromGridPartition"/>）。
    /// <para>会做一次校验：块数为 0、或存在非正尺寸块、或块间重叠时抛出 ——
    /// 宁可在构造点失败，也不要把一个坏布局发给 <c>SetWindowRgn</c>（那会让窗口整块不可见，
    /// 而表现是"画面没了"，极难反推到根因）。</para></summary>
    public static MosaicLayout FromTiles(IEnumerable<MosaicTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        var arr = tiles.ToImmutableArray();
        if (arr.Length == 0) throw new ArgumentException("拼接至少要有一块", nameof(tiles));

        for (var i = 0; i < arr.Length; i++)
        {
            var r = arr[i].Rect;
            if (!(r.Width > 0) || !(r.Height > 0))
                throw new ArgumentException($"第 {i} 块尺寸非正：{r.Width}x{r.Height}", nameof(tiles));
            if (!IsFinite(r))
                throw new ArgumentException($"第 {i} 块含 NaN/Inf：{r}", nameof(tiles));
        }

        // 两两不重叠（用容差比较，避免浮点边界误判）
        for (var i = 0; i < arr.Length; i++)
            for (var j = i + 1; j < arr.Length; j++)
                if (Overlaps(arr[i].Rect, arr[j].Rect))
                    throw new ArgumentException(
                        $"第 {i} 块与第 {j} 块重叠：{arr[i].Rect} / {arr[j].Rect}", nameof(tiles));

        return new MosaicLayout(arr);
    }

    /// <summary>从既有网格分区继承（"从网格切到自由拼接"时保住用户已拖好的分格）。
    /// <para>槽号取恒等（第 i 块显示第 i 路），与网格的"第 i 格第 i 路"同向。</para></summary>
    public static MosaicLayout FromGridPartition(GridPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var cells = partition.ComputeCells();
        if (cells.Length == 0) return Single();
        var builder = ImmutableArray.CreateBuilder<MosaicTile>(cells.Length);
        for (var i = 0; i < cells.Length; i++) builder.Add(new MosaicTile(cells[i], i));
        return new MosaicLayout(builder.MoveToImmutable());
    }

    /// <summary>把第 <paramref name="tileIndex"/> 块按 <paramref name="axis"/> 切成两块。
    ///
    /// <para><paramref name="fraction"/> 是**相对该块**的切分位置（0~1）。它会被钳到
    /// <c>[MinSpanFraction, 1 - MinSpanFraction]</c>，保证两侧都 ≥ 最小可见尺寸。</para>
    ///
    /// <para>新块的槽号：<paramref name="newRoute"/> 为 null 时取"当前<b>未被占用</b>的最小槽号"
    /// （找不到就沿用被切块的槽号）。<b>为什么默认不是"沿用父块槽号"</b>：
    /// 用户在编辑器里切一刀，期望的是"这一块变成两块、各看一路"；
    /// 若两块都沿用父槽号，切开后画面完全没变化（两块显示同一路），读起来就是"切了没反应" ——
    /// 那正是本项目最忌讳的静默无效果。给定值就用它（供"先切开、稍后再指定"的用法）。</para></summary>
    public MosaicLayout Split(int tileIndex, SplitAxis axis, double fraction, int? newRoute = null)
    {
        if (axis == SplitAxis.Both)
            throw new ArgumentException("切分轴必须是 X 或 Y（Both 是十字交叉点，无切分语义）", nameof(axis));
        if (tileIndex < 0 || tileIndex >= Tiles.Length)
            throw new ArgumentOutOfRangeException(nameof(tileIndex),
                $"块号越界：{tileIndex}（共 {Tiles.Length} 块）");

        var src = Tiles[tileIndex];
        var box = src.Rect;

        // fraction 相对该块；非有限值退回 0.5（不让 NaN 进几何 —— 它会让 CreateRectRgn
        // 收到翻转矩形，表现为"调用成功但窗口不可见"）。
        var f = double.IsFinite(fraction) ? Math.Clamp(fraction, 0.0, 1.0) : 0.5;

        // 钳到最小尺寸：下界按**整个容器**（见 MinSpanFraction 注释），换算到该块的相对比例。
        //
        // ⚠ 这里**不能**写成 `Math.Min(0.5, MinSpanFraction / span)`。
        //   那个写法在 `span ≤ 2 × MinSpanFraction` 时把下界**封顶到 0.5** ⇒
        //   继续切就退化成"对半劈"，块一路变小、突破文档承诺的 5%
        //   （审查轮实测：连切 12 次可到 0.000024 = 容器的 0.0024%）。
        //   块小到 1px 以下时区域根本表达不出来（`PlanRegionsFor` 会跳过该块）
        //   ⇒ 表现为"某一块在屏幕上彻底消失"，而用户只是多点了几下切分。
        //
        //   正确做法：父块**本来就小于**最小尺寸时，**拒绝切分**（返回 this）。
        //   这与"宁可这一层不跟手，也不产生坏几何"的取向一致：
        //   用户的后果是"这块已经太小、切不动"，而不是"切了一下、画面少一块"。
        var span = axis == SplitAxis.X ? box.Width : box.Height;
        if (span < 2 * MinSpanFraction) return this;     // 切了必然有一块 < 下界 ⇒ 拒绝
        var minRel = MinSpanFraction / span;             // span ≥ 2×Min ⇒ minRel ≤ 0.5，安全
        f = Math.Clamp(f, minRel, 1.0 - minRel);

        var route = newRoute ?? NextFreeRoute(src.Route);
        var builder = ImmutableArray.CreateBuilder<MosaicTile>(Tiles.Length + 1);
        for (var i = 0; i < Tiles.Length; i++)
        {
            if (i != tileIndex) { builder.Add(Tiles[i]); continue; }

            if (axis == SplitAxis.X)
            {
                var w1 = box.Width * f;
                var w2 = box.Width - w1;
                builder.Add(new MosaicTile(new CellRect(box.X, box.Y, w1, box.Height), src.Route));
                builder.Add(new MosaicTile(new CellRect(box.X + w1, box.Y, w2, box.Height), route));
            }
            else
            {
                var h1 = box.Height * f;
                var h2 = box.Height - h1;
                builder.Add(new MosaicTile(new CellRect(box.X, box.Y, box.Width, h1), src.Route));
                builder.Add(new MosaicTile(new CellRect(box.X, box.Y + h1, box.Width, h2), route));
            }
        }
        return new MosaicLayout(builder.MoveToImmutable());
    }

    /// <summary>合并：把第 <paramref name="tileIndex"/> 块并入与它<b>共边且同槽号</b>的邻块，
    /// 还原成合并前的那个矩形（切分的逆运算）。
    ///
    /// <para><b>只在"两块拼起来恰好是一个矩形"时才合并</b>（即完全共边、且另一轴区间相同）——
    /// 否则合并结果不是矩形，无法用本模型的 <see cref="CellRect"/> 表达。
    /// 找不到这样的邻块时返回 <c>this</c>（幂等，不静默产生非法几何）。</para>
    ///
    /// <para>用"同槽号"作为可合并条件，是为了让编辑器的"删掉这道线"语义直观：
    /// 两侧显示同一路时才谈得上"去掉中间的线"。</para></summary>
    public MosaicLayout Merge(int tileIndex)
    {
        if (tileIndex < 0 || tileIndex >= Tiles.Length) return this;
        var a = Tiles[tileIndex];

        for (var j = 0; j < Tiles.Length; j++)
        {
            if (j == tileIndex) continue;
            var b = Tiles[j];
            if (a.Route != b.Route) continue;

            // 左右相邻：Y 区间相同、X 首尾相接
            if (NearlyEqual(a.Rect.Y, b.Rect.Y) && NearlyEqual(a.Rect.Height, b.Rect.Height) &&
                (NearlyEqual(a.Rect.X + a.Rect.Width, b.Rect.X) || NearlyEqual(b.Rect.X + b.Rect.Width, a.Rect.X)))
            {
                var x = Math.Min(a.Rect.X, b.Rect.X);
                var w = a.Rect.Width + b.Rect.Width;
                return ReplacePair(Math.Min(tileIndex, j), Math.Max(tileIndex, j),
                    new MosaicTile(new CellRect(x, a.Rect.Y, w, a.Rect.Height), a.Route));
            }

            // 上下相邻：X 区间相同、Y 首尾相接
            if (NearlyEqual(a.Rect.X, b.Rect.X) && NearlyEqual(a.Rect.Width, b.Rect.Width) &&
                (NearlyEqual(a.Rect.Y + a.Rect.Height, b.Rect.Y) || NearlyEqual(b.Rect.Y + b.Rect.Height, a.Rect.Y)))
            {
                var y = Math.Min(a.Rect.Y, b.Rect.Y);
                var h = a.Rect.Height + b.Rect.Height;
                return ReplacePair(Math.Min(tileIndex, j), Math.Max(tileIndex, j),
                    new MosaicTile(new CellRect(a.Rect.X, y, a.Rect.Width, h), a.Route));
            }
        }
        return this;   // 没有可合并的邻块
    }

    private MosaicLayout ReplacePair(int lo, int hi, MosaicTile merged)
    {
        var builder = ImmutableArray.CreateBuilder<MosaicTile>(Tiles.Length - 1);
        for (var i = 0; i < Tiles.Length; i++)
        {
            if (i == hi) continue;             // hi 被并掉
            if (i == lo) { builder.Add(merged); continue; }
            builder.Add(Tiles[i]);
        }
        return new MosaicLayout(builder.MoveToImmutable());
    }

    /// <summary>设定某块的槽号（第几路）。编辑器里"这一块看哪一路"就是这一步。
    /// <para>越界槽号被钳到 <c>[0, max(0, routeCount-1)]</c>：钳而非抛，
    /// 因为路数变化（关掉一路）的中途会短暂出现"槽号比路数大"的过渡态。</para></summary>
    public MosaicLayout WithRoute(int tileIndex, int route, int routeCount)
    {
        if (tileIndex < 0 || tileIndex >= Tiles.Length) return this;
        var max = Math.Max(0, routeCount - 1);
        var r = Math.Clamp(route, 0, max);
        if (Tiles[tileIndex].Route == r) return this;
        return new MosaicLayout(Tiles.SetItem(tileIndex, Tiles[tileIndex] with { Route = r }));
    }

    /// <summary>全部可拖分割线段。
    ///
    /// <para><b>怎么从块表推出来</b>（与绘制层同一口径，见 <c>LayoutOverlayWindow.Render</c>）：
    /// 逐块取它的<b>右边界</b>与<b>下边界</b>（只在该边不贴容器外沿时），
    /// 相邻块的边自然拼成一条线段。T 形交叉因此自动正确：上半两块各发一段右边界，
    /// 拼起来只覆盖上半；下半是整块、不发右边界 ⇒ 那条竖线不会贯穿到下半。</para>
    ///
    /// <para>返回的线段已按（轴, 位置, 起点）排序并合并共线相邻段，
    /// 于是"一条 T 形竖线"在 <c>y</c> 上是一段连续的区间而不是两段零碎。</para></summary>
    public ImmutableArray<MosaicSplit> Splits()
    {
        // 收集候选边（右/下），再按 (轴, 位置) 聚合成线段
        var vert = new List<(double X, double Y0, double Y1)>();   // 竖线
        var horz = new List<(double Y, double X0, double X1)>();   // 横线

        foreach (var t in Tiles)
        {
            var r = t.Rect;
            if (r.X + r.Width < 1.0 - GridPartition.Epsilon)
                vert.Add((r.X + r.Width, r.Y, r.Y + r.Height));
            if (r.Y + r.Height < 1.0 - GridPartition.Epsilon)
                horz.Add((r.Y + r.Height, r.X, r.X + r.Width));
        }

        var result = ImmutableArray.CreateBuilder<MosaicSplit>();
        foreach (var (pos, from, to) in MergeCollinear(vert.Select(v => (v.X, v.Y0, v.Y1))))
            result.Add(new MosaicSplit(SplitAxis.X, pos, from, to));
        foreach (var (pos, from, to) in MergeCollinear(horz.Select(h => (h.Y, h.X0, h.X1))))
            result.Add(new MosaicSplit(SplitAxis.Y, pos, from, to));
        return result.ToImmutable();
    }

    /// <summary>把同一条直线上的相邻区间合并成连续段（容差比较位置与端点）。
    /// <para>不做合并的话，T 形竖线会被报成"两段各覆盖一块"，调用方（命中/拖动）
    /// 就得自己再合一次 —— 那种"两处各写一份"正是几何漂移的温床。</para></summary>
    private static IEnumerable<(double Pos, double From, double To)> MergeCollinear(
        IEnumerable<(double Pos, double From, double To)> edges)
    {
        foreach (var group in edges.GroupBy(e => Math.Round(e.Pos, 9)).OrderBy(g => g.Key))
        {
            var segs = group.OrderBy(e => e.From).ToList();
            var start = segs[0].From;
            var end = segs[0].To;
            for (var i = 1; i < segs.Count; i++)
            {
                if (segs[i].From <= end + GridPartition.Epsilon)
                {
                    end = Math.Max(end, segs[i].To);
                    continue;
                }
                yield return (group.Key, start, end);
                start = segs[i].From;
                end = segs[i].To;
            }
            yield return (group.Key, start, end);
        }
    }

    /// <summary>含 T 形交叉时，同一条"整线"可能由多段组成 —— 本方法把它视为一条"逻辑线"，
    /// 于是拖动可以一次带动整条线（用户视觉上看到的是连贯的一条）。
    ///
    /// <para><b>为什么按位置（而非按段）分组</b>：用户拖的是"那条线"，不是"其中一段"。
    /// 若按段独立拖，一条 T 形竖线的上下两段会各自跑开，视觉上断开。</para>
    ///
    /// <para>返回长度 = 不同位置数；每项为该位置上的位置值与覆盖区间并集。</para></summary>
    public ImmutableArray<(double Position, SplitAxis Axis, double From, double To)> LogicalLines()
    {
        var splits = Splits();
        var builder = ImmutableArray.CreateBuilder<(double, SplitAxis, double, double)>();
        foreach (var g in splits.GroupBy(s => (s.Axis, Math.Round(s.Position, 9))).OrderBy(g => g.Key.Axis).ThenBy(g => g.Key.Item2))
        {
            var from = g.Min(s => s.From);
            var to = g.Max(s => s.To);
            builder.Add((g.Key.Item2, g.Key.Axis, from, to));
        }
        return builder.ToImmutable();
    }

    /// <summary>拖动一条<b>逻辑线</b>（同位置的全部线段一起动）到新的归一化位置。
    ///
    /// <para><b>必须先找到这条线"真正分开的是哪两列块"</b>，而不是"所有边落在这个位置的块"。
    /// 后者是错的，而且错得很隐蔽：考虑"左侧一整条 + 右侧又横切成上下两块"的形态，
    /// 位置 0.3 处既有左条块的右边界、又有右上/右下块的左边界；
    /// 若把它们各自按同一个 delta 平移，左条会一直长到覆盖右上块 —— 两块<b>重叠</b>。
    /// （这个缺陷是随机化单测抓出来的：trial=4 时块 0 长到 0.95 而块 1 仍在 0.613，重叠。）
    ///
    /// <para><b>正确做法</b>：按线段覆盖区间逐段处理。对区间内每一个"另一轴上的 y 层"
    /// （由参与块的 y 边界切出），找出该层里<b>紧邻这条线左右</b>的两块，
    /// 把宽度从一侧搬到另一侧。这样每次搬运都在<b>同一层内</b>完成，
    /// 层的上下边界不动 ⇒ 不重叠与铺满继续由构造保证。</para>
    ///
    /// <para>位置被钳到 <c>[MinSpanFraction, 1 - MinSpanFraction]</c>（按整个容器），
    /// 避免把某块压成 0 宽 —— 0 宽区域会让 <c>SetWindowRgn</c> 失败、窗口整块不可见。
    /// 若钳制后位置没变则返回 <c>this</c>，调用方据此避免无谓重排。</para></summary>
    /// <param name="axis">线段轴向（X = 竖线，Y = 横线）。</param>
    /// <param name="oldPosition">该线当前的位置（用以认定"哪些块的边落在这条线上"）。</param>
    /// <param name="newPosition">目标位置（0~1）。</param>
    /// <param name="from">线段覆盖区间的起点（另一轴，0~1）。</param>
    /// <param name="to">线段覆盖区间的终点（另一轴，0~1）。</param>
    public MosaicLayout DragLine(SplitAxis axis, double oldPosition, double newPosition, double from, double to)
    {
        if (axis == SplitAxis.Both) return this;
        if (!double.IsFinite(newPosition)) return this;      // NaN 不进几何
        var oldPos = double.IsFinite(oldPosition) ? oldPosition : 0.5;
        var lo = double.IsFinite(from) ? Math.Min(from, to) : 0.0;
        var hi = double.IsFinite(from) ? Math.Max(from, to) : 1.0;

        var clamped = Math.Clamp(newPosition, MinSpanFraction, 1.0 - MinSpanFraction);
        if (NearlyEqual(clamped, oldPos)) return this;
        var delta = clamped - oldPos;

        // 先全部算出再一次性应用：避免"前一对块的修改影响后一对块的匹配"
        var newRects = new CellRect[Tiles.Length];
        for (var i = 0; i < Tiles.Length; i++) newRects[i] = Tiles[i].Rect;

        // ★ 只处理"干净层"：一对块（线左 + 线右）必须在**另一轴**上的区间完全一致，
        //   否则说明这一层有块跨线，搬运会让它与邻块重叠（随机化单测 trial=12 抓到过）。
        //   宁可这一层不跟手，也不产生重叠或 0 宽块。
        var touched = false;
        for (var li = 0; li < Tiles.Length; li++)
        {
            var lr = newRects[li];
            var lMainEnd = axis == SplitAxis.X ? lr.X + lr.Width : lr.Y + lr.Height;
            var lCross0 = axis == SplitAxis.X ? lr.Y : lr.X;
            var lCross1 = axis == SplitAxis.X ? lr.Y + lr.Height : lr.X + lr.Width;
            if (!NearlyEqual(lMainEnd, oldPos)) continue;                 // 不是线左块
            if (!OverlapsRange(lCross0, lCross1, lo, hi)) continue;       // 不在线段覆盖内

            for (var ri = 0; ri < Tiles.Length; ri++)
            {
                if (ri == li) continue;
                var rr = newRects[ri];
                var rMainStart = axis == SplitAxis.X ? rr.X : rr.Y;
                var rCross0 = axis == SplitAxis.X ? rr.Y : rr.X;
                var rCross1 = axis == SplitAxis.X ? rr.Y + rr.Height : rr.X + rr.Width;
                if (!NearlyEqual(rMainStart, oldPos)) continue;           // 不是线右块
                if (!NearlyEqual(rCross0, lCross0) || !NearlyEqual(rCross1, lCross1)) continue;

                var lSpan = axis == SplitAxis.X ? lr.Width : lr.Height;
                var rSpan = axis == SplitAxis.X ? rr.Width : rr.Height;
                if (lSpan + delta < MinSpanFraction - 1e-12) continue;
                if (rSpan - delta < MinSpanFraction - 1e-12) continue;

                newRects[li] = axis == SplitAxis.X
                    ? new CellRect(lr.X, lr.Y, lSpan + delta, lr.Height)
                    : new CellRect(lr.X, lr.Y, lr.Width, lSpan + delta);
                newRects[ri] = axis == SplitAxis.X
                    ? new CellRect(rr.X + delta, rr.Y, rSpan - delta, rr.Height)
                    : new CellRect(rr.X, rr.Y + delta, rr.Width, rSpan - delta);
                touched = true;
                break;   // 一个线左块只配一个线右块
            }
        }

        if (!touched) return this;

        var builder = ImmutableArray.CreateBuilder<MosaicTile>(Tiles.Length);
        for (var i = 0; i < Tiles.Length; i++) builder.Add(Tiles[i] with { Rect = newRects[i] });
        return new MosaicLayout(builder.MoveToImmutable());
    }

    private static bool RectsEqual(CellRect a, CellRect b) =>
        NearlyEqual(a.X, b.X) && NearlyEqual(a.Y, b.Y) &&
        NearlyEqual(a.Width, b.Width) && NearlyEqual(a.Height, b.Height);

    /// <summary>两个一维区间是否有交（半开，容差比较）。</summary>
    private static bool OverlapsRange(double a0, double a1, double b0, double b1) =>
        a0 < b1 - GridPartition.Epsilon && b0 < a1 - GridPartition.Epsilon;

    /// <summary>把某个槽号占据的全部块换算成<b>窗口相对、物理像素</b>的区域集合。
    ///
    /// <para><b>坐标语义（易错，务必对齐）</b>：区域是<b>窗口相对</b>坐标 ——
    /// <c>SetWindowRgn</c> 的矩形以窗口左上角为原点（见 <c>WindowRegionClipper</c> 的类型注释）。
    /// 而块是<b>容器相对</b>的归一化矩形 ⇒ 换算时必须<b>减去窗口原点</b>（<paramref name="windowPx"/>.X/Y），
    /// 而不是加上它。这与 <c>CompareCropPlanner.Plan</c> 的
    /// <c>region = cellPx - windowPx</c> 是同一套口径。
    /// ⚠ 本方法第一版写成了"加窗口偏移"，于是区域整体偏移了窗口原点（实测 2,2），
    /// 表现是"画面露出一条父窗底色/裁错位置"——由真机判据抓出。</para>
    ///
    /// <para><b>三条不变式沿用 <see cref="WipeBands.PlanRegions"/> 的口径</b>：
    /// 每块至少 1px（0 尺寸区域会被 <c>CreateRectRgn</c> 当空区域、<c>SetWindowRgn</c> 直接失败
    /// ⇒ 窗口整块不可见）。与带表不同的是这里<b>不做"铺满整窗"的保证</b> ——
    /// 那是整份拼接的性质（见 <see cref="FromTiles"/> 的校验），不是单路区域的性质。</para>
    ///
    /// <para><b>去重</b>：同一槽号占相邻块时，两块并集可能本身就是个矩形；
    /// 这里不合并（交给 <c>WindowRegionClipper.NormalizeRects</c> 统一处理），
    /// 于是本方法只做"坐标换算"这一件事，合并规则只有一处。</para></summary>
    /// <para><b>⚠ 取整必须走"共享边界表"，不能逐块各自四舍五入</b>（这是本方法最重要的一条）：
    /// 相邻块在归一化空间里<b>数学上恰好共享一条边</b>，但两侧的表达式不同
    /// （<c>A + B</c> vs <c>0.95</c>），浮点结果会差约 <c>1e-16</c>。
    /// 当那条边乘以窗口尺寸后<b>恰好落在 <c>.5</c> 平局上</b>时，这 <c>1e-16</c> 会决定
    /// 两侧向<b>相反方向</b>取整 ⇒ 中间空出整整一行像素（或反过来重叠一行）。
    /// 实测反例（deterministic）：`0.8341561705991691 + 0.11584382940083071 = 0.9499999999999998`
    /// 而下一块起点是 `0.95`，在 h=2450 时分别取整到 2327 / 2328 ⇒ <b>第 2327 行无人覆盖</b>
    /// （整条 401px 宽都露出父窗底色）。频率不低：固定一份布局、在 [200,2400] 的 2201 个高度里
    /// **有 110 个高度会露缝**（即拖动窗口大小时时有时无），20k 份随机布局里约 1 份中招。
    ///
    /// <para>修法：**先把该轴所有块的边界位置收成一个去重、排序的整数表**，
    /// 再让每块的像素区间从这张表里取（<c>[idx(lo), idx(hi))</c>）。
    /// 于是"共享同一条归一化边"的两块**必然取到同一个整数**，缝与重叠都不可能产生 ——
    /// 这是从构造上消除，而不是靠事后钳制。</para>
    ///
    /// <para><b>为什么原来的面积判据抓不到它</b>：缝宽 <c>g</c> 只让面积和少 <c>g</c>，
    /// 而面积容差是 <c>1e-6</c> ⇒ <c>1e-16</c> 级的缝对面积判据完全不可见
    /// （实测：<c>1e-6</c> 级缝能检出，<c>1e-7</c> 级不能）。
    /// 换句话说面积判据在结构上就不可能发现这一级缺陷 —— 这正是它被漏掉的原因。</para>

    /// <param name="windowPx">该路窗口在<b>容器</b>坐标下的物理像素矩形（宽高须 &gt; 0）。
    /// 其 X/Y 用于把容器坐标换算到窗口相对坐标。</param>
    /// <param name="route">槽号（第几路）。</param>
    /// <returns>区域数组；该槽号不占任何块、或窗口非法时返回空数组。</returns>
    public Rect32[] PlanRegionsFor(Rect32 windowPx, int route)
    {
        if (!windowPx.IsValid) return Array.Empty<Rect32>();

        // 共享边界表（每轴一张）：所有块的 x/y 边界取整后去重排序。
        // ⚠ 必须**在循环外**建一次，且所有块都从同一张表取端点 —— 这是消除缝/重叠的关键。
        var xCuts = BuildPixelCuts(Tiles.Select(t => t.Rect.X), Tiles.Select(t => t.Rect.X + t.Rect.Width), windowPx.Width);
        var yCuts = BuildPixelCuts(Tiles.Select(t => t.Rect.Y), Tiles.Select(t => t.Rect.Y + t.Rect.Height), windowPx.Height);

        var builder = ImmutableArray.CreateBuilder<Rect32>();
        foreach (var t in Tiles)
        {
            if (t.Route != route) continue;
            var r = t.Rect;

            // 从共享表里取端点：同一归一化边 ⇒ 同一整数（构造上保证无缝无叠）
            var x0 = xCuts.At(r.X * windowPx.Width);
            var x1 = xCuts.At((r.X + r.Width) * windowPx.Width);
            var y0 = yCuts.At(r.Y * windowPx.Height);
            var y1 = yCuts.At((r.Y + r.Height) * windowPx.Height);

            var w = x1 - x0;
            var h = y1 - y0;
            // 每块至少 1px：不足以表达时跳过该块（调用方据此决定是否不裁剪），
            // 绝不产生 0 宽区域让 SetWindowRgn 失败。
            if (w < 1 || h < 1) continue;

            // ⚠ 窗口相对：**不减 X/Y** —— 因为块是容器归一化坐标，而窗口在拼合/拉动下铺满容器，
            //    两者原点重合。下面这行刻意写成 x0/y0 而不做偏移，避免"加偏移"的经典错误。
            builder.Add(new Rect32(x0, y0, w, h));
        }
        return builder.ToImmutable().ToArray();
    }

    /// <summary>一根轴上的<b>共享像素边界表</b>：把该轴所有块的边界位置取整、去重、排序，
    /// 之后所有块都从这张表里取端点。
    ///
    /// <para><b>它存在的唯一理由：消除"独立取整"造成的缝与重叠</b>（见
    /// <see cref="PlanRegionsFor"/> 的注释）。相邻块在归一化空间共享一条边，
    /// 但两侧表达式不同、浮点差约 <c>1e-16</c>；当 <c>边 × 窗口尺寸</c> 恰好落在 <c>.5</c> 平局上时，
    /// 这个差会让两侧向相反方向取整 ⇒ 空出一整行像素。
    /// 走共享表后，"同一条归一化边"**必然**映射到同一个整数 ⇒ 缝/叠在构造上不可能出现。</para>
    ///
    /// <para>查找用"最近边界"（容差 = 0.5px）而不是精确相等：调用方传进来的是
    /// <c>r.X * width</c> 这样的浮点乘积，与建表时的同一个乘积在<b>同一表达式</b>下必然相等，
    /// 但相邻块的 <c>(r.X + r.Width) * width</c> 与另一块的 <c>r.X * width</c> 会差 1e-13 量级
    /// ⇒ 必须容差匹配，否则又回到"各自取整"的老问题。</para></summary>
    private readonly struct PixelCuts
    {
        private readonly int[] _cuts;
        public PixelCuts(int[] cuts) => _cuts = cuts;

        /// <summary>把该轴上的像素位置吸附到最近的共享边界；越界时钳到两端。</summary>
        public int At(double px)
        {
            if (_cuts.Length == 0) return (int)Math.Round(px, MidpointRounding.AwayFromZero);

            // 二分找最近的边界（表已排序）。容差 0.5px ⇒ 与"四舍五入到格"同量级。
            var lo = 0;
            var hi = _cuts.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_cuts[mid] < px) lo = mid + 1; else hi = mid;
            }
            // lo 是第一个 >= px 的位置；与 lo-1 比谁更近
            var best = _cuts[lo];
            if (lo > 0 && Math.Abs(_cuts[lo - 1] - px) <= Math.Abs(best - px)) best = _cuts[lo - 1];
            return best;
        }
    }

    private static PixelCuts BuildPixelCuts(IEnumerable<double> starts, IEnumerable<double> ends, int size)
    {
        var set = new SortedSet<int>();
        if (size > 0)
        {
            foreach (var v in starts) set.Add(ClampPx(Math.Round(v * size, MidpointRounding.AwayFromZero), size));
            foreach (var v in ends) set.Add(ClampPx(Math.Round(v * size, MidpointRounding.AwayFromZero), size));
        }
        // 两端必须存在，否则端点吸附会跑出窗口
        set.Add(0);
        if (size > 0) set.Add(size);
        return new PixelCuts(set.ToArray());
    }

    private static int ClampPx(double v, int size)
    {
        if (double.IsNaN(v)) return 0;
        if (v < 0) return 0;
        if (v > size) return size;
        return (int)v;
    }

    /// <summary>当前<b>未被占用</b>的最小槽号；全被占用时返回 <paramref name="fallback"/>。
    ///
    /// <para><b>为什么按"未被占用的最小号"而不是"count()+1"</b>：用户可能已经把中间的某个槽号
    /// 删掉（合并）了，此时 count()+1 会跳号，而"最小未占用"能把空出来的号复用上 ——
    /// 界面上看到的就是"1 2 3"而不是"1 2 4"。</para></summary>
    private int NextFreeRoute(int fallback)
    {
        var used = new HashSet<int>();
        foreach (var t in Tiles) used.Add(t.Route);
        for (var r = 0; r < used.Count + 1; r++)
            if (!used.Contains(r)) return r;
        return fallback;
    }

    /// <summary>槽号 → 它占据的全部块号。
    /// <para>同一路占多块（含不相邻）时，区域 = 这些块的并集；
    /// <b>不相邻重复需要镜像会话</b>（一个 HWND 只能在一个位置），见 <c>docs/51 §九</c>。</para></summary>
    public ImmutableArray<int> TilesOfRoute(int route)
    {
        var builder = ImmutableArray.CreateBuilder<int>();
        for (var i = 0; i < Tiles.Length; i++)
            if (Tiles[i].Route == route) builder.Add(i);
        return builder.ToImmutable();
    }

    /// <summary>用到的不同槽号数（= 需要几条带 / 几路参与）。</summary>
    public int RouteCount => Tiles.Select(t => t.Route).Distinct().Count();

    /// <summary>值相等（编辑器"预览是否变化"与撤销栈去重都要它）。</summary>
    public bool SameAs(MosaicLayout? other)
    {
        if (other is null || other.Tiles.Length != Tiles.Length) return false;
        for (var i = 0; i < Tiles.Length; i++)
        {
            if (Tiles[i].Route != other.Tiles[i].Route) return false;
            var a = Tiles[i].Rect;
            var b = other.Tiles[i].Rect;
            if (!NearlyEqual(a.X, b.X) || !NearlyEqual(a.Y, b.Y) ||
                !NearlyEqual(a.Width, b.Width) || !NearlyEqual(a.Height, b.Height)) return false;
        }
        return true;
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) <= GridPartition.Epsilon;

    private static bool IsFinite(CellRect r) =>
        double.IsFinite(r.X) && double.IsFinite(r.Y) &&
        double.IsFinite(r.Width) && double.IsFinite(r.Height);

    private static bool Overlaps(CellRect a, CellRect b) =>
        a.X < b.X + b.Width - GridPartition.Epsilon &&
        b.X < a.X + a.Width - GridPartition.Epsilon &&
        a.Y < b.Y + b.Height - GridPartition.Epsilon &&
        b.Y < a.Y + a.Height - GridPartition.Epsilon;
}
