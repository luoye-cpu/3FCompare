using _3FCompare.App.Capture;
using _3FCompare.Core.Capture;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 抓屏门面 <see cref="FrameCapture"/> 的**胶水层**行为：探测结果 → 选路 → 执行 → 失败计数 → GDI 回退。
///
/// <para>这段此前零覆盖，而 docs/28 §一 指出的风险恰好集中在这里（"不可用"与"失败"被混为一谈、
/// 最小化被误计入失败、降级后仍每次付 WGC 超时代价）。</para>
///
/// <para><b>期望值来源</b>：全部照抄 <c>docs/27-抓屏双线WGC-GDI-实现规划.zh.md</c> §三 的策略表，
/// 不从实现反推。该表原文（条件 → 行为）：</para>
/// <list type="number">
/// <item><c>Wgc_IsSupported() == false</c> → 直接走 GDI</item>
/// <item>DLL 缺失 / 加载失败 → 走 GDI</item>
/// <item><c>Wgc_Create</c> 失败 → 走 GDI（计一次 WGC 失败）</item>
/// <item><c>Wgc_CaptureFrame</c> 失败 → **本次**回退 GDI，下次仍先试 WGC</item>
/// <item>WGC 连续失败 N 次（建议 3）→ 本会话降级为 GDI，不再付超时代价</item>
/// <item>窗口最小化 → 直接 GDI（WGC 必然黑帧）</item>
/// </list>
///
/// <para>被测方法 <c>RouteAndCapture</c> 是 <c>CaptureWindowFrame</c> 的同一份实现（前者把三个环境
/// 输入与两条线参数化），因此这里断言的是**生产代码本身**，不是测试里重写的一份等价逻辑。</para>
/// </summary>
public class FrameCaptureRoutingTests
{
    /// <summary>非零假句柄：替身不解析句柄，取非零只为绕过"零句柄"守卫。</summary>
    private const nint Hwnd = 0x1234;

    /// <summary>docs/27 §三 建议的降级阈值。</summary>
    private const int DocumentedThreshold = 3;

    private static CaptureRouter NewRouter() => new(DocumentedThreshold);

    // ══════════ 选路：三个"直接走 GDI"的条件与默认的 WGC ══════════

    /// <summary>表格默认分支：一切正常 ⇒ 走 WGC（唯一能抓 flip-model 的线），成功就不该再动兜底线。</summary>
    [Fact]
    public void WGC可用且窗口可见时走WGC线()
    {
        var wgc = new FakeLane(CaptureRoute.Wgc);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = NewRouter();

        var frame = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);

        Assert.NotNull(frame);
        var f = frame.Value;
        try
        {
            Assert.Equal(CaptureRoute.Wgc, f.Route);
            Assert.Equal(1, wgc.CaptureCalls);
            Assert.Equal(0, gdi.CaptureCalls);
            Assert.Equal(Hwnd, Assert.Single(wgc.Handles));
            Assert.Equal(0, router.ConsecutiveWgcFailures);
            Assert.False(router.IsWgcDowngraded);
        }
        finally { f.Bitmap.Dispose(); }
    }

    /// <summary>
    /// 表格第 1、2 行：系统不支持 / 库缺失 ⇒ **直接**走 GDI。
    /// "不可用"不是"失败"：不该去试 WGC（试了必然失败、白等超时），也不该累计失败计数。
    /// </summary>
    [Theory]
    [InlineData(false, true)]   // Wgc_IsSupported() == 0
    [InlineData(true, false)]   // DLL 缺失 / 加载失败
    public void WGC不可用时直接走GDI且完全不尝试WGC(bool wgcSupported, bool wgcDllAvailable)
    {
        var wgc = new FakeLane(CaptureRoute.Wgc);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = NewRouter();
        var factoryCalls = 0;

        IFrameCapture Factory()
        {
            factoryCalls++;
            return wgc;
        }

        var frame = FrameCapture.RouteAndCapture(Hwnd, wgcSupported, wgcDllAvailable, false, Factory, gdi, router);

        Assert.NotNull(frame);
        var f = frame.Value;
        try
        {
            Assert.Equal(CaptureRoute.Gdi, f.Route);
            Assert.Equal(1, gdi.CaptureCalls);
            Assert.Equal(0, wgc.CaptureCalls);
            Assert.Equal(0, factoryCalls);                  // 连 WGC 线都不该被构造（缺库时构造本身就是浪费）
            Assert.Equal(0, router.ConsecutiveWgcFailures);  // 不可用 ≠ 失败
            Assert.False(router.IsWgcDowngraded);
        }
        finally { f.Bitmap.Dispose(); }
    }

    // ══════════ 回退：单次失败不粘住，连续失败才降级 ══════════

    /// <summary>表格第 4 行：单次失败只影响**本次**，下次调用仍先试 WGC。</summary>
    [Fact]
    public void WGC单次失败只回退本次下次仍先试WGC()
    {
        // 第 1 次失败、第 2 次成功：用来区分"每次重试"与"失败一次就粘在 GDI"。
        var wgc = new FakeLane(CaptureRoute.Wgc, succeeds: n => n >= 2);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = NewRouter();

        var first = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
        Assert.NotNull(first);
        var f1 = first.Value;
        try
        {
            Assert.Equal(CaptureRoute.Gdi, f1.Route);        // 本次回退
            Assert.Equal(1, wgc.CaptureCalls);
            Assert.Equal(1, gdi.CaptureCalls);
            Assert.Equal(1, router.ConsecutiveWgcFailures);
            Assert.False(router.IsWgcDowngraded);
        }
        finally { f1.Bitmap.Dispose(); }

        var second = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
        Assert.NotNull(second);
        var f2 = second.Value;
        try
        {
            Assert.Equal(CaptureRoute.Wgc, f2.Route);        // 关键：仍先试 WGC
            Assert.Equal(2, wgc.CaptureCalls);
            Assert.Equal(1, gdi.CaptureCalls);               // 没有第二次回退
            Assert.Equal(0, router.ConsecutiveWgcFailures);   // 成功即清零（"连续"语义）
        }
        finally { f2.Bitmap.Dispose(); }
    }

    /// <summary>
    /// 表格第 5 行 + 该行的目的（"避免每次都付超时代价"）：
    /// 连续失败达到阈值后，**后续调用不再尝试 WGC**。
    /// </summary>
    [Fact]
    public void WGC连续失败三次后本会话降级并不再尝试WGC()
    {
        var wgc = new FakeLane(CaptureRoute.Wgc, succeeds: false);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = NewRouter();

        for (var i = 1; i <= DocumentedThreshold; i++)
        {
            var call = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
            Assert.NotNull(call);
            var f = call.Value;
            try
            {
                Assert.Equal(CaptureRoute.Gdi, f.Route);
                Assert.Equal(i, wgc.CaptureCalls);            // 达阈值之前每次都必须真去试
                Assert.Equal(i, router.ConsecutiveWgcFailures);
                Assert.Equal(i, gdi.CaptureCalls);
            }
            finally { f.Bitmap.Dispose(); }
        }

        Assert.True(router.IsWgcDowngraded);

        var after = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
        Assert.NotNull(after);
        var fa = after.Value;
        try
        {
            Assert.Equal(CaptureRoute.Gdi, fa.Route);
            Assert.Equal(DocumentedThreshold, wgc.CaptureCalls);   // 降级后不再付 WGC 的超时代价
            Assert.Equal(DocumentedThreshold + 1, gdi.CaptureCalls);
        }
        finally { fa.Bitmap.Dispose(); }
    }

    /// <summary>
    /// 降级阈值必须来自注入的 <see cref="CaptureRouter"/>，而不是门面里写死的 3
    /// —— 否则"可配置阈值"只是 router 内部的摆设。
    /// </summary>
    [Fact]
    public void 降级阈值取自Router而非写死()
    {
        var wgc = new FakeLane(CaptureRoute.Wgc, succeeds: false);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = new CaptureRouter(failureThreshold: 2);

        for (var i = 0; i < 2; i++)
        {
            var call = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
            Assert.NotNull(call);
            call.Value.Bitmap.Dispose();
        }

        Assert.True(router.IsWgcDowngraded);

        var third = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
        Assert.NotNull(third);
        var f = third.Value;
        try
        {
            Assert.Equal(CaptureRoute.Gdi, f.Route);
            Assert.Equal(2, wgc.CaptureCalls);       // 阈值 2 ⇒ 第 3 次已降级，不再试 WGC
        }
        finally { f.Bitmap.Dispose(); }
    }

    /// <summary>
    /// "连续"而非"累计"：中间成功过一次，后续两次失败不得凑成降级。
    /// 若门面漏调 <c>ReportWgcSuccess</c>，第 3 次就会跨过阈值 ⇒ 本用例失败。
    /// </summary>
    [Fact]
    public void 中间成功一次后连续计数清零()
    {
        // 调用序列：失败 → 成功 → 失败 → 失败
        var wgc = new FakeLane(CaptureRoute.Wgc, succeeds: n => n == 2);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = NewRouter();

        for (var i = 0; i < 4; i++)
        {
            var call = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
            Assert.NotNull(call);
            call.Value.Bitmap.Dispose();
        }

        Assert.Equal(4, wgc.CaptureCalls);
        Assert.Equal(2, router.ConsecutiveWgcFailures);
        Assert.False(router.IsWgcDowngraded);
    }

    // ══════════ 最小化：瞬态原因，直接 GDI 且不计失败 ══════════

    /// <summary>
    /// 表格第 6 行：最小化 ⇒ 直接 GDI（WGC 必然黑帧，不该去白等约 2s），且**不计入失败次数**。
    /// 循环次数刻意大于阈值：一旦被误计入失败，WGC 会被频繁最小化永久降级。
    /// </summary>
    [Fact]
    public void 窗口最小化时直接走GDI且不计入失败次数()
    {
        var wgc = new FakeLane(CaptureRoute.Wgc);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = NewRouter();

        for (var i = 0; i < DocumentedThreshold + 2; i++)
        {
            var call = FrameCapture.RouteAndCapture(Hwnd, true, true, windowMinimized: true, () => wgc, gdi, router);
            Assert.NotNull(call);
            var f = call.Value;
            try { Assert.Equal(CaptureRoute.Gdi, f.Route); }
            finally { f.Bitmap.Dispose(); }
        }

        Assert.Equal(0, wgc.CaptureCalls);
        Assert.Equal(0, router.ConsecutiveWgcFailures);
        Assert.False(router.IsWgcDowngraded);

        // 最小化是**瞬态**原因：窗口还原后必须回到 WGC（不像降级那样粘住）
        var restored = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);
        Assert.NotNull(restored);
        var fr = restored.Value;
        try
        {
            Assert.Equal(CaptureRoute.Wgc, fr.Route);
            Assert.Equal(1, wgc.CaptureCalls);
        }
        finally { fr.Bitmap.Dispose(); }
    }

    // ══════════ 兜底与守卫 ══════════

    /// <summary>两条线都失败 ⇒ 返回 null（调用方据此判定"抓不到帧"，而不是拿到一张错图）。</summary>
    [Fact]
    public void 两条线都失败时返回null()
    {
        var wgc = new FakeLane(CaptureRoute.Wgc, succeeds: false);
        var gdi = new FakeLane(CaptureRoute.Gdi, succeeds: false);
        var router = NewRouter();

        var frame = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, router);

        Assert.Null(frame);
        Assert.Equal(1, wgc.CaptureCalls);
        Assert.Equal(1, gdi.CaptureCalls);
        Assert.Empty(wgc.HandedOut);
        Assert.Empty(gdi.HandedOut);
    }

    /// <summary>零句柄：在选路与任何抓帧之前就返回 null，两条线都不被触碰。</summary>
    [Fact]
    public void 零句柄返回null且两条线都不被触碰()
    {
        var wgc = new FakeLane(CaptureRoute.Wgc);
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = NewRouter();
        var factoryCalls = 0;

        IFrameCapture Factory()
        {
            factoryCalls++;
            return wgc;
        }

        var frame = FrameCapture.RouteAndCapture(0, true, true, false, Factory, gdi, router);

        Assert.Null(frame);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, wgc.CaptureCalls);
        Assert.Equal(0, gdi.CaptureCalls);
        Assert.Equal(0, router.ConsecutiveWgcFailures);
    }
}
