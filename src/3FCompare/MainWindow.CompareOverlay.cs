using System;
using Avalonia;
using _3FCompare.Controls;
using _3FCompare.Core.Display;
using _3FCompare.Platform;

namespace _3FCompare;

/// <summary>叠加（左右拉动）模式（对标 NVIDIA ICAT <i>Single Screen</i>，见 docs/31 §2.1「阶段 2」）。
///
/// <para><b>是什么</b>：网格对比（合并态）的一种<b>呈现变体</b> —— 不换 Core 的分区几何，
/// 只在 UI 侧加一个开关：两路子 HWND <b>都铺满整个对比区</b>，再各由
/// <see cref="WindowRegionClipper"/> 裁成<b>互补且互不重叠</b>的两半 ——
/// B（第 1 路）露靠近原点的一侧 <c>[0, split]</c>、A（第 0 路）露另一侧 <c>[split, 1]</c>，
/// 于是拖动分割线即可"揭示 / 擦除"。这就是"两路真实视频叠加"（此前的 <c>AbSliderView</c>
/// 只是渐变占位，因为真实子 HWND 无法在托管层合成）。</para>
///
/// <para><b>那条分割线存在哪儿</b>：<see cref="_wipePartition"/> —— 一份形状恒为 2 路的
/// <see cref="GridPartition"/>（水平 = 2×1，垂直 = 1×2），揭示比例就是它的
/// <see cref="GridPartition.PrimaryBoundary"/>。它与网格那边的 <c>_partition</c> 分开存，
/// 于是退出拉动时用户拖好的分格宽度不会被揭示位置顶掉。</para>
///
/// <para><b>为什么两路都裁（而不是"只裁 B、靠 Z 序盖住 A"）</b>：Avalonia 给每个
/// <c>NativeControlHost</c> 子窗口又套了一层容器 HWND，实测两路视频窗口的<b>父窗口不同</b>、
/// 彼此是顶层窗口下的兄弟 ⇒ "谁在上"由<b>容器之间</b>的 Z 序决定，而 <c>SetWindowPos</c>
/// 只作用于容器内部、对两路的相对可见性无效，靠创建顺序纯属偶然。既然两路区域互补且
/// 不重叠（并集 = 整窗），可见结果就与 Z 序<b>完全无关</b> —— 把一个隐性、会静默失效的
/// 依赖整个消掉。</para>
///
/// <para><b>为什么可行</b>：<c>SetWindowRgn</c> 对内核 D3D11 flip-model 呈现已验证生效
/// （docs/26 §10.1：区域外 5/5 露出下层）。</para>
///
/// <para><b>为什么单独一个 partial 文件</b>：与 <c>MainWindow.CompareCrop.cs</c> 同样的理由 ——
/// <c>MainWindow.axaml.cs</c> 混有未提交改动，新逻辑集中在这里，原文件只加几处单行挂钩。</para>
///
/// <para><b>退出必须清除区域</b>：两路的区域若残留，窗口会被<b>永久裁剪</b>（只有重启才恢复）。
/// 故退出走 <see cref="ClearCompareCrop"/>，与放大路径共用同一份 <c>_compareCropApplied</c> 状态。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>叠加揭示方向。两种方向只差一个矩形，共用同一条换算 / 下发 / 清除链路。</summary>
    internal enum OverlayAxis
    {
        /// <summary>水平：B 只露左侧 <c>[0, split]</c>、A 露右侧（分割线竖直，左右拖动）。</summary>
        Horizontal,

        /// <summary>垂直：B 只露上方 <c>[0, split]</c>、A 露下方（分割线水平）。</summary>
        Vertical,
    }

    /// <summary>叠加模式下<b>每一路</b>共用的格表：都铺满整个对比区（全 1×1）。
    ///
    /// <para><b>为什么"都铺满"仍然正确</b>：拉动的画面语义由 <c>SetWindowRgn</c> 的区域表达
    /// （每路只露出自己那一条带），格表只用来告诉 <c>CompareGridView.ArrangeOverride</c>
    /// "这一路的子窗口排多大"。窗口必须铺满 —— 只有窗口是整幅，内核才会把<b>整幅</b>画面
    /// 渲染进来，各带才拼得回一整幅；窗口若只有带那么大，内核会把整幅 fit 进窄带（画面被压扁）。</para>
    ///
    /// <para><b>为什么长度 = 路数（而不是恒 2）</b>：长度决定"有几路能落在格表里"。
    /// 旧实现长度恒 2 ⇒ 第 3 路起被 <c>ArrangeOverride</c> 隐藏 ⇒ 用户点「加路」看不到任何变化
    /// （这正是用户报的"加路功能失效"）。现在长度 = 参与对比的路数，N 路各有一条带。</para>
    ///
    /// <para><b>分配</b>：拖动分割线会逐帧调用 <c>ApplyCompareLayout</c>，故缓存一份并只在
    /// 路数变化时重建 —— 避免每帧新建 N 个 <see cref="CellRect"/>。
    /// 反正全部是 <c>(0,0,1,1)</c>，内容与被拖动的权重无关（权重只影响区域，不影响窗口矩形）。</para></summary>
    private CellRect[]? _compareOverlayCellsCache;
    private int _compareOverlayCellsCacheCount = -1;
    private bool _compareOverlayCellsCacheMosaic;

    private CellRect[] CompareOverlayLayout(int bandCount, bool mosaic)
    {
        var n = Math.Max(1, bandCount);
        if (_compareOverlayCellsCache is null
            || _compareOverlayCellsCacheCount != n
            || _compareOverlayCellsCacheMosaic != mosaic)
        {
            var cells = new CellRect[n];
            // ⚠ 两种形态下**每一路的窗口都必须是整幅**，这不是巧合而是机制要求：
            //   内核把整幅画面 fit 进窗口 ⇒ 只有窗口是整幅，"这块露出整幅的一部分"
            //   才成立（区域裁的才是画面切片而不是把整幅缩进窄条）。
            //   差异只在"有几路"：N 带 = 参与路数；自由拼接 = 参与路数（块数可能更多，
            //   但窗口数按路算 —— 同一路占多块时它只有一个 HWND，靠区域并集露出多块）。
            for (var i = 0; i < n; i++) cells[i] = new CellRect(0, 0, 1, 1);
            _compareOverlayCellsCache = cells;
            _compareOverlayCellsCacheCount = n;
            _compareOverlayCellsCacheMosaic = mosaic;
        }
        return _compareOverlayCellsCache;
    }

    /// <summary>上层路（B）在<b>旧两路语义</b>下的索引。N 带语义下"哪条带归哪一路"由
    /// <c>_wipeRoutes</c> 决定（见 <see cref="WipeRouteOfBand"/>），本常量仅供两路兼容路径与
    /// 既有自测读数使用。</summary>
    private const int CompareOverlayTopIndex = 1;

    /// <summary>拉动模式下第 i 条带显示的是第几路。
    ///
    /// <para><b>默认恒等</b>（第 i 条带 = 第 i 路），与网格拼合的 <c>CellRoute</c> 同一取向。
    /// 用户经「选择路」对话框指定后写入此表，于是<b>同一条带可以重复显示某一路</b>
    /// （用户决策：允许重复，见 docs/51 §八）。</para>
    ///
    /// <para><b>与 <c>Grid.CellRoute</c> 的分工</b>：那条是<b>网格</b>的格→路映射，拉动下刻意置 null
    /// （拉动不用格映射，它用窗口区域）。本表是拉动专用的带→路映射，两条路径各管各的，
    /// 避免"两套机制互相打架"（这正是既有注释里 <c>OnSwapCompareCells</c> 加了叠加守卫的原因）。</para>
    /// </summary>
    private int[]? _wipeRoutes;

    /// <summary>拉动当前该由几条带承载（= 参与对比的路数）。</summary>
    internal int WipeBandCount => _wipeRoutes is { Length: > 0 } r ? r.Length : Math.Max(0, _sync.Count);

    /// <summary>拉动当前需要几个子窗口（= 参与对比的<b>路数</b>，不是块数）。
    ///
    /// <para><b>为什么按路而不是按块</b>：一个 <see cref="Controls.PlayerSurface"/> 只有一个
    /// 子 HWND，它只能占一个位置；同一路占多块时靠<b>区域并集</b>露出多块（
    /// <c>MosaicLayout.PlanRegionsFor</c> 给出多个矩形，经 <c>CombineRgn(OR)</c> 合成）。
    /// 这与"不相邻重复需要镜像会话"是同一件事的两面（docs/51 §九）。</para>
    ///
    /// <para>自由拼接下"参与路数"= 槽号的最大值 + 1（槽号可能不连续，因为用户可以只改其中几块）；
    /// 至少要覆盖 <c>Grid.Count</c>，否则用户已打开的其它路会没有窗口可落。</para></summary>
    internal int OverlayWindowCount
    {
        get
        {
            if (_wipeMosaic is not { } mosaic) return WipeBandCount;
            var maxSlot = -1;
            foreach (var t in mosaic.Tiles) if (t.Route > maxSlot) maxSlot = t.Route;
            // 槽号从 0 起 ⇒ 窗口数 = maxSlot + 1；再与已打开路数取大，
            // 避免"拼接里只用了槽号 0/1，但用户开 5 路"时后 3 路无窗口。
            return Math.Max(maxSlot + 1, Math.Max(1, _sync.Count));
        }
    }

    /// <summary>第 <paramref name="bandIndex"/> 条带显示的路号；未指定时恒等。</summary>
    internal int WipeRouteOfBand(int bandIndex)
    {
        if (_wipeRoutes is { Length: > 0 } r && bandIndex >= 0 && bandIndex < r.Length)
            return r[bandIndex];
        return bandIndex;
    }

    /// <summary>第 <paramref name="bandIndex"/> 条带由哪个<b>槽</b>承载（槽号可能与路号不同）。
    ///
    /// <para><b>为什么需要"带 → 槽"而不是"带 → 路"</b>：不相邻重复时同一路占两条带，
    /// 而一个 <see cref="Controls.PlayerSurface"/>（一个 HWND）只能在一个位置 ⇒
    /// 第二条带必须由<b>镜像槽</b>承载。于是"这条带显示第几路"与"这条带用哪个会话"
    /// 变成两个问题：前者查 <c>_wipeRoutes</c>，后者查镜像表。</para>
    ///
    /// <para><b>分配规则</b>：同一路的多条带按出现顺序分配 ——
    /// 第一条用<b>源槽</b>，其后的每一段（被别的带隔开的那一段）各用一个镜像槽。</para>
    ///
    /// <para>找不到（镜像创建失败）时退回源路号：那一处带显示源路的画面（重复呈现同一位置），
    /// 好过完全空白 —— 且创建失败已在状态栏说明过。</para></summary>
    internal int SlotOfBand(int bandIndex)
    {
        var route = WipeRouteOfBand(bandIndex);
        var routes = _wipeRoutes;
        if (routes is not { Length: > 0 }) return route;

        // 这一路在该带之前出现过几次、且中间是否被别的路隔开
        var sources = SlotSourcesByRoute(route);
        if (sources.Length == 0) return route;

        // 数"从该路第一次出现到本带为止，被隔开的段数"
        var firstPos = -1;
        for (var i = 0; i < routes.Length; i++) if (routes[i] == route) { firstPos = i; break; }
        if (firstPos < 0 || bandIndex < firstPos) return route;

        var segment = 0;
        var prev = firstPos;
        for (var i = firstPos + 1; i <= bandIndex && i < routes.Length; i++)
        {
            if (routes[i] != route) { prev = -1; continue; }
            if (prev < 0) segment++;      // 新起一段
            prev = i;
        }

        return segment < sources.Length ? sources[segment] : route;
    }

    /// <summary>某个路号可用的承载槽（[0] = 源槽，其后 = 各镜像槽），按槽号升序。</summary>
    private int[] SlotSourcesByRoute(int route)
    {
        var list = new List<int> { route };
        foreach (var (index, source) in _sync.Mirrors())
            if (source == route) list.Add(index);
        list.Sort();
        return list.ToArray();
    }

    /// <summary>设定拉动带→路映射（「选择路」对话框与自测的共同入口）。
    ///
    /// <para><b>允许重复</b>：<paramref name="routes"/> 的每一项是"该条带显示第几路"，
    /// 同一个路号可以出现多次（用户决策，见 docs/51 §八）。越界的路号被钳到
    /// <c>[0, 路数)</c>，长度被钳到 <c>[1, MaxBands]</c> —— 钳而非抛，
    /// 因为调用方（对话框 / 自测）经过过渡态时抛出会把一次布局抖动放大成崩溃。</para>
    ///
    /// <para><b>同一条带不会静默改写用户的重复选择</b>：这里不做去重、不重排，
    /// 只做越界钳制。旧 <c>NormalizeRoute</c> 用 <c>used[]</c> 把重复路号换成别的路，
    /// 那是网格"排列"语义的要求；拉动的带表允许重复，故**不能**套用同一套归一化。</para></summary>
    internal void SetWipeRoutes(int[]? routes)
    {
        if (routes is null || routes.Length == 0)
        {
            _wipeRoutes = null;   // 回到恒等
        }
        else
        {
            var routeCount = Math.Max(1, Grid.Count);
            var n = Math.Clamp(routes.Length, 1, WipeBands.MaxBands);
            var clamped = new int[n];
            for (var i = 0; i < n; i++)
                clamped[i] = Math.Clamp(routes[i], 0, routeCount - 1);
            _wipeRoutes = clamped;
        }

        if (!_compareOverlayActive) return;

        // 先收敛镜像：不相邻重复需要为那一路再开一个会话（一个子窗口只能在一个位置）。
        // 放在重建几何**之前** —— 镜像会改变槽数，而带表/格表要按最终槽数收敛。
        SyncWipeMirrors();

        RebuildWipeGeometry();
    }

    /// <summary>自测专用入口：与用户走的是<b>同一个</b> <see cref="SetWipeRoutes"/>
    /// （判据必须打在生产线路上，不能另写一份重算）。</summary>
    internal void SetWipeRoutesForTest(int[]? routes) => SetWipeRoutes(routes);

    /// <summary>当前拉动带→路映射（null = 恒等）。供自测与 UI 读取。</summary>
    internal int[]? WipeRoutes => _wipeRoutes;

    // ══════════ 自由拼接（MosaicLayout）：拉动布局的第二形态 ══════════
    //
    // 与"单轴 N 带"的关系：N 带是**一维**的（一条轴切 n 段），自由拼接是**二维**的
    // （横竖混排、可含 T 形交叉、可不对称）。用户要求后者（docs/51 §十），
    // 但 N 带那条路已经过判据验证、且是"最常用形态"（左右分屏），故**两者并存**：
    //   _wipeMosaic 为 null ⇒ 走 N 带（默认，行为与改动前一致）
    //   _wipeMosaic 非 null ⇒ 走自由拼接（用户在布局编辑器里拼出来的）
    // 这样"常用路径不被新模型替换"（风险最小），而"自由拼接"作为能力增量。

    /// <summary>自由拼接布局；null = 未启用，拉动走单轴 N 带。</summary>
    private MosaicLayout? _wipeMosaic;

    /// <summary>当前是否处于自由拼接形态（供自测与 UI 读取）。</summary>
    internal bool WipeMosaicActive => _wipeMosaic is not null;

    /// <summary>当前自由拼接布局（null = 未启用）。供自测与布局编辑器读取。</summary>
    internal MosaicLayout? WipeMosaic => _wipeMosaic;

    /// <summary>设定自由拼接布局（布局编辑器与自测的共同入口）。
    ///
    /// <para><b>传 null 回到单轴 N 带</b>：两种形态互斥，切换时统一走
    /// <see cref="RebuildWipeGeometry"/> 重排 + 重下区域。</para>
    ///
    /// <para><b>为什么宿主只存一份布局而不自己算块</b>：块表是拉动几何的唯一真源，
    /// 窗口矩形、区域、命中测试三处都从它派生。若宿主另存一份"算好的块"，
    /// 两处迟早漂移（表现是"画出来的线与裁出来的块不一致"）。</para></summary>
    internal void SetWipeMosaic(MosaicLayout? layout)
    {
        _wipeMosaic = layout;
        if (!_compareOverlayActive) return;
        RebuildWipeGeometry();
    }

    /// <summary>自测专用入口（与用户走同一个 <see cref="SetWipeMosaic"/>）。</summary>
    internal void SetWipeMosaicForTest(MosaicLayout? layout) => SetWipeMosaic(layout);

    /// <summary>按当前形态（N 带 / 自由拼接）重建拉动几何并重排 + 重下区域。
    ///
    /// <para>所有"会改变拉动几何"的入口都收口到这里，避免每处各写一遍
    /// （漏一处就是"改了布局但画面没跟着变"）。</para></summary>
    private void RebuildWipeGeometry()
    {
        if (_wipeMosaic is null)
        {
            // 单轴 N 带：第一条线沿用当前位置，其余按权重均匀
            var fraction = _wipePartition.HandleCount > 0 ? _wipePartition.PrimaryBoundary() : 0.5;
            _wipePartition = WipePartition(ToWipeAxis(_compareOverlayAxis), fraction, WipeBandCount);
            _layoutOverlay?.SetFreeformLines(null);      // 清掉自由拼接通道（两形态互斥）
            _layoutOverlay?.Update(_wipePartition);
        }
        else
        {
            // 自由拼接：把逻辑线交给覆盖层（可含 T 形交叉 ⇒ 线段而不是整线）。
            // 仍然 Update 一份分区：覆盖层用它做"手柄数量上限/降级判据"等既有逻辑，
            // 而线段由 SetFreeformLines 给出（覆盖层内有优先级：自由拼接优先）。
            _layoutOverlay?.Update(PrimaryPartitionForOverlay());
            _layoutOverlay?.SetFreeformLines(WipeMosaicLines());
        }

        _compareOverlayCellsCache = null;   // 块数可能变了，格表缓存作废
        _compareOverlayCellsCacheCount = -1;
        ApplyCompareLayout();
        ScheduleCompareCrop();
    }

    /// <summary>自由拼接的逻辑线（覆盖层与命中测试共用同一份）。</summary>
    private (SplitAxis Axis, double Position, double From, double To)[] WipeMosaicLines()
    {
        if (_wipeMosaic is not { } mosaic) return Array.Empty<(SplitAxis, double, double, double)>();
        var lines = mosaic.LogicalLines();
        var result = new (SplitAxis, double, double, double)[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            var (pos, axis, from, to) = lines[i];
            result[i] = (axis, pos, from, to);
        }
        return result;
    }

    /// <summary>覆盖层在自由拼接下用的分区：只用于"手柄数量"这一个上报，
    /// 实际线段由 <see cref="MosaicLayout.Splits"/> 决定。
    /// <para>拼接的块数决定手柄上限，但手柄的**位置**是每块的边，覆盖层会按块表逐个画
    /// （见 <c>LayoutOverlayWindow.Render</c> 的逐块发边逻辑）—— 故这里给一个"块数 × 1"的
    /// 单轴分区，只为让 HandleCount 与实际线段数同量级，不参与任何几何计算。</para></summary>
    private GridPartition PrimaryPartitionForOverlay()
    {
        var lines = _wipeMosaic!.LogicalLines();
        var n = Math.Clamp(Math.Max(1, lines.Length + 1), 1, WipeBands.MaxBands);
        return GridPartition.Uniform(n, n, 1);
    }

    /// <summary>把带表扩到 <paramref name="laneCount"/> 条并重建分区（「加路」在拉动下调用）。
    ///
    /// <para><b>为什么带表要跟着 lane 走</b>：带数决定"有几路能落在格表里"，
    /// 而格表长度决定谁能被排布。旧实现带数恒 2 ⇒ 第 3 条 lane 起被隐藏 ⇒ 用户点「加路」
    /// 画面零变化（用户报的"加路功能失效"）。</para>
    ///
    /// <para><b>用户已指定带表时不静默改写</b>：若 <c>_wipeRoutes</c> 是用户经「选择路」设过的，
    /// 扩表只在尾部补齐（新 lane 追加为最后一条带），既不丢用户的既有选择、也让新 lane 有带可落。
    /// 这比"直接重置成恒等"尊重用户意图。</para>
    ///
    /// <para>⚠ 本方法**会做布局与裁剪下发**，只能从"用户显式加路"这类路径调用。
    /// 从 <c>UpdateStatus</c> 走的路径必须用 <see cref="GrowWipeBandsDataOnly"/>（见其注释）。</para></summary>
    private void GrowWipeBandsTo(int laneCount)
    {
        GrowWipeBandsDataOnly(laneCount);

        // 重建分区（带数变了）并重排：第一条线沿用当前位置，其余按权重均匀。
        var fraction = _wipePartition.HandleCount > 0 ? _wipePartition.PrimaryBoundary() : 0.5;
        _wipePartition = WipePartition(ToWipeAxis(_compareOverlayAxis), fraction, WipeBandCount);
        _layoutOverlay?.Update(_wipePartition);
        _compareOverlayCellsCache = null;             // 格表长度变了，缓存作废
        _compareOverlayCellsCacheCount = -1;
        ApplyCompareLayout();
        ScheduleCompareCrop();
    }

    /// <summary><b>只改带表数据</b>（不改分区、不做布局、不下发裁剪）。
    ///
    /// <para><b>为什么必须单独一个版本</b>：带表需要跟着 lane 数长的收敛点有两个，性质完全不同：
    /// <list type="bullet">
    /// <item><description>"用户点了加路" —— 可以做布局（<see cref="GrowWipeBandsTo"/>）；</description></item>
    /// <item><description>"路数在别处变了"（拖放 / 菜单打开 / 会话重载 / 崩溃重建）——
    /// 收敛点只能放在 <c>SyncCompareModeToRouteCount</c>，而它<b>由 <c>UpdateStatus</c> 调用</b>。
    /// 在那里做布局 = 在"刷状态栏"里改布局并连发 <c>SetWindowRgn</c>，重入且时序危险。</description></item>
    /// </list>
    /// 真机实测的后果是致命的：启动 5 路档时进程直接原生崩溃
    /// （<c>baseline 3/3 不崩</c> → <c>改后 3/3 崩</c>，退出码 <c>0xC0000005</c> /
    /// <c>0x80131604</c>）。故这一支**只碰数据**，布局由调用链里既有的延后路径去做。</para></summary>
    private void GrowWipeBandsDataOnly(int laneCount)
    {
        var n = Math.Clamp(laneCount, 1, WipeBands.MaxBands);
        if (_wipeRoutes is { Length: > 0 } cur)
        {
            if (cur.Length >= n) return;              // 已够长：不动
            var grown = new int[n];
            Array.Copy(cur, grown, cur.Length);
            // 尾部补齐：新 lane 各占自己那条带（恒等补齐，与 NormalizeRoute 同一取向）
            for (var i = cur.Length; i < n; i++) grown[i] = i;
            _wipeRoutes = grown;
        }
        else
        {
            _wipeRoutes = MakeIdentityWipeRoutes(n);
        }
    }

    /// <summary>叠加模式是否激活。<b>只有它决定</b> <c>ApplyCompareLayout</c> 用"两路铺满"格表。</summary>
    private bool _compareOverlayActive;

    /// <summary>当前揭示方向。默认水平：分割线竖直，即 <c>_wipePartition</c> 取 2×1（垂直时取 1×2）。</summary>
    private OverlayAxis _compareOverlayAxis = OverlayAxis.Horizontal;

    /// <summary>叠加模式是否激活（供 <c>ApplyCompareLayout</c> / 命中测试 / 自测读取）。</summary>
    internal bool CompareOverlayActive => _compareOverlayActive;

    /// <summary>当前揭示方向（供自测读取）。</summary>
    internal OverlayAxis CompareOverlayAxis => _compareOverlayAxis;

    /// <summary>叠加模式入口（快捷键 <c>S</c>）：开 → 关 → 开。
    /// <para>关闭时<b>不退出网格对比</b>，只回到网格的分格 —— 叠加本就是它的一种呈现变体，
    /// 而网格那边拖好的分格宽度存在 <see cref="_partition"/> 里，全程没被揭示线碰过。</para></summary>
    internal void ToggleCompareOverlay()
    {
        if (_compareOverlayActive)
        {
            ExitCompareOverlay();
            NotifyCompareOverlay();
            return;
        }
        EnterCompareOverlay();
    }

    /// <summary>UI 的揭示方向 → Core 的轴向枚举。
    ///
    /// <para><b>为什么要有这个换算</b>：<see cref="WipeBands"/> 住在 Core，Core 不能引用 UI 的
    /// <see cref="OverlayAxis"/>（那是 <c>MainWindow</c> 的嵌套类型）。两者语义一一对应，
    /// 在边界处换算一次，好过让 Core 去猜 UI 的枚举、也好过在 Core 里重复定义一份"方向"。</para></summary>
    private static WipeAxis ToWipeAxis(OverlayAxis axis)
        => axis == OverlayAxis.Horizontal ? WipeAxis.Horizontal : WipeAxis.Vertical;

    /// <summary>按轴向与揭示比例造出拉动自己那份"N 带单轴"分区。
    ///
    /// <para><b>N 带（而不是恒两格）</b>：带数 = 参与对比的路数，由 <see cref="WipeBands.Partition"/>
    /// 统一构造（水平 = n×1 一条竖线组、垂直 = 1×n）。于是覆盖层沿整条线抓的既有命中规则、
    /// 以及 n-1 个手柄的独立拖动全部复用 <see cref="GridPartition"/> 的既有实现，不必为拉动
    /// 单独维护第二份几何。</para>
    ///
    /// <para><paramref name="fraction"/> 是<b>第一条线</b>的初始位置：从网格切过来时沿用网格
    /// 当前那条竖线的位置，用户看到的是"同一根线换了职责"（旧两路语义的保留）。
    /// 带数 &gt; 2 时只把第一条线放在该位置、其余均匀分布 —— 拖动后各自独立。</para></summary>
    private static GridPartition WipePartition(WipeAxis axis, double fraction, int bandCount)
    {
        var n = Math.Clamp(bandCount, 1, WipeBands.MaxBands);
        var part = WipeBands.Partition(n, axis);
        if (n < 2) return part;   // 单带没有内部线可放
        // 只摆第一条线；fraction 非有限时退回均匀（WithHandleDragged 内部对 NaN 幂等，
        // 但显式判一次让"初始位置"这件事在调用点可读）。
        if (!double.IsFinite(fraction)) return part;
        return axis == WipeAxis.Horizontal
            ? part.WithHandleDragged(0, fraction, 0.5)
            : part.WithHandleDragged(0, 0.5, fraction);
    }

    /// <summary>进入叠加模式。路数 &lt; 2 时给状态栏提示且不进入（不静默）。
    ///
    /// <para><b>进入顺序</b>：先复位无缝放大（两者语义互斥：放大是"窗口大于格 + 区域裁出格"，
    /// 叠加是"两窗铺满 + 区域裁出半区"，同时生效会互相破坏），再确保处于网格对比态
    /// （覆盖层窗口、裁剪链路都在它上面建立），然后把揭示线初始化成网格当前那条竖线的位置
    /// —— 切换时线不跳，用户看到的是"同一根线换了职责"。</para></summary>
    /// <returns>真的进入为 true；路数不足或覆盖层降级为 false。</returns>
    internal bool EnterCompareOverlay()
    {
        if (_sync.Count < 2)
        {
            StatusInfo.Text = Loc("左右拉动对比需要至少 2 路视频。", "Left-right wipe needs at least 2 routes.");
            return false;
        }
        if (_compareOverlayActive) return true;

        ResetCompareMagnify();

        var startFraction = _compareActive ? _partition.PrimaryBoundary() : 0.5;
        if (!_compareActive) EnterCompareMode();
        if (!_compareActive) return false; // 覆盖层 fail-fast 降级（见 VerifyOverlayDegradation）

        _compareOverlayActive = true;
        // 带数 = 当前参与对比的路数（用户可用「选择路」改，见 _wipeRoutes）。
        // 未指定时恒等 ⇒ 第 i 条带显示第 i 路。
        _wipeRoutes ??= MakeIdentityWipeRoutes(_sync.Count);
        // 揭示线初始位置 = 网格当前那条竖线的位置 ⇒ 切换时线不跳（"同一根线换了职责"）。
        // 只在 N 带形态下有意义；自由拼接的线来自它自己的块表。
        if (_wipeMosaic is null)
        {
            _wipePartition = WipePartition(ToWipeAxis(_compareOverlayAxis), startFraction, WipeBandCount);
        }
        // 统一走 RebuildWipeGeometry：它按当前形态刷覆盖层、重排、重下区域。
        // 四件事必须一起做，故收口到一处（见该方法注释）。
        RebuildWipeGeometry();
        NotifyCompareOverlay();
        return true;
    }

    /// <summary>恒等带→路映射（第 i 条带显示第 i 路）。</summary>
    private static int[] MakeIdentityWipeRoutes(int n)
    {
        var r = new int[Math.Max(0, n)];
        for (var i = 0; i < r.Length; i++) r[i] = i;
        return r;
    }

    /// <summary>退出叠加模式：清状态 + <b>同步</b>清除区域 + 回到网格的分格。
    ///
    /// <para><b>必须同步清除</b>：延后一拍的话这一拍内两路仍被裁着，而任何异常路径
    /// （含 <c>OnClosing</c>）都不会再来补一次 ⇒ 窗口被永久裁剪。</para>
    ///
    /// <para>退出后各路各占自己那格、不再重叠，区域也一并清掉，故不需要任何 Z 序补偿。</para></summary>
    internal void ExitCompareOverlay()
    {
        if (!_compareOverlayActive) return;
        _compareOverlayActive = false;
        ClearCompareCrop();
        if (_compareActive)
        {
            _layoutOverlay?.Update(_partition); // 覆盖层从揭示线换回网格的那一组线
            ApplyCompareLayout();               // 回到网格分格
            ScheduleCompareCrop();              // 重新下发网格的裁剪（Avalonia 取整产生的 ≤1px 外扩）
        }
    }

    /// <summary>切换揭示方向（水平 / 垂直）。本阶段是<b>内部开关</b>：用户入口只有"开关叠加"的
    /// <c>S</c> 键，垂直方向由自测覆盖；方向只影响那条揭示线是竖是横，换算与下发完全共用。
    /// <para>换轴向会重建分区（n×1 ↔ 1×n），故第一条线的位置要显式搬过去 ——
    /// 否则换方向后揭示位置会跳回 0.5。</para></summary>
    internal void SetCompareOverlayAxis(OverlayAxis axis)
    {
        if (_compareOverlayAxis == axis) return;
        var fraction = _wipePartition.PrimaryBoundary();
        _compareOverlayAxis = axis;
        if (_compareOverlayActive)
        {
            _wipePartition = WipePartition(ToWipeAxis(axis), fraction, WipeBandCount);
            _layoutOverlay?.Update(_wipePartition);
            ScheduleCompareCrop();
        }
    }

    /// <summary>菜单「视图 → 布局编辑器（自由拼接）」入口：打开二级模态窗，
    /// 确定后把布局交给拉动呈现。
    ///
    /// <para><b>为什么先确保处于拉动态</b>：自由拼接是拉动的一种呈现形态（块表决定"哪块露出什么"），
    /// 在网格对比下改布局没有意义。故这里先 <see cref="EnterCompareOverlay"/>；
    /// 路数不足（&lt; 2）时它会给状态栏提示并返回 false，本方法据此不打开编辑器
    /// —— 否则用户在一个不会生效的模式里精心拼了半天。</para>
    ///
    /// <para><b>编辑器当前布局的来源</b>：已有拼接就用它（用户再次打开时接着上次改，
    /// 不是每次从单块重来）；没有则用"当前 N 带"作为起点（<c>FromGridPartition</c> 的思路），
    /// 于是"从左右分屏切到自由拼接"不会把用户已经看到的布局清空。</para></summary>
    private async void OnOpenLayoutEditor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_sync.Count < 2)
            {
                StatusInfo.Text = Loc("自由拼接至少需要 2 路视频。", "Free-form tiling needs at least 2 routes.");
                return;
            }
            if (!_compareOverlayActive && !EnterCompareOverlay()) return;

            var current = _wipeMosaic ?? MosaicLayout.FromGridPartition(_wipePartition);
            var dlg = new Views.MosaicLayoutEditorWindow(current, Math.Max(1, Grid.Count));
            await dlg.ShowDialog(this);

            if (!dlg.Accepted || dlg.Result is null) return;   // 取消：不动现有布局

            SetWipeMosaic(dlg.Result);
            StatusInfo.Text = Loc(
                $"已应用自由拼接（{dlg.Result.Tiles.Length} 块 / {dlg.Result.RouteCount} 路参与）：" +
                "拖动分割线调整，按 S 退出 / 按 G 回网格对比。",
                $"Free-form layout applied ({dlg.Result.Tiles.Length} tiles / {dlg.Result.RouteCount} routes): " +
                "drag a divider to adjust, press S to exit / G for the grid.");
        }
        catch (Exception ex)
        {
            // 顶层 async void 必须有异常边界（同 MainWindow 其它 async void 处理器的约定）：
            // 否则一次窗口创建失败会把进程带走，而用户只看到"点了没反应"。
            _3FCompare.Core.Diagnostics.AppLog.Warn("LayoutEditor", $"打开布局编辑器失败：{ex.GetType().Name}: {ex.Message}");
            StatusInfo.Text = Loc("打开布局编辑器失败（详见日志）。", "Failed to open the layout editor (see the log).");
        }
    }

    /// <summary>镜像失败的原因（null = 没有待显示的降级）。
    ///
    /// <para><b>为什么必须单独存一份、而不是直接写状态栏</b>：
    /// <c>SyncWipeMirrors</c> 之后总会跟一次 <c>RebuildWipeGeometry</c>，
    /// 而布局落地会触发 <c>OnVisibleRoutesChanged → Dispatcher.Post(ApplyRouteActivity)</c>，
    /// 后者结尾调 <c>NotifyCompareOverlay()</c> —— **它会把状态栏整条重算**，
    /// 把"镜像失败"那行冲掉。用户于是看到"某一处带是空的"却没有任何解释，
    /// 与用户最初报的"点了没反应"是同一类静默失效。</para>
    ///
    /// <para>故：失败原因存在这里，由 <see cref="NotifyCompareOverlay"/> 每次重算状态栏时
    /// <b>追加</b>上去，直到下一次镜像收敛成功才清空。</para></summary>
    private string? _wipeMirrorWarning;

    /// <summary>把镜像槽收敛到"当前带表真正需要的镜像"（不相邻重复才需要）。
    ///
    /// <para><b>为什么需要镜像</b>：一个 <see cref="Controls.PlayerSurface"/> 只有一个子 HWND，
    /// 只能落在屏幕上一个位置；而"同一路出现在两条<b>不相邻</b>的带上"要求它同时呈现在两处。
    /// 内核的 <c>OutputWindow</c> 是会话级字段 ⇒ 必须再开一个会话绑到第二个窗口。
    /// 相邻重复不需要（区域并集即可表达，见 <c>MosaicLayout.PlanRegionsFor</c>）。</para>
    ///
    /// <para><b>同步怎么保证</b>：镜像槽是普通槽、偏移跟随源路，
    /// 既有的漂移校正会每拍把它拉回 <c>masterPos + 源路偏移</c>
    /// （Core 单测 <c>SyncMirrorTests</c> 已逐条钉住"镜像与源路永远同位"）。
    /// 故这里**不需要任何新同步代码**。</para>
    ///
    /// <para><b>成本与风险（必须记账）</b>：每个镜像是一份独立的解码器 + 交换链 ⇒
    /// 内存/GPU 开销线性增长，且 <c>docs/43</c> 已定案"崩溃率 ∝ 并发交换链数 N"。
    /// 故镜像<b>只在真的必要时创建</b>（不相邻重复），并在不再需要时立即释放；
    /// 创建失败不阻断其余路（那一处带显示为空，状态栏说明）。</para></summary>
    private void SyncWipeMirrors()
    {
        if (!_compareOverlayActive) return;

        // 当前带表需要的镜像：源路 → 需要几处"不相邻的额外呈现"
        var needed = new Dictionary<int, int>();
        var routes = _wipeRoutes;
        if (routes is { Length: > 0 })
        {
            // 每个路号的出现位置；出现位置不相邻时，除第一处外都需要镜像
            var positions = new Dictionary<int, List<int>>();
            for (var i = 0; i < routes.Length; i++)
            {
                if (!positions.TryGetValue(routes[i], out var list)) positions[routes[i]] = list = new List<int>();
                list.Add(i);
            }
            foreach (var kv in positions)
            {
                if (kv.Value.Count < 2) continue;
                // 数"被别的路隔开的段数"：连续的算一处，之后每新起一段需要一个镜像
                var segments = 1;
                for (var k = 1; k < kv.Value.Count; k++)
                    if (kv.Value[k] != kv.Value[k - 1] + 1) segments++;
                if (segments > 1) needed[kv.Key] = segments - 1;
            }
        }

        // 现有镜像统计（按源路）。注意元组字段名是 (Index, Source)。
        var have = new Dictionary<int, List<int>>();
        foreach (var (index, source) in _sync.Mirrors())
        {
            if (!have.TryGetValue(source, out var list)) have[source] = list = new List<int>();
            list.Add(index);
        }

        var changed = false;

        // ① 多余的镜像：释放（倒序删，避免下标漂移）
        foreach (var entry in have)
        {
            var want = needed.TryGetValue(entry.Key, out var n) ? n : 0;
            var indices = entry.Value;
            var extra = indices.Count - want;
            for (var i = 0; i < extra; i++)
            {
                // 每次删最后一个（下标最大的那个），保证剩下的下标仍有效
                var idx = indices[indices.Count - 1 - i];
                Grid.GetSurface(idx)?.DetachSession();
                _sync.RemoveSlotAt(idx);
                changed = true;
            }
        }

        // ② 不足的镜像：创建（创建失败不阻断其余路）
        var anyFailed = false;
        foreach (var kv in needed)
        {
            var want = kv.Value;
            var got = have.TryGetValue(kv.Key, out var list) ? list.Count : 0;
            for (var i = got; i < want; i++)
            {
                if (!TryCreateMirror(kv.Key)) { anyFailed = true; break; }
                changed = true;
            }
        }

        // 记下降级原因（不直接写状态栏：随后必然被 NotifyCompareOverlay 重算覆盖，
        // 那正是"用户看到空带却没有任何解释"的成因，见 _wipeMirrorWarning 的注释）。
        // 本轮全部建成 ⇒ 清空旧告警（"自愈后仍挂着旧警告"也是误导）。
        _wipeMirrorWarning = anyFailed
            ? Loc("⚠ 有一处重复的带未能显示（镜像会话创建失败）：那一处会留空。详见日志。",
                  "⚠ One repeated band could not be shown (mirror session failed); it stays blank. See the log.")
            : null;

        if (changed)
        {
            // 镜像增删改变了槽数 ⇒ 带表/格表要跟着收敛
            GrowWipeBandsTo(Math.Max(Grid.Count, _sync.Count));
            ScheduleCompareCrop();
        }
    }

    /// <summary>为第 <paramref name="sourceIndex"/> 路创建一个镜像槽（同一文件、第二个输出窗口）。
    ///
    /// <para>返回是否成功。失败只写日志 + 状态栏，不抛 —— 一处带显示不出来，
    /// 好过整个拉动态崩掉。</para>
    ///
    /// <para><b>次序（内核有硬要求，写错就永远建不出来）</b>：
    /// 先扩 lane → 取该 lane 的 surface → <c>EnsureHwndAsync</c> 拿真实 HWND → 才建会话。
    /// <b>不能先建会话再绑窗口</b>：内核 <c>FFF3FP_Create</c> 校验输出窗口，传 0 直接
    /// <c>InvalidArgument</c>（实测日志见 <c>PlaybackCoordinator.CreateMirrorSession</c> 注释），
    /// 表现是"镜像永远建不出来 ⇒ 那一处带恒空白"。</para></summary>
    private bool TryCreateMirror(int sourceIndex)
    {
        try
        {
            var slots = _sync.Slots;
            if (sourceIndex < 0 || sourceIndex >= slots.Count) return false;
            var path = slots[sourceIndex].Path;

            // ① 先扩 lane 并取到承载窗口
            var mirrorIndex = _sync.Count;
            if (Grid.Count <= mirrorIndex) Grid.SetCount(mirrorIndex + 1, _realMode);
            var surface = Grid.GetSurface(mirrorIndex);
            if (surface is null)
            {
                _3FCompare.Core.Diagnostics.AppLog.Warn("Mirror",
                    $"第 {mirrorIndex} 条 lane 的 surface 不存在，镜像未创建");
                return false;
            }

            // ② 拿真实 HWND（真实模式下没它就一定被内核拒）
            nint hwnd = 0;
            if (_realMode)
            {
                hwnd = surface.EnsureHwndAsync().GetAwaiter().GetResult();
                if (hwnd == nint.Zero)
                {
                    _3FCompare.Core.Diagnostics.AppLog.Warn("Mirror", "镜像窗口 HWND 未创建，镜像未创建");
                    return false;
                }
            }

            // ③ 建会话（与该路相同设置；色彩按镜像自己的窗口解析）
            var session = _coordinator.CreateMirrorSession(path, hwnd);
            if (session is null) return false;

            // ④ 入槽 + 绑定
            if (!_sync.AddMirror(session, path, sourceIndex))
            {
                session.Dispose();
                return false;
            }
            surface.AttachSession(session);
            _sync.ResyncMirrorOffsets();

            _3FCompare.Core.Diagnostics.AppLog.Info("Mirror",
                $"已为第 {sourceIndex} 路创建镜像槽 {mirrorIndex}（同一文件、第二个输出窗口 hwnd=0x{hwnd:X}）");
            return true;
        }
        catch (Exception ex)
        {
            _3FCompare.Core.Diagnostics.AppLog.Warn("Mirror",
                $"为第 {sourceIndex} 路创建镜像失败：{ex.GetType().Name}: {ex.Message}");
            // ⚠ 这里**不写** StatusInfo：随后必然被 NotifyCompareOverlay 重算覆盖。
            //   降级原因由调用方（SyncWipeMirrors）记进 _wipeMirrorWarning，在那里追加到状态栏。
            return false;
        }
    }

    /// <summary>菜单「视图 → 选择路」入口：打开模态窗指定各条带显示哪几路。
    ///
    /// <para>与布局编辑器同样的前置（先进入拉动态）与同样的异常边界。</para></summary>
    private async void OnOpenRoutePicker(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (_sync.Count < 2)
            {
                StatusInfo.Text = Loc("左右拉动对比需要至少 2 路视频。", "Left-right wipe needs at least 2 routes.");
                return;
            }
            if (!_compareOverlayActive && !EnterCompareOverlay()) return;

            var dlg = new Views.RoutePickerWindow(_wipeRoutes, WipeBandCount, Math.Max(1, Grid.Count));
            await dlg.ShowDialog(this);
            if (!dlg.Accepted || dlg.Result is null) return;

            SetWipeRoutes(dlg.Result);
            StatusInfo.Text = Loc(
                $"已应用带→路选择（{dlg.Result.Length} 条带）：{DescribeWipeRoutes(dlg.Result)}",
                $"Band→route mapping applied ({dlg.Result.Length} bands): {DescribeWipeRoutes(dlg.Result)}");
        }
        catch (Exception ex)
        {
            _3FCompare.Core.Diagnostics.AppLog.Warn("RoutePicker", $"打开选择路失败：{ex.GetType().Name}: {ex.Message}");
            StatusInfo.Text = Loc("打开选择路失败（详见日志）。", "Failed to open the route picker (see the log).");
        }
    }

    /// <summary>把带→路映射写成一行可读文本（1 起编号）。</summary>
    private static string DescribeWipeRoutes(int[] routes)
    {
        var parts = new string[routes.Length];
        for (var i = 0; i < routes.Length; i++) parts[i] = (routes[i] + 1).ToString();
        return string.Join("、", parts);
    }

    /// <summary>自由拼接的线段被拖动：按线段语义更新块表并重下发区域。
    ///
    /// <para><b>为什么不能走 <see cref="OnPartitionChanged"/></b>：那条路径把覆盖层报回的
    /// <c>GridPartition</c> 当成真值，而自由拼接的几何是 <see cref="MosaicLayout"/> 的块表
    /// —— 分区模型表达不了线段（T 形交叉下同一位置有多段、覆盖区间不同）。
    /// 若在那边处理，会变成"拖了线、块表没动"，即两处各存一份几何而互相漂移。</para>
    ///
    /// <para>流程：算新位置 → <c>DragLine</c> → 若真的变了则回灌新的线段表 + 重下发区域。
    /// <c>DragLine</c> 对"不干净的层"会返回原布局（保守取舍，见其注释），
    /// 此时不回灌、也不重下发（避免无谓的 SetWindowRgn 连发）。</para></summary>
    private void OnFreeformLineDragged(int lineIndex, double normalizedPosition)
    {
        if (!_compareOverlayActive || _wipeMosaic is not { } mosaic) return;
        if (DragWipeLine(lineIndex, normalizedPosition))
        {
            // 块表变了 ⇒ 线段位置也变了，回灌给覆盖层（否则线停在旧位置、与块表脱节）
            _layoutOverlay?.SetFreeformLines(WipeMosaicLines());
        }
    }

    /// <summary>状态栏提示（与 <see cref="NotifyCompareMode"/> 同一风格：说明当前状态 + 下一步怎么按）。
    /// 退出叠加时直接复用 <see cref="NotifyCompareMode"/>，因为退出后回到的正是"网格对比"。</summary>
    private void NotifyCompareOverlay()
    {
        if (!_compareOverlayActive)
        {
            NotifyCompareMode();
            return;
        }

        var axis = _compareOverlayAxis == OverlayAxis.Horizontal
            ? Loc("水平揭示", "horizontal reveal")
            : Loc("垂直揭示", "vertical reveal");
        var scope = DescribeDeactivatedRoutes();   // 读实测启用集合，见该方法注释
        var bands = WipeBandCount;
        // 镜像降级原因**追加**在末尾：它不能被这次重算冲掉（见 _wipeMirrorWarning 的注释）。
        var mirrorWarn = _wipeMirrorWarning is { Length: > 0 } w ? " " + w : "";
        StatusInfo.Text = Loc(
            $"左右拉动对比（{axis}）：{bands} 条带并列铺满，拖动分割线调整各带占比{scope}" +
            $"（按 S 退出 / 按 G 回网格对比）{mirrorWarn}",
            $"Left-right wipe ({axis}): {bands} band(s) tiled full-area, drag a divider to resize bands{scope} " +
            $"(press S to exit / G for the grid){mirrorWarn}");
    }

    /// <summary>拉动模式的区域下发：把该路占据的**全部带**换算成区域集合后一次下发。
    ///
    /// <para><b>为什么是集合而不是单矩形</b>：用户可选同一路占多条带（决策见 docs/51 §八），
    /// 且两条带可以<b>不相邻</b>。此时这一路的可见区域 = 若干矩形的并集，
    /// 由 <c>WindowRegionClipper.ApplyRects</c> 经 <c>CombineRgn(OR)</c> 合成一个区域句柄。
    /// 单带形态下集合只有一个元素，与旧两路实现等价。</para>
    ///
    /// <para><b>为什么按"带"算、按"路"归并</b>：区域按带算（每条带的位置由分区决定），
    /// 而一个 HWND 要收下它所有带的并集 ⇒ 先按带算出全部区域，再把"属于这一路"的带挑出来。
    /// ⚠ 调用方传的 <paramref name="routeIndex"/> 是<b>路号</b>（外层循环按 <c>Grid.Count</c> 走），
    /// 不是带号 —— 两者只有在恒等映射下才相等，写成"传带号"会在 N 带时裁错块。</para></summary>
    private void ApplyWipeCrop(int routeIndex, Controls.PlayerSurface surface, nint hwnd, double scaling)
    {
        var overlayWinPx = CompareCropPlanner.ToPhysicalRect(surface.Bounds, scaling);

        // ── 自由拼接形态：区域直接由块表换算（每块一个矩形，同一路可占多块）──
        if (_wipeMosaic is { } mosaic)
        {
            var regions = mosaic.PlanRegionsFor(overlayWinPx, routeIndex);
            if (regions.Length == 0)
            {
                // 这一路不占任何块（被用户从拼接里移除了），或窗口小到无法表达：
                // 清掉它的区域，绝不下发 0 宽区域（那会让 SetWindowRgn 失败、窗口整块不可见）。
                ClearOne(routeIndex, hwnd);
                return;
            }
            ApplyCropRegions(routeIndex, hwnd, regions);
            return;
        }

        // ── 单轴 N 带形态（默认）──
        var bandCount = WipeBandCount;
        if (bandCount < 1)
        {
            ClearOne(routeIndex, hwnd);
            return;
        }

        var axis = ToWipeAxis(_compareOverlayAxis);
        var boundaries = WipeBands.PartitionBoundaries(_wipePartition, axis);
        var allRegions = WipeBands.PlanRegions(overlayWinPx, boundaries, axis);
        if (allRegions is null)
        {
            // 对比区窄于带数：无法让每条带都 ≥1px ⇒ 不裁剪。
            // 退回"整窗可见"而不是产生 0 宽区域让 SetWindowRgn 失败（那会让窗口整块不可见）。
            ClearOne(routeIndex, hwnd);
            return;
        }

        // 这一路占据哪些带：所有"该带用的槽 == routeIndex"的带。
        // 恒等映射下就只有它自己那一条；用户指定重复路时可能有多条（含不相邻）。
        // ⚠ 判据必须是**槽**而不是**路号**：不相邻重复会为那一路再开一个镜像槽，
        //    两条带分别由"源槽"与"镜像槽"承载 ⇒ 按路号比会让两条带抢同一个 HWND，
        //    镜像拿不到区域（表现为"其中一处始终空白"）。
        var mine = new List<Rect32>();
        for (var b = 0; b < allRegions.Length && b < bandCount; b++)
        {
            if (SlotOfBand(b) != routeIndex) continue;
            if (allRegions[b].IsValid) mine.Add(allRegions[b]);
        }

        if (mine.Count == 0)
        {
            // 这一路没有任何带（例如用户把它从带表里移除了）：清除它的区域。
            // 与"旧两路下第 3 路被隐藏"同样处理 —— 不做区域下发。
            ClearOne(routeIndex, hwnd);
            return;
        }

        ApplyCropRegions(routeIndex, hwnd, mine);
    }

    /// <summary>拉动模式下点落在第几条带 ⇒ 哪一路。
    ///
    /// <para><b>与旧 <c>HitOverlayAt</c> 的区别</b>：旧实现是"点是否在 B 的揭示区内 → B，否则 A"
    /// （只有两路）。N 带下要先按坐标算出带号，再经带→路映射取路号 —— 映射允许重复，
    /// 故必须查表而不是直接返回带号。</para>
    ///
    /// <para><b>为什么按分区而非按区域判</b>：区域是 Win32 物理像素、且是"窗口相对"坐标，
    /// 命中测试跑在 Avalonia 的 DIP 空间里。按归一化分区判既躲开 DPI 换算的分叉，
    /// 又与"区域由同一份分区算出"同源 —— 两处不会各算一份。</para></summary>
    private Controls.PlayerSurface? HitWipeAt(Point p, double w, double h)
    {
        if (w <= 0 || h <= 0) return null;

        // 归一化坐标
        var u = p.X / w;
        var v = p.Y / h;
        if (!double.IsFinite(u) || !double.IsFinite(v)) return null;

        // ── 自由拼接：找出点落在哪一块（块表是唯一真源，与区域/绘制同源）──
        if (_wipeMosaic is { } mosaic)
        {
            for (var i = 0; i < mosaic.Tiles.Length; i++)
            {
                var r = mosaic.Tiles[i].Rect;
                if (u >= r.X && u <= r.X + r.Width && v >= r.Y && v <= r.Y + r.Height)
                    return Grid.GetSurface(mosaic.Tiles[i].Route);
            }
            return null;   // 理论上块表铺满整窗，这里兜底
        }

        // ── 单轴 N 带：按界标算第几条带，再经带→路映射取路 ──
        var axisCoord = _compareOverlayAxis == OverlayAxis.Horizontal ? u : v;
        var boundaries = WipeBands.PartitionBoundaries(_wipePartition, ToWipeAxis(_compareOverlayAxis));
        var band = 0;
        for (var i = 1; i < boundaries.Length; i++)
        {
            if (axisCoord < boundaries[i]) { band = i - 1; break; }
            band = i - 1;   // 落最后一条带
        }
        if (band < 0) band = 0;

        // ⚠ 用 SlotOfBand 而不是 WipeRouteOfBand：不相邻重复时同一路的两条带由
        //    源槽与镜像槽分别承载，返回路号会恒选源槽（点第二条带却选到第一处的窗口）。
        return Grid.GetSurface(SlotOfBand(band));
    }

    /// <summary>命中的<b>逻辑线</b>编号（-1 = 没命中任何线）。
    ///
    /// <para><b>为什么需要</b>：单轴 N 带下覆盖层自己按 <c>HandlePositions</c> 做命中；
    /// 但自由拼接的线是<b>线段</b>（T 形交叉时同一位置有多段、覆盖区间不同），
    /// 必须按"点是否落在该线段的覆盖区间内"判，否则会抓到一条根本不经过该点的线。</para>
    ///
    /// <para>命中容差按归一化取 <paramref name="toleranceDip"/> ÷ 容器尺寸，
    /// 于是"手感粗细"与屏幕尺寸无关（高分屏上不会变得更难抓）。</para></summary>
    internal int HitWipeLineAt(Point p, double w, double h, double toleranceDip)
    {
        if (_wipeMosaic is not { } mosaic) return -1;
        if (w <= 0 || h <= 0) return -1;

        var u = p.X / w;
        var v = p.Y / h;
        var tolX = toleranceDip / w;
        var tolY = toleranceDip / h;

        var lines = mosaic.LogicalLines();
        for (var i = 0; i < lines.Length; i++)
        {
            var (pos, axis, from, to) = lines[i];
            if (axis == SplitAxis.X)
            {
                // 竖线：X 在 pos 附近，且 Y 落在 [from, to]
                if (Math.Abs(u - pos) > tolX) continue;
                if (v < from - tolY || v > to + tolY) continue;
            }
            else
            {
                if (Math.Abs(v - pos) > tolY) continue;
                if (u < from - tolX || u > to + tolX) continue;
            }
            return i;
        }
        return -1;
    }

    /// <summary>按逻辑线编号拖动自由拼接（布局编辑器与拉动拖动共用）。
    /// <para>返回是否真的改变了几何（供调用方决定要不要重排，避免无谓重下发）。</para></summary>
    internal bool DragWipeLine(int lineIndex, double newPosition)
    {
        if (_wipeMosaic is not { } mosaic) return false;
        var lines = mosaic.LogicalLines();
        if (lineIndex < 0 || lineIndex >= lines.Length) return false;

        var (pos, axis, from, to) = lines[lineIndex];
        var next = mosaic.DragLine(axis, pos, newPosition, from, to);
        if (next.SameAs(_wipeMosaic)) return false;

        _wipeMosaic = next;
        ScheduleCompareCrop();
        return true;
    }

    /// <summary>拉动模式下的命中测试：先算点落在第几条带，再经带→路映射取路。
    ///
    /// <para><b>为什么必须单独判</b>：拉动下所有路的窗口都铺满且互相重叠，<c>HitSurfaceAt</c>
    /// 的通用循环按 <c>Bounds</c> 命中会恒选第 0 路，与用户实际看到的内容不符
    /// （探针 / 滚轮缩放 / 选中路径会读错路）。区域已经把"看不见的部分"裁掉了，
    /// 所以判定只需看"点落在哪条带"。</para></summary>
    private PlayerSurface? HitOverlayAt(Point windowPos)
    {
        var origin = Grid.TranslatePoint(new Point(0, 0), this);
        if (origin is not { } o) return null;
        var w = Grid.Bounds.Width;
        var h = Grid.Bounds.Height;
        if (w <= 0 || h <= 0) return null;

        var p = new Point(windowPos.X - o.X, windowPos.Y - o.Y);
        if (p.X < 0 || p.Y < 0 || p.X > w || p.Y > h) return null; // 点在对比区之外

        return HitWipeAt(p, w, h);
    }
}
