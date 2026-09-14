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
        lock (_gate) { _slots.Add(new SyncSlot { Session = session, Path = path }); }
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

    /// <summary>全部会话 Seek 到指定规范时间（各会话自动加自身偏移并 clamp）。</summary>
    public void SeekTo(long target100ns)
    {
        ForEachSlot("Seek", slot =>
        {
            var t = Math.Clamp(target100ns + slot.Offset100ns, 0, long.MaxValue);
            slot.Session.Seek(t);
        }, notify: true);
    }

    /// <summary>按帧步进（F12）：帧率一致时 master 用 StepFrame 精确推进，其余 Seek 到 master 新位置；否则全部按时间换算。
    /// <para>本方法可能阻塞数十至上百毫秒（含 50ms 复测等待与可能的 Seek），
    /// <b>不要在 UI 线程直接调用</b>——调用方应包 <c>Task.Run</c>。</para></summary>
    public void StepFrames(int frames)
    {
        var slots = SnapshotSlots();
        if (slots.Length == 0) return;
        var master = slots[0];
        try
        {
            var snap = master.Session.ReadSnapshot();
            var duration = snap.Duration100ns;
            var fps = EstimateFps(snap);

            // 尝试精确帧步进：master 直接 StepFrame，其余按 master 新位置对齐
            var oldPos = snap.Position100ns;
            var oldFrame = snap.FrameIndex;
            try { master.Session.StepFrame(frames); }
            catch (Exception ex) { ReportRuntimeError($"帧步进 master", ex); /* 回退时间步进 */ }

            var newSnap = master.Session.ReadSnapshot();
            var newPos = newSnap.Position100ns;
            // 用 timelineGeneration 判定 StepFrame 是否真正生效（seek 成功才递增）。
            // 旧判据 newPos==oldPos 在异步解码下不可靠：StepFrame 入队后快照大概率
            // 仍返回旧位置，会被误判"不支持"而降级为时间 Seek。
            // StepFrame 在内核侧异步执行，立即读快照大概率还没落地——先给一次
            // 短等待复测（50ms），仍无变化才降级为时间换算，避免每次帧步进都
            // 付出 av_seek_frame + 解码器 flush 的全量成本。
            var stepApplied = newSnap.TimelineGeneration != snap.TimelineGeneration ||
                              newSnap.FrameIndex != snap.FrameIndex;
            if (!stepApplied)
            {
                System.Threading.Thread.Sleep(50);
                newSnap = master.Session.ReadSnapshot();
                newPos = newSnap.Position100ns;
                stepApplied = newSnap.TimelineGeneration != snap.TimelineGeneration ||
                              newSnap.FrameIndex != snap.FrameIndex;
            }
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
                    var t = Math.Clamp(newPos + slot.Offset100ns, 0, long.MaxValue);
                    slot.Session.Seek(t);
                }
                catch (Exception ex) { ReportRuntimeError($"帧步进对齐第 {i} 路", ex); }
            }
        }
        catch (Exception ex) { ReportRuntimeError("帧步进读取 master", ex); /* master 读取失败则整体跳过 */ }
        StateChanged?.Invoke(this, EventArgs.Empty);
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
        ForEachSlot("偏移重对齐", slot =>
        {
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
