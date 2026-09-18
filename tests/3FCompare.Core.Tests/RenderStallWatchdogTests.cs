using System;
using _3FCompare.Core.Sync;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// RenderStallWatchdog（呈现停滞判定）测试。
/// 覆盖历史上真实出过的两个缺陷：
/// P1-5 暂停时也检测 ⇒ 空转累加、恢复播放立刻误触发；
/// P1-6 用"轮询次数"当时间 ⇒ 平移期间间隔压缩到 83ms 时 415ms 就误判。
/// </summary>
public sealed class RenderStallWatchdogTests
{
    private static RenderStallWatchdog New() => new(TimeSpan.FromMilliseconds(1250));

    /// <summary>模拟轮询：从 t0 起每 stepMs 一次、共 count 次，presented 恒定。</summary>
    private static StallAction Poll(RenderStallWatchdog w, long presented, long stepMs, int count,
        long startMs = 0, bool uiPlaying = true, bool engineActive = true)
    {
        var action = StallAction.None;
        for (var i = 1; i <= count; i++)
            action = w.Update(uiPlaying, engineActive, presented, startMs + i * stepMs);
        return action;
    }

    [Fact]
    public void FirstObservation_IsBaseline_NoAction()
    {
        var w = New();
        Assert.Equal(StallAction.None, w.Update(true, true, 42, 0));
    }

    [Fact]
    public void Paused_DoesNotAccumulate() // P1-5
    {
        var w = New();
        Assert.Equal(StallAction.None, w.Update(true, true, 42, 0));
        // 暂停 10 秒（presented 不增长），再恢复播放：不得立刻误触发
        for (var t = 250; t <= 10_000; t += 250)
            Assert.Equal(StallAction.None, w.Update(false, true, 42, t));
        // 恢复播放后仍需等满一个阈值窗口
        Assert.Equal(StallAction.None, w.Update(true, true, 42, 10_250));
        Assert.Equal(StallAction.None, w.Update(true, true, 42, 11_000));
        Assert.Equal(StallAction.LightRecovery, w.Update(true, true, 42, 11_500));
    }

    [Fact]
    public void EngineNotActive_DoesNotTrigger()
    {
        var w = New();
        w.Update(true, true, 42, 0);
        Assert.Equal(StallAction.None, Poll(w, 42, 250, 20, 0, engineActive: false));
    }

    [Fact]
    public void BelowThreshold_NoAction() // P1-6：按真实时长，与轮询频率无关
    {
        var w = New();
        w.Update(true, true, 42, 0);
        // 250ms 轮询 × 4 次 = 1000ms < 1250ms
        Assert.Equal(StallAction.None, Poll(w, 42, 250, 4));
        // 83ms 轮询（平移期间）累计到 1162ms 仍不触发
        Assert.Equal(StallAction.None, Poll(w, 42, 83, 14));
    }

    [Theory]
    // 注意：停滞计时从**首次观察到无增长**（即第一次轮询时刻）起算，不是从 t=0。
    // 故判定条件是 (count-1)×step ≥ 1250：
    [InlineData(250, 6)]   // (6-1)×250 = 1250
    [InlineData(83, 17)]   // (17-1)×83 = 1328
    [InlineData(16, 80)]   // (80-1)×16 = 1264
    public void ThresholdIsWallClock_RegardlessOfPollRate(long stepMs, int count) // P1-6
    {
        var w = New();
        w.Update(true, true, 42, 0);
        var action = Poll(w, 42, stepMs, count);
        Assert.Equal(StallAction.LightRecovery, action);
    }

    [Fact]
    public void EscalatesToFullRebuild_AfterLightRecoveryFails()
    {
        var w = New();
        w.Update(true, true, 42, 0);
        Assert.Equal(StallAction.LightRecovery, Poll(w, 42, 250, 6));          // t=1500
        // 轻量恢复后 presented 仍不涨：必须再等满一个阈值窗口才升级
        Assert.Equal(StallAction.None, Poll(w, 42, 250, 4, 1500));             // 到 2500ms
        Assert.Equal(StallAction.FullRebuild, Poll(w, 42, 250, 2, 2500));      // 到 3000ms
    }

    [Fact]
    public void GrowthResetsEscalation()
    {
        var w = New();
        w.Update(true, true, 42, 0);
        Assert.Equal(StallAction.LightRecovery, Poll(w, 42, 250, 6));          // 触发第一级
        // presented 恢复增长
        Assert.Equal(StallAction.None, w.Update(true, true, 43, 1600));
        // 再次停滞：应重新从第一级开始，而不是直接升级
        Assert.Equal(StallAction.None, Poll(w, 43, 250, 4, 1600));
        Assert.Equal(StallAction.LightRecovery, Poll(w, 43, 250, 2, 2600));
    }

    [Fact]
    public void Reset_ClearsEscalationForNewMedia()
    {
        var w = New();
        w.Update(true, true, 42, 0);
        Assert.Equal(StallAction.LightRecovery, Poll(w, 42, 250, 6));
        w.Reset(); // 打开新媒体
        w.Update(true, true, 100, 5000);
        Assert.Equal(StallAction.None, Poll(w, 100, 250, 4, 5000));
        Assert.Equal(StallAction.LightRecovery, Poll(w, 100, 250, 2, 6000));
    }
}
