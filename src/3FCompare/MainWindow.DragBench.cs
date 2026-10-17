// ==============================================================================
// 3FCompare — 分割线拖动「提交成本」测量档（--dragbench）
// ==============================================================================
//
// 这个档量的是什么：
//   量的是宿主侧一次 OnPartitionChanged(GridPartition) 提交的重活成本 —— 覆盖层把
//   新分区回灌给宿主（宿主回调 = MainWindow.OnPartitionChanged，做
//   Update(partition) + ApplyCompareLayout() + ScheduleCompareCrop() 三件事）之后，
//   每一个 16ms 合并提交点上宿主实际花掉的 wall 时间，以及同一次提交带来的两次副作用：
//     1) 提交体自身的耗时分布 p50 / p95 / max；
//     2) 几路子 HWND 真的被重新安排了尺寸。
//        ⚠ 「期望 2」这个先验**已被核算否证**（2026-10-01，5 路 / 2×3 实测 medianResizes=5）：
//        权重模型下拖动的是**一条贯穿的整线**，它会把该线右侧（或下侧）的所有格一起推移
//        —— 2 列 × 3 行的网格里，动那条竖线就是 6 个格全部变宽/变位 ⇒ 路数几就重排几。
//        所以这个读数的意义是"重排是不是等于几何上必须动的格数"，而不是"越小越好"：
//        要砍重复 Win32 动作，得看**没有变化的格有没有被白重排**（那才是浪费），
//        本档现在把逐笔 changed 数报出来，判读口径以此为准。
//     3) SetWindowRgn 实际下发了几次（口径 = 既有埋点 RegionApply 那条，去重之后
//        「确实调了 Win32」的那一次；RegionClear / RegionClearAll 不计）。
//
// 这个档不量什么：
//   不量渲染帧率、不量 GPU/解码耗时、不量拖动跟手度、不量覆盖层自己的画线成本。
//   ProbeMoveTo 只是激励源（它转调的就是生产私有方法 BeginHandleDrag/ApplyDrag/
//   EndHandleDrag），它单次耗时不是被测对象。出帧率那一账归 --magnifybench。
//
// 关于 16ms 门槛的背书：
//   GestureCommitIntervalMs（默认 16）这个门槛是从「平移」那条已经实测过的纪律抄来的，
//   不是本站点自己的数 —— 目前它的背书借自 --magnifybench 的 [平移提交] 那一组
//   分位数（z=2、2 路：p50=0.03ms / p95=0.27ms / max=0.78ms，见
//   tests/output/magnifybench-pancommit-z2-20260929.log）。**本站点自己的数由这个档产出。**
//
// 命令行形态（与 --magnifybench 同族，位置参数一致）：
//   3FCompare.exe --dragbench <media> <routes>
//     <routes> 会被钳到 [2,9]（对比模式本就至少 2 路，网格上限 3x3=9 格）
//
// 输出约定（供门禁/账本摘录，勿改格式）：
//   人读行：dragbench: [拖动提交] routes=N commits=K p50=..ms p95=..ms max=..ms
//                    resizesP95=.. regionApplyTotal=..
//   机器行：dragbench: arm=N p50=.. p95=.. max=.. medianResizes=.. region=.. commits=K moves=M
//   同时经既有 ComponentLog 入口落一条 DragBenchSummary（Comp.Overlay，与
//   SplitChanged/RegionApply 同一事件流，不新建 sink）。
//
// 判据与几何一律不复制：朝向候选向 GridPartition.ShapesFor 要、手柄轴向向
// HandleAxisAt 要、位置向 HandlePositionAt 要、拖动由覆盖层探针口驱动。
// 本文件所有计数器都被 _dragBenchArmed 门住：非 bench 态生产路径零开销、零行为改动。
// ==============================================================================

using Avalonia;
using _3FCompare.Core.Display;
using _3FCompare.Diagnostics;
using _3FCompare.Services;

namespace _3FCompare;

public partial class MainWindow
{
    /// <summary>是否处于 dragbench 测量态。<b>唯一的门</b>：<see cref="OnPartitionChanged"/>
    /// 里的计时/计数与 <see cref="ApplyCropRegion"/> 里的区域下发记账都只在它为 true 时执行；
    /// false 时两条路径各自只剩一次布尔判断（无分配、无 IO、行为与改前逐项一致）。</summary>
    private bool _dragBenchArmed;

    /// <summary>每次提交的同步体耗时（ms，进入 OnPartitionChanged 到离开）。
    /// <para>刻意不含布局拍：Avalonia 的 Arrange 与 Background 优先级的裁剪派发都在
    /// <see cref="ScheduleCompareCrop"/> 返回之后才跑，那一段的<b>结果</b>由下面两列
    /// （子 HWND 尺寸变化数 / 区域下发数）在结算窗口里观测，不混进 wall 数字里。</para></summary>
    private readonly System.Collections.Generic.List<double> _dragBenchCommitMs = new();

    /// <summary>每次提交结算后「各路 Surface.Bounds 真的变了」的路数。
    /// <b>不要按"越小越好"读它</b>：贯穿整线的模型下，重排数约等于用到的格数是几何必然
    /// （2 列 × 3 行里动那条竖线 ⇒ 6 个格全变宽/变位）。有鉴别力的问法是
    /// "有没有<b>没变化</b>的格也被重排"，本档把逐笔 changed 数报出来就是给这个问法用的。</summary>
    private readonly System.Collections.Generic.List<int> _dragBenchResizeCounts = new();

    /// <summary>每次提交结算窗口内 RegionApply 的下发次数。</summary>
    private readonly System.Collections.Generic.List<int> _dragBenchRegionCounts = new();

    /// <summary>RegionApply 累计计数（口径见 <see cref="ApplyCropRegion"/> 里那一行记账）。</summary>
    private int _dragBenchRegionApplies;

    /// <summary>上一次提交开始时的各路 Bounds 快照；null = 没有待结算的提交。</summary>
    private Rect[]? _dragBenchPendingBounds;

    /// <summary>上一次提交开始时的 RegionApply 累计值（做差用）。</summary>
    private int _dragBenchPendingRegionBaseline;

    /// <summary>是否有一笔提交正等着在下一个观测点结算（拖动是单线程 UI 路径，不存在并发）。</summary>
    private bool _dragBenchHasPending;

    /// <summary>扫幅口径：照抄 SelfTest 的 DragCommitUnit —— 当前边界 ±5%。
    /// <para>为什么不是 0.30~0.70：后者在 ≥3 列时会被邻居的 5% 权重下界
    /// （<see cref="GridPartition.MinSpanFraction"/>）钳住，被钳住的 move 不改变几何、
    /// 于是不进分母 ⇒ 读数失真。±5% 保证整段扫幅都在可动区间内。</para></summary>
    private const double DragBenchSpanFraction = 0.05;

    /// <summary>探针喂入次数（默认 ≈ 高回报率鼠标持续拖动 3 秒以上的量）。
    ///
    /// <para>可以用 <c>FC_DRAGBENCH_MOVES</c> 调小。<b>为什么必须能调</b>：这一档按 16ms 连喂真实提交，
    /// 本身就是 dxgi/交换链那条崩溃带上的强激励（2026-10-01 实测：默认 200 次时 2 路与 5 路
    /// 四次全崩，一个分位数都没打出来）。要拿到成本分布做判断，先得让激励强度可调；
    /// 调小只改变样本数，不改变被测的那条路径。</para></summary>
    private static readonly int DragBenchMoves = EnvInt("FC_DRAGBENCH_MOVES", 200);

    private static int EnvInt(string name, int dflt)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) && v > 0 ? v : dflt;
    }

    /// <summary>两次 move 之间的间隔（ms）：必须真的等过 16ms 门槛，否则整轮只有一两次提交、
    /// 量不出分布；同时这一拍也让布局与 Background 优先级的裁剪派发落地，
    /// 于是「上一笔提交的副作用」恰好落在下一笔提交的开始时刻之前被观测到。
    /// 与 --magnifybench [平移提交] 的 `await Task.Delay(16)` 同一手法。</summary>
    private static readonly int DragBenchMoveIntervalMs = EnvInt("FC_DRAGBENCH_INTERVAL_MS", 16);

    /// <summary>判「这一路真的被重新安排了尺寸」的 Bounds 容差（DIP）。
    /// <para>取 0.001 DIP：远小于一个物理像素（1.5 缩放下 1px≈0.67DIP），故不会把真实变化读成
    /// 没变；又能吸收逐位不同但等值的浮点尾巴。没变化的那几格由同一份权重算出同一份矩形，
    /// 差值恒为 0，不会被这个容差掩盖。</para></summary>
    private const double DragBenchResizeEpsilonDip = 0.001;

    /// <summary>
    /// 是否以 dragbench 形态启动：<c>--dragbench &lt;media&gt; &lt;routes&gt;</c>（argc≥4）。
    /// <para>写成方法而不是内联条件，是为了让「白名单置位」与「分派」两处对
    /// 参数个数的理解只有这一个真源 —— <c>cmdArgs.Length &gt;= 2</c> 那条白名单
    /// 只认模式名，参数不足时由分派链末尾的 <c>else if (_selfTestMode)</c> 兜底
    /// （docs/41 P0-2），这里少要一个参数都会让兜底误报「参数不足」。</para>
    /// </summary>
    internal static bool ShouldRunDragBench(string[] args) =>
        args.Length >= 4 && args[1] == "--dragbench";

    /// <summary>
    /// 测量本体：起盘 <paramref name="routes"/> 路 → 按
    /// <see cref="GridPartition.ShapesFor"/> 的末项（该路数下格数最多的朝向）摆格 →
    /// 用覆盖层探针拖动一条分割线 <see cref="DragBenchMoves"/> 次 → 统计宿主提交成本并输出。
    /// <para>节奏参照 <c>RunMagnifyBenchAsync</c>：看门狗 → 起盘 → 等就绪 → 进对比 →
    /// 采样 → 打印 → <c>ExitSelfTest(code)</c>。只打印、不判红（除「一个样本都没拿到」
    /// 这种夹具失效），因为本站点还没有可作为阈值的基线数 —— 这个档就是用来产出它的。</para>
    /// </summary>
    /// <param name="videoPath">媒体路径（同一份素材按路数重复打开，与 magnifybench 同口径）。</param>
    /// <param name="routes">路数；钳到 [2,9]。</param>
    private async System.Threading.Tasks.Task RunDragBenchAsync(string videoPath, int routes)
    {
        // 先留一份原始请求：clamp 会静默改值，跑实验的人看的是输出不是注释（同 magnifybench）。
        var requestedRoutes = routes;
        routes = Math.Clamp(routes, 2, 9);
        // 总时限兜底：起盘 9 路 4K 的等待上限就是 30s，拖动整轮约 200×16ms≈3.2s，
        // 240s 与 comparemodetest / magnifybench 同一档（外部脚本的 timeout 都比它大）。
        StartDeadlineWatchdog("dragbench", 240);

        Console.WriteLine(
            $"dragbench: 路数={routes} 素材={videoPath} 移动次数={DragBenchMoves} " +
            $"间隔={DragBenchMoveIntervalMs}ms 扫幅=边界±{DragBenchSpanFraction:0.##} " +
            $"（门槛 {_3FCompare.Controls.LayoutOverlayWindow.ProbeCommitIntervalMs}ms 的背书目前借自平移档，本站点自测中）");
        if (requestedRoutes != routes)
            Console.WriteLine($"dragbench: ⚠ 路数已钳到 {routes}（请求 {requestedRoutes}，允许区间 [2,9]）");

        var code = 1;
        try
        {
            if (!File.Exists(videoPath))
            {
                Console.Error.WriteLine($"dragbench: 文件不存在 {videoPath}");
                ExitSelfTest(2);
                return;
            }

            _step = "拖动基准-打开多路";
            Grid.SetCount(routes, _realMode);
            _coordinator.OpenFiles(Enumerable.Repeat(videoPath, routes).ToList(), autoPlay: false);
            Console.WriteLine(
                $"dragbench: 请求打开 {routes} 路（{(_realMode ? "真实" : "演示")}），等待就绪...");

            var readyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < readyDeadline)
            {
                var snaps = _sync.ReadAllSnapshots();
                if (snaps.Count == routes &&
                    snaps.All(s => s is not null && PlaybackCoordinator.IsReadyState(s.State)))
                    break;
                await System.Threading.Tasks.Task.Delay(200);
            }
            var readyCount = _sync.ReadAllSnapshots()
                .Count(s => s is not null && PlaybackCoordinator.IsReadyState(s.State));
            if (readyCount != routes)
                throw new InvalidOperationException($"仅 {readyCount}/{routes} 路就绪");
            Console.WriteLine($"dragbench: {routes} 路就绪 ✓");

            await System.Threading.Tasks.Task.Delay(600);

            // 摆出「该路数下格数最多」的朝向：格数最多 ⇒ 一次提交要动的子 HWND 最多，
            // 是这条路径上最贵的形态（与 magnifybench 取 shapes[^1] 同一理由）。
            _step = "拖动基准-摆形状";
            var lanes = Math.Max(routes, Grid.Count);
            var shapes = GridPartition.ShapesFor(lanes);
            if (shapes.Count == 0)
                throw new InvalidOperationException($"dragbench 夹具：{lanes} 条 lane 没有朝向候选");
            var (cols, rows) = shapes[^1];
            await EnterGridShapeAsync(cols, rows);

            var ov = _layoutOverlay ?? throw new InvalidOperationException("dragbench：覆盖层未创建");
            if (!ov.IsOverlayVisible)
                throw new InvalidOperationException("dragbench：覆盖层不可见，探针灌进去的移动不会被处理");
            if (ov.HandlePositions().Length == 0)
                throw new InvalidOperationException($"dragbench：{cols}x{rows} 没有可拖分割线");

            // 手柄 0 管的是竖线还是横线由分区自己说（不复制判据）：
            // 竖线扫 fx、横线扫 fy，另一个分量固定 0.5 —— 与 DragCommitUnit 同一口径。
            var axis = _partition.HandleAxisAt(0);
            var handlePos = _partition.HandlePositionAt(0);
            var boundary = axis == SplitAxis.X ? handlePos.X : handlePos.Y;

            // 播放中量（与用户实际拖动的情境一致：轮询定时器与渲染线程都在跑，
            // 数字里因此含这份并发）。刻意不采 fps —— 帧率不是这个档的账（见文件头）。
            _step = "拖动基准-起播";
            _sync.Play();
            _isPlaying = true;
            await System.Threading.Tasks.Task.Delay(1500);

            _step = "拖动提交成本";
            ResetDragBenchCounters();
            _dragBenchArmed = true;

            // ── 驱动：抓第 0 条线 → 200 次移动（每次之间真的等过 16ms 门槛）→ 松手 ──
            // ProbeGrabHandle 会把提交时间戳清零，因此第一次 move 立刻提交（没有 16ms 死区）。
            ov.ProbeGrabHandle(0);
            for (var i = 0; i < DragBenchMoves; i++)
            {
                var v = boundary - DragBenchSpanFraction +
                        2 * DragBenchSpanFraction * i / (DragBenchMoves - 1);
                if (axis == SplitAxis.X) ov.ProbeMoveTo(v, 0.5);
                else ov.ProbeMoveTo(0.5, v);
                await System.Threading.Tasks.Task.Delay(DragBenchMoveIntervalMs);
            }
            var movesThatChangedGeometry = ov.ProbeDragMoves;
            ov.ProbeRelease();

            // 松手补发的那一笔也要结算：它没有「下一笔提交」来触发结算，只能在这里补一次观测点。
            // 先等一拍，让布局与 Background 优先级的裁剪派发落地（与拖动期间自然发生的两拍同构）。
            await System.Threading.Tasks.Task.Delay(80);
            SettlePendingDragBenchCommit();

            var commits = _dragBenchCommitMs.Count;
            var totalRegionApplies = _dragBenchRegionApplies;

            Console.WriteLine(
                $"dragbench: 形状={cols}x{rows} 手柄0={(axis == SplitAxis.X ? "竖线" : "横线")} " +
                $"边界={boundary:F5} 移动={DragBenchMoves}（改变几何 {movesThatChangedGeometry}）→ " +
                $"宿主提交 {commits} 次（覆盖层计数 {ov.ProbeHostCommits}）");

            if (movesThatChangedGeometry == 0)
                throw new InvalidOperationException(
                    $"dragbench 夹具：{DragBenchMoves} 次移动一次都没改变几何" +
                    $"（{cols}x{rows} 边界 {boundary:F5}±{DragBenchSpanFraction:0.##}）⇒ 扫幅失配，样本为空");
            if (commits == 0)
                throw new InvalidOperationException(
                    "dragbench：整轮没有一次宿主提交 ⇒ 通知线断了或门槛把全部移动都挡下，无成本可报");

            var ms = _dragBenchCommitMs.ToArray();
            Array.Sort(ms);
            var p50 = DragBenchPercentile(ms, 0.50);
            var p95 = DragBenchPercentile(ms, 0.95);
            var max = ms[^1];

            var resizes = _dragBenchResizeCounts.Select(x => (double)x).ToArray();
            Array.Sort(resizes);
            var medianResizes = DragBenchPercentile(resizes, 0.50);
            var resizesP95 = DragBenchPercentile(resizes, 0.95);

            var regions = _dragBenchRegionCounts.Select(x => (double)x).ToArray();
            Array.Sort(regions);
            var regionP95 = DragBenchPercentile(regions, 0.95);
            var regionSettledTotal = _dragBenchRegionCounts.Sum();

            // 人读行（格式对齐 magnifybench 的 [平移提交]）
            Console.WriteLine(
                $"dragbench: [拖动提交] routes={routes} commits={commits} p50={p50:F2}ms " +
                $"p95={p95:F2}ms max={max:F2}ms resizesP95={resizesP95:0.##} " +
                $"regionApplyTotal={regionSettledTotal}");
            // 机器可读行（arm = 路数档；commits/moves 追加在末尾，前面的字段顺序即摘录顺序）
            Console.WriteLine(
                $"dragbench: arm={routes} p50={p50:F2} p95={p95:F2} max={max:F2} " +
                $"medianResizes={medianResizes:0.##} region={regionSettledTotal} " +
                $"commits={commits} moves={movesThatChangedGeometry}");
            Console.WriteLine(
                $"dragbench: 附注 每笔提交结算窗口内区域下发 p95={regionP95:0.##}、" +
                $"累计 {totalRegionApplies} 次（RegionApply 口径，含本次之外的派发）；" +
                $"期望子 HWND 变化数=2（一条线只改相邻两格）");
            Console.Out.Flush();

            // 既有日志入口（Comp.Overlay 事件流，与 SplitChanged / RegionApply 同一条日志文件）
            if (ComponentLog.IsEnabled)
                ComponentLog.Log(Comp.Overlay, "DragBenchSummary", -1,
                    $"routes={routes} shape={cols}x{rows} moves={movesThatChangedGeometry}/{DragBenchMoves} " +
                    $"commits={commits} p50ms={p50:F2} p95ms={p95:F2} maxms={max:F2} " +
                    $"medianResizes={medianResizes:0.##} resizesP95={resizesP95:0.##} " +
                    $"regionApplyTotal={regionSettledTotal}");

            // ⚠ 不判红：成本阈值还没被这个档自己定出来，先落账本。
            // 唯一算失败的是「没有样本」，那已经在上面抛出走 code=1。
            code = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dragbench[步骤{_step}]: 失败 ✗ {ex.Message}");
            code = 1;
        }
        finally
        {
            // 门必须关：ExitSelfTest 之后的清理链（退出对比模式 / 关窗）还会走 OnPartitionChanged，
            // 留着 armed 只会往已经报完的样本里追加噪声。
            _dragBenchArmed = false;
            Console.Out.Flush();
            ExitSelfTest(code);
        }
    }

    /// <summary>清零 bench 状态（可重复调用；每次起测前调一次，避免上一轮残留）。</summary>
    private void ResetDragBenchCounters()
    {
        _dragBenchCommitMs.Clear();
        _dragBenchResizeCounts.Clear();
        _dragBenchRegionCounts.Clear();
        _dragBenchRegionApplies = 0;
        _dragBenchPendingBounds = null;
        _dragBenchPendingRegionBaseline = 0;
        _dragBenchHasPending = false;
    }

    /// <summary><see cref="OnPartitionChanged"/> 的计时入口。<b>非 bench 态立即返回 0</b>。
    /// <para>顺序很关键：先把<b>上一笔</b>提交结算掉（此刻它的布局拍与裁剪派发都已经落地），
    /// 再取本次的起点快照。于是「本次开始 → 下次开始」这段就是上一笔提交的完整结算窗口，
    /// 子 HWND 尺寸变化与区域下发都能归到真正引起它们的那一笔上。</para></summary>
    private long DragBenchEnterCommit()
    {
        if (!_dragBenchArmed) return 0L;
        SettlePendingDragBenchCommit();
        _dragBenchPendingBounds = SnapshotRouteBounds();
        _dragBenchPendingRegionBaseline = _dragBenchRegionApplies;
        _dragBenchHasPending = true;
        return System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary><see cref="OnPartitionChanged"/> 的计时出口。<paramref name="startTimestamp"/>
    /// 为 0（非 bench 态）时立即返回，不做任何分配。</summary>
    private void DragBenchExitCommit(long startTimestamp)
    {
        if (!_dragBenchArmed || startTimestamp == 0L) return;
        _dragBenchCommitMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(
            startTimestamp, System.Diagnostics.Stopwatch.GetTimestamp()).TotalMilliseconds);
    }

    /// <summary>把待结算的那一笔按「前后两拍的 Bounds 差 + RegionApply 差」记账。
    /// <para>没有待结算的那一笔（或本轮从没进入 bench 态）时立即返回。</para></summary>
    private void SettlePendingDragBenchCommit()
    {
        var before = _dragBenchPendingBounds;
        if (!_dragBenchHasPending || before is null) return;
        var after = SnapshotRouteBounds();
        var changed = 0;
        var n = Math.Min(before.Length, after.Length);
        for (var i = 0; i < n; i++)
            if (BoundsActuallyChanged(before[i], after[i])) changed++;

        _dragBenchResizeCounts.Add(changed);
        _dragBenchRegionCounts.Add(_dragBenchRegionApplies - _dragBenchPendingRegionBaseline);
        _dragBenchPendingBounds = null;
        _dragBenchHasPending = false;
    }

    /// <summary>逐路取 PlayerSurface 的 Bounds（DIP）。
    /// <para>为什么用 Bounds 而不是 GetWindowRect：PerMonitorV2 下布局量本身就是 DIP，
    /// 前后两拍同源即可判「有没有被重排」，SelfTest 的格表驱动布局断言读的就是同一份值。
    /// 表面尚未创建（null）记为 <c>default</c>，前后一致 ⇒ 不计入变化。</para></summary>
    private Rect[] SnapshotRouteBounds()
    {
        var n = Grid.Count;
        var arr = new Rect[n];
        for (var i = 0; i < n; i++) arr[i] = Grid.GetSurface(i)?.Bounds ?? default;
        return arr;
    }

    /// <summary>两拍 Bounds 是否算「真的被重新安排了」（位置或尺寸任一超容差）。</summary>
    private static bool BoundsActuallyChanged(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) > DragBenchResizeEpsilonDip ||
        Math.Abs(a.Y - b.Y) > DragBenchResizeEpsilonDip ||
        Math.Abs(a.Width - b.Width) > DragBenchResizeEpsilonDip ||
        Math.Abs(a.Height - b.Height) > DragBenchResizeEpsilonDip;

    /// <summary>排序数组取分位数。
    /// <para>取法与 --magnifybench 一致（<c>n*0.95-1</c> 那一档），不引第三方库：
    /// 索引 = round(p·n)-1，再夹进 [0, n-1]；空数组返回 NaN 并由调用方如实打印。</para></summary>
    private static double DragBenchPercentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return double.NaN;
        var idx = (int)Math.Round(p * sorted.Length) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sorted.Length) idx = sorted.Length - 1;
        return sorted[idx];
    }
}
