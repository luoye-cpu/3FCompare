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

    public sealed class SyncSlot
    {
        public required IPlayerSession Session { get; init; }
        public required string Path { get; init; }
        /// <summary>相对 master 的媒体时间偏移（100ns）。</summary>
        public long Offset100ns { get; set; }
        public bool Failed { get; set; }
        public string? Error { get; set; }
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
        set { lock (_gate) { _profile = value; } StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public bool LoopEnabled
    {
        get { lock (_gate) { return _loopEnabled; } }
        set { lock (_gate) { _loopEnabled = value; } StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public long LoopStart100ns
    {
        get { lock (_gate) { return _loopStart100ns; } }
        set { lock (_gate) { _loopStart100ns = value; } StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public long LoopEnd100ns
    {
        get { lock (_gate) { return _loopEnd100ns; } }
        set { lock (_gate) { _loopEnd100ns = value; } StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void AddSlot(IPlayerSession session, string path)
    {
        lock (_gate)
        {
            _slots.Add(new SyncSlot { Session = session, Path = path });
            Volatile.Write(ref _fpsMismatchCache, -1);   // 路数变了，帧率差异需重算
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
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
                }
            }
        }
        // Dispose 移到锁外：原生释放期间不应持有锁
        try { slot.Session.Dispose(); } catch { /* 忽略释放异常 */ }
        StateChanged?.Invoke(this, EventArgs.Empty);
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
            try { slot.Session.Dispose(); } catch { /* 忽略 */ }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
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

    public void Play() => ForEachSlot("播放", s => s.Session.Play(), notify: true);

    public void Pause() => ForEachSlot("暂停", s => s.Session.Pause(), notify: true);

    /// <summary>向所有会话广播统一的视口变换（缩放 + 平移），保证多路看到同一区域。</summary>
    public void SetViewTransform(float zoom, float panX, float panY)
        => ForEachSlot("视图变换", s => s.Session.SetViewTransform(zoom, panX, panY), notify: false);

    public void Stop() => ForEachSlot("停止", s => s.Session.Stop(), notify: true);

    /// <summary>通知各路重新呈现最后一帧（幂等，成本很低）。
    ///
    /// 内核契约（<c>FFF.Player.Api.h</c> 对 <c>FFF3FP_Redraw</c> 的说明）：
    /// "the App calls it after a child HWND resize **so flips continue issuing**"。
    /// 也就是说，子窗口尺寸变化后若不调 Redraw，呈现会停住——
    /// 表现为 Playing、位置照常推进，但 <c>PresentedVideoFrames</c> 不再增长。
    /// 这正是"最大化/还原后渲染停滞"的症状，而单靠 Pause→Play 唤不醒它。
    /// </summary>
    public void RedrawAll() => ForEachSlot("重绘", s => s.Session.Redraw(), notify: false);

    /// <summary>统一的"遍历所有未失败槽位"执行器：取副本 → 锁外执行 → 锁外通知。</summary>
    /// <remarks>用带索引的 for 而非 foreach：错误消息必须带路号。
    /// 多路对比下某一路挂掉时，"播放会话失败"这种没有路号的信息等于没有信息。</remarks>
    private void ForEachSlot(string actionName, Action<SyncSlot> action, bool notify)
    {
        var slots = SnapshotSlots();
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.Failed) continue;
            try { action(slot); }
            catch (Exception ex) { ReportRuntimeError($"{actionName}第 {i} 路", ex); }
        }
        if (notify) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>带槽位索引的遍历。需要区分 master（第 0 路）的场合用它——
    /// 单独开一个方法而不是改上面那个的签名，是为了不动它已有的 4 处调用。</summary>
    private void ForEachSlotIndexed(string actionName, Action<SyncSlot, int> action, bool notify)
    {
        var slots = SnapshotSlots();
        for (var i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.Failed) continue;
            try { action(slot, i); }
            catch (Exception ex) { ReportRuntimeError($"{actionName}第 {i} 路", ex); }
        }
        if (notify) StateChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>全部会话 Seek 到指定规范时间（各会话自动加自身偏移并 clamp）。</summary>
    public void SeekTo(long target100ns)
    {
        ForEachSlotIndexed("Seek", (slot, i) =>
        {
            slot.Session.Seek(ClampToDuration(slot, target100ns + OffsetOf(slot, i)));
        }, notify: true);
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
        // 注意 StateChanged 必须留在锁外，避免在锁内触发事件导致重入。
        lock (_stepGate)
        {
            StepFramesCore(frames);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
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
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].Failed) continue;
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
            var newSnap = master.Session.ReadSnapshot();
            var newPos = newSnap.Position100ns;
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
                if (slot.Failed) continue;
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

    /// <summary>按秒步进（F12）：全部会话按规范时间换算后 Seek。</summary>
    public void StepSeconds(double seconds)
    {
        var slots = SnapshotSlots();
        if (slots.Length == 0) return;
        var master = slots[0];
        try
        {
            var snap = master.Session.ReadSnapshot();
            var target = FrameTimeline.StepBySeconds(snap.Position100ns, snap.Duration100ns, seconds);
            SeekTo(target);
        }
        catch (Exception ex) { ReportRuntimeError("秒步进读取 master", ex); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>获取当前规范时间（master 位置，未含偏移）。</summary>
    public long GetMasterPosition100ns()
    {
        var snap = ReadMasterSnapshot();
        return snap?.Position100ns ?? 0;
    }

    /// <summary>偏移变动后，让所有会话按新偏移重新对齐（位置不变，各会话实际 Seek 到 = master ± offset）。</summary>
    public void RefreshAllPositions()
    {
        var masterPos = GetMasterPosition100ns();
        ForEachSlotIndexed("偏移重对齐", (slot, i) =>
        {
            // master 的位置本来就是基准，不能再按它自己的偏移挪一次
            if (i == 0) return;
            slot.Session.Seek(Math.Clamp(masterPos + slot.Offset100ns, 0, long.MaxValue));
        }, notify: true);
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
    /// （曾导致 4K H.264 显示 15360fps 的 bug）。无数据时回退 24。</summary>
    public static double EstimateFps(EngineSnapshot snap)
    {
        if (snap.FrameRate > 0) return snap.FrameRate;
        // 无帧率数据时保守回退 24：frameTimeBase 是流时间基（如 1/15360）而非
        // 帧率，无法从它推导 fps（曾导致 4K H.264 显示 15360fps 的 bug）。
        return 24.0;
    }

    // ──────────── 漂移检测与校正（docs/15 §3.3）────────────

    /// <summary>检测周期。各路独立时钟，长片播放必然发散，所以要周期对齐。</summary>
    private const long DriftCheckIntervalMs = 1000;

    /// <summary>一次检测后到下一次的冷却。与检测周期同值即可形成"每 1s 最多校正一次"，
    /// 避免"校正 → 下一拍又检测到残余偏差 → 再校正"的来回抖动。</summary>
    private const long DriftCooldownMs = 1000;

    private long _lastDriftCheckTicks;

    // 帧率不会变，所以"是否存在帧率差异"只需算一次；槽位增减时失效。
    // -1 = 尚未计算，0 = 无差异，1 = 有差异
    private int _fpsMismatchCache = -1;

    /// <summary>各路帧率是否与 master 存在显著差异（相对差 &gt; 1%）。
    ///
    /// 帧率不同时"同帧号"并非同一时刻的内容（24fps 的第 100 帧在 4.17s，
    /// 60fps 的第 100 帧在 1.67s），逐帧对比的语义会随之改变，需要让用户知道
    ///（docs/15 §3.6）。本属性供状态栏/提示使用，结果惰性缓存。
    /// </summary>
    public bool HasFpsMismatch
    {
        get
        {
            var cached = Volatile.Read(ref _fpsMismatchCache);
            if (cached >= 0) return cached == 1;

            var result = false;
            var slots = SnapshotSlots();
            double? masterFps = null;
            for (var i = 0; i < slots.Length; i++)
            {
                if (slots[i].Failed) continue;
                EngineSnapshot? snap;
                try { snap = slots[i].Session.ReadSnapshot(); }
                catch { continue; }
                if (snap is null) continue;

                var fps = EstimateFps(snap);
                if (fps <= 0) continue;
                if (masterFps is null) { masterFps = fps; continue; }
                if (Math.Abs(fps - masterFps.Value) / masterFps.Value > 0.01) { result = true; break; }
            }
            Volatile.Write(ref _fpsMismatchCache, result ? 1 : 0);
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
    /// ① 只在**播放中**校正——暂停/帧步进时机位由用户或步进逻辑精确控制，
    ///    插手反而会破坏刚对齐的状态；
    /// ② 偏差超过**半帧**才动。半帧是"看得出错帧"的下限，低于此值不动；
    /// ③ 每 1s 最多校正一次（冷却），杜绝抖动；
    /// ④ 只校正 follower，master 是基准，永不校正。</para>
    ///
    /// <para>本方法由轮询调用，自身按 <see cref="DriftCheckIntervalMs"/> 节流。</para>
    /// </summary>
    public void TickDrift()
    {
        var slots = SnapshotSlots();
        if (slots.Length < 2) return;   // 单路没有漂移可言

        var now = Environment.TickCount64;
        if (now - _lastDriftCheckTicks < DriftCheckIntervalMs) return;

        // 先读一次快照：判定播放态需要它，后面算偏差也用它，避免重复读
        var snaps = new EngineSnapshot?[slots.Length];
        for (var i = 0; i < slots.Length; i++)
        {
            if (slots[i].Failed) continue;
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
            if (slots[i].Failed) continue;
            var s = snaps[i];
            if (s is null || s.State != PlayerState.Playing) continue;

            var expected = masterPos + slots[i].Offset100ns;
            if (Math.Abs(s.Position100ns - expected) <= threshold) continue;

            try
            {
                slots[i].Session.Seek(ClampToDuration(slots[i], expected));
                Diagnostics.AppLog.Debug("Sync",
                    $"漂移校正 路{i} Δ={s.Position100ns - expected} → Seek {expected}");
            }
            catch (Exception ex) { ReportRuntimeError($"漂移校正第 {i} 路", ex); }
        }

        _lastDriftCheckTicks = now;
    }

    /// <summary>处理循环：若开启区间循环且 master 位置越过终点，Seek 回起点。</summary>
    public void TickLoop()
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
}
