using _3FCompare.Diagnostics;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// #24 高频埋点的落盘节流（<see cref="ComponentLog.LogThrottled"/> / <see cref="ComponentLog.ShouldFlushNow"/>）。
///
/// <para><b>缺陷形态</b>：<c>PlayerSurface</c> 的 WM_SIZE <c>Resize</c> 埋点走的是
/// <see cref="ComponentLog.Log(string,string,int,string?)"/> —— 该方法<b>逐条同步 flush 落盘</b>，
/// 而 WM_SIZE 在 UI 线程上处理、且会被"拖动窗口边框"连续触发 ⇒ 磁盘卡顿/杀软扫描时冻结 UI。</para>
///
/// <para><b>本文件钉住什么</b>：① 节流判定的边界（纯函数）；② 连续高频埋点**不落盘**、
/// 普通埋点**每条都落盘**（用 <see cref="ComponentLog.FlushCount"/> /
/// <see cref="ComponentLog.ThrottledSkipCount"/> 两个计数钩子，确定性、不依赖计时）。
/// 反向验证：把 <c>LogThrottled</c> 改回"无条件 flush"⇒ 跳过计数恒为 0 ⇒ 判红。</para>
///
/// <para><b>覆盖边界（诚实说明）</b>：本文件证明的是"节流机制本身成立"，即
/// "调用 <c>LogThrottled</c> 不会逐条落盘"。<b>调用点</b>（<c>PlayerSurface</c> 的 WM_SIZE 分支
/// 确实用了它）需要真实子 HWND 才能驱动，只能在 <c>--selftest</c> 的
/// "Resize 埋点不阻塞 UI 线程"一步覆盖（那里连续改窗口尺寸并断言跳过计数增长）。
/// 两处合起来才完整：机制靠本文件，接线靠实机自测。</para>
///
/// <para><b>副作用说明</b>：本文件会调用 <see cref="ComponentLog.Initialize"/>，
/// 于是在测试输出目录下生成 <c>logs/component-*.log</c>（与生产同一套代码路径，
/// 是"真的能落盘"这一前提所必需的）。测试进程内只有本类触碰该静态类。</para>
/// </summary>
public class ComponentLogThrottleTests
{
    // ══════════ ① 节流判定（纯函数） ══════════

    [Fact]
    public void 从未落盘过_必须放行()
    {
        // 否则进程刚起来若第一条埋点就是高频埋点，它会一直等到下一次普通埋点才落盘。
        Assert.True(ComponentLog.ShouldFlushNow(1_000_000, -1, ComponentLog.FlushThrottleMs));
    }

    [Fact]
    public void 距上次落盘不足窗口_抑制()
    {
        Assert.False(ComponentLog.ShouldFlushNow(1_000_000, 999_999, ComponentLog.FlushThrottleMs));
        Assert.False(ComponentLog.ShouldFlushNow(1_000_000, 1_000_000 - (ComponentLog.FlushThrottleMs - 1),
            ComponentLog.FlushThrottleMs));
    }

    [Fact]
    public void 恰好达到窗口_放行()
    {
        Assert.True(ComponentLog.ShouldFlushNow(1_000_000, 1_000_000 - ComponentLog.FlushThrottleMs,
            ComponentLog.FlushThrottleMs));
    }

    [Fact]
    public void 节流窗口必须短于心跳周期()
    {
        // 这是"取证时间分辨率不降级"的依据：心跳每条都 flush（1s 一条），
        // 只要节流窗口 ≤ 心跳周期，被节流的行最多晚一个心跳落盘，
        // 日志对"崩前各组件在做什么"的 1s 分辨率不受影响。
        Assert.True(ComponentLog.FlushThrottleMs <= ComponentLog.HeartbeatIntervalMs,
            $"FlushThrottleMs={ComponentLog.FlushThrottleMs} 必须 ≤ HeartbeatIntervalMs={ComponentLog.HeartbeatIntervalMs}");
    }

    // ══════════ ② 落盘行为（计数钩子） ══════════

    /// <summary>初始化组件日志；失败（磁盘只读 / 被 FFF_NO_COMPONENT_LOG 关闭）时**明确判红**，
    /// 而不是静默通过——否则本文件会变成"什么都没测"的假绿。</summary>
    private static void EnsureLogReady()
    {
        ComponentLog.Initialize();
        Assert.True(ComponentLog.IsEnabled,
            "组件日志未就绪（无法创建 logs 目录，或被环境变量 FFF_NO_COMPONENT_LOG=1 关闭），" +
            "本用例无法判别节流行为");
    }

    [Fact]
    public void 高频埋点连续调用_不逐条落盘()
    {
        EnsureLogReady();
        // 先做一次普通埋点，把"上次落盘时刻"钉在当下 ⇒ 接下来的高频埋点全部落在节流窗口内。
        ComponentLog.Log(Comp.Env, "ThrottleWarmup");
        var skips = ComponentLog.ThrottledSkipCount;

        for (var i = 0; i < 5; i++)
            ComponentLog.LogThrottled(Comp.Surface, "Resize", 0, $"w={100 + i} h=100");

        // 计数只由 LogThrottled 递增（生产上只有 PlayerSurface 的 WM_SIZE 调它，测试进程里只有本文件），
        // 因此这个差值不受并行测试类影响，可以断言精确值。
        Assert.Equal(5, ComponentLog.ThrottledSkipCount - skips);
    }

    [Fact]
    public void 普通埋点_每条都落盘()
    {
        EnsureLogReady();
        var flushes = ComponentLog.FlushCount;

        for (var i = 0; i < 3; i++)
            ComponentLog.Log(Comp.Env, "FlushTest", 0, $"i={i}");

        // 逐条 flush 是"硬崩不丢最后一条"的保证，不许被本次改动削弱。
        // 用 >= 而不是 ==：同一个测试进程里别的测试类（如 FrameCaptureTests）也在写组件日志，
        // 会并发落盘 —— 精确值会假红。`>= 3` 仍能抓住"Log 不再逐条落盘"这一回归。
        Assert.True(ComponentLog.FlushCount - flushes >= 3,
            $"3 条普通埋点至少应落盘 3 次，实际 {ComponentLog.FlushCount - flushes} 次");
    }

    [Fact]
    public void 高频埋点_内容已写入缓冲_但未落到文件()
    {
        // 这是"节流不等于丢埋点"的字节级证据：行已经在缓冲里（顺序、内容都在），
        // 只是还没推进 OS 页缓存；下一次普通埋点（真实路径上就是 1s 一条的心跳）
        // 必然把它一起带走。仅凭计数钩子无法区分"没写"与"写了没落盘"，故补这一条。
        EnsureLogReady();
        ComponentLog.Log(Comp.Env, "BufferWarmup");
        var marker = $"w=marker{Environment.ProcessId} h=1";

        var flushesBefore = ComponentLog.FlushCount;
        var skipsBefore = ComponentLog.ThrottledSkipCount;
        ComponentLog.LogThrottled(Comp.Surface, "Resize", 0, marker);
        var flushesAfter = ComponentLog.FlushCount;

        // 节流必须生效（计数只由 LogThrottled 递增，不受并行测试类影响）。
        Assert.Equal(1, ComponentLog.ThrottledSkipCount - skipsBefore);

        // 字节级检查：同一进程里别的测试类（如 FrameCaptureTests）也在写组件日志并落盘，
        // 它们的 flush 会把同一个缓冲里的 marker 一并推下去 —— 那会让"文件里还没有 marker"
        // 不再成立（**竞态**：必须把读文件前后都夹在计数不变的条件里）。
        // 因此：读文件之前与之后各看一次落盘计数，两次都没变才说明"这段时间内没有任何落盘"，
        // 结论才可信。否则只跳过字节级检查，不做"因为没法判别就判绿"的假通过
        // —— 节流机制本身已由上面的计数断言钉住。
        if (flushesAfter == flushesBefore)
        {
            var content = ReadLogFile();
            if (ComponentLog.FlushCount == flushesBefore)
                Assert.DoesNotContain(marker, content);
        }

        // 无论上面是否被干扰，marker 都必须最终落盘（节流只推迟、不丢内容）。
        ComponentLog.Log(Comp.Env, "BufferDrain");
        Assert.Contains(marker, ReadLogFile());
    }

    /// <summary>读日志文件全文。必须以 <c>FileShare.ReadWrite</c> 打开：写侧持有写句柄
    /// （<c>FileShare.ReadWrite</c>），读侧若用默认的 <c>FileShare.Read</c> 会被拒。</summary>
    private static string ReadLogFile()
    {
        var path = ComponentLog.CurrentFile
            ?? throw new InvalidOperationException("组件日志未初始化（CurrentFile 为 null）");
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }
}
