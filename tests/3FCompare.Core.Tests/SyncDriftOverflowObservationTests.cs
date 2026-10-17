using _3FCompare.Core.Backend;
using _3FCompare.Core.Sync;
using _3FCompare.Core.Tests.Infrastructure;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary><see cref="SyncController"/> 的 #42 观测位（<c>ObserveSkippedDrift</c>）里
/// 那条 <c>catch (OverflowException)</c> 的**运行时前提**判据（RA-04）。
///
/// <para><b>为什么必须有实测而不是读码</b>：工程从未设置 <c>CheckForOverflowUnderflow</c>
/// （<c>src/3FCompare.Core/3FCompare.Core.csproj</c> 无该项，仓根也没有 <c>Directory.Build.props</c>）
/// ⇒ C# 默认 <b>unchecked</b> ⇒ <c>long</c> 相减**环绕且不抛** ⇒ 那个 catch 一次也不可能被触发，
/// 而环绕出来的数还会被当成"一个大偏差"计进读数（本条实测的就是这个形态）。
/// 判据全部走公开读数 <see cref="SyncController.DriftSkipDiag"/>，不碰私有字段。</para></summary>
public class SyncDriftOverflowObservationTests
{
    private const long Duration = 10 * TimeSpan.TicksPerSecond;
    private const double Fps = 24.0;

    /// <summary>两路：master 在 0 处播放（否则 <c>TickDriftCore</c> 整拍早退，观测位根本跑不到），
    /// 从路停在 <c>Paused</c>（观测位只收非 Playing/Ended 的那几路）。</summary>
    private static (SyncController sync, ManualClock clock, FakeSession master, FakeSession follower) Create()
    {
        var engine = new FakeEngine { DefaultDuration100ns = Duration, DefaultFrameRate = Fps };
        var clock = new ManualClock();
        var sync = new SyncController(clock);
        var master = (FakeSession)engine.CreateSession(
            new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
        master.OpenAsync("m.mp4").GetAwaiter().GetResult();
        var follower = (FakeSession)engine.CreateSession(
            new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
        follower.OpenAsync("f.mp4").GetAwaiter().GetResult();
        sync.AddSlot(master, "m.mp4");
        sync.AddSlot(follower, "f.mp4");
        master.State = PlayerState.Playing;
        master.Position100ns = 0;
        follower.State = PlayerState.Paused;    // 刻意排除项 ⇒ 正是观测位要记的那种
        follower.Duration100ns = Duration;
        return (sync, clock, master, follower);
    }

    /// <summary>改前红的那一条：<c>Position - expected</c> 落在 long 域外时，
    /// 环绕值会被计成一次"Paused 大偏差"，而 <c>Δ</c> 印出来是个**负数**（环绕的直接证据）。</summary>
    [Fact]
    public void ObserveSkippedDrift_OverflowingDelta_IsNotCounted()
    {
        var (sync, _, _, follower) = Create();
        try
        {
            sync.Slots[1].Offset100ns = -1;              // expected = 0 + (-1) = -1（|.| ≤ ±24h，不会被过滤掉）
            follower.Position100ns = long.MaxValue;      // MaxValue - (-1) = 2^63 ⇒ 有符号溢出

            sync.TickDrift();

            var diag = sync.DriftSkipDiag();
            Assert.Contains("Paused大偏差=0", diag);
            // 环绕才会出的负 Δ：修好后这条读数根本不该出现（漏计与误计都算坏，见 README 的取舍说明）
            Assert.DoesNotContain("Δ=-", diag);
        }
        finally { sync.Clear(); }
    }

    /// <summary>正对照：同量级但**落在 long 域内**的偏差仍要计一次，且 Δ 是正的。
    /// 没有这一条，上一句可以靠"把观测位整个关掉"来变绿。</summary>
    [Fact]
    public void ObserveSkippedDrift_RepresentableHugeDelta_IsStillCounted()
    {
        var (sync, _, _, follower) = Create();
        try
        {
            sync.Slots[1].Offset100ns = 0;                   // expected = 0 ⇒ 相减 = MaxValue-1000，可表示
            follower.Position100ns = long.MaxValue - 1000;

            sync.TickDrift();

            var diag = sync.DriftSkipDiag();
            // 手算：(long.MaxValue - 1000) / 10000 = 922337203685477（整数除法，不是从实现里引常数）
            Assert.Contains("Paused大偏差=1", diag);
            Assert.Contains("Δ=922337203685477ms", diag);
        }
        finally { sync.Clear(); }
    }

    /// <summary>BCL 前提读数（不测本项目代码，只把实现依赖的那条事实钉住）：
    /// <c>Math.Abs(long.MinValue)</c> 在本工程默认 <b>unchecked</b> 语境下**抛 OverflowException**
    /// （CoreLib 的取负是 checked 的；实测原文 "Negating the minimum value of a twos complement
    /// number is invalid."）。<c>SyncController</c> 里
    /// <c>delta == long.MinValue ? long.MaxValue : Math.Abs(delta)</c> 那一支的有效性全靠这条撑着
    /// ——相减本身在 unchecked 下只环绕不抛，所以"防抛"必须防在 <c>Math.Abs</c> 上。
    /// 哪天 BCL 改了行为，这里先红。</summary>
    [Fact]
    public void BclFact_MathAbs_OfLongMinValue_Throws()
    {
        Assert.Throws<System.OverflowException>(() => System.Math.Abs(long.MinValue));
        // 同一条上下文的另一半：相减**不**抛，只是环绕（这才是 RA-04 那个死守卫的成因）
        Assert.Equal(long.MinValue, unchecked(long.MaxValue - (-1)));
    }

    /// <summary>另一头：真正该报的大偏差（正常量级、Paused）照旧报，且只报第一次。</summary>
    [Fact]
    public void ObserveSkippedDrift_OrdinaryPausedDrift_StillReported()
    {
        var (sync, clock, _, follower) = Create();
        try
        {
            follower.Position100ns = 5 * TimeSpan.TicksPerSecond;   // 与 expected=0 差 5s

            sync.TickDrift();
            Assert.Contains("Paused大偏差=1", sync.DriftSkipDiag());

            clock.Advance(TimeSpan.FromMilliseconds(1050));         // 漂移检查按 1s 节流，不推进就还是同一拍
            sync.TickDrift();                                       // 同一原因只留一条详情，计数继续走
            Assert.Contains("Paused大偏差=2", sync.DriftSkipDiag());
        }
        finally { sync.Clear(); }
    }
}
