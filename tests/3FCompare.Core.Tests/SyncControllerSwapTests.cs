using _3FCompare.Core.Backend;
using _3FCompare.Core.Sync;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>SyncController.SwapSlots：交换两路的位置（连同偏移等全部槽位状态）。
///
/// <para>重点覆盖"交换涉及 master（第 0 路）"时的<b>偏移重基准</b>——这是最容易错的场景。
/// 全部期望值按 <c>偏移 = 该路媒体位置 − 规范时间（新 master 的媒体位置）</c> 独立推导，
/// 未照抄实现，用于交叉验证实现的正确性。</para>
///
/// <para>语义前提（已核实）：各路偏移是<b>相对 master</b> 的（<c>SyncSlot.Offset100ns</c> 注释、
/// <c>OffsetOf</c> 对第 0 路强制取 0、漂移校正用 <c>masterPos + offset</c>）。</para>
/// </summary>
public class SyncControllerSwapTests
{
    private static SyncController CreateSync(int count)
    {
        var engine = new SimulatedEngine();
        var sync = new SyncController();
        for (var i = 0; i < count; i++)
        {
            var session = engine.CreateSession(new EngineSessionOptions { OutputWindow = 0, HardwareDecode = false });
            session.OpenAsync($"test{i}.mp4").GetAwaiter().GetResult();
            sync.AddSlot(session, $"test{i}.mp4");
        }
        return sync;
    }

    private static string[] Paths(SyncController sync) => sync.Slots.Select(s => s.Path).ToArray();
    private static long[] Fields(SyncController sync) => sync.Slots.Select(s => s.Offset100ns).ToArray();

    /// <summary>各路的<b>语义偏移</b>：master（第 0 路）恒为 0，其余取字段值。
    /// 物理媒体位置 = 规范时间 + 语义偏移，因此两路语义偏移之差即二者的物理对齐差。</summary>
    private static Dictionary<string, long> SemanticOffsets(SyncController sync)
    {
        var result = new Dictionary<string, long>();
        var slots = sync.Slots;
        for (var i = 0; i < slots.Count; i++)
            result[slots[i].Path] = i == 0 ? 0 : slots[i].Offset100ns;
        return result;
    }

    // ══════════ 基础：顺序与偏移随之交换 ══════════

    /// <summary>不涉及 master 的交换：两路整体对调（含 Session 引用与偏移），master 不受影响。</summary>
    [Fact]
    public void SwapSlots_不涉及master_顺序与偏移随之交换()
    {
        var sync = CreateSync(3);
        try
        {
            sync.Slots[1].Offset100ns = 1000;
            sync.Slots[2].Offset100ns = 3000;
            var session1 = sync.Slots[1].Session;
            var session2 = sync.Slots[2].Session;

            sync.SwapSlots(1, 2);

            Assert.Equal(new[] { "test0.mp4", "test2.mp4", "test1.mp4" }, Paths(sync));
            Assert.Same(session2, sync.Slots[1].Session);   // 槽位数据（会话）随位置一起搬移
            Assert.Same(session1, sync.Slots[2].Session);
            Assert.Equal(new long[] { 0, 3000, 1000 }, Fields(sync));
        }
        finally { sync.Clear(); }
    }

    // ══════════ master 偏移恒为 0 ══════════

    /// <summary>交换涉及 master 后，落到第 0 位的新 master 偏移必须为 0；
    /// 且语义上基准确实由新 master 承担——SeekTo(T) 后新 master 落在 T 本身，
    /// 而不是 T 加上某个残留偏移。</summary>
    [Fact]
    public void SwapSlots_涉及master_新master偏移恒为0()
    {
        var sync = CreateSync(3);
        try
        {
            sync.Slots[1].Offset100ns = 1000;
            sync.Slots[2].Offset100ns = 3000;

            sync.SwapSlots(0, 1);

            Assert.Equal(0, sync.Slots[0].Offset100ns);

            var t = 5 * TimeSpan.TicksPerSecond;
            sync.SeekTo(t);
            Assert.Equal(t, sync.Slots[0].Session.ReadSnapshot()!.Position100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>老会话可能在 master 字段里残留非 0 偏移（<c>OffsetOf</c> 一律忽略它）。
    /// 该路被换离第 0 位后字段重新获得语义，此时必须按"master 旧偏移 = 0"重算，
    /// 而不是拿残留值去减——否则该路会带着一个凭空多出来的偏移。</summary>
    [Fact]
    public void SwapSlots_master字段残留非0_不参与重基准()
    {
        var sync = CreateSync(2);
        try
        {
            sync.Slots[0].Offset100ns = 999_999;   // 模拟老会话存档写入的无效残留
            sync.Slots[1].Offset100ns = 1000;

            sync.SwapSlots(0, 1);

            // 推导：新 master = 原第 1 路（旧偏移 1000）⇒ 新规范 = 旧规范 + 1000。
            // 原 master 媒体位置 = 旧规范，新偏移 = 旧规范 − (旧规范 + 1000) = −1000。
            // 若误用残留值 999999，这里会得到 999999 − 1000 = 998999。
            Assert.Equal(0, sync.Slots[0].Offset100ns);
            Assert.Equal(-1000, sync.Slots[1].Offset100ns);
        }
        finally { sync.Clear(); }
    }

    // ══════════ 涉及 master 的重基准（重点，独立推导）══════════

    /// <summary>4 路、交换第 0 与第 2 路。期望值独立推导如下：
    ///
    /// 交换前：规范时间 T 锚在 s0；s0/s1/s2/s3 的旧偏移依次为 0/1000/3000/5000
    ///（s0 取语义 0），即媒体位置依次为 T / T+1000 / T+3000 / T+5000。
    /// 交换后 s2 落到第 0 位成为新 master ⇒ 新规范 T' = s2 的媒体位置 = T + 3000。
    /// 任一路新偏移 = 媒体位置 − T'：
    ///   s0 → T − (T+3000) = −3000
    ///   s1 → (T+1000) − (T+3000) = −2000
    ///   s2 → (T+3000) − (T+3000) = 0
    ///   s3 → (T+5000) − (T+3000) = +2000
    /// 次序为 s2 / s1 / s0 / s3。</summary>
    [Fact]
    public void SwapSlots_交换第0与第2路_偏移重基准正确()
    {
        var sync = CreateSync(4);
        try
        {
            sync.Slots[1].Offset100ns = 1000;
            sync.Slots[2].Offset100ns = 3000;
            sync.Slots[3].Offset100ns = 5000;

            sync.SwapSlots(0, 2);

            Assert.Equal(new[] { "test2.mp4", "test1.mp4", "test0.mp4", "test3.mp4" }, Paths(sync));
            Assert.Equal(new long[] { 0, -2000, -3000, 2000 }, Fields(sync));
        }
        finally { sync.Clear(); }
    }

    /// <summary>重基准后物理对齐必须原样保留：SeekTo(T) 时各路媒体位置 = T + 新偏移，
    /// 且彼此之差与交换前一致（只是 master 换成了谁）。</summary>
    [Fact]
    public void SwapSlots_涉及master_物理对齐不变()
    {
        var sync = CreateSync(4);
        try
        {
            sync.Slots[1].Offset100ns = 1000;
            sync.Slots[2].Offset100ns = 3000;
            sync.Slots[3].Offset100ns = 5000;
            var before = SemanticOffsets(sync);

            sync.SwapSlots(0, 2);

            var after = SemanticOffsets(sync);
            foreach (var a in before.Keys)
                foreach (var b in before.Keys)
                    Assert.Equal(before[a] - before[b], after[a] - after[b]);

            var t = 5 * TimeSpan.TicksPerSecond;
            sync.SeekTo(t);
            foreach (var slot in sync.Slots)
                Assert.Equal(t + after[slot.Path], slot.Session.ReadSnapshot()!.Position100ns);
        }
        finally { sync.Clear(); }
    }

    /// <summary>A-B 循环区间是规范时间轴上的坐标：基准平移后区间必须同步平移，
    /// 否则回绕会跳到另一段内容（与 RemoveSlotAt 移除 master 时的处理一致）。</summary>
    [Fact]
    public void SwapSlots_涉及master_循环区间同步平移()
    {
        var sync = CreateSync(3);
        try
        {
            sync.LoopStart100ns = 2 * TimeSpan.TicksPerSecond;
            sync.LoopEnd100ns = 6 * TimeSpan.TicksPerSecond;
            sync.Slots[1].Offset100ns = 3000;   // 新 master 的旧偏移 = 平移量

            sync.SwapSlots(0, 1);

            Assert.Equal(2 * TimeSpan.TicksPerSecond + 3000, sync.LoopStart100ns);
            Assert.Equal(6 * TimeSpan.TicksPerSecond + 3000, sync.LoopEnd100ns);
        }
        finally { sync.Clear(); }
    }

    // ══════════ 边界与校验 ══════════

    /// <summary>同索引交换是 no-op：状态分毫不动，也不应触发 StateChanged。</summary>
    [Fact]
    public void SwapSlots_同索引_noop且不通知()
    {
        var sync = CreateSync(3);
        try
        {
            sync.Slots[1].Offset100ns = 1000;
            sync.Slots[2].Offset100ns = 3000;
            var pathsBefore = Paths(sync);
            var fieldsBefore = Fields(sync);
            var notifications = 0;
            sync.StateChanged += (_, _) => notifications++;

            sync.SwapSlots(1, 1);
            sync.SwapSlots(0, 0);

            Assert.Equal(pathsBefore, Paths(sync));
            Assert.Equal(fieldsBefore, Fields(sync));
            Assert.Equal(0, notifications);
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void SwapSlots_索引越界_抛ArgumentOutOfRangeException()
    {
        var sync = CreateSync(2);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => sync.SwapSlots(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => sync.SwapSlots(0, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => sync.SwapSlots(0, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => sync.SwapSlots(2, 0));

            // 越界抛异常后不得改动任何状态
            Assert.Equal(new[] { "test0.mp4", "test1.mp4" }, Paths(sync));
        }
        finally { sync.Clear(); }
    }

    [Fact]
    public void SwapSlots_空列表_抛ArgumentOutOfRangeException()
    {
        var sync = CreateSync(0);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => sync.SwapSlots(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => sync.SwapSlots(0, 0));
        }
        finally { sync.Clear(); }
    }

    /// <summary>单路：与自己交换是 no-op，与不存在的索引交换抛异常。</summary>
    [Fact]
    public void SwapSlots_单路_同索引noop越界抛异常()
    {
        var sync = CreateSync(1);
        try
        {
            sync.SwapSlots(0, 0);
            Assert.Single(sync.Slots);
            Assert.Equal("test0.mp4", sync.Slots[0].Path);
            Assert.Throws<ArgumentOutOfRangeException>(() => sync.SwapSlots(0, 1));
        }
        finally { sync.Clear(); }
    }

    // ══════════ 可逆性与多路多次交换的一致性 ══════════

    /// <summary>同一对交换两次必须完全还原（交换是自身的逆运算）；再换一次则回到"已交换"态。</summary>
    [Fact]
    public void SwapSlots_三次交换_状态按奇偶还原()
    {
        var sync = CreateSync(4);
        try
        {
            sync.Slots[1].Offset100ns = 1000;
            sync.Slots[2].Offset100ns = 3000;
            sync.Slots[3].Offset100ns = 5000;
            sync.LoopStart100ns = 1 * TimeSpan.TicksPerSecond;
            sync.LoopEnd100ns = 4 * TimeSpan.TicksPerSecond;
            var originalPaths = Paths(sync);
            var originalFields = Fields(sync);
            var originalLoop = (sync.LoopStart100ns, sync.LoopEnd100ns);

            sync.SwapSlots(0, 2);
            var afterFirstPaths = Paths(sync);
            var afterFirstFields = Fields(sync);

            sync.SwapSlots(0, 2);   // 第二次：完全还原
            Assert.Equal(originalPaths, Paths(sync));
            Assert.Equal(originalFields, Fields(sync));
            Assert.Equal(originalLoop, (sync.LoopStart100ns, sync.LoopEnd100ns));

            sync.SwapSlots(0, 2);   // 第三次：与第一次结果相同
            Assert.Equal(afterFirstPaths, Paths(sync));
            Assert.Equal(afterFirstFields, Fields(sync));
        }
        finally { sync.Clear(); }
    }

    /// <summary>多路（4 路）连续多次交换（含涉及 master 与不涉及 master 两种）后，
    /// master 偏移始终为 0，且任意两路的物理对齐差保持不变——与 RemoveSlotAt 的
    /// "偏移整体重基准"场景同源的健全性约束。</summary>
    [Fact]
    public void SwapSlots_多路多次交换_对齐差不变且master偏移恒0()
    {
        var sync = CreateSync(4);
        try
        {
            sync.Slots[1].Offset100ns = 1000;
            sync.Slots[2].Offset100ns = 3000;
            sync.Slots[3].Offset100ns = 5000;
            var baseline = SemanticOffsets(sync);

            var moves = new[] { (0, 1), (2, 3), (0, 3), (1, 2), (0, 2) };
            foreach (var (a, b) in moves)
            {
                sync.SwapSlots(a, b);

                Assert.Equal(0, sync.Slots[0].Offset100ns);

                var current = SemanticOffsets(sync);
                foreach (var x in baseline.Keys)
                    foreach (var y in baseline.Keys)
                        Assert.Equal(baseline[x] - baseline[y], current[x] - current[y]);
            }
        }
        finally { sync.Clear(); }
    }
}
