using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Sync;

/// <summary>多会话同步协调器（04 文档设计）。
/// - 以第 0 会话为 master，媒体时间（100ns）为规范时间轴；
/// - 支持 帧步进 / 秒步进 / 时间 Seek / 循环 / 偏移校准；
/// - 所有命令应在 UI 线程（同一 SynchronizationContext）调用。</summary>
public sealed class SyncController
{
    private readonly List<SyncSlot> _slots = new();
    private StepProfile _profile = new();
    private bool _loopEnabled;
    private long _loopStart100ns = -1;
    private long _loopEnd100ns = -1;

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
    public string? LastRuntimeError { get; private set; }

    /// <summary>记录运行时错误（不抛出不打断流程）。</summary>
    private void ReportRuntimeError(string action, Exception ex)
        => LastRuntimeError = $"{action}: {ex.Message}";

    public IReadOnlyList<SyncSlot> Slots => _slots;

    public int Count => _slots.Count;

    public StepProfile StepProfile
    {
        get => _profile;
        set { _profile = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public bool LoopEnabled
    {
        get => _loopEnabled;
        set { _loopEnabled = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public long LoopStart100ns { get => _loopStart100ns; set { _loopStart100ns = value; StateChanged?.Invoke(this, EventArgs.Empty); } }
    public long LoopEnd100ns { get => _loopEnd100ns; set { _loopEnd100ns = value; StateChanged?.Invoke(this, EventArgs.Empty); } }

    public void AddSlot(IPlayerSession session, string path)
    {
        _slots.Add(new SyncSlot { Session = session, Path = path });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveSlotAt(int index)
    {
        if (index < 0 || index >= _slots.Count) return;
        var slot = _slots[index];
        _slots.RemoveAt(index);
        try { slot.Session.Dispose(); } catch { /* 忽略释放异常 */ }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        foreach (var slot in _slots)
        {
            try { slot.Session.Dispose(); } catch { /* 忽略 */ }
        }
        _slots.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>读取 master（第 0 路）快照；无会话时返回 null。</summary>
    public EngineSnapshot? ReadMasterSnapshot()
    {
        if (_slots.Count == 0) return null;
        try { return _slots[0].Session.ReadSnapshot(); }
        catch (Exception ex) { ReportRuntimeError("读取 master 快照", ex); return null; }
    }

    private EngineSnapshot?[] _snapshotCache = Array.Empty<EngineSnapshot?>();

    /// <summary>读取全部会话快照（UI 轮询用）。
    /// ⚠ 契约：返回的是内部复用数组，仅在**下一次调用前、且只在 UI 线程上**有效；
    /// 调用方不得缓存引用或跨线程读取。需要长期持有时应自行复制。
    /// （当前唯一消费方 PollSnapshots 满足该契约；若将来有多消费者需改为拷贝语义。）</summary>
    public IReadOnlyList<EngineSnapshot?> ReadAllSnapshots()
    {
        if (_snapshotCache.Length != _slots.Count)
            _snapshotCache = new EngineSnapshot?[_slots.Count];
        for (var i = 0; i < _slots.Count; i++)
        {
            try { _snapshotCache[i] = _slots[i].Session.ReadSnapshot(); }
            catch (Exception ex) { ReportRuntimeError($"读取第 {i} 路快照", ex); _snapshotCache[i] = null; }
        }
        return _snapshotCache;
    }

    public void Play()
    {
        foreach (var slot in _slots)
        {
            if (slot.Failed) continue;
            try { slot.Session.Play(); }
            catch (Exception ex) { ReportRuntimeError($"播放第 {_slots.IndexOf(slot)} 路", ex); }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        foreach (var slot in _slots)
        {
            if (slot.Failed) continue;
            try { slot.Session.Pause(); }
            catch (Exception ex) { ReportRuntimeError($"暂停第 {_slots.IndexOf(slot)} 路", ex); }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>向所有会话广播统一的视口变换（缩放 + 平移），保证多路看到同一区域。</summary>
    public void SetViewTransform(float zoom, float panX, float panY)
    {
        foreach (var slot in _slots)
        {
            if (slot.Failed) continue;
            try { slot.Session.SetViewTransform(zoom, panX, panY); }
            catch (Exception ex) { ReportRuntimeError($"视图变换第 {_slots.IndexOf(slot)} 路", ex); }
        }
    }

    public void Stop()
    {
        foreach (var slot in _slots)
        {
            if (slot.Failed) continue;
            try { slot.Session.Stop(); }
            catch (Exception ex) { ReportRuntimeError($"停止第 {_slots.IndexOf(slot)} 路", ex); }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>全部会话 Seek 到指定规范时间（各会话自动加自身偏移并 clamp）。</summary>
    public void SeekTo(long target100ns)
    {
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Failed) continue;
            try
            {
                var t = Math.Clamp(target100ns + slot.Offset100ns, 0, long.MaxValue);
                slot.Session.Seek(t);
            }
            catch (Exception ex) { ReportRuntimeError($"Seek 第 {i} 路", ex); }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按帧步进（F12）：帧率一致时 master 用 StepFrame 精确推进，其余 Seek 到 master 新位置；否则全部按时间换算。</summary>
    public void StepFrames(int frames)
    {
        if (_slots.Count == 0) return;
        var master = _slots[0];
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
            var stepApplied = newSnap.TimelineGeneration != snap.TimelineGeneration ||
                              newSnap.FrameIndex != snap.FrameIndex;
            if (!stepApplied && fps > 0)
            {
                // StepFrame 无效（可能不支持），按时间换算
                newPos = FrameTimeline.StepByFrames(oldPos, duration, frames, fps);
                master.Session.Seek(newPos);
                newSnap = master.Session.ReadSnapshot();
                newPos = newSnap.Position100ns;
            }

            // 其余会话按 master 新位置对齐
            for (var i = 1; i < _slots.Count; i++)
            {
                var slot = _slots[i];
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
        if (_slots.Count == 0) return;
        var master = _slots[0];
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
        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            if (slot.Failed) continue;
            try
            {
                slot.Session.Seek(Math.Clamp(masterPos + slot.Offset100ns, 0, long.MaxValue));
            }
            catch (Exception ex) { ReportRuntimeError($"偏移重对齐第 {i} 路", ex); }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
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
        if (snap.FrameTimeBaseDen > 0 && snap.FrameTimeBaseNum > 0 && snap.RawFramePts > 1)
        {
            // 单帧增量未知时无法从时间基推 fps；仅在能拿到帧号差时才可靠。
            // 这里保守回退默认值，避免把时间基当帧率。
            return 24.0;
        }
        return 24.0;
    }

    /// <summary>处理循环：若开启区间循环且 master 位置越过终点，Seek 回起点。</summary>
    public void TickLoop()
    {
        if (!_loopEnabled || _loopEnd100ns < 0) return;
        var pos = GetMasterPosition100ns();
        if (pos >= _loopEnd100ns)
        {
            SeekTo(_loopStart100ns >= 0 ? _loopStart100ns : 0);
        }
    }
}