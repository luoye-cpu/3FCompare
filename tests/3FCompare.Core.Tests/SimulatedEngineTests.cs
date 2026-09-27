using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 演示模式引擎（<see cref="SimulatedEngine"/>）自身的契约（docs/41 §4.5 第 9 项）。
///
/// <para><b>⚠ 别拿真实内核的预期来套它</b>：<c>SimulatedEngine</c> 是**产品代码**里的
/// 演示实现，三条行为与真实内核**相反**（到片尾会回绕、永不进入 Ended、Seek 内部
/// clamp）。本组测的正是它自己的契约——它是"无后端环境下 UI 全流程"的唯一承载者，
/// 契约错了会让所有无内核的演示/回归跑偏。</para>
///
/// <para><b>期望值来源</b>：时长为 10 秒，写成字面量 <c>100_000_000</c>
/// （10 × 10,000,000 = 1 秒的 100ns 刻度数），不调用被测实现取常量。</para>
///
/// <para><b>无全局状态</b>：<c>SimSession</c> 除一个"色相自增"计数器外无共享状态，
/// 故本类不需要集合隔离。</para>
/// </summary>
public class SimulatedEngineTests
{
    /// <summary>演示模式时长：10 秒（写死常量，独立于实现推算）。</summary>
    private const long 时长100ns = 100_000_000L;

    /// <summary>新建并**已打开**的会话（Seek/ReadSnapshot 都要求 _opened）。</summary>
    private static IPlayerSession 已打开会话()
    {
        var session = new SimulatedEngine().CreateSession(new EngineSessionOptions());
        // OpenAsync 在本实现里同步完成（返回 Task.CompletedTask），取结果不会阻塞
        session.OpenAsync("simulated.mp4").GetAwaiter().GetResult();
        return session;
    }

    [Fact]
    public void Seek_负值_钳到0()
    {
        using var session = 已打开会话();

        session.Seek(-1L);
        Assert.Equal(0L, session.ReadSnapshot().Position100ns);

        session.Seek(-时长100ns);   // 整段时长量级的负值
        Assert.Equal(0L, session.ReadSnapshot().Position100ns);
    }

    [Fact]
    public void Seek_超过时长_钳到时长()
    {
        using var session = 已打开会话();

        session.Seek(long.MaxValue);
        Assert.Equal(时长100ns, session.ReadSnapshot().Position100ns);

        // 刚好越界 1 tick 也必须落在时长上（差一错误的高发点）
        session.Seek(时长100ns + 1);
        Assert.Equal(时长100ns, session.ReadSnapshot().Position100ns);

        // 边界内则原样保留（证明上面不是"恒等于时长"）
        session.Seek(时长100ns / 2);
        Assert.Equal(时长100ns / 2, session.ReadSnapshot().Position100ns);
    }

    /// <summary>未 Open 时 <c>ReadMediaInfo</c> 返回 null，且状态为 Idle。
    /// 打开之后必须变成"有媒体信息"——后半段是防"恒 null 也能过"的对照。</summary>
    [Fact]
    public async Task 未打开时_ReadMediaInfo为null且状态为Idle()
    {
        using var session = new SimulatedEngine().CreateSession(new EngineSessionOptions());

        Assert.Null(session.ReadMediaInfo());
        var snap = session.ReadSnapshot();
        Assert.Equal(PlayerState.Idle, snap.State);
        Assert.Equal(0L, snap.Position100ns);

        // 对照：打开后同一断言必须翻转，否则上面两条可能是恒真
        await session.OpenAsync("simulated.mp4");
        var info = session.ReadMediaInfo();
        Assert.NotNull(info);
        Assert.Equal("simulated.mp4", info!.Path);
        Assert.NotEqual(PlayerState.Idle, session.ReadSnapshot().State);
    }

    /// <summary>未 Open 时 Seek 是 no-op：不得凭空产生位置
    /// （否则 UI 在"刚建会话还没打开"的窗口期会把从路 Seek 到一个假位置）。</summary>
    [Fact]
    public void 未打开时_Seek不生效()
    {
        using var session = new SimulatedEngine().CreateSession(new EngineSessionOptions());

        session.Seek(50_000_000L);   // 5 秒

        Assert.Equal(0L, session.ReadSnapshot().Position100ns);
    }

    [Fact]
    public void Dispose后_调用抛ObjectDisposedException()
    {
        var session = 已打开会话();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Seek(0L));
        Assert.Throws<ObjectDisposedException>(() => session.ReadSnapshot());
        Assert.Throws<ObjectDisposedException>(() => session.ReadMediaInfo());
        Assert.Throws<ObjectDisposedException>(() => session.Play());

        // 并发守卫：重复 Dispose 不得抛（Interlocked 闸门），否则 UI 关闭路径会炸
        session.Dispose();
    }
}
