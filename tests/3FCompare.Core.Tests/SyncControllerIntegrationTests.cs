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
            WindowMaximized = true,
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
        Assert.Equal(s.WindowMaximized, back.WindowMaximized);
    }
}