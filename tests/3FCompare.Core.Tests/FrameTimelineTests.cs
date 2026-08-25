using _3FCompare.Core.Sync;
using Xunit;

namespace _3FCompare.Core.Tests;

public class FrameTimelineTests
{
    [Fact]
    public void FrameDuration_24fps_IsCorrect()
    {
        // 1 秒 = 10^7 ticks(100ns)，1/24 秒 = 416666.67 ≈ 416667
        Assert.Equal(416_667, FrameTimeline.FrameDuration100ns(24.0));
    }

    [Fact]
    public void FrameDuration_NonPositiveFps_ReturnsZero()
    {
        Assert.Equal(0, FrameTimeline.FrameDuration100ns(0));
        Assert.Equal(0, FrameTimeline.FrameDuration100ns(-30));
    }

    [Fact]
    public void StepByFrames_Forward_AddsFrameDuration()
    {
        // 当前位置 0，24fps，前进 1 帧
        var result = FrameTimeline.StepByFrames(0, TimeSpan.FromMinutes(1).Ticks, 1, 24.0);
        Assert.Equal(FrameTimeline.FrameDuration100ns(24.0), result);
    }

    [Fact]
    public void StepByFrames_ClampsToDuration()
    {
        var duration = TimeSpan.FromSeconds(1).Ticks;
        var result = FrameTimeline.StepByFrames(duration - 1, duration, 10, 24.0);
        Assert.Equal(duration, result); // 越界 clamp 到时长
    }

    [Fact]
    public void StepBySeconds_Forward()
    {
        var result = FrameTimeline.StepBySeconds(0, TimeSpan.FromMinutes(10).Ticks, 2.5);
        Assert.Equal(TimeSpan.FromSeconds(2.5).Ticks, result);
    }

    [Fact]
    public void StepBySeconds_Backward_ClampsToZero()
    {
        var result = FrameTimeline.StepBySeconds(TimeSpan.FromSeconds(1).Ticks, TimeSpan.FromMinutes(10).Ticks, -5);
        Assert.Equal(0, result);
    }

    [Fact]
    public void StepBySeconds_OverDuration_Clamps()
    {
        var duration = TimeSpan.FromSeconds(10).Ticks;
        var result = FrameTimeline.StepBySeconds(duration - 1, duration, 100);
        Assert.Equal(duration, result);
    }
}

public class StepProfileTests
{
    [Fact]
    public void DefaultProfile_IsOneFrameOneSecond()
    {
        var p = new StepProfile();
        Assert.Equal(1, p.FrameStep);
        Assert.Equal(1.0, p.SecondsStep);
    }
}

public class SyncControllerTests
{
    [Fact]
    public void EstimateFps_UsesSnapshotFrameRate()
    {
        // 修复后：优先用快照携带的媒体帧率（来自媒体信息 nominalFrameRate）
        var snap = new _3FCompare.Core.Backend.EngineSnapshot
        {
            Position100ns = 0,
            Duration100ns = TimeSpan.FromMinutes(1).Ticks,
            FrameIndex = 0,
            FrameRate = 25.0,
        };
        Assert.Equal(25.0, SyncController.EstimateFps(snap));
    }

    [Fact]
    public void EstimateFps_TimeBaseIsNotFps_FallsBackToDefault()
    {
        // 回归测试：frameTimeBase 是流时间基（如 MP4 的 1/15360），不是帧率。
        // 旧实现直接 Den/Num 把 4K H.264 (tbn=15360) 算成 15360fps —— 已修复为回退默认值。
        var snap = new _3FCompare.Core.Backend.EngineSnapshot
        {
            Position100ns = 0,
            Duration100ns = TimeSpan.FromMinutes(1).Ticks,
            FrameIndex = 0,
            FrameTimeBaseNum = 1,
            FrameTimeBaseDen = 15360,
        };
        Assert.Equal(24.0, SyncController.EstimateFps(snap));
    }

    [Fact]
    public void EstimateFps_NoData_FallsBackTo24()
    {
        var snap = new _3FCompare.Core.Backend.EngineSnapshot
        {
            Position100ns = 0,
            Duration100ns = TimeSpan.FromMinutes(1).Ticks,
            FrameIndex = 0,
            FrameTimeBaseNum = 0,
            FrameTimeBaseDen = 0,
        };
        Assert.Equal(24.0, SyncController.EstimateFps(snap));
    }
}