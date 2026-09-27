using _3FCompare.Core.Backend;
using _3FCompare.Core.Sync;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>SyncController 的<b>最小手写替身</b>：只复刻本文件两组用例需要的引擎语义。
///
/// <para><b>为什么不复用 <see cref="SimulatedEngine"/></b>：它的时钟到片尾会**回绕**
///（<c>_position100ns = 0</c>），永远不产生 <c>Ended</c>，也没有"Seek 把 Ended 变成
/// Paused"这条转换——而 #21 的两个情形恰恰依赖这两点。所以这里按内核
/// <c>third_party/fff_project/FFF.Native/3FP/Core/PlayerSession.cpp</c> 的**实际**行为复刻：</para>
/// <list type="number">
/// <item><c>FlushAtEnd</c>（:2915 附近）：位置推进到时长 ⇒ 位置**钉在时长**、状态置 <c>Ended</c>；</item>
/// <item><c>DoSeek</c> 尾部（:2969）：对 <c>Ended</c> 的会话 Seek ⇒ 状态变 <c>Paused</c>（不是 Playing）；</item>
/// <item><c>Play</c>（:691）：只接受 Ready/Paused/Ended，且对 <c>Ended</c> 会先 <c>DoSeek(0)</c>（从头重播）。</item>
/// </list>
/// <para>第 2、3 条共同决定了"先 Seek 后 Play"能否把从路精确恢复到期望位置而不回到 0；
/// 少了任何一条，替身都会给出与实机相反的结论。</para>
///
/// <para>时钟默认<b>冻结</b>（<see cref="AutoAdvance"/>=false），使断言完全确定，
/// 不依赖"读快照之间真实时间推进了多少"。</para></summary>
internal sealed class StubSession : IPlayerSession
{
    private readonly object _lock = new();
    private readonly long _duration100ns;
    private readonly double _fps;
    private long _position100ns;
    private PlayerState _state = PlayerState.Ready;
    private DateTime _lastTick;

    public StubSession(long duration100ns, double fps = 24.0)
    {
        _duration100ns = duration100ns;
        _fps = fps;
    }

    // ── 观测计数（用例的断言对象）──
    public int SeekCount { get; private set; }
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }

    /// <summary>true = 播放中按真实时间推进位置（默认 false，保证断言确定）。</summary>
    public bool AutoAdvance { get; set; }

    /// <summary>true = 位置推进到片尾后自动转 <c>Ended</c>（复刻内核 <c>FlushAtEnd</c>）。</summary>
    public bool AutoEnd { get; set; } = true;

    /// <summary>Seek 被调用时触发（在真正改动位置**之前**），用来制造并发窗口。</summary>
    public Action<long>? OnSeek { get; set; }

    /// <summary>Pause 被调用时触发（无条件触发，包括状态本来就不是 Playing 的场合）。</summary>
    public Action? OnPause { get; set; }

    public long Position100ns { get { lock (_lock) { return _position100ns; } } }
    public PlayerState State { get { lock (_lock) { return _state; } } }

    /// <summary>用例专用：把会话强行置成指定状态与位置（绕过引擎规则，模拟"已到片尾"等既成事实）。</summary>
    public void ForceState(PlayerState state, long position100ns)
    {
        lock (_lock) { _state = state; _position100ns = position100ns; }
    }

    public void Play()
    {
        lock (_lock)
        {
            PlayCount++;
            // 内核只接受 Ready/Paused/Ended（其余返回 InvalidState，状态不变）
            if (_state is not (PlayerState.Ready or PlayerState.Paused or PlayerState.Ended)) return;
            // 内核：Ended ⇒ 先 DoSeek(0)，即**从头重播**。这条是"必须先 Seek 再 Play"的依据。
            if (_state == PlayerState.Ended) _position100ns = 0;
            _state = PlayerState.Playing;
            _lastTick = DateTime.UtcNow;
        }
    }

    public void Pause()
    {
        OnPause?.Invoke();      // 探针必须在锁外、且在状态判断之前触发
        lock (_lock)
        {
            PauseCount++;
            if (_state == PlayerState.Playing) _state = PlayerState.Paused;
        }
    }

    public void Seek(long position100ns)
    {
        OnSeek?.Invoke(position100ns);
        lock (_lock)
        {
            SeekCount++;
            _position100ns = Math.Clamp(position100ns, 0, _duration100ns);
            // 内核 DoSeek 尾部：Ended --Seek--> Paused。
            // 少了这一条，用例会误以为"Seek 之后从路自己就会继续走"。
            if (_state == PlayerState.Ended) _state = PlayerState.Paused;
            if (_state == PlayerState.Playing) _lastTick = DateTime.UtcNow;
        }
    }

    public void Stop()
    {
        lock (_lock) { _state = PlayerState.Ready; _position100ns = 0; }
    }

    public void StepFrame(int direction)
    {
        lock (_lock)
        {
            // 内核 StepFrame 会 SetState(Paused)
            _state = PlayerState.Paused;
            var frameTicks = TimeSpan.TicksPerSecond / _fps;
            var frame = (long)(_position100ns / frameTicks);
            frame = Math.Clamp(frame + direction, 0L, (long)(_duration100ns / frameTicks));
            _position100ns = (long)(frame * frameTicks);
        }
    }

    public void SeekFrame(long frameIndex)
    {
        lock (_lock)
        {
            var frameTicks = TimeSpan.TicksPerSecond / _fps;
            _position100ns = Math.Clamp((long)(Math.Max(0, frameIndex) * frameTicks), 0, _duration100ns);
        }
    }

    public EngineSnapshot ReadSnapshot()
    {
        lock (_lock)
        {
            if (AutoAdvance && _state == PlayerState.Playing)
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastTick).TotalSeconds;
                if (elapsed > 0)
                {
                    _lastTick = now;
                    _position100ns += (long)(elapsed * TimeSpan.TicksPerSecond);
                    if (AutoEnd && _position100ns >= _duration100ns)
                    {
                        _position100ns = _duration100ns;    // 内核：位置钉在时长
                        _state = PlayerState.Ended;         // 内核：FlushAtEnd
                    }
                }
            }

            return new EngineSnapshot
            {
                Position100ns = _position100ns,
                Duration100ns = _duration100ns,
                FrameIndex = (long)(_position100ns / (TimeSpan.TicksPerSecond / _fps)),
                FrameRate = _fps,
                State = _state,
                TimelineGeneration = (ulong)SeekCount,
            };
        }
    }

    public Task OpenAsync(string localPath, CancellationToken cancellationToken = default)
    {
        lock (_lock) { _state = PlayerState.Ready; _position100ns = 0; }
        return Task.CompletedTask;
    }

    // ── 以下成员与本组用例无关，空实现（替身只承诺"够用"）──
    public void SelectAudioStream(int streamIndex) { }
    public void SelectVideoStream(int streamIndex) { }
    public void SetVolume(float volume, bool muted) { }
    public void SetColorMode(ColorMode mode, bool? contentIsHdr = null) { }
    public bool SetPresentConfig(bool tearing) => true;
    public void SetViewTransform(float zoom, float panX, float panY) { }
    public EngineMediaInfo? ReadMediaInfo() => null;
    public bool TryReadPixel(int x, int y, out PixelSample sample)
    {
        sample = default;
        return false;
    }
    public bool TryReadPixelRegion(int x, int y, int width, int height,
        float[] buffer, out uint outputBitDepth)
    {
        outputBitDepth = 8;
        return false;
    }
    public void Redraw() { }
    public bool ReadRenderTargetInfo(out RenderTargetInfo info)
    {
        info = default;
        return false;
    }

    /// <summary>本替身不产生引擎事件，显式空访问器（否则编译器会报"字段已赋值但从未使用"）。</summary>
    public event EventHandler<EngineEvent>? EngineEvent
    {
        add { }
        remove { }
    }

    public void Dispose() { }   // 无原生资源
}

/// <summary>
/// docs/41 #20 / #21 的定点回归。
///
/// <para><b>#20</b>（<c>SeekTo</c> / <c>RefreshAllPositions</c> / <c>StepSeconds</c> 未取
/// <c>_stepGate</c>）：这三个方法与线程池上的 <c>StepFrames</c> 交错时，会把从路拉回
/// <b>步进之前</b>算出的过期位置。这里用"Pause 探针"直接测互斥，而不是测最终位置——
/// 后者在 SeekTo 场景下恰好也能被凑对，区分不出有没有交错。</para>
///
/// <para><b>#21</b>（<c>s.State != Playing</c> 一刀切跳过校正）：从路一旦落到非播放态
/// （内核到片尾置 Ended、Seek 又把它变成 Paused）就永久失步。用例把两种情形分别钉死：
/// 内容范围内却非 Playing ⇒ 必须拉回并恢复推进；自身内容已放完 ⇒ 必须不动（不抖、不重复 Seek）。</para>
/// </summary>
public class SyncControllerStepGateAndEndedTests
{
    private static readonly long Sec = TimeSpan.TicksPerSecond;

    private static (SyncController Sync, StubSession Master, StubSession Follower) CreatePair(
        long masterDuration = 10, long followerDuration = 10)
    {
        var master = new StubSession(masterDuration * Sec);
        var follower = new StubSession(followerDuration * Sec);
        var sync = new SyncController();
        sync.AddSlot(master, "master.mp4");
        sync.AddSlot(follower, "follower.mp4");
        return (sync, master, follower);
    }

    // ══════════════════════ #20：与帧步进的互斥 ══════════════════════

    /// <summary>#20 的公共断言：<paramref name="operation"/> 不得与 <c>StepFrames</c> 交错。
    ///
    /// <para>手法：让被考察的方法停在"第 1 路（从路）的第一次 Seek"上——此刻它应当已经
    /// 持有 <c>_stepGate</c>；再在线程池上发起一次 <c>StepFrames</c>，最后才放行。
    /// 闸门有效 ⇒ StepFrames 只能等到放行之后才可能走到它的**第一步**（对全路 <c>Pause</c>）；
    /// 闸门缺失 ⇒ StepFrames 立刻执行 Pause，被探针记录下来。</para>
    ///
    /// <para>为什么拿 Pause 当探针：它是 <c>StepFramesCore</c> 的第一步，只要它发生在放行之前，
    /// 就证明两次操作确实交错了。而"最终位置一致"这类断言在 SeekTo 场景下**恰好也能成立**
    ///（SeekTo 会把所有路都 Seek 到同一目标），所以不能用来区分。</para></summary>
    private static void AssertSerializedWithStepFrames(Action<SyncController> operation)
    {
        var stubs = new[] { new StubSession(10 * Sec), new StubSession(10 * Sec), new StubSession(10 * Sec) };
        var sync = new SyncController();
        foreach (var (s, i) in stubs.Select((s, i) => (s, i))) sync.AddSlot(s, $"stub{i}.mp4");
        try
        {
            foreach (var s in stubs) s.ForceState(PlayerState.Playing, 2 * Sec);

            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var pauseSeen = new ManualResetEventSlim();
            var blocked = 0;
            var pauseBeforeRelease = 0;

            // 只拦"第 1 路的第一次 Seek"：三个被考察的方法都会走到它，且都在取得闸门之后。
            stubs[1].OnSeek = _ =>
            {
                if (Interlocked.Exchange(ref blocked, 1) == 0)
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            };
            foreach (var s in stubs)
                s.OnPause = () =>
                {
                    if (!release.IsSet)
                    {
                        Interlocked.Increment(ref pauseBeforeRelease);
                        pauseSeen.Set();
                    }
                };

            var op = Task.Run(() => operation(sync));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "被考察的方法没有走到第 1 路的 Seek");

            // 此刻 op 正停在 Seek 里：若它持有闸门，StepFrames 应当被挡在外面。
            var step = Task.Run(() => sync.StepFrames(1));
            // 闸门有效时这里必然超时；闸门缺失时会被 Pause 立刻唤醒（比固定 sleep 更稳）。
            pauseSeen.Wait(TimeSpan.FromMilliseconds(400));
            release.Set();

            Assert.True(Task.WaitAll(new[] { op, step }, TimeSpan.FromSeconds(10)), "操作未在超时内完成");
            Assert.Equal(0, Volatile.Read(ref pauseBeforeRelease));
        }
        finally
        {
            sync.Clear();
        }
    }

    [Fact]
    public void SeekTo_与帧步进互斥_不产生交错()
        => AssertSerializedWithStepFrames(s => s.SeekTo(5 * Sec));

    [Fact]
    public void StepSeconds_与帧步进互斥_不产生交错()
        => AssertSerializedWithStepFrames(s => s.StepSeconds(3));

    [Fact]
    public void RefreshAllPositions_与帧步进互斥_不产生交错()
        => AssertSerializedWithStepFrames(s => s.RefreshAllPositions());

    // ══════════════════════ #21：从路到片尾后的失步 ══════════════════════

    /// <summary>#21 主用例：从路已停在自己的片尾（Ended），而 master 还在中间 ⇒ 必须拉回并恢复推进。
    ///
    /// <para>旧代码 <c>s.State != Playing ⇒ continue</c> 在此直接跳过，从路永远停在片尾。</para></summary>
    [Fact]
    public void TickDrift_从路提前到片尾_仍被拉回并恢复播放()
    {
        var (sync, master, follower) = CreatePair();
        try
        {
            master.ForceState(PlayerState.Playing, 5 * Sec);
            follower.ForceState(PlayerState.Ended, 10 * Sec);

            sync.TickDrift();

            // ① 位置被拉回 master 基准，而不是停在片尾
            Assert.Equal(5 * Sec, follower.Position100ns);
            // ② 状态恢复成 Playing：只 Seek 不 Play 的话它位置对了却不推进，
            //    下一拍又判为偏差 ⇒ 每秒一次 Seek 风暴，等于没修
            Assert.Equal(PlayerState.Playing, follower.State);
            // ③ 必须是"从期望位置续播"而不是从头重播：PlayCount 恰好 1 次，
            //    且位置仍是 5s（若顺序反成 Play→Seek，内核 Play 的 DoSeek(0) 会把位置打回 0）
            Assert.Equal(1, follower.PlayCount);
        }
        finally { sync.Clear(); }
    }

    /// <summary>#21 情形 A：从路自身内容已放完（期望位置超出**它自己**的时长）。
    /// 多素材时长不同是常态，此时停在最后一帧是正确行为——不该拉回，也不该每秒重复 Seek。</summary>
    [Fact]
    public void TickDrift_从路内容已放完_不拉回也不重复Seek()
    {
        // 从路素材只有 3s，master 有 10s
        var (sync, master, follower) = CreatePair(masterDuration: 10, followerDuration: 3);
        try
        {
            master.ForceState(PlayerState.Playing, 8 * Sec);
            follower.ForceState(PlayerState.Playing, 3 * Sec);   // 停在最后一帧，但状态仍是 Playing

            sync.TickDrift();

            // 旧代码：Playing 通过状态判断、偏差 5s 远超阈值 ⇒ Seek 到被 clamp 的片尾，
            // 且每 1s 重复一次（每次都是无谓的 av_seek_frame + 解码器 flush）
            Assert.Equal(0, follower.SeekCount);
            Assert.Equal(3 * Sec, follower.Position100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>#21 情形 A 的另一半：从路已到片尾且已转 Ended、master 仍在其内容之后 ⇒ 同样不动。</summary>
    [Fact]
    public void TickDrift_从路Ended且内容已放完_不拉回()
    {
        var (sync, master, follower) = CreatePair(masterDuration: 10, followerDuration: 3);
        try
        {
            master.ForceState(PlayerState.Playing, 8 * Sec);
            follower.ForceState(PlayerState.Ended, 3 * Sec);

            sync.TickDrift();

            Assert.Equal(0, follower.SeekCount);
            Assert.Equal(0, follower.PlayCount);
            Assert.Equal(PlayerState.Ended, follower.State);
        }
        finally { sync.Clear(); }
    }

    /// <summary>防过度校正：从路真到片尾、master 也快到片尾 ⇒ 这是正常收敛，不许往回拉。
    /// 若把"Ended 就恢复"写成一刀切，这里会 Seek+Play 而抖动/循环。</summary>
    [Fact]
    public void TickDrift_从路已到片尾且master也快到片尾_不来回拉()
    {
        var (sync, master, follower) = CreatePair();
        try
        {
            master.ForceState(PlayerState.Playing, 9_990 * TimeSpan.TicksPerMillisecond);   // 半帧以内
            follower.ForceState(PlayerState.Ended, 10 * Sec);

            sync.TickDrift();

            Assert.Equal(0, follower.SeekCount);
            Assert.Equal(0, follower.PlayCount);
            Assert.Equal(PlayerState.Ended, follower.State);
        }
        finally { sync.Clear(); }
    }

    /// <summary>Paused 从路刻意不参与校正、也不被自动恢复播放。
    /// 暂停在这里是"这一路被有意移出播放组"的语义（multitest 判别实验正是靠直接 Pause
    /// 部分路来减少并发 Present），自动 Play 回来会破坏该语义与其实验结论。</summary>
    [Fact]
    public void TickDrift_Paused从路_刻意不校正也不恢复播放()
    {
        var (sync, master, follower) = CreatePair();
        try
        {
            master.ForceState(PlayerState.Playing, 5 * Sec);
            follower.ForceState(PlayerState.Paused, 1 * Sec);

            sync.TickDrift();

            Assert.Equal(0, follower.SeekCount);
            Assert.Equal(0, follower.PlayCount);
            Assert.Equal(1 * Sec, follower.Position100ns);
            Assert.Equal(PlayerState.Paused, follower.State);
        }
        finally { sync.Clear(); }
    }
}
