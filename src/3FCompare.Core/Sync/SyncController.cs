using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Sync;

/// <summary>多会话同步协调器（04 文档设计）。
/// - 以第 0 会话为 master，媒体时间（100ns）为规范时间轴；
/// - 支持 帧步进 / 秒步进 / 时间 Seek / 循环 / 偏移校准；
/// - <b>线程安全</b>：所有公开成员均可从任意线程调用。
/// <para><b>P1-3 修复说明</b>：本类原本没有任何同步保护，而调用方横跨三种线程——
/// UI 线程（键盘步进、菜单命令）、线程池（<c>Task.Run(() =&gt; StepFrames(...))</c>）、
/// 轮询定时器（<c>ReadAllSnapshots</c>，播放中每 16ms 一次）。
/// 并发下 <c>List</c> 的遍历与增删交错会抛 <c>InvalidOperationException</c>（集合已被修改）
/// 或索引越界，且症状随机、难以复现。</para>
/// <para>现在统一采用<b>「锁内取副本 → 锁外执行 → 锁外通知」</b>模式：
/// ① 取副本避免长时间持锁（帧步进内含 50ms 复测等待，持锁会阻塞关窗等 urgent 操作）；
/// ② 锁外触发 <c>StateChanged</c>，避免在锁内回调外部代码导致死锁；
/// ③ <c>Dispose</c> 同样移到锁外，避免原生释放期间持锁。</para></summary>
public sealed class SyncController
{
    private readonly object _gate = new();
    /// <summary>帧步进串行化。StepFrames 内含 Sleep(50) 复测，并发调用会交错
    /// 导致从路落在过期位置（docs/15 §2.4）。</summary>
    private readonly object _stepGate = new();
    private readonly List<SyncSlot> _slots = new();
    private StepProfile _profile = new();
    private bool _loopEnabled;
    private long _loopStart100ns = -1;
    private long _loopEnd100ns = -1;
    private string? _lastRuntimeError;

    /// <summary>单调毫秒时间源。存在的唯一理由是让"1s 漂移冷却"可被确定性测试
    /// （docs/41 §4.3「依赖真实挂钟」+ §4.5 配套基建 <see cref="IClock"/>）。</summary>
    /// <para>默认取 <see cref="Environment.TickCount64"/>：单调、不受系统时间调整影响，
    /// 即<b>不注入时钟时生产行为与改造前逐位一致</b>。只有测试显式注入
    /// <c>ManualClock</c> 时才改用注入时钟的毫秒刻度，从而把"等 1 秒"变成"跳 1 秒"，
    /// 去掉 <c>Thread.Sleep(1050)</c> 这类既慢又 flaky 的等待。</para>
    private readonly Func<long> _nowMs;

    public SyncController() : this(clock: null) { }

    /// <param name="clock">可注入时钟；<c>null</c> ⇒ 使用真实的单调毫秒计数（生产默认）。
    /// 仅影响漂移校正的节流判定，不影响任何位置/状态语义。</param>
    public SyncController(IClock? clock)
    {
        _nowMs = clock is null
            ? static () => Environment.TickCount64
            : () => clock.UtcNow.ToUnixTimeMilliseconds();

        // 漂移检测时戳必须用"当前时钟回拨一个检测周期"初始化，**不能留 0**：
        // 留 0 的含义是"上次检测发生在时钟原点"，而节流判据是 now − 上次 < 1s ⇒
        // 只要注入时钟的原点离 0 不足 1s（例如从 UnixEpoch 起算的 ManualClock，
        // 现有的 ManualClock 从 2026 起算所以线上没暴露），**首拍检测会被整拍吞掉**
        // ——而"刚打开就漂了"恰恰是最该校正的一拍。
        // 回拨一个周期等价于"上次检测刚好在一周期之前"：无论时钟原点在 epoch 还是
        // 2026，首拍一律放行，此后仍是每 1s 一次的节流语义（不变）。
        _lastDriftCheckTicks = _nowMs() - DriftCheckIntervalMs;
    }

    public sealed class SyncSlot
    {
        public required IPlayerSession Session { get; init; }
        public required string Path { get; init; }
        /// <summary>相对 master 的媒体时间偏移（100ns）。</summary>
        public long Offset100ns { get; set; }
        public bool Failed { get; set; }
        public string? Error { get; set; }

        /// <summary>本路是否<b>启用</b>：当前视图模式下真正占格的路为 true，没占格的为 false。
        ///
        /// <para><b>为什么不能只靠"隐藏"</b>：对比模式常把 N 路收敛到更少的格子（ABCD 只显示前 4 路、
        /// 左右拉动只显示 2 路），此前多余路只是 <c>IsVisible=false</c>，会话仍在满速解码 + Present。
        /// 实测（2026-09-27，5 路进分屏）：未占格的第 5 路 1.2 秒内 <c>PresentedVideoFrames</c> 仍涨 41，
        /// 与看得见的四路（39~45）同速 ⇒ 9 路用 ABCD 时有 5 路在白烧解码与呈现线程。</para>
        ///
        /// <para>为 false 时：会话被 Pause，且 <see cref="Play"/>/<see cref="Pause"/>/<see cref="Stop"/>/
        /// <see cref="RedrawAll"/>/<see cref="SeekTo"/>/帧秒步进/漂移校正一律跳过它。
        /// 重新启用时先 Seek 回规范时间再按当前播放态恢复，避免"切回来那一路还停在停用前的旧帧"。
        /// 第 0 路（master）例外：它是规范时间轴的基准，停用等于全部停，故恒为 true。</para>
        ///
        /// <para><b>读写走 Volatile</b>：启用态由 UI 线程写（<see cref="SetActiveRoutes"/>），
        /// 而 <see cref="Play"/> 也可能从线程池进来（<c>PlaybackCoordinator.CompleteBatch</c>），
        /// 漂移校正又在轮询线程读 —— 普通 bool 字段在这里没有可见性保证，
        /// 表现是"切回来的那一路偶尔不播"。</para></summary>
        private bool _active = true;
        public bool Active
        {
            get => System.Threading.Volatile.Read(ref _active);
            set => System.Threading.Volatile.Write(ref _active, value);
        }
    }

    public event EventHandler? StateChanged;

    /// <summary>运行时错误（最近一次被吞掉的会话异常；UI 可显示）。</summary>
    public string? LastRuntimeError
    {
        get { lock (_gate) { return _lastRuntimeError; } }
        private set { lock (_gate) { _lastRuntimeError = value; } }
    }

    /// <summary>记录运行时错误（不抛出不打断流程）。</summary>
    private void ReportRuntimeError(string action, Exception ex)
        => LastRuntimeError = $"{action}: {ex.Message}";

    /// <summary>触发 <see cref="StateChanged"/>，并隔离订阅者异常。
    ///
    /// <para><b>为什么必须吞掉订阅者抛出的异常</b>：StateChanged 的订阅者是 UI 层代码
    ///（状态栏、传输栏、媒体信息面板等），任何一个抛出都会沿调用栈穿透
    /// <c>Play/Pause/SeekTo/Clear</c> 的后续流程。典型事故：<see cref="Clear"/> 是在
    /// 释放完全部原生会话之后才发通知的，若此处抛出，UI 永远收不到"已清空"——
    /// 界面仍显示 9 路，而内核会话已全部释放，后续任何操作都是对已释放对象的调用。</para>
    ///
    /// <para>异常不是被丢弃，而是转记 <see cref="LastRuntimeError"/>（与会话异常同一套
    /// 上报通道，不新造机制），控制流程得以继续，UI 仍能看到这条错误。</para></summary>
    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { ReportRuntimeError("状态通知", ex); }
    }

    /// <summary>当前槽位的<b>副本</b>。返回副本而非内部 List，
    /// 使调用方遍历时不会因并发增删而抛异常（9 路规模的分配成本可忽略）。</summary>
    public IReadOnlyList<SyncSlot> Slots
    {
        get { lock (_gate) { return _slots.ToArray(); } }
    }

    public int Count
    {
        get { lock (_gate) { return _slots.Count; } }
    }

    /// <summary>取一份槽位副本（所有遍历操作的统一入口）。</summary>
    private SyncSlot[] SnapshotSlots()
    {
        lock (_gate) { return _slots.ToArray(); }
    }

    public StepProfile StepProfile
    {
        get { lock (_gate) { return _profile; } }
        set { lock (_gate) { _profile = value; } RaiseStateChanged(); }
    }

    public bool LoopEnabled
    {
        get { lock (_gate) { return _loopEnabled; } }
        set { lock (_gate) { _loopEnabled = value; } RaiseStateChanged(); }
    }

    public long LoopStart100ns
    {
        get { lock (_gate) { return _loopStart100ns; } }
        set { lock (_gate) { _loopStart100ns = value; } RaiseStateChanged(); }
    }

    public long LoopEnd100ns
    {
        get { lock (_gate) { return _loopEnd100ns; } }
        set { lock (_gate) { _loopEnd100ns = value; } RaiseStateChanged(); }
    }

    public void AddSlot(IPlayerSession session, string path)
    {
        lock (_gate)
        {
            _slots.Add(new SyncSlot { Session = session, Path = path });
            Volatile.Write(ref _fpsMismatchCache, -1);   // 路数变了，帧率差异需重算
        }
        RaiseStateChanged();
    }

    /// <summary>释放一路会话。**绝不静默吞异常**：Dispose 抛异常意味着这一路的原生资源
    /// （解码器、交换链、GPU 句柄）<b>并没有真的释放</b>，属于永久泄漏；而"忽略释放异常"
    /// 的写法让它只在句柄缓慢增长时才被察觉，届时已无法归因。
    ///
    /// <para><b>LockRecursionException 单独标注</b>：它不是"释放失败"，而是"释放时仍持有某个
    /// 递归锁/读写锁" ⇒ 说明调用点在锁作用域内，必须把 Dispose 移到锁外；当作普通异常忽略的话，
    /// 每次关会话都漏一路且日志全无痕迹（docs/45 P2：#9 的修复目标此前正是被 catch{} 吞掉的）。</para></summary>
    private static void DisposeSlotSession(SyncSlot slot, string caller)
    {
        try { slot.Session.Dispose(); }
        catch (Exception ex)
        {
            var kind = ex is System.Threading.LockRecursionException
                ? "（锁递归 ⇒ Dispose 仍在锁作用域内，这是调用点缺陷，不是释放失败）"
                : "";
            Diagnostics.AppLog.Warn("Sync",
                $"{caller}: 会话释放失败，该路原生资源泄漏: {ex.GetType().Name}: {ex.Message}{kind}");
        }
    }

    public void RemoveSlotAt(int index)
    {
        SyncSlot? slot;
        lock (_gate)
        {
            if (index < 0 || index >= _slots.Count) return;
            slot = _slots[index];
            _slots.RemoveAt(index);
            Volatile.Write(ref _fpsMismatchCache, -1);

            // 移除的是 master 时必须做**偏移重基准**（docs/15 §3.5）：
            // 各路的偏移都是"相对旧 master"的，旧 master 一走，语义就断了。
            // 新 master（原第 1 路）的偏移要归零，其余路要减去它原来的偏移值，
            // 否则全部路整体错位，且 A-B 循环区间也跟着偏。
            if (index == 0 && _slots.Count > 0)
            {
                var newMasterOffset = _slots[0].Offset100ns;
                if (newMasterOffset != 0)
                {
                    for (var i = 0; i < _slots.Count; i++)
                        _slots[i].Offset100ns -= newMasterOffset;

                    // A-B 循环区间是**规范时间轴**上的坐标，与各路偏移同一套坐标系。
                    // 偏移重基准后 canonical 时间整体平移了 newMasterOffset
                    //（新 canonical = 旧 canonical + newMasterOffset，因为
                    // media = canonical + offset 必须保持各路媒体位置不变），
                    // 区间不动就会指向偏移之前的另一段内容 —— 偏移量越大偏得越离谱。
                    // -1 是"未设置"哨兵（见 TickLoop），必须跳过，否则会被算成有效区间。
                    if (_loopStart100ns >= 0)
                        _loopStart100ns = Math.Max(0, _loopStart100ns + newMasterOffset);
                    if (_loopEnd100ns >= 0)
                        _loopEnd100ns = Math.Max(0, _loopEnd100ns + newMasterOffset);
                }
            }
        }
        // Dispose 移到锁外：原生释放期间不应持有锁
        DisposeSlotSession(slot, "RemoveSlotAt");
        RaiseStateChanged();
    }

    /// <summary>交换两路的位置（连同各自偏移、失败态、错误信息等<b>全部</b>槽位状态一起搬移）。
    ///
    /// <para><b>偏移语义</b>：各路偏移是<b>相对 master（第 0 路）</b>的媒体时间偏移，
    /// 规范时间轴以 master 为基准（见 <see cref="OffsetOf"/> 与 <see cref="SyncSlot.Offset100ns"/>）。
    /// 所以交换只改"槽位在列表中的次序"，**不改任何会话的媒体位置**；
    /// 但当交换涉及 master 时，基准本身换成了另一路，规范时间轴随之整体平移，
    /// 必须像 <see cref="RemoveSlotAt"/> 那样做<b>偏移重基准</b>，否则全部路错位。</para>
    ///
    /// <para><b>重基准推导</b>（独立于实现，便于复核）：设交换前规范时间为 T，
    /// 被换到第 0 位的那一路（旧偏移 <c>off_k</c>）的媒体位置为 T + off_k。
    /// 交换后它以自身为基准 ⇒ 新规范时间 T' = T + off_k。
    /// 于是任一路的新偏移 = 媒体位置 − T' = <b>旧偏移 − off_k</b>；
    /// master 的旧偏移按语义恒取 0（<see cref="OffsetOf"/> 已确立该约定，
    /// 第 0 位字段里可能残留老会话存档写入的历史值，不得当作有效偏移参与推导）。
    /// A-B 循环区间是规范时间轴上的坐标，同样要平移 off_k。</para>
    ///
    /// <para>不涉及 master 的交换（两路都在第 1 位之后）不改变基准，
    /// 偏移与循环区间原样随槽位搬移即可。</para>
    ///
    /// <para>线程安全：与 <see cref="AddSlot"/> / <see cref="RemoveSlotAt"/> 一致，
    /// 结构改动在 <see cref="_gate"/> 内完成，<c>StateChanged</c> 留在锁外触发。</para>
    /// </summary>
    /// <param name="indexA">第一个槽位索引。</param>
    /// <param name="indexB">第二个槽位索引。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一索引不在 <c>[0, Count)</c> 内（空列表亦然）。</exception>
    public void SwapSlots(int indexA, int indexB)
    {
        lock (_gate)
        {
            if (indexA < 0 || indexA >= _slots.Count)
                throw new ArgumentOutOfRangeException(nameof(indexA), indexA,
                    $"槽位索引越界（当前 {_slots.Count} 路）。");
            if (indexB < 0 || indexB >= _slots.Count)
                throw new ArgumentOutOfRangeException(nameof(indexB), indexB,
                    $"槽位索引越界（当前 {_slots.Count} 路）。");

            // 与自己交换是 no-op：不改状态、也不发通知。
            if (indexA == indexB) return;

            if (indexA == 0 || indexB == 0)
            {
                // 交换后落到第 0 位的那一路成为新 master，其旧偏移即规范轴的平移量。
                var newMasterOldOffset = _slots[indexA == 0 ? indexB : indexA].Offset100ns;

                // 先在**原位**重基准：槽位对象随后整体对调，偏移随对象一起搬移，不会错位。
                // i == 0 取语义值 0 而非其字段残留值，理由见方法注释。
                for (var i = 0; i < _slots.Count; i++)
                    _slots[i].Offset100ns = (i == 0 ? 0 : _slots[i].Offset100ns) - newMasterOldOffset;

                // A-B 循环区间与各路偏移同一套（规范时间轴）坐标系：
                // 新规范 = 旧规范 + newMasterOldOffset，区间不同步平移就会指向偏移之前的另一段内容。
                // -1 是"未设置"哨兵（见 TickLoop），必须跳过。
                if (newMasterOldOffset != 0)
                {
                    if (_loopStart100ns >= 0)
                        _loopStart100ns = Math.Max(0, _loopStart100ns + newMasterOldOffset);
                    if (_loopEnd100ns >= 0)
                        _loopEnd100ns = Math.Max(0, _loopEnd100ns + newMasterOldOffset);
                }

                // 基准路换了，以 master 为参照的帧率差异缓存结论不再成立，需重算。
                Volatile.Write(ref _fpsMismatchCache, -1);
            }

            (_slots[indexA], _slots[indexB]) = (_slots[indexB], _slots[indexA]);
        }
        RaiseStateChanged();
    }

    public void Clear()
    {
        SyncSlot[] doomed;
        lock (_gate)
        {
            doomed = _slots.ToArray();
            _slots.Clear();
            Volatile.Write(ref _fpsMismatchCache, -1);
            // 连同会话一起复位"不该跨会话残留"的状态。
            // 只清 _slots 会让上一次的 A-B 循环继续生效：加载一个未开循环的会话后
            // 仍然 LoopEnabled=true、TickLoop 仍按旧区间回绕，传输栏 Loop 也仍亮着
            // （docs/14 §3.1）。这里直接改字段而非走属性 setter，避免重复触发
            // StateChanged——循环了几次就多几个事件，末尾统一发一次。
            _loopEnabled = false;
            _loopStart100ns = -1;
            _loopEnd100ns = -1;
            _lastRuntimeError = null;
        }
        foreach (var slot in doomed)
        {
            DisposeSlotSession(slot, "CloseAll");
        }
        RaiseStateChanged();
    }

    /// <summary>读取 master（第 0 路）快照；无会话时返回 null。</summary>
    public EngineSnapshot? ReadMasterSnapshot()
    {
        var slots = SnapshotSlots();
        if (slots.Length == 0) return null;
        try { return slots[0].Session.ReadSnapshot(); }
        catch (Exception ex) { ReportRuntimeError("读取 master 快照", ex); return null; }
    }

    /// <summary>读取全部会话快照（UI 轮询用）。
    /// 拷贝语义：每次返回新数组，调用方可安全持有引用（9 路快照的分配成本可忽略）。</summary>
    public IReadOnlyList<EngineSnapshot?> ReadAllSnapshots()
    {
        var slots = SnapshotSlots();
        var snapshots = new EngineSnapshot?[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            try { snapshots[i] = slots[i].Session.ReadSnapshot(); }
            catch (Exception ex) { ReportRuntimeError($"读取第 {i} 路快照", ex); snapshots[i] = null; }
        }
        return snapshots;
    }

    /// <summary>播放扇出：<b>必须持 <see cref="_stepGate"/></b>。
    /// <para>为什么：帧步进是"先把所有路 Pause、再把各从路 Seek 到 master 新位置"这样一个整体
    /// （见 <see cref="StepFramesCore"/>）。中间插进来一次 <c>Play()</c> 会让从路在**还没对齐**
    /// 的位置上继续播 —— 正是那个方法注释里写明的"画面立刻错帧"。步进本身跑在线程池上，
    /// 所以这条不是理论风险。</para>
    /// <para>代价：步进在途时按播放会等它结束（含 50ms 复测，最坏约百 ms）。播放/暂停是用户
    /// 低频动作，这个等待换来的是"不会错帧"，值。<b><see cref="RedrawAll"/> 刻意不加闸门</b>：
    /// 它只让内核重画当前帧、不改位置也不改播放态，与步进交错最多多刷一帧，
    /// 而它由 resize/布局高频触发，加闸门反而会把布局阻塞在步进的等待上。</para></summary>
    public void Play()
    {
        WantPlaying = true;
        lock (_stepGate) { ForEachSlot("播放", s => s.Session.Play(), notify: false); }
        RaiseStateChanged();
    }

    /// <summary>"用户要的是播放"这一意图（不是某一路的当前状态）：未启用的路被 Play 跳过，
    /// 等它重新占格时由 <see cref="SetActiveRoutes"/> 按这个意图恢复，
    /// 否则切回那一格会停着不动。Pause/Stop/帧步进都会撤掉这个意图。
    /// <para>读写走 Volatile：<see cref="Play"/> 可能从线程池（打开完成回调）进来，
    /// 而 <see cref="SetActiveRoutes"/> 在 UI 线程读它。</para></summary>
    private bool _wantPlaying;
    private bool WantPlaying
    {
        get => System.Threading.Volatile.Read(ref _wantPlaying);
        set => System.Threading.Volatile.Write(ref _wantPlaying, value);
    }

    public void Pause()
    {
        WantPlaying = false;
        lock (_stepGate) { ForEachSlot("暂停", s => s.Session.Pause(), notify: false); }
        RaiseStateChanged();
    }

    /// <summary>向所有会话广播统一的视口变换（缩放 + 平移），保证多路看到同一区域。</summary>
    public void SetViewTransform(float zoom, float panX, float panY)
        => ForEachSlot("视图变换", s => s.Session.SetViewTransform(zoom, panX, panY), notify: false);

    public void Stop()
    {
        WantPlaying = false;
        // 与 Play 同理：Stop 会把所有路打出"停止"态，插在帧步进中间会让刚对齐的那一帧作废。
        lock (_stepGate) { ForEachSlot("停止", s => s.Session.Stop(), notify: false); }
        RaiseStateChanged();
    }

    /// <summary>通知各路重新呈现最后一帧（幂等，成本很低）。
    ///
    /// 内核契约（<c>FFF.Player.Api.h</c> 对 <c>FFF3FP_Redraw</c> 的说明）：
    /// "the App calls it after a child HWND resize **so flips continue issuing**"。
    /// 也就是说，子窗口尺寸变化后若不调 Redraw，呈现会停住——
    /// 表现为 Playing、位置照常推进，但 <c>PresentedVideoFrames</c> 不再增长。
    /// 这正是"最大化/还原后渲染停滞"的症状，而单靠 Pause→Play 唤不醒它。
    /// </summary>
    public void RedrawAll() => ForEachSlot("重绘", s => s.Session.Redraw(), notify: false);

    /// <summary>统一的"遍历所有<b>启用的</b>、未失败槽位"执行器：取副本 → 锁外执行 → 锁外通知。
    /// <remarks>用带索引的 for 而非 foreach：错误消息必须带路号。
    /// 多路对比下某一路挂掉时，"播放会话失败"这种没有路号的信息等于没有信息。</remarks>
    /// <para><see cref="SyncSlot.Active"/> 为 false 的路一并跳过：这正是"没占格的路不占性能"的实现点。
    /// 播放/暂停/停止/重绘/Seek/步进五条路径全部经过这里，所以只在这一处判据，
    /// 不在各调用方分别过滤（分头过滤必然漏一条 —— 漏掉的那条表现为"隐藏的路还在响"）。</para></summary>
    private void ForEachSlot(string actionName, Action<SyncSlot> action, bool notify)
    {
        var slots = SnapshotSlots();
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.Failed || !slot.Active) continue;
            try { action(slot); }
            catch (Exception ex) { ReportRuntimeError($"{actionName}第 {i} 路", ex); }
        }
        if (notify) RaiseStateChanged();
    }

    /// <summary>带槽位索引的遍历（<b>只走启用的路</b>，理由同 <see cref="ForEachSlot"/>）。
    /// 需要区分 master（第 0 路）的场合用它——
    /// 单独开一个方法而不是改上面那个的签名，是为了不动它已有的 4 处调用。</summary>
    private void ForEachSlotIndexed(string actionName, Action<SyncSlot, int> action, bool notify)
    {
        var slots = SnapshotSlots();
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.Failed || !slot.Active) continue;
            try { action(slot, i); }
            catch (Exception ex) { ReportRuntimeError($"{actionName}第 {i} 路", ex); }
        }
        if (notify) RaiseStateChanged();
    }

    /// <summary>把"当前视图模式真正占格"的那几路设为启用，其余<b>停用</b>（Pause + 不再参与任何逐路操作）。
    ///
    /// <para><b>为什么由 UI 调、判据却不在 UI 里</b>：可见路的唯一真源是 <c>CompareGridView</c> 的排布
    /// （格数、格↔路映射、单屏选择都在那里落地），UI 只负责把它算出的集合原样交进来；
    /// 停用/重新启用的动作与"跳过未启用路"的判据都留在本类。</para>
    ///
    /// <para><b>重新启用为什么要先 Seek</b>：停用期间它既不跟随 master 播放、也不被 Seek 与漂移校正碰过，
    /// 位置停在停用那一刻。直接 Play 会让用户看到"切回这一路时它是旧画面，然后自己跳回来"。
    /// 所以顺序是：标启用 → Seek 到 master 当前位置（叠加本路偏移，与 <see cref="SeekTo"/> 同语义）
    /// → 若全局在播放则恢复播放。</para>
    ///
    /// <para><b>master 恒启用</b>：第 0 路是规范时间基准，停用它等于全部停；
    /// 呈现停滞看门狗读的也是第 0 路（<c>MainWindow.PollSnapshots</c>）。</para></summary>
    /// <param name="active">当前占格的路号集合（不含 0 也会自动补上 0）。</param>
    /// <returns>状态发生变化的路数（0 表示这次调用什么都没改，调用方可据此免打日志）。</returns>
    public int SetActiveRoutes(IReadOnlySet<int> active)
    {
        var slots = SnapshotSlots();
        if (slots.Length == 0) return 0;

        var changed = 0;
        lock (_stepGate)
        {
            // 只在真的需要挪位置时才读 master 快照（停用路径用不到它）。
            long masterPos = 0;
            var masterRead = false;

            for (var i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.Failed) continue;
                var want = i == 0 || active.Contains(i);
                if (want == slot.Active) continue;
                try
                {
                    // 逐路留痕（只记录、不改判决）：本机 4K 多路的偶发崩溃带就落在这个窗口里
                    // （停用/重新启用的 Pause+Seek+Play 与裁剪下发交错），
                    // 没有这两行时崩溃只能定位到"某一拍之间"，定不到是哪一路的哪个动作。
                    _3FCompare.Core.Diagnostics.AppLog.Debug("Sync",
                        $"{(want ? "启用" : "停用")}第 {i} 路开始（playing={WantPlaying}）");
                    if (!want)
                    {
                        slot.Active = false;
                        slot.Session.Pause();
                    }
                    else
                    {
                        if (!masterRead)
                        {
                            masterPos = slots[0].Session.ReadSnapshot().Position100ns;
                            masterRead = true;
                        }
                        slot.Active = true;
                        slot.Session.Seek(ClampToDuration(slot, masterPos + slot.Offset100ns));
                        // "该不该立刻播"由本类自己记账，不接受调用方传参：
                        // 传参的版本要读 UI 线程的 _isPlaying，异步下发时可能读到过期值
                        // （表现为切回来的那一路停在正确位置却不播）。
                        if (WantPlaying) slot.Session.Play();
                    }
                    _3FCompare.Core.Diagnostics.AppLog.Debug("Sync", $"第 {i} 路{(want ? "启用" : "停用")}完成");
                    changed++;
                }
                catch (Exception ex) { ReportRuntimeError($"{(want ? "启用" : "停用")}第 {i} 路", ex); }
            }
        }
        if (changed > 0) RaiseStateChanged();
        return changed;
    }

    /// <summary>本路当前是否启用（供 UI 与自测读取，判据只有一份）。</summary>
    public bool IsRouteActive(int index)
    {
        var slots = SnapshotSlots();
        return index >= 0 && index < slots.Length && slots[index].Active;
    }

    /// <summary>当前<b>实测</b>未启用（没占格）的路号，升序。
    /// <para>状态栏文案读它，而不是拿"格数"去推"后几路"：做过左右互换 / 轮换之后，
    /// 没占格的未必是编号靠后的那几路；而 <see cref="SyncSlot.Failed"/> 的路由
    /// <see cref="SetActiveRoutes"/> 跳过、永远保持启用，按格数推会把它们也说成"已停用"。</para></summary>
    public IReadOnlyList<int> InactiveRoutes()
    {
        var slots = SnapshotSlots();
        var list = new List<int>();
        for (var i = 0; i < slots.Length; i++)
            if (!slots[i].Failed && !slots[i].Active) list.Add(i);
        return list;
    }

    /// <summary>第 0 路（master）是**规范时间轴的基准**，其偏移必须恒为 0。
    /// 给它加偏移等于把基准本身挪走：SeekTo(T) 会把 master 放到 T+off0，
    /// 而 StepFrames 又按 master 的裸位置去对齐从路 ⇒ 两套偏移语义互相矛盾，
    /// 每次微调都会累积，并且会被会话存档原样保存（docs/15 §3.1）。</summary>
    private static long OffsetOf(SyncSlot slot, int index) => index == 0 ? 0 : slot.Offset100ns;

    /// <summary>把时间点钳制到**该路自身**的时长内。
    ///
    /// 原先一律 <c>Clamp(0, long.MaxValue)</c>：多路片源时长不一致时，
    /// 短的那一路会被 Seek 到超出片尾的位置，表现是停在最后一帧或状态异常
    ///（docs/15 §3.4）。读取失败时退回原值，不因拿不到时长而阻断 Seek。
    /// </summary>
    private static long ClampToDuration(SyncSlot slot, long t)
    {
        if (t < 0) return 0;
        long duration;
        try { duration = slot.Session.ReadSnapshot()?.Duration100ns ?? 0; }
        catch { return t; }
        return duration > 0 ? Math.Min(t, duration) : t;
    }

    /// <summary>全部会话 Seek 到指定规范时间（各会话自动加自身偏移并 clamp）。
    ///
    /// <para><b>为什么本方法也要取 <see cref="_stepGate"/>（docs/41 #20）</b>：
    /// 调用方横跨两种线程——UI 线程（时间轴刮擦每 150ms 一次、书签跳转、快照恢复）
    /// 与 <see cref="StepFrames"/> 所在的线程池。两者都在做"逐路 Seek"，互不知情时
    /// 必然交错，且交错点就在循环中间：本方法 Seek 完第 0/1 路后，StepFrames
    /// 暂停全路并按 master 的新帧对齐全部从路，本方法再回来把剩下的路 Seek 到
    /// <b>步进之前</b>算出的目标 ⇒ 各路落在不同时间点。更常见的是反向顺序
    ///（步进先完成、本方法用步进前的 target 又 Seek 一遍）——从路刚被对齐到新帧
    /// 就被拉回旧位置，这正是"C4 已修"后错帧症状仍可复现的根因。</para>
    ///
    /// <para><b>锁的边界（含一处已知例外，勿据"通知一律在锁外"推断）</b>：本方法自身
    /// 只把"逐路 Seek"包进闸门，通知在 <c>lock</c> 块<b>之后</b>发。但"通知一律在锁外"
    /// 这句话对<b>整条调用链</b>不成立：<see cref="TickLoop"/> 是<b>持闸门</b>调本方法的，
    /// 于是 <c>TickLoop → SeekTo → RaiseStateChanged</c> 这条路径上的通知仍在闸门内触发
    /// （本次审查发现旧文案与实现不符，据实修正）。<see cref="RefreshAllPositions"/>、
    /// <see cref="StepFrames"/>、<see cref="Clear"/> 等入口则是真的在锁外发。</para>
    ///
    /// <para><b>为什么保留这处例外而不顺手改掉</b>：① 只在循环回绕的那一拍发生
    /// （<see cref="TickLoop"/> 由定时器以约 4Hz 驱动，绝大多数拍走"未启用/未越界"的早退分支）；
    /// ② 回调不会反向等闸门——<c>RaiseStateChanged</c> 已隔离订阅者异常，唯一的订阅者是
    /// UI 的 <c>UpdateStatus/UpdatePanelsForSelection</c>，只做非阻塞的控件更新，
    /// 因此"持闸门者等 UI 线程、UI 线程等闸门"这个环构造不出来；
    /// ③ 真要移出去，得把这里的 <c>SeekTo</c> 换成 <c>SeekToCore</c> 并在 <c>Monitor.Exit</c>
    /// 之后再补一次通知，收益是"少一次持锁回调"，代价是让回绕与通知不再原子——
    /// 风险大于收益，故本次<b>只把文案改准</b>，不动锁结构。
    /// 若将来新增会在回调里同步等待 UI 线程的订阅者（<c>Dispatcher.UIThread.Invoke</c> 之类），
    /// 这处例外必须一并处理，届时上面的死锁论证 ② 也不再成立。</para>
    ///
    /// <para><b>为什么不引入死锁</b>（三条独立论据，缺一不可）：
    /// ① <see cref="_stepGate"/> 是 <c>object</c> + Monitor，<b>不是</b>
    ///    <c>SemaphoreSlim</c>/<c>ReaderWriterLockSlim</c>：Monitor 对同一线程可重入，
    ///    所以 <see cref="TickLoop"/>（已持闸门）再调本方法只会增加递归计数，不会自锁。
    ///    本方法全程无 <c>await</c>，"锁不跨异步点"这一条自然成立。
    /// ② <b>本方法</b>持闸门期间只调原生会话方法，<b>从不等待 UI 线程</b>：引擎事件一律经
    ///    <c>Dispatcher.UIThread.Post</c>（非阻塞）投递，没有任何 <c>Invoke</c> 式同步回程。
    ///    因此"持闸门者等 UI 线程、UI 线程等闸门"这个环构造不出来，
    ///    本方法在 UI 线程上等闸门是<b>有界等待</b>（上限＝一次帧步进的耗时）。
    ///    ⚠ 该论据只覆盖本方法与 <see cref="StepFrames"/> 这类"锁外发通知"的路径；
    ///    <see cref="TickLoop"/> 会在持闸门时同步回调订阅者（见上面的锁边界说明），
    ///    其安全性依赖"订阅者不阻塞等 UI 线程"这一前提。
    /// ③ 持锁顺序仍是既有的 <c>_stepGate → _gate</c>（<see cref="StepFramesCore"/> 先持
    ///    闸门再取槽位副本，<see cref="ForEachSlotIndexed"/> 内的错误上报再取 <c>_gate</c>），
    ///    与 <see cref="TickLoop"/> 一致，没有反向获取 <c>_gate → _stepGate</c> 的路径，
    ///    故不存在锁序倒置。</para></summary>
    public void SeekTo(long target100ns)
    {
        lock (_stepGate) { SeekToCore(target100ns); }
        RaiseStateChanged();
    }

    /// <summary>逐路 Seek 的实体。调用方必须已持有 <see cref="_stepGate"/>；
    /// 自身不发通知——通知由调用方发，且<b>除 <see cref="TickLoop"/> 外</b>都在锁外发
    /// （<see cref="TickLoop"/> 持闸门期间无法在锁外发，属已知例外，理由见 <see cref="SeekTo"/>）。</summary>
    private void SeekToCore(long target100ns)
    {
        ForEachSlotIndexed("Seek", (slot, i) =>
        {
            slot.Session.Seek(ClampToDuration(slot, target100ns + OffsetOf(slot, i)));
        }, notify: false);
    }

    /// <summary>按帧步进（F12）：master 用 <c>StepFrame</c> 精确推进一帧，
    /// 其余各路 Seek 到 master 的新位置（各自叠加自身偏移）。
    ///
    /// <para><b>关于帧率</b>（docs/15 §2.3 复核结论）：本方法**没有**按帧率分支，
    /// 各路帧率不同时也从路按时间对齐——这是**刻意且正确**的。
    /// 帧率不同的两个片源，"同帧号"对应的时刻完全不同
    ///（24fps 的第 100 帧在 4.17s，60fps 的第 100 帧在 1.67s），按帧号对齐反而会让
    /// 画面指向毫无关系的内容；按**时间**对齐才是"同一时刻的画面"这一对比语义。
    /// 旧注释承诺的"帧率不一致时全部按时间换算"分支并不存在，也无需存在。
    /// 帧率差异本身由 <see cref="HasFpsMismatch"/> 暴露给 UI 提示用户。</para>
    ///
    /// <para>本方法可能阻塞数十至上百毫秒（含 50ms 复测等待与可能的 Seek），
    /// <b>不要在 UI 线程直接调用</b>——调用方应包 <c>Task.Run</c>。</para></summary>
    public void StepFrames(int frames)
    {
        // 串行化：本方法内含 Thread.Sleep(50) 复测且原先不持锁，
        // 连按方向键时两个任务会交错（各自读快照、StepFrame、Sleep、Seek 从路），
        // 从路可能落地在先发任务算出的旧位置（docs/15 §2.4）。
        // 这把锁同时也是与 TickDrift / TickLoop 的互斥凭证：那两者用 TryEnter
        // 非阻塞探测它，取不到就整拍跳过，从而不打断一次完整的"暂停→步进→对齐"。
        // 注意 StateChanged 必须留在锁外，避免在锁内触发事件导致重入。
        lock (_stepGate)
        {
            StepFramesCore(frames);
        }
        RaiseStateChanged();
    }

    private void StepFramesCore(int frames)
    {
        var slots = SnapshotSlots();
        if (slots.Length == 0) return;

        // 步进前**必须**先把全部路暂停。
        // master 走 StepFrame 时内核会 SetState(Paused)（PlayerSession.cpp:969），
        // 而从路走 Seek —— Seek 不改变播放状态。若在播放中步进，
        // master 停在这一帧、其余各路 Seek 完继续播放，画面立刻错帧。
        // 逐帧对比的前提就是"所有路停在同一帧"（docs/15 §2.1）。
        // 同时撤掉"要播放"的意图：否则此间被停用的路重新占格时会被自动 Play，
        // 把刚对齐好的那一帧又推走（与上面那条同一个道理）。
        WantPlaying = false;
        for (var i = 0; i < slots.Length; i++)
        {
            // 未启用的路本来就停着，不必再 Pause；对齐也一样跳过（见 SyncSlot.Active）
            if (slots[i].Failed || !slots[i].Active) continue;
            try { slots[i].Session.Pause(); }
            catch (Exception ex) { ReportRuntimeError($"帧步进前暂停第 {i} 路", ex); }
        }

        var master = slots[0];
        try
        {
            var snap = master.Session.ReadSnapshot();
            var duration = snap.Duration100ns;
            var fps = EstimateFps(snap);

            // 尝试精确帧步进：master 直接 StepFrame，其余按 master 新位置对齐
            var oldPos = snap.Position100ns;
            var oldFrame = snap.FrameIndex;

            // 内核 StepFrame **只接受 ±1**（PlayerSession.cpp:824 对其余值返回
            // InvalidArgument），而 AppSettings.FrameStep 允许 1~999。
            // 原先无条件透传 ⇒ 步长 >1 时每次步进都先抛一次异常，再降级到时间换算。
            // 这里只在 ±1（默认且最常见）时走精确帧步进；其余直接走时间换算，
            // 既消除无谓的报错，也避免依赖内核 ScheduleStep 的 repeat 语义。
            //
            // 原先在分支前还读了一次快照给 newSnap/newPos 赋初值，纯属浪费——
            // 只有 frames==±1 分支会用到这对变量，且分支内第一件事就是重新读。
            // 初值改为由 oldPos 兜底：步长 >1 且帧率未知时从路保持在原位置。
            EngineSnapshot newSnap;
            var newPos = oldPos;
            var stepApplied = false;
            if (frames == 1 || frames == -1)
            {
                try { master.Session.StepFrame(frames); }
                catch (Exception ex) { ReportRuntimeError("帧步进 master", ex); /* 回退时间步进 */ }

                newSnap = master.Session.ReadSnapshot();
                newPos = newSnap.Position100ns;
                // 用 timelineGeneration 判定 StepFrame 是否真正生效（seek 成功才递增）。
                // 旧判据 newPos==oldPos 在异步解码下不可靠：StepFrame 入队后快照大概率
                // 仍返回旧位置，会被误判"不支持"而降级为时间 Seek。
                // StepFrame 在内核侧异步执行，立即读快照大概率还没落地——先给一次
                // 短等待复测（50ms），仍无变化才降级为时间换算，避免每次帧步进都
                // 付出 av_seek_frame + 解码器 flush 的全量成本。
                stepApplied = newSnap.TimelineGeneration != snap.TimelineGeneration ||
                              newSnap.FrameIndex != snap.FrameIndex;
                if (!stepApplied)
                {
                    System.Threading.Thread.Sleep(50);
                    newSnap = master.Session.ReadSnapshot();
                    newPos = newSnap.Position100ns;
                    stepApplied = newSnap.TimelineGeneration != snap.TimelineGeneration ||
                                  newSnap.FrameIndex != snap.FrameIndex;
                }
            }   // 结束：仅在 ±1 时才尝试精确帧步进
            if (!stepApplied && fps > 0)
            {
                // StepFrame 复测后仍无变化（可能不支持），按时间换算
                Diagnostics.AppLog.Debug("Sync", $"StepFrame 未生效，回退时间步进 fps={fps} oldFrame={oldFrame}");
                newPos = FrameTimeline.StepByFrames(oldPos, duration, frames, fps);
                master.Session.Seek(newPos);
                newSnap = master.Session.ReadSnapshot();
                newPos = newSnap.Position100ns;
            }

            // 其余会话按 master 新位置对齐
            for (var i = 1; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.Failed || !slot.Active) continue;
                try
                {
                    slot.Session.Seek(ClampToDuration(slot, newPos + slot.Offset100ns));
                }
                catch (Exception ex) { ReportRuntimeError($"帧步进对齐第 {i} 路", ex); }
            }
        }
        catch (Exception ex) { ReportRuntimeError("帧步进读取 master", ex); /* master 读取失败则整体跳过 */ }
        // 注意：不要在这里触发 StateChanged —— 它已移到外层 StepFrames 的锁外，
        // 以免在 _stepGate 内触发事件造成重入。
    }

    /// <summary>按秒步进（F12）：全部会话按规范时间换算后 Seek。
    ///
    /// <para><b>为什么本方法也要取 <see cref="_stepGate"/>（docs/41 #20）</b>：
    /// 本方法是一个不可分割的整体——"读 master 位置 → 换算目标 → 逐路 Seek"。
    /// 若不与帧步进互斥，"读"与"Seek"之间可以被塞进一次完整的帧步进，于是目标值按
    /// <b>步进前</b>的 master 位置算出，却作用在<b>步进后</b>的会话上 ⇒ 各路落回旧时间点。
    /// 因此锁必须覆盖"读 + Seek"整段，只锁 Seek 那一段是无效的。</para>
    ///
    /// <para>锁边界与死锁论证同 <see cref="SeekTo"/>：无 <c>await</c>、Monitor 可重入、
    /// 持闸门期间不等待 UI 线程、锁序仍是 <c>_stepGate → _gate</c>。</para></summary>
    public void StepSeconds(double seconds)
    {
        lock (_stepGate)
        {
            var slots = SnapshotSlots();
            // 空列表直接返回：lock 在 return 处正常释放，且与原实现一样不发通知。
            if (slots.Length == 0) return;
            var master = slots[0];
            try
            {
                var snap = master.Session.ReadSnapshot();
                var target = FrameTimeline.StepBySeconds(snap.Position100ns, snap.Duration100ns, seconds);
                // 走 Core 版本而不是 SeekTo：闸门已在手，再进 SeekTo 只是多一层递归计数，
                // 且会把通知提前到锁内触发。
                SeekToCore(target);
            }
            catch (Exception ex) { ReportRuntimeError("秒步进读取 master", ex); }
        }
        RaiseStateChanged();
    }

    /// <summary>获取当前规范时间（master 位置，未含偏移）。</summary>
    public long GetMasterPosition100ns()
    {
        var snap = ReadMasterSnapshot();
        return snap?.Position100ns ?? 0;
    }

    /// <summary>偏移变动后，让所有会话按新偏移重新对齐（位置不变，各会话实际 Seek 到 = master ± offset）。
    ///
    /// <para><b>为什么本方法也要取 <see cref="_stepGate"/>（docs/41 #20）</b>：与
    /// <see cref="StepSeconds"/> 同构——"读 master 位置 + 逐路 Seek"是一个整体。
    /// 夹进一次帧步进就会用<b>步进前</b>的 master 位置重排全部从路，把刚对齐好的画面推走；
    /// 本方法由 UI 线程调用（改偏移的那几个入口），帧步进在线程池，正是会撞上的组合。</para>
    ///
    /// <para>锁边界与死锁论证同 <see cref="SeekTo"/>。注意 master（第 0 路）本就被跳过，
    /// 闸门在这里保护的纯粹是"读到的 master 位置"与"从路 Seek 依据"的一致性。</para></summary>
    public void RefreshAllPositions()
    {
        lock (_stepGate)
        {
            var masterPos = GetMasterPosition100ns();
            ForEachSlotIndexed("偏移重对齐", (slot, i) =>
            {
                // master 的位置本来就是基准，不能再按它自己的偏移挪一次
                if (i == 0) return;
                // 必须走 ClampToDuration，与 <see cref="SeekTo"/> 保持同一语义：
                // 原先只做 Math.Clamp(0, long.MaxValue)，短片 + 正偏移时会把该路
                // Seek 到超出自身片尾的位置（停在最后一帧或进入异常态），
                // 而其他入口都已经按各自时长钳制，唯独这里漏了 ⇒ 同一次偏移调整
                // 下各路行为不一致。ClampToDuration 已含下界 0 的钳制。
                slot.Session.Seek(ClampToDuration(slot, masterPos + slot.Offset100ns));
            }, notify: false);
        }
        RaiseStateChanged();
    }

    public long GetMasterDuration100ns()
    {
        var snap = ReadMasterSnapshot();
        return snap?.Duration100ns ?? 0;
    }

    /// <summary>从快照估算帧率（fps）。
    /// 优先用快照携带的媒体帧率（来自媒体信息 nominalFrameRate，准确）。
    /// 回退：帧 PTS 增量换算（fps = timeBaseDen / (timeBaseNum × pts增量)），
    /// 注意 frameTimeBase 是流时间基（如 1/15360）而非帧率——直接 Den/Num 是错的
    /// （曾导致 4K H.264 显示 15360fps 的 bug）。无数据时回退 24。
    ///
    /// <para><b>用途边界（P4）</b>：24 这个回退值是<b>内部漂移校正</b>的既定语义
    ///（半帧阈值、<see cref="HasFpsMismatch"/> 判定），不得更改。
    /// <b>显示路径请勿直接用它</b>：快照的 <c>FrameRate</c> 取自引擎的媒体信息缓存，
    /// 缓存未预热时它是 0，于是显示会被回退值谎报成 24fps。显示应优先用
    /// <c>EngineMediaInfo.FrameRate</c>（内核 JSON 的 streams[].nominalFrameRate），
    /// 本方法仅作兜底。</para></summary>
    public static double EstimateFps(EngineSnapshot snap)
    {
        // 非有限值（NaN / ±∞）同样不可用作帧率：NaN 会让下游所有比较恒为 false，
        // +∞ 会让"半帧阈值"算出 0（任何微小偏差都触发校正）。
        if (double.IsFinite(snap.FrameRate) && snap.FrameRate > 0) return snap.FrameRate;
        // 无帧率数据时保守回退 24：frameTimeBase 是流时间基（如 1/15360）而非
        // 帧率，无法从它推导 fps（曾导致 4K H.264 显示 15360fps 的 bug）。
        return 24.0;
    }

    // ──────────── 漂移检测与校正（docs/15 §3.3）────────────

    /// <summary>检测周期。各路独立时钟，长片播放必然发散，所以要周期对齐。</summary>
    private const long DriftCheckIntervalMs = 1000;

    /// <summary>上次漂移检测的时钟刻度。由构造函数用"当前时钟 − 一个检测周期"初始化
    /// （首拍必须放行，不能留 0，理由见构造函数）。</summary>
    private long _lastDriftCheckTicks;

    // 帧率不会变，所以"是否存在帧率差异"只需算一次；槽位增减时失效。
    // -1 = 尚未计算，0 = 无差异，1 = 有差异
    private int _fpsMismatchCache = -1;

    /// <summary>各路帧率是否与 master 存在显著差异（相对差 &gt; 1%）。
    ///
    /// <para>帧率不同时"同帧号"并非同一时刻的内容（24fps 的第 100 帧在 4.17s，
    /// 60fps 的第 100 帧在 1.67s），逐帧对比的语义会随之改变，需要让用户知道
    ///（docs/15 §3.6）。本属性供状态栏/提示使用，结果惰性缓存。</para>
    ///
    /// <para><b>缓存前提（C3）</b>：只有当"所有非失败路都拿到了真实帧率
    ///（<c>FrameRate &gt; 0</c>）"时才允许写入缓存。9 路并行打开时每装载完一路
    /// 就触发一次计算，此刻尚未打开完的路 <c>FrameRate == 0</c>；若把这种中间态
    /// 缓存下来，状态栏的「⚠ 帧率不一致」就会常驻且<b>永不自愈</b>（帧率不会变，
    /// 缓存也就再也不会失效）。中间态一律不写缓存，每拍重算。</para>
    ///
    /// <para><b>基准必须是 master</b>：master（第 0 路）失败或读不到帧率时判为
    /// "无法判定"（返回 false 且不缓存），而不是跳过它改用第 1 路当基准——
    /// 那样"与 master 不一致"的语义就被悄悄改成了"与第一条可用路不一致"，
    /// 结论会随哪一路失败而漂移。</para>
    /// </summary>
    public bool HasFpsMismatch
    {
        get
        {
            var cached = Volatile.Read(ref _fpsMismatchCache);
            if (cached >= 0) return cached == 1;

            var slots = SnapshotSlots();
            // 路数不足没有"不一致"可言。这里也刻意不写缓存：装载过程中路数持续增长，
            // 写进去等于把"只开了一路"的中间态固化成永久结论。
            if (slots.Length < 2) return false;

            if (slots[0].Failed) return false;
            EngineSnapshot masterSnap;
            try { masterSnap = slots[0].Session.ReadSnapshot(); }
            catch { return false; }     // 读不到基准 ⇒ 无法判定（不缓存，下次重算）

            // 这里刻意不经过 EstimateFps：它的 24fps 兜底会把"尚未装载完成
            //（FrameRate == 0）"的路伪造成 24fps，再与真实 25/30 一比立刻判为
            // 不一致 —— 那正是打开过程中误报告警的根因。读取中的路直接跳过。
            if (masterSnap.FrameRate <= 0) return false;

            var masterFps = masterSnap.FrameRate;
            var allKnown = true;    // 是否所有非失败路都拿到了真实帧率
            var result = false;
            for (var i = 1; i < slots.Length; i++)
            {
                if (slots[i].Failed) continue;
                EngineSnapshot snap;
                try { snap = slots[i].Session.ReadSnapshot(); }
                catch { allKnown = false; continue; }
                if (snap.FrameRate <= 0) { allKnown = false; continue; }

                if (Math.Abs(snap.FrameRate - masterFps) / masterFps > 0.01) { result = true; break; }
            }

            // 只有"每一路都拿到了真实帧率"才允许缓存：装载中的中间态每拍都在变，
            // 缓存它就会让错误告警不再自愈。
            if (allKnown) Volatile.Write(ref _fpsMismatchCache, result ? 1 : 0);
            return result;
        }
    }

    /// <summary>播放期漂移检测与校正：以 master 为 leader，其余路为 follower。
    ///
    /// <para>背景：Play() 只是逐路下发一次播放命令，之后各路按自己的时钟走，
    /// 没有任何对齐机制；9 路下长片必然发散，且首帧就存在启动时间差
    ///（TryAutoPlayAfterOpen 逐路 Play）。</para>
    ///
    /// <para>策略（保守优先，宁可少校正也不要抖）：
    /// ① 只在 master **播放中**校正——暂停/帧步进时机位由用户或步进逻辑精确控制，
    ///    插手反而会破坏刚对齐的状态；
    /// ② 偏差超过**半帧**才动。半帧是"看得出错帧"的下限，低于此值不动；
    /// ③ 每 1s 最多校正一次（冷却），杜绝抖动；
    /// ④ 只校正 follower，master 是基准，永不校正；
    /// ⑤ 从路**自身内容已放完**（期望位置超出该路时长）时视为已收敛，不校正也不重复 Seek
    ///   —— 多素材时长不同是常态，短的那一路停在最后一帧就是正确行为；
    /// ⑥ 从路在自身内容范围内却不在期望位置时，**不看它的播放状态**（Playing / Ended
    ///   都校正，Ended 还要补一次 Play 恢复推进）；只有 Paused 刻意排除（见
    ///   <see cref="TickDriftCore"/> 内注释）。这一条是 docs/41 #21 的修复点：
    ///   旧写法 `State != Playing ⇒ 跳过` 会让从路一旦落到非播放态就**永久失步**。</para>
    ///
    /// <para>本方法由轮询调用，自身按 <see cref="DriftCheckIntervalMs"/> 节流。</para>
    /// </summary>
    public void TickDrift()
    {
        // ── 与帧步进互斥（docs 审查项 C4）──
        // StepFrames 走线程池（内部含 50ms 复测等待），本方法走 UI 轮询定时器。
        // 两者完全不互相知情时的典型交错是：
        //   ① 本方法读完快照得到 masterPos；
        //   ② StepFrames 暂停全路、把各从路精确对齐到新帧；
        //   ③ 本方法用步骤①的**过期** masterPos 去 Seek 从路 ⇒ 刚对齐好的画面又被推走。
        // 表现为"播放中按方向键逐帧后画面随机错帧"，且难以复现。
        // 这里用 TryEnter 非阻塞取锁：取不到就整拍跳过（1s 后自然补下一拍），
        // **绝不能**改成 lock —— StepFramesCore 内有 Thread.Sleep(50)，会卡住 UI 线程。
        if (!Monitor.TryEnter(_stepGate)) return;
        try
        {
            TickDriftCore();
        }
        finally
        {
            Monitor.Exit(_stepGate);
        }
    }

    private void TickDriftCore()
    {
        var slots = SnapshotSlots();
        if (slots.Length < 2) return;   // 单路没有漂移可言

        var now = _nowMs();
        if (now - _lastDriftCheckTicks < DriftCheckIntervalMs) return;

        // 先读一次快照：判定播放态需要它，后面算偏差也用它，避免重复读
        var snaps = new EngineSnapshot?[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            // 未启用的路不参与校正，连快照都不必读（每秒一次 × 多余路，纯浪费）。
            if (slots[i].Failed || !slots[i].Active) continue;
            try { snaps[i] = slots[i].Session.ReadSnapshot(); }
            catch (Exception ex) { ReportRuntimeError($"漂移检测第 {i} 路", ex); }
        }

        var master = snaps[0];
        if (master is null || master.State != PlayerState.Playing)
        {
            // 非播放态不校正，但仍要更新时戳，否则恢复播放瞬间会立刻触发一次
            _lastDriftCheckTicks = now;
            return;
        }

        var masterPos = master.Position100ns;
        var fps = EstimateFps(master);
        // 阈值 = 半帧；拿不到帧率时退化为 20ms（约 50fps 的半帧），同样保守
        var threshold = fps > 0
            ? (long)(TimeSpan.TicksPerSecond / (2.0 * fps))
            : 20 * TimeSpan.TicksPerMillisecond;

        for (var i = 1; i < slots.Length; i++)
        {
            // 未启用（当前模式没占格）的路不校正：它被有意停在停用那一刻，
            // 重新启用时由 SetActiveRoutes 统一 Seek 回规范时间。
            if (slots[i].Failed || !slots[i].Active) continue;
            var s = snaps[i];
            if (s is null) continue;

            // ── 哪些状态参与校正（docs/41 #21）──
            // 旧写法是 `s.State != Playing ⇒ continue`，一刀切且**没有替代判据**：
            // 从路一旦被钉在片尾就永远不再被校正（详见下面的情形 A/B）。
            // Playing：常态。
            // Ended  ：从路已放到自己的片尾（内核 FlushAtEnd 置 Ended 并把位置钉在时长上）。
            //          必须参与校正——它可能就是"提前结束"或"内容范围内却被落在非播放态"
            //          的那一路（情形 B），跳过就永久失步。
            // Paused ：**刻意排除**。暂停在这里是"这一路被有意移出播放组"的语义：
            //          multitest 的判别实验正是靠直接 Pause 部分路来减少并发 Present
            //          （见 MainWindow.SelfTest 的"被暂停的路不会被自动恢复播放"），
            //          自动恢复播放会破坏该语义与其实验结论，故只对 Ended 做恢复。
            //          ⚠ 已知残留（刻意不在此处解决）：广播 Seek 会把 Ended 从路变成 Paused
            //          （内核 DoSeek 尾部），此后它落进本排除项、不再被自动恢复。
            //          要覆盖它就得区分"被谁暂停"，那需要 SyncController 自己记账，
            //          属另一条独立决策，不在本次 #21 范围内。
            // 其余（Idle/Opening/Ready/Closed/Failed）：尚未就绪，交给打开流程处理，别插手。
            if (s.State is not (PlayerState.Playing or PlayerState.Ended)) continue;

            var expected = masterPos + slots[i].Offset100ns;

            // ── 情形 A：该路自身的内容已经放完（期望位置超出它**自己**的时长）──
            // 多素材时长不同是**常态**（本项目要求按时间对齐而非帧号，从路比 master 短
            // 是必须支持的场景）。此时从路停在最后一帧就是正确行为：
            //   · 不该把它"往回拉"——它已经无处可去，强行 Seek 只会抖动；
            //   · 也不该每秒重复 Seek 一次。旧代码在从路仍是 Playing 时会持续
            //     Seek(被 clamp 到片尾的期望值)，每一次都是无谓的 av_seek_frame +
            //     解码器 flush；9 路下就是周期性 CPU 尖峰与无谓的画面重建。
            if (s.Duration100ns > 0 && expected > s.Duration100ns) continue;

            // ── 情形 B：该路在自身内容范围内，位置就应该等于 expected ──
            // 偏差在半帧以内视为已收敛。这一条同时覆盖了"从路真到片尾、master 也快到片尾"
            // 的正常收敛：此时从路位置≈自身时长≈expected，差值落在半帧内，直接跳过，
            // 因此不会出现"来回拉"的抖动或循环。
            // ⚠ 不能裸写 Math.Abs(a - b)：任一端是 long.MinValue 时会抛 OverflowException，
            // 而本行在 try **之外** ⇒ 异常冒到轮询的顶层 catch 被吞，表现为位置/时间码停更、
            // 每拍重抛、界面形似卡死（docs/45 P1-9）。
            // 加载侧（MainWindow 载入 .3fcs）已把偏移钳制到 ±24h，这里是二次防御：
            // 期望值仍然离谱时放弃该路本拍的校正，好过把整个轮询打断。
            if (expected < -_3FCompare.Core.Settings.SessionSnapshot.MaxOffset100ns ||
                expected > _3FCompare.Core.Settings.SessionSnapshot.MaxOffset100ns)
                continue;

            if (Math.Abs(s.Position100ns - expected) <= threshold) continue;

            try
            {
                slots[i].Session.Seek(ClampToDuration(slots[i], expected));
                // Ended 从路必须补一次 Play 才会继续推进：Seek 只把它从 Ended 改成 Paused
                //（内核 PlayerSession::DoSeek 尾部：`if (state == Ended) SetState(Paused,"seek")`），
                // 位置对了却停着不动 ⇒ 下一拍又判为偏差 ⇒ 每秒一次 Seek 风暴且画面永久冻住。
                // 顺序必须是"先 Seek 后 Play"：Play 只对 **Ended** 才走 DoSeek(0) 的重播分支，
                // 而上面的 Seek 已把状态改成 Paused，所以这次 Play 从 expected 续播、不会回到 0。
                if (s.State == PlayerState.Ended) slots[i].Session.Play();

                Diagnostics.AppLog.Debug("Sync",
                    $"漂移校正 路{i} 态={s.State} Δ={s.Position100ns - expected} → Seek {expected}");
            }
            catch (Exception ex) { ReportRuntimeError($"漂移校正第 {i} 路", ex); }
        }

        _lastDriftCheckTicks = now;
    }

    /// <summary>处理循环：若开启区间循环且 master 位置越过终点，Seek 回起点。</summary>
    public void TickLoop()
    {
        // 与 TickDrift 同理（C4）：帧步进期间读到的 master 位置会在步进落地后失效，
        // 用过期位置判定"是否越过终点"会误回绕、并把各路一起 Seek 到错误起点。
        // 同样非阻塞：取不到锁就跳过本拍，循环的判定下一拍会自然补做。
        if (!Monitor.TryEnter(_stepGate)) return;
        try
        {
            bool enabled; long end, start;
            lock (_gate)
            {
                enabled = _loopEnabled;
                end = _loopEnd100ns;
                start = _loopStart100ns;
            }
            if (!enabled || end < 0) return;
            if (GetMasterPosition100ns() >= end)
            {
                SeekTo(start >= 0 ? start : 0);
            }
        }
        finally
        {
            Monitor.Exit(_stepGate);
        }
    }
}
