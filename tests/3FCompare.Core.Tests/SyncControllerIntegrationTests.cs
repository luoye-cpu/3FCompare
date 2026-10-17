using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;
using _3FCompare.Core.Tests.Infrastructure;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>SyncController 多会话同步测试：偏移/重排/循环/双步进/漂移校正。
///
/// <para><b>为什么用 <see cref="FakeEngine"/> 而不是 <see cref="SimulatedEngine"/></b>
/// （docs/41 §4.3「依赖真实挂钟」）：<c>SimulatedEngine</c> 在 Playing 态按**真实挂钟**
/// 推进位置，于是"两次读快照之间位置差了多少"取决于机器负载——断言只能写成宽泛上界
/// （如"残差 &lt; 60ms"），机器卡顿时判红。替身把位置变成**用例可脚本化的静态量**，
/// 于是断言可以是精确等式。</para>
///
/// <para>替身的两处参数与 <c>SimulatedEngine</c> 对齐（10s 素材 / 24fps），
/// 使半帧阈值仍是 ≈20.83ms，既有断言的量级不变。</para></summary>
public class SyncControllerIntegrationTests
{
    /// <summary>素材时长：与 <c>SimulatedEngine</c> 的 10s 一致。</summary>
    private const long Duration = 10 * TimeSpan.TicksPerSecond;

    /// <summary>帧率：与 <c>SimulatedEngine</c> 的 24fps 一致 ⇒ 半帧阈值 ≈ 20.83ms。</summary>
    private const double Fps = 24.0;

    /// <param name="clock">注入时钟（可确定性推进"1s 漂移冷却"）；null = 真实单调时钟。</param>
    /// <param name="clampSeek">替身的 Seek 是否自己钳制到时长。
    /// false 用于坐实<b>托管侧</b>的 clamp 真的生效（替身不兜底）。</param>
    private static (SyncController sync, IPlayerEngine engine) CreateSync(
        int count, IClock? clock = null, bool clampSeek = true)
    {
        var engine = new FakeEngine { DefaultDuration100ns = Duration, DefaultFrameRate = Fps };
        var sync = new SyncController(clock);
        for (var i = 0; i < count; i++)
        {
            var session = (FakeSession)engine.CreateSession(
                new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
            session.ClampSeek = clampSeek;
            session.OpenAsync($"test{i}.mp4").GetAwaiter().GetResult();
            sync.AddSlot(session, $"test{i}.mp4");
        }
        return (sync, engine);
    }

    /// <summary>视图变换的跨会话一致性（2026-09-29 发现）：广播过一次放大<b>之后</b>才建的会话
    /// （加路、把新文件开进空 lane、崩溃自愈重建都走这条路）必须立刻拿到同一变换。
    ///
    /// <para><b>为什么这是缺陷而不是"没做"</b>：<c>SyncController.SetViewTransform</c> 自己的注释是
    /// "保证多路看到同一区域"，但它只向**当时存在**的会话广播、不留状态 ⇒ 后建那一路停在"铺满窗口"、
    /// 老路仍在 4×，多路看到的区域不再相同，而屏幕上没有任何提示。用户随后随手一拖会把整套值广播过去，
    /// 表现为"那一路突然跳一下放大"。</para></summary>
    [Fact]
    public void AddSlot_AfterSetViewTransform_NewSessionReceivesSameTransform()
    {
        var (sync, engine) = CreateSync(1);
        try
        {
            sync.SetViewTransform(4f, 0.2f, -0.3f);
            var first = (FakeSession)sync.Slots[0].Session;
            Assert.Single(first.ViewTransforms);                   // 前置：当时存在的那一路确实收到了

            var late = (FakeSession)engine.CreateSession(
                new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
            late.OpenAsync("late.mp4").GetAwaiter().GetResult();
            sync.AddSlot(late, "late.mp4");

            Assert.Equal(new[] { (4f, 0.2f, -0.3f) }, late.ViewTransforms);
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void MultiSession_AllSlotsSeekTogether()
    {
        var (sync, engine) = CreateSync(3);
        try
        {
            sync.SeekTo(TimeSpan.FromSeconds(5).Ticks);
            var snaps = sync.ReadAllSnapshots();
            Assert.All(snaps, s => Assert.NotNull(s));
            Assert.All(snaps, s => Assert.Equal(TimeSpan.FromSeconds(5).Ticks, s!.Position100ns));
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void Offset_AppliedOnSeekAndRefresh()
    {
        var (sync, engine) = CreateSync(2);
        try
        {
            sync.SeekTo(0);
            sync.Slots[1].Offset100ns = TimeSpan.FromSeconds(2).Ticks;
            sync.RefreshAllPositions();

            var snaps = sync.ReadAllSnapshots();
            Assert.Equal(TimeSpan.FromSeconds(2).Ticks, snaps[1]!.Position100ns);
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void StepFrames_DualSession_StaysInSync()
    {
        var (sync, engine) = CreateSync(2);
        try
        {
            sync.SeekTo(0);
            sync.StepFrames(1);
            var snaps = sync.ReadAllSnapshots();
            Assert.Equal(snaps[0]!.Position100ns, snaps[1]!.Position100ns);
            Assert.True(snaps[0]!.Position100ns > 0);
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void StepSeconds_AdvancesByDuration()
    {
        var (sync, engine) = CreateSync(1);
        try
        {
            sync.SeekTo(0);
            sync.StepSeconds(2);
            Assert.Equal(TimeSpan.FromSeconds(2).Ticks, sync.GetMasterPosition100ns());
        }
        finally { sync.Clear(); }
    }

    // ══════════ docs/14 §3.1：Clear 后循环状态不得残留 ══════════

    /// <summary>
    /// 设过 A-B 循环后清空会话，再加载一个"未开循环"的会话时，
    /// 旧的循环区间必须已经失效——否则传输栏 Loop 仍亮、TickLoop 仍按旧区间回绕。
    /// </summary>
    [Fact]
    public void Clear_复位循环状态_不跨会话残留()
    {
        var (sync, _) = CreateSync(2);
        try
        {
            sync.LoopEnabled = true;
            sync.LoopStart100ns = TimeSpan.FromSeconds(1).Ticks;
            sync.LoopEnd100ns = TimeSpan.FromSeconds(5).Ticks;
            Assert.True(sync.LoopEnabled);

            sync.Clear();

            Assert.False(sync.LoopEnabled);
            Assert.Equal(-1, sync.LoopStart100ns);
            Assert.Equal(-1, sync.LoopEnd100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>
    /// docs/15 §2.1：帧步进必须先把**全部路**暂停。
    ///
    /// master 走 <c>StepFrame</c> 时内核会 <c>SetState(Paused)</c>（SimulatedEngine 同样
    /// 模拟了这点），但**从路走 Seek，而 Seek 不改变播放状态** ⇒ 播放中步进会让
    /// master 停住、从路继续播，画面立刻错帧。
    ///
    /// 为什么必须 3 路：单路时 master 自己就会被置 Paused，无论修没修都通过，
    /// 测不出这条缺陷——用从路才能把"有没有先 Pause"区分开。
    /// </summary>
    [Fact]
    public void StepFrames_先把全部路暂停_播放中步进不会错帧()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            // 前置条件坐实：确实处于播放态，否则这条断言等于没测到目标场景
            var playingBefore = sync.Slots.Count(s => s.Session.ReadSnapshot()?.State == PlayerState.Playing);
            Assert.Equal(3, playingBefore);

            sync.StepFrames(1);

            var stillPlaying = sync.Slots
                .Select((s, i) => (Index: i, State: s.Session.ReadSnapshot()?.State))
                .Where(x => x.State == PlayerState.Playing)
                .Select(x => x.Index)
                .ToArray();
            Assert.Empty(stillPlaying);
        }
        finally { sync.Clear(); }
    }

    /// <summary>
    /// docs/15 §3.1：master（第 0 路）是**规范时间轴的基准**，其偏移必须恒为 0。
    ///
    /// 给它加偏移的后果：SeekTo(T) 会把 master 放到 T+off0，而帧步进又按 master 的
    /// 裸位置去对齐从路 ⇒ 两套偏移语义互相矛盾，且每次微调都累积，还会被会话存档
    /// 原样保存。所以即便 slot 上被写入了非 0 值（例如老会话），也不得参与计算。
    /// </summary>
    [Fact]
    public void SeekTo_master偏移不参与计算_基准不被挪走()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            var t2s = 2 * TimeSpan.TicksPerSecond;
            var t3s = 3 * TimeSpan.TicksPerSecond;

            sync.Slots.ElementAt(0).Offset100ns = t3s;   // 模拟老会话存下来的 off0
            sync.SeekTo(t2s);

            // master 应落在 2s（基准本身），而不是 2s + 3s
            Assert.Equal(t2s, sync.GetMasterPosition100ns());

            // 从路的偏移照常生效，说明只是 master 被豁免
            sync.Slots.ElementAt(1).Offset100ns = TimeSpan.TicksPerSecond;
            sync.SeekTo(t2s);
            Assert.Equal(t3s, sync.Slots.ElementAt(1).Session.ReadSnapshot()?.Position100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>
    /// docs/15 §3.5：移除 master 后必须做偏移重基准。
    /// 各路偏移都是"相对旧 master"的，旧 master 一走语义就断了：
    /// 新 master（原第 1 路）的偏移要归零，其余路要减去它原来的偏移值，
    /// 否则全部路整体错位（A-B 循环区间也会跟着偏）。
    /// </summary>
    [Fact]
    public void RemoveSlotAt_移除master时_偏移整体重基准()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Slots.ElementAt(1).Offset100ns = 1000;
            sync.Slots.ElementAt(2).Offset100ns = 3000;

            sync.RemoveSlotAt(0);

            // 新 master 偏移归零；原第 2 路 3000 - 1000 = 2000
            Assert.Equal(0, sync.Slots.ElementAt(0).Offset100ns);
            Assert.Equal(2000, sync.Slots.ElementAt(1).Offset100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>docs/15 §3.4：Seek 不得把任何一路推到超出它自己时长的位置。
    ///
    /// <para><b>改造前为什么不会红</b>：<c>SimulatedEngine</c> 的 Seek 内部本身就
    /// <c>Clamp(0, _duration100ns)</c>，所以无论托管侧的 <c>ClampToDuration</c> 在不在，
    /// 位置都恰好落在时长上——这条断言测的是替身的行为，不是被测代码的行为。</para>
    ///
    /// <para><b>现在怎么才可信</b>：替身关掉自己的钳制（<c>ClampSeek=false</c>），
    /// 于是"位置 ≤ 时长"只能由托管侧的 <c>ClampToDuration</c> 提供。删掉它 ⇒ 位置停在
    /// 10 分钟处 ⇒ 判红。同时覆盖 <c>t &lt; 0 ⇒ 0</c> 的下界分支。</para></summary>
    [Fact]
    public void SeekTo_不得超出各路自身时长_且负目标钳到零()
    {
        var (sync, _) = CreateSync(3, clampSeek: false);
        try
        {
            sync.SeekTo(10 * 60 * TimeSpan.TicksPerSecond);   // 远超 10s 素材时长

            foreach (var s in sync.Slots)
            {
                var snap = s.Session.ReadSnapshot();
                Assert.NotNull(snap);
                Assert.True(snap!.Position100ns <= snap.Duration100ns,
                    $"位置 {snap.Position100ns} 超出时长 {snap.Duration100ns}");
            }

            // 负目标：下界同样必须由托管侧兜住（替身此刻不会替我们钳）
            sync.SeekTo(-5 * TimeSpan.TicksPerSecond);

            foreach (var s in sync.Slots)
                Assert.Equal(0, s.Session.ReadSnapshot()!.Position100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>
    /// docs/15 §3.3：播放中若某路漂出阈值（半帧），应被 Seek 拉回 master 基准。
    ///
    /// <para><b>改造前的问题</b>：替身按真实挂钟推进位置，校正后两路的残差
    /// ＝"两次读快照之间过了多少真实时间"，只能写成 <c>&lt; 60ms</c> 的宽泛上界——
    /// 机器卡顿（GC / 抢占）时残差可能超过上界而<b>假红</b>。</para>
    ///
    /// <para><b>现在</b>：替身位置是静态量，校正后残差必须**恰好为 0**；
    /// 若校正被删除/改坏，残差就是注入的 500ms ⇒ 判红。</para>
    /// </summary>
    [Fact]
    public void TickDrift_播放中偏差超阈值_把从路拉回()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            // 人为让第 1 路漂走 500ms，远超半帧阈值（24fps ⇒ ≈20.83ms）
            sync.Slots.ElementAt(1).Session.Seek(
                2 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond);

            sync.TickDrift();

            var m = sync.Slots.ElementAt(0).Session.ReadSnapshot()!.Position100ns;
            var s = sync.Slots.ElementAt(1).Session.ReadSnapshot()!.Position100ns;
            Assert.Equal(m, s);
            Assert.Equal(2 * TimeSpan.TicksPerSecond, s);   // 被拉回的是 master 基准，不是"随便对齐"
        }
        finally { sync.Clear(); }
    }

    /// <summary>
    /// 只在**播放中**校正：暂停/帧步进时位置由用户或步进逻辑精确控制，
    /// 漂移校正插手反而会破坏刚对齐的状态。
    /// 暂停态下模拟引擎不推进时钟，可以精确断言"位置分毫未动"。
    /// </summary>
    [Fact]
    public void TickDrift_暂停态不校正()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            var drifted = 2 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond;
            sync.Slots.ElementAt(1).Session.Seek(drifted);

            sync.TickDrift();   // 未 Play ⇒ Paused，不应校正

            Assert.Equal(drifted, sync.Slots.ElementAt(1).Session.ReadSnapshot()!.Position100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>#42 的"跳过见证"：master 在播、从路落到 Paused 且偏差 500ms。
    ///
    /// <para>这条同时钉两件事，缺一不可：
    /// ① <b>语义没被观测改动带偏</b>——<c>Paused</c> 仍按 docs/41 #21 的决定**不校正**，位置分毫不动；
    /// ② <b>"该校正却没人管"这件事现在有读数了</b>——判据侧看到的 0.25–0.40s 漂移与校正侧
    ///   日志里的 90–150ms 差 2–4 倍，之前三条静默跳过路径（gate 忙 / master 非播 / 从路态排除）
    ///   在日志里长得一模一样，分不出是哪一条。这一条把"态排除"那一条变成可断言的显式读数。</para>
    /// <para>只读 <c>DriftSkipDiag()</c> 的字符串，不去反射私有计数——它本来就是给人看的判据面。</para>
    /// </summary>
    [Fact]
    public void TickDrift_从路Paused大偏差_不校正但留下跳过见证()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            var drifted = 2 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond;
            var follower = sync.Slots.ElementAt(1).Session;
            follower.Seek(drifted);
            follower.Pause();          // 只把这一路钉成 Paused，master 保持播放

            sync.TickDrift();

            Assert.Equal(drifted, follower.ReadSnapshot()!.Position100ns);   // ① 语义：不校正
            var diag = sync.DriftSkipDiag();
            Assert.Contains("Paused大偏差=1", diag);                          // ② 读数：记下了
        }
        finally { sync.Clear(); }
    }

    /// <summary>上一条的**反向臂**：同样 500ms 偏差，但从路是 Playing（会被正常校正）。
    /// 没有这一条，上一条判据对"无条件把计数置 1"的坏实现也是绿的——那等于没测。
    /// 两臂合起来才说明计的是"该管而没人管"，不是"只要有偏差就记一笔"。</summary>
    [Fact]
    public void TickDrift_同样偏差但Playing_被校正就不算跳过()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            sync.Slots.ElementAt(1).Session.Seek(
                2 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond);

            sync.TickDrift();

            var m = sync.Slots.ElementAt(0).Session.ReadSnapshot()!.Position100ns;
            Assert.Equal(m, sync.Slots.ElementAt(1).Session.ReadSnapshot()!.Position100ns);
            // 反向断言要直白：计数必须还是 0（"被校正过"不该被记成"没人管"）
            Assert.Contains("Paused大偏差=0", sync.DriftSkipDiag());
        }
        finally { sync.Clear(); }
    }

    /// <summary>#42 的代数核对（正臂）：漂移校正发出 Seek、内核那边真做成了一次 demuxer seek
    /// ⇒ 下一拍把"代数已变"记进 <see cref="SyncController.DriftSkipDiag"/>，且<b>只记一次</b>。
    ///
    /// <para>为什么需要这一对：现场日志里 <c>→ Seek</c> 行每秒都在发、位置却从不回，
    /// 而 <c>PlayerSession::Seek</c> 只是 <c>Enqueue</c> 并立刻返回 Success ⇒ "调用返回成功"
    /// 根本不能证明 Seek 做成过。唯一的正面证据是内核只在 <c>av_seek_frame</c> 成功后
    /// 才做的 <c>++snapshot_.timelineGeneration</c>。</para></summary>
    [Fact]
    public void TickDrift_Seek做成了_下一拍核对记为代数已变且只核对一次()
    {
        var clock = new ManualClock();
        var (sync, _) = CreateSync(2, clock);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            sync.Slots.ElementAt(1).Session.Seek(
                2 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond);

            sync.TickDrift();                                        // 这一拍发 Seek，还没资格核对
            Assert.Contains("变=0 未变=0", sync.DriftSkipDiag());

            clock.Advance(TimeSpan.FromMilliseconds(1050));
            sync.TickDrift();                                        // 下一拍核对
            Assert.Contains("变=1 未变=0", sync.DriftSkipDiag());
            Assert.Equal(Pos(sync, 0), Pos(sync, 1));                // 位置也确实回来了

            clock.Advance(TimeSpan.FromMilliseconds(1050));
            sync.TickDrift();                                        // 第三次不该再重复计同一笔
            Assert.Contains("变=1 未变=0", sync.DriftSkipDiag());
        }
        finally { sync.Clear(); }
    }

    /// <summary>上一条的**反向臂**：Seek 被接受、但内核里什么都没做成（<c>av_seek_frame</c>
    /// 失败那条路径的建模，见 <see cref="FakeSession.SeekIsNoOp"/>）⇒ 必须记成"代数未变"。
    ///
    /// <para>没有这一臂，正臂对"无条件把 变 加一"的坏实现也是绿的。两臂合起来才说明
    /// 这个读数计的是"那一次 Seek 在内核里到底做成没有"。</para></summary>
    [Fact]
    public void TickDrift_Seek没做成_下一拍核对记为代数未变()
    {
        var clock = new ManualClock();
        var (sync, _) = CreateSync(2, clock);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            var follower = (FakeSession)sync.Slots.ElementAt(1).Session;
            var drifted = 2 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond;
            follower.Position100ns = drifted;    // 直接摆位：不经过 Seek，免得把代数先动了
            follower.SeekIsNoOp = true;
            var gen0 = follower.TimelineGeneration;

            sync.TickDrift();
            clock.Advance(TimeSpan.FromMilliseconds(1050));
            sync.TickDrift();

            Assert.Contains("变=0 未变=1", sync.DriftSkipDiag());
            Assert.Equal(gen0, follower.TimelineGeneration);         // 内核什么都没做
            Assert.Equal(drifted, follower.Position100ns);           // 位置也没回 ⇒ 判据下一轮必然抓红
        }
        finally { sync.Clear(); }
    }

    // ══════════ 漂移校正（docs/15 §3.3 / 交接 §2.4）补充覆盖 ══════════

    // 真实多路长片调参被上游崩溃（issue #7）阻塞，这里用 SimulatedEngine 3 路
    // 先把**可确定性验证**的语义面钉死：节流、阈值下界、只动 follower、尊重偏移、
    // 以及"1s 冷却 + 半帧阈值"这组参数能否每次都收敛。

    private static long Pos(SyncController sync, int index)
        => sync.Slots.ElementAt(index).Session.ReadSnapshot()!.Position100ns;

    /// <summary>1s 冷却：冷却期内的重复调用必须被忽略。
    /// 否则 4Hz 轮询下每秒 4 次 av_seek_frame，多路就是周期性 CPU 尖峰。</summary>
    [Fact]
    public void TickDrift_冷却期内不重复校正()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);

            // 第一次：推走 500ms，应被拉回（替身位置是静态量 ⇒ 收敛即精确相等）
            sync.Slots.ElementAt(1).Session.Seek(Pos(sync, 0) + 500 * TimeSpan.TicksPerMillisecond);
            sync.TickDrift();
            Assert.Equal(Pos(sync, 0), Pos(sync, 1));

            // 冷却期内再次推走：不应被拉回（位置必须原样停在注入值上）
            sync.Slots.ElementAt(1).Session.Seek(Pos(sync, 0) + 500 * TimeSpan.TicksPerMillisecond);
            sync.TickDrift();
            Assert.Equal(Pos(sync, 0) + 500 * TimeSpan.TicksPerMillisecond, Pos(sync, 1));
        }
        finally { sync.Clear(); }
    }

    /// <summary>半帧阈值下界：小于半帧的偏差不校正。
    /// SimulatedEngine 默认 24fps ⇒ 半帧 = 1/(2×24)s ≈ 20.83ms。</summary>
    [Fact]
    public void TickDrift_偏差小于半帧_不校正()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            // 10ms < 20.83ms
            sync.Slots.ElementAt(1).Session.Seek(Pos(sync, 0) + 10 * TimeSpan.TicksPerMillisecond);
            sync.TickDrift();
            // 位置必须原样停在注入值上（多校 1 tick 都是"无谓 Seek"）
            Assert.Equal(Pos(sync, 0) + 10 * TimeSpan.TicksPerMillisecond, Pos(sync, 1));
        }
        finally { sync.Clear(); }
    }

    /// <summary>只动 follower：master 是基准轴，漂移校正绝不能改动它
    /// （给它校正 ⇒ 与 SeekTo 语义矛盾、每次微调会累积，docs/15 §3.1）。</summary>
    [Fact]
    public void TickDrift_不改动Master()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            // 让 master 自己相对其它路漂出去 800ms
            sync.Slots.ElementAt(0).Session.Seek(
                2 * TimeSpan.TicksPerSecond + 800 * TimeSpan.TicksPerMillisecond);
            var before = Pos(sync, 0);
            sync.TickDrift();
            var after = Pos(sync, 0);
            // master 是基准：一个 tick 都不许动（替身不推进时间 ⇒ 必须是精确相等）
            Assert.Equal(before, after);
        }
        finally { sync.Clear(); }
    }

    /// <summary>期望值 = master + offset：有偏移的从路不该被当成漂移拉平。</summary>
    [Fact]
    public void TickDrift_尊重各路偏移()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.Slots.ElementAt(1).Offset100ns = 500 * TimeSpan.TicksPerMillisecond;
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);   // 路1 落在 2.5s
            sync.TickDrift();
            var d = Pos(sync, 1) - Pos(sync, 0) - 500 * TimeSpan.TicksPerMillisecond;
            Assert.Equal(0, d);   // 偏移必须原样保留（替身不推进时间 ⇒ 精确相等）
        }
        finally { sync.Clear(); }
    }

    /// <summary>交接 §2.4 参数实测的可确定性替代：跨过 1s 冷却反复注入漂移，
    /// 每次都必须收敛到半帧内。这里验证的是"1s + 半帧"这组参数**确实能收敛**，
    /// 至于真实长片下的漂移速率是否会超过校正能力，仍需实机（被上游崩溃阻塞）。
    ///
    /// <para><b>改造前的问题</b>：靠 <c>Thread.Sleep(1050)</c> 等真实挂钟跨冷却，
    /// 既慢（3 轮 ≈2.1s）又 flaky（机器卡顿/时钟粒度不足时冷却没跨过去 ⇒ 假红）。
    /// 现在把 <c>ManualClock</c> 注入 <see cref="SyncController"/>，"等 1 秒"变成"跳 1 秒"。</para></summary>
    [Fact]
    public void TickDrift_反复漂移_每次都收敛到半帧内()
    {
        var clock = new ManualClock();
        var (sync, _) = CreateSync(3, clock);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            for (var round = 0; round < 3; round++)
            {
                sync.Slots.ElementAt(1).Session.Seek(
                    Pos(sync, 0) + (round + 1) * 300 * TimeSpan.TicksPerMillisecond);
                sync.TickDrift();
                Assert.Equal(Pos(sync, 0), Pos(sync, 1));   // 收敛即精确对齐，不留残差
                if (round < 2) clock.Advance(TimeSpan.FromMilliseconds(1050)); // 跨过 1s 冷却
            }
        }
        finally { sync.Clear(); }
    }

    /// <summary>Clear 后再开循环应能正常工作（复位没有把状态机搞坏）。</summary>
    [Fact]
    public void Clear_之后仍可重新启用循环()
    {
        var (sync, _) = CreateSync(2);
        try
        {
            sync.LoopEnabled = true;
            sync.Clear();

            sync.LoopEnabled = true;
            sync.LoopStart100ns = 0;
            sync.LoopEnd100ns = TimeSpan.FromSeconds(3).Ticks;
            Assert.True(sync.LoopEnabled);
            Assert.Equal(TimeSpan.FromSeconds(3).Ticks, sync.LoopEnd100ns);
        }
        finally { sync.Clear(); }
    }
    // ══════════ #25：再启用后掉播的"可判别性"（诊断只报不补发）══════════
    //
    // 这一组测试钉的是 <c>docs/50</c> §二十二 那两例间歇红（2/25 = 8%）周围<b>已经确认</b>的行为，
    // 不是未经确认的修复方案：机制还没分清"根本没请求起播"与"请求了但在队列里丢了"，
    // 所以现在只允许加计数与告警，<b>不允许</b>自动补发 —— multitest 的判别实验正是靠
    // "被有意停着的那一路不会被自动叫醒"这条语义活着，而 Core 里还没有"有意停"这个状态。

    [Fact]
    public void Reactivation_WhileGroupPaused_DoesNotRequestPlay()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            var all = new System.Collections.Generic.HashSet<int> { 0, 1, 2 };
            sync.SetActiveRoutes(all);
            var s1 = (FakeSession)sync.Slots[1].Session;
            var playsBefore = s1.PlayCount;

            sync.Pause();                                     // 整组不在播
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0 });
            sync.SetActiveRoutes(all);                        // 再启用：此刻 WantPlaying=false

            Assert.Equal(playsBefore, s1.PlayCount);          // 激活分支不请求起播
            Assert.Equal(0, sync.Slots[1].PlayRequests);      // 连"请求过"的计数都不该有
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void GroupPlay_RequestsPlayOnEveryActiveRoute_AndCountsIt()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
            sync.Play();

            for (var i = 0; i < 3; i++)
            {
                Assert.True(sync.Slots[i].PlayRequests >= 1, $"第 {i} 路应被记到至少一次起播请求");
                Assert.Equal(PlayerState.Playing, ((FakeSession)sync.Slots[i].Session).State);
            }
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void DetectLostPlay_NeedsTwoConsecutiveStalledTicks_AndNeverWakesTheRoute()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
            sync.Play();
            var s1 = (FakeSession)sync.Slots[1].Session;
            var playsAtStart = s1.PlayCount;

            // 人为造出间歇红的形状：请求过了，但会话不在播放态（"排队瞬态"由"要连续两拍"挡掉）
            s1.State = PlayerState.Paused;

            sync.RetryPendingPlays();
            Assert.Equal(1, sync.Slots[1].NotPlayingStreak);      // 单拍不报：那是正常的排队窗口

            sync.RetryPendingPlays();
            Assert.Equal(2, sync.Slots[1].NotPlayingStreak);      // 连续第二拍才认定掉了

            // 只诊断不补发：检测器一条 Play 都不许替用户发出去
            Assert.Equal(playsAtStart, s1.PlayCount);
            Assert.Equal(PlayerState.Paused, s1.State);
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void DetectLostPlay_SilentWhilePlaying_AndSilentForNeverRequestedRoute()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
            sync.Play();
            for (var t = 0; t < 4; t++) sync.RetryPendingPlays();
            Assert.Equal(0, sync.Slots[1].NotPlayingStreak);      // 健康态不该被报

            // 另一头：从没请求过起播的路（上面 T1 的形状）也不该被同一条告警盖住 ——
            // 否则"没请求"与"请求了又丢了"这两种成因就会被合并成一个假信号。
            var sync2 = CreateSync(3).sync;      // 外层已占用 _ 这个弃元，嵌套作用域里不能再隐式推断
            try
            {
                sync2.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
                sync2.Pause();
                ((FakeSession)sync2.Slots[1].Session).State = PlayerState.Paused;
                for (var t = 0; t < 4; t++) sync2.RetryPendingPlays();
                Assert.Equal(0, sync2.Slots[1].PlayRequests);
                Assert.Equal(0, sync2.Slots[1].NotPlayingStreak);
            }
            finally { sync2.Clear(); }
        }
        finally { sync.Clear(); }
    }

    /// <summary>组播放扇出被内核状态守卫拒掉时，必须挂进既有的补发账（本轮实测驱动的最小修复）。
    ///
    /// <para>复现的是 2026-10-02 03:52:59.442 的形态：`Pause()` 与 `Play()` 在同一毫秒里背靠背扇出，
    /// 处于 <c>Opening</c> 的那一路两边都被拒 ⇒ 修复前那次"要播"既不计数也不挂起，
    /// 而先入队的 Pause 照常执行 ⇒ 该路永久停在 Paused，整组却在播。
    /// 用 <c>GuardThrows</c> 的假会话把内核那条守卫搬进测试（假引擎旧默认是"不允许就静默 no-op"，
    /// 那个形态根本表达不出这类丢失）。</para></summary>
    [Fact]
    public void GroupPlay_GuardRejection_IsHeldPendingAndRecoveredByRetry()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
            var r1 = (FakeSession)sync.Slots[1].Session;
            var r2 = (FakeSession)sync.Slots[2].Session;
            r1.GuardThrows = true; r2.GuardThrows = true;
            r1.State = PlayerState.Paused;      // 守卫会接受 ⇒ 正对照
            r2.State = PlayerState.Opening;     // 守卫会拒 ⇒ 被测那一路

            sync.Play();

            Assert.Equal(PlayerState.Playing, r1.State);
            Assert.Equal(1, sync.Slots[1].PlayRequests);
            // 被拒的那一路：请求**没有**被记成已发出（计数为 0），但必须挂起等补发
            Assert.Equal(0, sync.Slots[2].PlayRequests);
            Assert.True(sync.Slots[2].PlayPending, "组播放被守卫拒掉后必须挂 PlayPending，否则这一下要播永久丢失");
            // 抛错仍要进既有的运行错误上报通道（这一条同时钉住"catch 里别把上报改掉/丢掉"）
            Assert.Contains("播放第 2 路", sync.LastRuntimeError);

            // 过渡态落定成 Paused（内核执行完那条 Pause）⇒ 下一拍补发应当真的叫醒
            r2.State = PlayerState.Paused;
            var woke = sync.RetryPendingPlays();

            Assert.Equal(1, woke);
            Assert.Equal(PlayerState.Playing, r2.State);
            Assert.False(sync.Slots[2].PlayPending);
        }
        finally { sync.Clear(); }
    }

    /// <summary>修复不得越界：整组暂停时，挂着的补发意图不能把这一路叫醒（multitest 判别实验依赖）。</summary>
    [Fact]
    public void GroupPlay_GuardRejection_NeverWakesWhileGroupPaused()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
            var r2 = (FakeSession)sync.Slots[2].Session;
            r2.GuardThrows = true;
            r2.State = PlayerState.Opening;

            sync.Play();                        // 触发一次被拒 ⇒ PlayPending=true
            Assert.True(sync.Slots[2].PlayPending);

            sync.Pause();                       // 整组改主意：不再要播
            r2.State = PlayerState.Paused;      // 就算过渡态落定成"可播"，也不许被补发叫醒
            var woke = sync.RetryPendingPlays();

            Assert.Equal(0, woke);
            Assert.Equal(PlayerState.Paused, r2.State);
            Assert.False(sync.Slots[2].PlayPending);   // 意图不作数了 ⇒ 挂账要一并清掉，不许留到下次误补
        }
        finally { sync.Clear(); }
    }

    /// <summary>停用那一路的 `Pause` 被内核守卫拒掉时，`SetActiveRoutes` 的返回值仍必须计入这一次。
    ///
    /// <para>实测当日 616 次 <c>EngineException: Pause 失败: InvalidState</c> 全走这一支，
    /// 而抛错会跳过 try 末尾的 <c>changed++</c> ⇒ 返回值把"翻过来的标志"报成 0；
    /// 调用方（<c>MainWindow.Playback.cs:145</c>）拿 <c>changed == 0</c> 当"什么都没改"直接 return
    /// ⇒ 状态栏与覆盖层这一拍根本不刷。牙：修复前这里拿到的是 <c>0</c>。</para></summary>
    [Fact]
    public void SetActiveRoutes_CountsTheFlipEvenWhenDeactivationPauseIsRejected()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            var r2 = (FakeSession)sync.Slots[2].Session;
            r2.GuardThrows = true;
            r2.State = PlayerState.Opening;      // 过渡态：Pause 的守卫只放行 Playing ⇒ 必拒

            var changed = sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1 });

            Assert.Equal(1, changed);                    // 停用第 2 路：标志翻了就得计入，与 Pause 成没成无关
            Assert.False(sync.IsRouteActive(2));         // 标志确实落地了
            Assert.Equal(1, sync.InactiveRoutes().Count);
        }
        finally { sync.Clear(); }
    }

    // ── RA-01：挂账对**所有下标**生效，补发却从 i=1 起 ⇒ route 0（master）零覆盖 ──────────
    // 上面那组同形用例（GroupPlay_GuardRejection_*）用的都是 Slots[2]，而
    //   ① `ForEachSlot` 的守卫拒绝挂账写在循环体内，循环从 i=0 起 ⇒ master 也会被挂；
    //   ② `RetryPendingPlays` 的循环从 **i=1** 起 ⇒ 下标 0 的 PlayPending 既不补发也不清零。
    // 于是"master 这一路的要播永久丢失"这件事，测试面上一条都看不见。下面三条只打在 route 0 上。

    /// <summary>master 被状态守卫拒掉的组起播，必须由 <see cref="SyncController.RetryPendingPlays"/> 补发。
    ///
    /// <para><b>判据为什么写成"补发到达会话"</b>：<c>PlayCount</c> 与 <c>State</c> 是会话侧的真读数
    /// ——把 <c>PlayPending</c> 抹掉（哪怕顺带 <c>woke</c> 也报对）也能骗过"标志已清"这种判据，
    /// 但那一条永远不会让 master 播起来。牙：修复前 <c>RetryPendingPlays()</c> 返回 0、
    /// <c>PlayCount</c> 停在 1、master 停在 Paused。</para></summary>
    [Fact]
    public void GroupPlay_MasterGuardRejection_IsRetriedByPendingPlays()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
            var r0 = (FakeSession)sync.Slots[0].Session;    // master（被测下标）
            var r1 = (FakeSession)sync.Slots[1].Session;    // 正对照：i≥1 走的是既有通路
            r0.GuardThrows = true; r1.GuardThrows = true;
            r0.State = PlayerState.Opening;     // 过渡态 ⇒ Play 的守卫必拒
            r1.State = PlayerState.Paused;      // 守卫接受 ⇒ 这一路照常起播

            sync.Play();

            // 前置：对照组必须已经补得上，否则"红"可能来自我的夹具而不是 route 0。
            Assert.Equal(PlayerState.Playing, r1.State);
            Assert.Equal(1, sync.Slots[1].PlayRequests);
            // master：这一下请求既没算"已发出"，也确实还挂在账上（扇出对 i=0 同样生效）
            Assert.Equal(1, r0.PlayCount);                  // 只试过这一次，且被拒
            Assert.Equal(PlayerState.Opening, r0.State);
            Assert.Equal(0, sync.Slots[0].PlayRequests);
            Assert.True(sync.Slots[0].PlayPending,
                "组播放扇出的挂账对所有下标生效，master 也在这条账上");

            // 过渡态落定成 Paused（内核那条 Pause 执行完）⇒ 这一拍该把 master 叫醒
            r0.State = PlayerState.Paused;
            var woke = sync.RetryPendingPlays();

            Assert.Equal(1, woke);                            // 补发计数含下标 0
            Assert.Equal(2, r0.PlayCount);                    // 补发**到达了会话**，不是只清标志
            Assert.Equal(PlayerState.Playing, r0.State);
            Assert.False(sync.Slots[0].PlayPending);
        }
        finally { sync.Clear(); }
    }

    /// <summary>补发覆盖 route 0 之后，<c>TickDriftCore</c> 的"master 非播放 ⇒ 整拍早退"才不再发生：
    /// 从路那 300ms 的偏差被真的 Seek 回来。</summary>
    [Fact]
    public void GroupPlay_MasterRecoveredByRetry_ResumesWholeGroupCorrection()
    {
        var clock = new ManualClock();
        var (sync, _) = CreateSync(2, clock);
        try
        {
            var r0 = (FakeSession)sync.Slots[0].Session;
            var r1 = (FakeSession)sync.Slots[1].Session;
            r0.GuardThrows = true;
            r0.State = PlayerState.Opening;
            sync.Play();                                    // master 被拒，从路起播
            Assert.True(sync.Slots[0].PlayPending);

            r0.State = PlayerState.Paused;                  // 过渡态落定
            Assert.Equal(1, sync.RetryPendingPlays());
            Assert.Equal(PlayerState.Playing, r0.State);

            r1.Position100ns = 300 * TimeSpan.TicksPerMillisecond;   // 从路漂 300ms（≫ 半帧 ≈20.8ms）
            clock.Advance(TimeSpan.FromMilliseconds(1050));
            sync.TickDrift();

            Assert.Contains("master非播放=0", sync.DriftSkipDiag());
            Assert.Equal(1, r1.SeekCount);                  // 校正这一拍真的发了 Seek
            Assert.Equal(0L, r1.Position100ns);             // 拉回 expected = masterPos + offset = 0
        }
        finally { sync.Clear(); }
    }

    /// <summary>修复不得越界（route 0 版）：整组改主意暂停时，挂在 master 上的补发意图
    /// 既不能把这一路叫醒，也必须**连同标志一起作废** —— 改前这里 <c>PlayPending</c> 永远滞留在 true，
    /// 于是下一次组播放之外的任何读账动作都会读到一条早已不作数的挂账。</summary>
    [Fact]
    public void GroupPlay_MasterGuardRejection_NeverWakesWhileGroupPaused()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.SetActiveRoutes(new System.Collections.Generic.HashSet<int> { 0, 1, 2 });
            var r0 = (FakeSession)sync.Slots[0].Session;
            r0.GuardThrows = true;
            r0.State = PlayerState.Opening;
            sync.Play();
            Assert.True(sync.Slots[0].PlayPending);

            sync.Pause();                       // 整组改主意：不再要播
            r0.State = PlayerState.Paused;      // 就算过渡态落定成"可播"，也不许把 master 自动叫醒
            var woke = sync.RetryPendingPlays();

            Assert.Equal(0, woke);
            Assert.Equal(PlayerState.Paused, r0.State);
            Assert.False(sync.Slots[0].PlayPending);   // 意图不作数了 ⇒ 下标 0 的挂账也要一并清掉
        }
        finally { sync.Clear(); }
    }

    /// <summary>反证：只把 route 0 的 <c>PlayPending</c> 抹掉而<b>不补发</b>（= "另一种修法"的形状），
    /// master 会永远停在非播放态，而早退是<b>整拍</b>的 ⇒ 全组（含从路）失去校正。
    /// 这一条改前改后都该是绿的：它钉住的是"为什么清标志不算修好"。</summary>
    [Fact]
    public void MasterFlagClearedWithoutReissue_LeavesWholeGroupUncorrected()
    {
        var clock = new ManualClock();
        var (sync, _) = CreateSync(2, clock);
        try
        {
            var r0 = (FakeSession)sync.Slots[0].Session;
            var r1 = (FakeSession)sync.Slots[1].Session;
            r0.GuardThrows = true;
            r0.State = PlayerState.Opening;
            sync.Play();

            sync.Slots[0].PlayPending = false;              // ← 只清标志，不重发
            r1.Position100ns = 300 * TimeSpan.TicksPerMillisecond;
            clock.Advance(TimeSpan.FromMilliseconds(1050));
            sync.TickDrift();

            Assert.Equal(PlayerState.Opening, r0.State);    // master 根本没起来
            Assert.Contains("master非播放=1", sync.DriftSkipDiag());
            Assert.Equal(0, r1.SeekCount);                  // 那 300ms 到这一拍结束仍无人管
        }
        finally { sync.Clear(); }
    }
}

public class SessionSnapshotSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var snap = new SessionSnapshot
        {
            GridLayout = 2,
            Position100ns = 123456789,
            LoopEnabled = true,
            LoopStart100ns = 1000,
            LoopEnd100ns = 5000,
        };
        snap.Items.Add(new SessionSnapshot.SessionItem
        {
            Path = @"C:\videos\a.mp4",
            Offset100ns = 777,
            HardwareDecode = false,
            AdapterIndex = 2,
        });

        var json = snap.ToJson();
        var back = SessionSnapshot.FromJson(json);

        Assert.NotNull(back);
        Assert.Equal(snap.GridLayout, back!.GridLayout);
        Assert.Equal(snap.Position100ns, back.Position100ns);
        Assert.Equal(snap.LoopEnabled, back.LoopEnabled);
        Assert.Equal(snap.LoopStart100ns, back.LoopStart100ns);
        Assert.Equal(snap.LoopEnd100ns, back.LoopEnd100ns);
        Assert.Single(back.Items);
        Assert.Equal(snap.Items[0].Path, back.Items[0].Path);
        Assert.Equal(snap.Items[0].Offset100ns, back.Items[0].Offset100ns);
        Assert.Equal(snap.Items[0].HardwareDecode, back.Items[0].HardwareDecode);
        Assert.Equal(snap.Items[0].AdapterIndex, back.Items[0].AdapterIndex);
    }

    [Fact]
    public void InvalidJson_ReturnsNull()
    {
        Assert.Null(SessionSnapshot.FromJson("{not valid json"));
    }
}

public class AppSettingsSerializationTests
{
    [Fact]
    public void RoundTrip_PreservesSettings()
    {
        var s = new AppSettings
        {
            HardwareDecode = false,
            PreferredAdapterIndex = 1,
            ColorMode = ColorModeSetting.MapToHdr,
            FrameStep = 5,
            SecondsStep = 2.5,
            StartFullscreen = true,
            HideChromeInFullscreen = false,
            WindowX = 100,
            WindowY = 200,
            WindowWidth = 1920,
            WindowHeight = 1080,
            WindowState = 2, // Maximized
        };

        // 用与生产相同的 JsonAotContext 序列化
        var json = System.Text.Json.JsonSerializer.Serialize(s, _3FCompare.Core.Settings.JsonAotContext.Default.AppSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, _3FCompare.Core.Settings.JsonAotContext.Default.AppSettings);

        Assert.NotNull(back);
        Assert.Equal(s.HardwareDecode, back!.HardwareDecode);
        Assert.Equal(s.PreferredAdapterIndex, back.PreferredAdapterIndex);
        Assert.Equal(s.ColorMode, back.ColorMode);
        Assert.Equal(s.FrameStep, back.FrameStep);
        Assert.Equal(s.SecondsStep, back.SecondsStep);
        Assert.Equal(s.WindowX, back.WindowX);
        Assert.Equal(s.WindowState, back.WindowState);
    }

}
