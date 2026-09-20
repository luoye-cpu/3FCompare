using _3FCompare.Core.Capture;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <see cref="CaptureRouter"/> 的选路 / 回退 / 降级测试。
///
/// <para>期望值**独立推导**：全部直接照抄
/// <c>docs/27-抓屏双线WGC-GDI-实现规划.zh.md</c> §三 的策略表，不引用实现内部常量，
/// 也不从实现反推。表格原文（条件 → 行为）：</para>
/// <list type="number">
/// <item><c>Wgc_IsSupported() == false</c> → 直接走 GDI</item>
/// <item>DLL 缺失 / 加载失败 → 走 GDI</item>
/// <item><c>Wgc_Create</c> 失败 → 走 GDI</item>
/// <item><c>Wgc_CaptureFrame</c> 失败 → **本次**回退 GDI，下次仍先试 WGC</item>
/// <item>WGC 连续失败 N 次（建议 3）→ 本会话降级为 GDI</item>
/// <item>窗口最小化 → 直接走 GDI</item>
/// <item>降级后遇到"重开媒体 / 显卡显示器变更" ⇒ <c>Reset()</c> 解除降级、重新先试 WGC（S4 恢复路径）</item>
/// </list>
///
/// <para><b>覆盖不到的边界</b>：真实的 P/Invoke 抓帧（<c>3FC.WgcCapture.dll</c> 的加载/调用）、
/// 以及门面里"探测结果 → 路由输入"的搬运，都无法在本工程（只引用 Core）内验证，
/// 这里不造假覆盖。</para>
/// </summary>
public class CaptureRouterTests
{
    /// <summary>docs/27 §三 建议的降级阈值。</summary>
    private const int DocumentedThreshold = 3;

    private static CaptureRouter NewRouter() => new(DocumentedThreshold);

    // ══════════ 选路：四个"直接走 GDI / 走 WGC"的条件 ══════════

    /// <summary>表格第 1 行：系统不支持 WGC ⇒ 直接 GDI（哪怕 DLL 正常、窗口可见）。</summary>
    [Fact]
    public void 系统不支持WGC时走GDI()
    {
        var router = NewRouter();

        var decision = router.SelectRoute(wgcSupported: false, wgcDllAvailable: true, windowMinimized: false);

        Assert.Equal(CaptureRoute.Gdi, decision.Route);
        Assert.Equal(CaptureRouteReason.WgcNotSupported, decision.Reason);
        Assert.False(decision.IsWgc);
    }

    /// <summary>表格第 2 行：DLL 缺失 / 加载失败 ⇒ 走 GDI。</summary>
    [Fact]
    public void 原生库不可用时走GDI()
    {
        var router = NewRouter();

        var decision = router.SelectRoute(wgcSupported: true, wgcDllAvailable: false, windowMinimized: false);

        Assert.Equal(CaptureRoute.Gdi, decision.Route);
        Assert.Equal(CaptureRouteReason.WgcUnavailable, decision.Reason);
    }

    /// <summary>表格按从上到下判定：系统不支持优先于库不可用（两者都为 false 时报第一个原因）。</summary>
    [Fact]
    public void 系统不支持优先于库不可用()
    {
        var router = NewRouter();

        var decision = router.SelectRoute(wgcSupported: false, wgcDllAvailable: false, windowMinimized: false);

        Assert.Equal(CaptureRoute.Gdi, decision.Route);
        Assert.Equal(CaptureRouteReason.WgcNotSupported, decision.Reason);
    }

    /// <summary>表格的默认分支：一切正常 ⇒ 走 WGC（主线路）。</summary>
    [Fact]
    public void 一切正常时走WGC()
    {
        var router = NewRouter();

        var decision = router.SelectRoute(wgcSupported: true, wgcDllAvailable: true, windowMinimized: false);

        Assert.Equal(CaptureRoute.Wgc, decision.Route);
        Assert.Equal(CaptureRouteReason.WgcPreferred, decision.Reason);
        Assert.True(decision.IsWgc);
    }

    /// <summary>表格第 6 行：窗口最小化 ⇒ 直接 GDI（WGC 必然黑帧，别去白等 2 秒超时）。</summary>
    [Fact]
    public void 窗口最小化时直接走GDI()
    {
        var router = NewRouter();

        var decision = router.SelectRoute(wgcSupported: true, wgcDllAvailable: true, windowMinimized: true);

        Assert.Equal(CaptureRoute.Gdi, decision.Route);
        Assert.Equal(CaptureRouteReason.WindowMinimized, decision.Reason);
    }

    /// <summary>最小化是**瞬态**原因：窗口还原后必须回到 WGC，不能像降级那样粘住。</summary>
    [Fact]
    public void 窗口还原后回到WGC()
    {
        var router = NewRouter();

        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, windowMinimized: true).Route);
        Assert.Equal(CaptureRoute.Wgc, router.SelectRoute(true, true, windowMinimized: false).Route);
    }

    /// <summary>最小化被提前拦掉、根本没试 WGC ⇒ 不得计入失败（否则频繁最小化会把 WGC 误降级）。</summary>
    [Fact]
    public void 最小化不计入WGC失败()
    {
        var router = NewRouter();

        for (var i = 0; i < DocumentedThreshold + 2; i++)
            Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, windowMinimized: true).Route);

        Assert.Equal(0, router.ConsecutiveWgcFailures);
        Assert.False(router.IsWgcDowngraded);
        Assert.Equal(CaptureRoute.Wgc, router.SelectRoute(true, true, windowMinimized: false).Route);
    }

    // ══════════ 回退：单次失败不降级，连续失败才降级 ══════════

    /// <summary>表格第 4 行：单次失败只影响**本次**，下次调用仍先试 WGC。</summary>
    [Fact]
    public void 单次失败后仍先试WGC()
    {
        var router = NewRouter();

        router.ReportWgcFailure();

        var decision = router.SelectRoute(true, true, windowMinimized: false);

        Assert.Equal(CaptureRoute.Wgc, decision.Route);
        Assert.Equal(CaptureRouteReason.WgcPreferred, decision.Reason);
        Assert.Equal(1, router.ConsecutiveWgcFailures);
        Assert.False(router.IsWgcDowngraded);
    }

    /// <summary>表格第 4 + 5 行：阈值 3 意味着第 1、2 次失败之后都还要再试 WGC。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void 未达阈值前每次都先试WGC(int failuresSoFar)
    {
        var router = NewRouter();
        for (var i = 0; i < failuresSoFar; i++) router.ReportWgcFailure();

        Assert.Equal(CaptureRoute.Wgc, router.SelectRoute(true, true, windowMinimized: false).Route);
        Assert.Equal(failuresSoFar, router.ConsecutiveWgcFailures);
        Assert.False(router.IsWgcDowngraded);
    }

    /// <summary>表格第 5 行：连续失败 3 次 ⇒ 本会话降级为 GDI。</summary>
    [Fact]
    public void 连续三次失败后降级为GDI()
    {
        var router = NewRouter();

        var downgradedAt = 0;
        for (var i = 1; i <= DocumentedThreshold; i++)
        {
            if (router.ReportWgcFailure()) downgradedAt = i;
        }

        Assert.Equal(DocumentedThreshold, downgradedAt);   // 恰好在第 3 次跨过阈值
        Assert.Equal(DocumentedThreshold, router.ConsecutiveWgcFailures);
        Assert.True(router.IsWgcDowngraded);

        var decision = router.SelectRoute(true, true, windowMinimized: false);
        Assert.Equal(CaptureRoute.Gdi, decision.Route);
        Assert.Equal(CaptureRouteReason.WgcDowngraded, decision.Reason);
    }

    /// <summary>降级是"本会话"级别：条件恢复（窗口可见、库正常）也不回 WGC，避免每次都付超时代价。</summary>
    [Fact]
    public void 降级后不再回到WGC()
    {
        var router = NewRouter();
        for (var i = 0; i < DocumentedThreshold; i++) router.ReportWgcFailure();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, windowMinimized: false).Route);
            Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, windowMinimized: true).Route);
        }
    }

    /// <summary>降级后即使又报失败，也必须保持降级（幂等，不"复活"）。</summary>
    [Fact]
    public void 降级后继续失败仍保持降级()
    {
        var router = NewRouter();
        for (var i = 0; i < DocumentedThreshold; i++) router.ReportWgcFailure();

        Assert.True(router.ReportWgcFailure());
        Assert.True(router.IsWgcDowngraded);
        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, windowMinimized: false).Route);
    }

    // ══════════ "连续"语义：成功一次即重新计数 ══════════

    /// <summary>失败—失败—成功—失败—失败：中间成功过，故未降级（"连续"而非"累计"）。</summary>
    [Fact]
    public void 成功一次后连续计数清零()
    {
        var router = NewRouter();

        router.ReportWgcFailure();
        router.ReportWgcFailure();
        router.ReportWgcSuccess();
        router.ReportWgcFailure();
        router.ReportWgcFailure();

        Assert.Equal(2, router.ConsecutiveWgcFailures);
        Assert.False(router.IsWgcDowngraded);
        Assert.Equal(CaptureRoute.Wgc, router.SelectRoute(true, true, windowMinimized: false).Route);

        // 再来一次就正好凑满 3 次连续失败
        Assert.True(router.ReportWgcFailure());
        Assert.True(router.IsWgcDowngraded);
    }

    /// <summary>成功本身不改变"系统是否支持 / 库是否可用"的判定。</summary>
    [Fact]
    public void 成功不清除不可用状态()
    {
        var router = NewRouter();

        router.ReportWgcSuccess();

        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(false, true, false).Route);
        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, false, false).Route);
    }

    // ══════════ 阈值可配置 ══════════

    /// <summary>阈值参数化：1 次即降级。</summary>
    [Fact]
    public void 阈值为1时一次失败即降级()
    {
        var router = new CaptureRouter(1);

        Assert.True(router.ReportWgcFailure());
        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, false).Route);
    }

    /// <summary>阈值参数化：5 次才降级（4 次之后仍走 WGC）。</summary>
    [Fact]
    public void 阈值为5时四次失败仍不降级()
    {
        var router = new CaptureRouter(5);

        for (var i = 0; i < 4; i++) Assert.False(router.ReportWgcFailure());
        Assert.False(router.IsWgcDowngraded);
        Assert.Equal(CaptureRoute.Wgc, router.SelectRoute(true, true, false).Route);

        Assert.True(router.ReportWgcFailure());
        Assert.True(router.IsWgcDowngraded);
    }

    /// <summary>非法阈值必须立刻报错，而不是默默产生"第一次选路就降级"这种行为。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 阈值小于1时抛异常(int threshold)
    {
        // 用块体 lambda：表达式体 `() => new CaptureRouter(...)` 同时可转 Action 与 Func<object>，
        // 会让 Assert.Throws 的重载解析产生歧义。
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new CaptureRouter(threshold); });
    }

    /// <summary>默认阈值应与 docs/27 §三 的建议值（3）一致。</summary>
    [Fact]
    public void 默认阈值为3()
    {
        var router = new CaptureRouter();

        Assert.Equal(3, router.FailureThreshold);
    }

    /// <summary>初始状态：未降级、计数为 0。</summary>
    [Fact]
    public void 初始状态未降级()
    {
        var router = NewRouter();

        Assert.Equal(0, router.ConsecutiveWgcFailures);
        Assert.False(router.IsWgcDowngraded);
        Assert.Equal(DocumentedThreshold, router.FailureThreshold);
    }

    // ══════════ S4：Reset —— 降级必须可恢复 ══════════

    /// <summary>
    /// 降级后 <see cref="CaptureRouter.Reset"/> 必须让选路重新回到 WGC，且连续失败计数一并清零。
    ///
    /// <para>这条守的是 S4：<c>CaptureRouter</c> 在 UI 层是进程级静态单例，若降级不可解除，
    /// 3 次瞬时失败（驱动重置 / 锁屏 / 目标窗口被临时占用）就会让本进程余下的导出帧与缩略图
    /// 永远走对 flip-model 不可靠的 GDI，用户持续看到错内容直到重启应用。</para>
    /// </summary>
    [Fact]
    public void Reset后重新先试WGC()
    {
        var router = NewRouter();
        for (var i = 0; i < DocumentedThreshold; i++) router.ReportWgcFailure();
        Assert.True(router.IsWgcDowngraded);
        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, false).Route);

        router.Reset();

        Assert.False(router.IsWgcDowngraded);
        Assert.Equal(0, router.ConsecutiveWgcFailures);
        var decision = router.SelectRoute(true, true, windowMinimized: false);
        Assert.Equal(CaptureRoute.Wgc, decision.Route);
        Assert.Equal(CaptureRouteReason.WgcPreferred, decision.Reason);
    }

    /// <summary>Reset 也清掉"还差一次就降级"的残留计数：否则失败会跨"重开媒体"累加，提前降级。</summary>
    [Fact]
    public void Reset清零未达阈值的连续失败计数()
    {
        var router = NewRouter();
        router.ReportWgcFailure();
        router.ReportWgcFailure();

        router.Reset();

        Assert.Equal(0, router.ConsecutiveWgcFailures);
        // 重置后再失败两次，仍不应降级（说明计数确实从头开始，而非接着 2 往上加）
        Assert.False(router.ReportWgcFailure());
        Assert.False(router.ReportWgcFailure());
        Assert.False(router.IsWgcDowngraded);
    }

    /// <summary>Reset 不得影响"系统是否支持 / 库是否可用"的判定（那是环境事实，不是会话状态）。</summary>
    [Fact]
    public void Reset不改变环境判定()
    {
        var router = NewRouter();
        for (var i = 0; i < DocumentedThreshold; i++) router.ReportWgcFailure();

        router.Reset();

        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(false, true, false).Route);
        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, false, false).Route);
        Assert.Equal(CaptureRoute.Gdi, router.SelectRoute(true, true, windowMinimized: true).Route);
    }

    /// <summary>
    /// 并发下的**状态一致性**（Reset 会从 UI 线程调用，而失败/成功上报来自抓帧线程）。
    ///
    /// <para>断言的是"降级标记与计数不得自相矛盾"这一不变量：计数 ≥ 阈值却未标记降级（或反之），
    /// 只可能来自撕裂读。<b>诚实说明</b>：这是不变量守卫，不是"证明了没有竞态"——
    /// 无锁实现也未必每次都判红；但配合锁后它恒成立，故可作为长期回归护栏。</para>
    /// </summary>
    [Fact]
    public async Task 并发失败与重置不会留下自相矛盾的状态()
    {
        var router = NewRouter();
        const int rounds = 5000;
        var workers = new Task[4];
        for (var t = 0; t < workers.Length; t++)
        {
            workers[t] = Task.Run(() =>
            {
                for (var i = 0; i < rounds; i++)
                {
                    router.ReportWgcFailure();
                    if ((i & 15) == 0) router.Reset();
                }
            });
        }
        await Task.WhenAll(workers);

        var count = router.ConsecutiveWgcFailures;
        var downgraded = router.IsWgcDowngraded;

        // 不变量①：降级标记与计数必须一致（达阈值 ⇔ 已降级）
        Assert.Equal(count >= router.FailureThreshold, downgraded);
        // 不变量②：选路结果必须与降级标记一致
        Assert.Equal(downgraded ? CaptureRoute.Gdi : CaptureRoute.Wgc,
            router.SelectRoute(true, true, windowMinimized: false).Route);
    }

    // ══════════ 路径标识 ══════════

    /// <summary>路径标识必须能区分两条线，且 <c>IsWgc</c> 与 <c>Route</c> 一致。</summary>
    [Theory]
    [InlineData(CaptureRoute.Wgc, true)]
    [InlineData(CaptureRoute.Gdi, false)]
    public void 路径标识与IsWgc一致(CaptureRoute route, bool expectedIsWgc)
        => Assert.Equal(expectedIsWgc, new CaptureRouteDecision(route, CaptureRouteReason.WgcPreferred).IsWgc);

    /// <summary>
    /// <c>default(CaptureRoute)</c> 必须落在**不可信**的一侧（GDI）。
    /// 否则任何"忘了赋值"的字段/结构体都会默认声称自己来自可信线路，
    /// 失败方向是危险的（docs/26 §3.2 的教训就是不能让不可信路径冒充可信）。
    /// </summary>
    [Fact]
    public void 默认值落在不可信一侧()
    {
        Assert.Equal(CaptureRoute.Gdi, default(CaptureRoute));
        Assert.False(default(CaptureRouteDecision).IsWgc);
    }
}
