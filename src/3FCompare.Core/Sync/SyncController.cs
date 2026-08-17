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
        catch { return null; }
    }

    /// <summary>读取全部会话快照（UI 轮询用）。</summary>
    public IReadOnlyList<EngineSnapshot?> ReadAllSnapshots()
    {
        var result = new EngineSnapshot?[_slots.Count];
        for (var i = 0; i < _slots.Count; i++)
        {
            try { result[i] = _slots[i].Session.ReadSnapshot(); }
            catch { result[i] = null; }
        }
        return result;
    }

    public void Play()
    {
        foreach (var slot in _slots)
        {
            if (slot.Failed) continue;
            try { slot.Session.Play(); } catch { /* 忽略 */ }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        foreach (var slot in _slots)
        {
            if (slot.Failed) continue;
            try { slot.Session.Pause(); } catch { /* 忽略 */ }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        foreach (var slot in _slots)
        {
            if (slot.Failed) continue;
            try { slot.Session.Stop(); } catch { /* 忽略 */ }
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
            catch { /* 忽略单路失败 */ }
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
            try { master.Session.StepFrame(frames); } catch { /* 回退时间步进 */ }

            var newSnap = master.Session.ReadSnapshot();
            var newPos = newSnap.Position100ns;
            if (newPos == oldPos && oldFrame == newSnap.FrameIndex && fps > 0)
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
                catch { /* 忽略 */ }
            }
        }
        catch { /* master 读取失败则整体跳过 */ }
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
        catch { /* 忽略 */ }
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
            catch { /* 忽略单路 */ }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public long GetMasterDuration100ns()
    {
        var snap = ReadMasterSnapshot();
        return snap?.Duration100ns ?? 0;
    }

    /// <summary>从快照估算帧率（fps）。优先用时间基，否则回退 24。</summary>
    public static double EstimateFps(EngineSnapshot snap)
    {
        if (snap.FrameTimeBaseDen > 0 && snap.FrameTimeBaseNum > 0)
            return (double)snap.FrameTimeBaseDen / snap.FrameTimeBaseNum;
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