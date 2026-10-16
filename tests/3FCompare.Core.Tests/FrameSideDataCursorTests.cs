using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 帧边数据环的消费游标（<see cref="FrameSideDataCursor"/>）。
///
/// <para><b>替身是"环模型"不是"录像带"</b>：测试用的 <see cref="FakeRing"/> 按内核
/// <c>PlayerSession::CopyFrameSideDataAfter</c> 的契约实现——空缓冲只答尺寸不写数据、
/// 容量不足报所需字节数且<b>不消费条目</b>、没匹配答 NotFound。
/// 这样游标的每一条前进/重试/不复位都能被模型裁决，而不是让测试自己念一遍脚本。</para>
/// </summary>
public class FrameSideDataCursorTests
{
    /// <summary>内核语义的内存模型：帧号 → 边数据字节。</summary>
    private sealed class FakeRing : IFrameSideDataRing
    {
        private readonly SortedList<long, byte[]> _entries = new();

        /// <summary>每次调用记录 (fromFrameIndex, 是否空缓冲探路)。</summary>
        public List<(long From, bool Probe)> Calls { get; } = new();

        /// <summary>&gt;0 时：下一次"取字节"那一趟先报容量不足（报这个尺寸），之后正常服务。
        /// 用来打 BufferTooSmall 重试分支——真实环里它只在两趟之间条目变大时出现。</summary>
        public uint GrowAndRetryOnce { get; set; }

        /// <summary>非 0 时：探路趟报出这个帧号之后把它从环里删掉，
        /// 于是第二趟（取字节）答 NotFound —— 对应"条目在两趟之间被 120 帧窗口挤掉"。</summary>
        public long DropAfterProbe { get; set; }

        public int TakeCalls { get; private set; }

        public void Add(long frameIndex, int size) =>
            _entries[frameIndex] = Enumerable.Range(0, size)
                .Select(i => (byte)((i + frameIndex) % 251))   // 内容随帧号变，便于判"缓冲被下一条覆盖"
                .ToArray();

        public void Remove(long frameIndex) => _entries.Remove(frameIndex);

        public SideDataReadOutcome CopyFrameSideDataAfter(uint sideDataType, long fromFrameIndex,
            byte[]? buffer, out uint requiredSize, out long foundFrameIndex)
        {
            var probe = buffer is null or { Length: 0 };
            Calls.Add((fromFrameIndex, probe));
            requiredSize = 0;
            foundFrameIndex = -1;

            // "帧号 ≥ from 且带该类型"的最小帧（与内核同一条件）。
            foreach (var pair in _entries)
            {
                if (pair.Key < fromFrameIndex) continue;
                foundFrameIndex = pair.Key;
                requiredSize = (uint)pair.Value.Length;
                break;
            }
            if (foundFrameIndex < 0) return SideDataReadOutcome.NotFound;
            if (probe)
            {
                if (DropAfterProbe == foundFrameIndex) _entries.Remove(foundFrameIndex);
                return SideDataReadOutcome.Found;   // 两趟式：空缓冲只报尺寸
            }

            TakeCalls++;
            if (GrowAndRetryOnce > 0)
            {
                var report = GrowAndRetryOnce;
                GrowAndRetryOnce = 0;
                requiredSize = report;
                return SideDataReadOutcome.BufferTooSmall;   // 条目未被消费
            }
            if (buffer!.Length < requiredSize) return SideDataReadOutcome.BufferTooSmall;
            _entries[foundFrameIndex].CopyTo(buffer);
            return SideDataReadOutcome.Found;
        }
    }

    [Fact]
    public void 两趟式取条目_游标只前进()
    {
        var ring = new FakeRing();
        ring.Add(5, 64);
        ring.Add(10, 32);
        var cursor = new FrameSideDataCursor(ring);

        Assert.True(cursor.TryReadNext(24, out var frame, out var bytes));
        Assert.Equal(5L, frame);
        Assert.Equal(64, bytes.Count);
        Assert.Equal(6, cursor.FromFrameIndex);           // found + 1，不是原值
        Assert.Equal(new[] { (0L, true), (5L, false) }, ring.Calls);  // 先探后取，取用的是探到的帧号

        ring.Calls.Clear();
        Assert.True(cursor.TryReadNext(24, out frame, out bytes));
        Assert.Equal(10L, frame);
        Assert.Equal(32, bytes.Count);
        Assert.Equal(11, cursor.FromFrameIndex);
        Assert.Equal(new[] { (6L, true), (10L, false) }, ring.Calls);

        // 环里没有 ≥11 的条目：正常回答，游标不许倒退也不许前跳。
        Assert.False(cursor.TryReadNext(24, out _, out _));
        Assert.Equal(11, cursor.FromFrameIndex);
    }

    [Fact]
    public void 环空是正常回答_不动游标不报错()
    {
        var ring = new FakeRing();
        var cursor = new FrameSideDataCursor(ring);

        Assert.False(cursor.TryReadNext(24, out var frame, out var bytes));
        Assert.Equal(-1L, frame);
        Assert.Equal(0, bytes.Count);
        Assert.Equal(0L, cursor.FromFrameIndex);
        // 只问了一次尺寸就收手：没找到条目时不该再发第二趟。
        Assert.Single(ring.Calls);
        Assert.True(ring.Calls[0].Probe);
    }

    [Fact]
    public void 容量不足按报告尺寸重试且拿到同一条()
    {
        var ring = new FakeRing();
        ring.Add(3, 4096);
        ring.GrowAndRetryOnce = 8192;    // 第一趟取字节报"要 8192"
        var cursor = new FrameSideDataCursor(ring);

        Assert.True(cursor.TryReadNext(24, out var frame, out var bytes));
        Assert.Equal(3L, frame);
        Assert.Equal(4096, bytes.Count);          // 实际字节数以内核最后一次报的为准
        Assert.Equal(2, ring.TakeCalls);          // 重试了一次
        Assert.True(cursor.BufferCapacity >= 8192,
            $"重试没有按报告尺寸放大缓冲（当前 {cursor.BufferCapacity}）");
        Assert.Equal(4L, cursor.FromFrameIndex);  // 只有真取到字节才推进
    }

    [Fact]
    public void 取字节那一趟条目被挤出环_游标原地不动()
    {
        var ring = new FakeRing();
        ring.Add(5, 64);
        ring.DropAfterProbe = 5;   // 探到了，取之前被 120 帧窗口挤掉（内核淘汰/flush 都会这样）
        var cursor = new FrameSideDataCursor(ring);

        Assert.False(cursor.TryReadNext(24, out _, out _));
        Assert.Equal(2, ring.Calls.Count);          // 确实走到了第二趟，不是探路就没找到
        Assert.Equal(0L, cursor.FromFrameIndex);    // 没取到就不许推进，否则那一段条目永远漏读
    }

    [Fact]
    public void 环被清空时游标必须复位()
    {
        // 契约见 FrameSideDataCursor 的类型注释：seek/flush 后 presentation index 重来，
        // 旧游标会停在一段更小的帧号区间之上 ⇒ 每一趟都答 NotFound，通路静默停摆。
        var ring = new FakeRing();
        ring.Add(500, 16);
        var cursor = new FrameSideDataCursor(ring);
        Assert.True(cursor.TryReadNext(24, out _, out _));
        Assert.Equal(501L, cursor.FromFrameIndex);

        cursor.Reset();
        Assert.Equal(0L, cursor.FromFrameIndex);

        // 复位后同一条目（新时间线上的 500 帧）可以再取一次。
        Assert.True(cursor.TryReadNext(24, out var frame, out _));
        Assert.Equal(500L, frame);
    }

    [Fact]
    public void 返回码翻译NotFound与失败分得开()
    {
        // NotFound(-8) 在环上是正常回答；把它当故障会让泵每帧留一条错误痕，
        // 反过来把 BufferTooSmall 当失败会丢掉"条目未消费可重试"这个信息。
        Assert.Equal(SideDataReadOutcome.Found,
            Fff3FpEngine.Fff3FpSession.MapSideDataOutcome(FffResult.Success));
        Assert.Equal(SideDataReadOutcome.NotFound,
            Fff3FpEngine.Fff3FpSession.MapSideDataOutcome(FffResult.NotFound));
        Assert.Equal(SideDataReadOutcome.BufferTooSmall,
            Fff3FpEngine.Fff3FpSession.MapSideDataOutcome(FffResult.BufferTooSmall));
        Assert.Equal(SideDataReadOutcome.Failed,
            Fff3FpEngine.Fff3FpSession.MapSideDataOutcome(FffResult.NativeFailure));
        Assert.Equal(SideDataReadOutcome.Failed,
            Fff3FpEngine.Fff3FpSession.MapSideDataOutcome(FffResult.InvalidArgument));
        Assert.Equal(SideDataReadOutcome.Failed,
            Fff3FpEngine.Fff3FpSession.MapSideDataOutcome(FffResult.NotSupported));
    }

    [Fact]
    public void 复用缓冲在下一趟之前有效()
    {
        var ring = new FakeRing();
        ring.Add(1, 8);
        ring.Add(2, 8);
        var cursor = new FrameSideDataCursor(ring);

        Assert.True(cursor.TryReadNext(24, out _, out var first));
        var snapshot = first.ToArray();     // 要留下就得当场拷走
        Assert.True(cursor.TryReadNext(24, out _, out var second));
        Assert.NotEqual(snapshot, second.ToArray());   // 同一块缓冲被下一条覆盖
    }

    [Fact]
    public void 空条目不送进模块()
    {
        var ring = new FakeRing();
        ring.Add(1, 0);   // 长度 0 的条目：内核侧不会缓存（0 字节没意义），这里只判游标的自守
        var cursor = new FrameSideDataCursor(ring);

        // required==0 ⇒ 直接答"这一趟没有"，不发第二趟、不推进游标。
        Assert.False(cursor.TryReadNext(24, out _, out _));
        Assert.Equal(0L, cursor.FromFrameIndex);
        Assert.Single(ring.Calls);
    }

    [Fact]
    public void 游标的环接缝不能为空()
    {
        Assert.Throws<ArgumentNullException>(() => new FrameSideDataCursor(null!));
    }
}
