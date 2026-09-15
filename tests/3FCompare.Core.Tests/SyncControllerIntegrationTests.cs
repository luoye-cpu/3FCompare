using _3FCompare.Core.Backend;
using _3FCompare.Core.Settings;
using _3FCompare.Core.Sync;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>SyncController 多会话同步（演示引擎）测试：偏移/重排/循环/双步进。</summary>
public class SyncControllerIntegrationTests
{
    private static (SyncController sync, IPlayerEngine engine) CreateSync(int count)
    {
        var engine = new SimulatedEngine();
        var sync = new SyncController();
        for (var i = 0; i < count; i++)
        {
            var session = engine.CreateSession(new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
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
    /// 说明：SimulatedEngine 的 Seek 内部本身就会 clamp，所以这条在本替身下恒绿；
    /// 它守的是"若将来替身或内核不再 clamp，托管侧仍要兜住"这层防御。</summary>
    [Fact]
    public void SeekTo_不得超出各路自身时长()
    {
        var (sync, _) = CreateSync(3);
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
        }
        finally { sync.Clear(); }
    }

    /// <summary>
    /// docs/15 §3.3：播放中若某路漂出阈值（半帧），应被 Seek 拉回 master 基准。
    /// </summary>
    [Fact]
    public void TickDrift_播放中偏差超阈值_把从路拉回()
    {
        var (sync, _) = CreateSync(3);
        try
        {
            sync.Play();
            sync.SeekTo(2 * TimeSpan.TicksPerSecond);
            // 人为让第 1 路漂走 500ms，远超半帧阈值
            sync.Slots.ElementAt(1).Session.Seek(
                2 * TimeSpan.TicksPerSecond + 500 * TimeSpan.TicksPerMillisecond);

            sync.TickDrift();

            var m = sync.Slots.ElementAt(0).Session.ReadSnapshot()!.Position100ns;
            var s = sync.Slots.ElementAt(1).Session.ReadSnapshot()!.Position100ns;
            // 残留只应来自两次读快照之间的真实时间推进，远小于 500ms
            Assert.True(Math.Abs(s - m) < 60 * TimeSpan.TicksPerMillisecond,
                $"漂移未被校正：master={m} 路1={s} Δ={s - m}");
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