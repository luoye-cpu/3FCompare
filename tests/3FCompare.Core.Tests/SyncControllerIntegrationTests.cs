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