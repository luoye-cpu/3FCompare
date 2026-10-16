namespace _3FCompare.Core.Backend;

/// <summary>帧边数据环的读取结果（内核 <c>FFF3FP_CopyFrameSideDataAfter</c> 返回码的宿主语义）。
/// <para>把 <c>FffResult</c> 翻译成一格公开语义有两层理由：<c>NotFound</c> 在环上是
/// <b>正常回答</b>（"这一趟没有可取的条目"），和别的失败码必须分得开；而内部结果码枚举
/// 不宜出现在宿主可消费的公共面上。</para></summary>
public enum SideDataReadOutcome
{
    /// <summary>找到了匹配条目：<paramref name="requiredSize"/> 报的是该条目的字节数
    /// （缓冲为空时是"需要多少"，非空时是"写了多少"）。</summary>
    Found,
    /// <summary>环里没有"帧号 ≥ fromFrameIndex 且带该类型"的条目 —— 正常回答，不是故障。</summary>
    NotFound,
    /// <summary>缓冲容量不足：条目<b>未被消费</b>，按报出的尺寸换大缓冲可以原地重试。</summary>
    BufferTooSmall,
    /// <summary>真失败（句柄已释放、原生内部错误、旧盘上 DLL 没有这个导出）。</summary>
    Failed,
}

/// <summary>中性帧边数据环的宿主可见接缝（由 <see cref="Fff3FpEngine.Fff3FpSession"/> 实现）。
/// <para>没有把这个方法挂进 <see cref="IPlayerSession"/>：那个接口有多个实现
/// （含演示模式与测试替身），追加必需成员会让所有实现一起改线；本接口单独存在，
/// 调用方按 <c>session is IFrameSideDataRing</c> 探测，探测不到就按"内核不支持"降级。</para></summary>
public interface IFrameSideDataRing
{
    /// <summary>取"帧号 ≥ <paramref name="fromFrameIndex"/> 且带该类型边数据"的最小帧的原始字节。
    /// <para>两趟式契约：<paramref name="buffer"/> 传 null 只问尺寸（此时内核不写任何东西）；
    /// 传非空则把条目拷进缓冲。出参 <paramref name="requiredSize"/> 在两条路上都是该条目的字节数，
    /// <paramref name="foundFrameIndex"/> 是它的 presentation 帧号（未找到为 -1）。</para>
    /// <para>环上界由内核定：120 帧 / 1 MiB / 单块 16 KiB，超界条目根本不进环。</para></summary>
    SideDataReadOutcome CopyFrameSideDataAfter(uint sideDataType, long fromFrameIndex,
        byte[]? buffer, out uint requiredSize, out long foundFrameIndex);
}

/// <summary>帧边数据环的消费游标：把两趟式契约（探尺寸 → 取字节）摊成"给我下一条"。
/// <para><b>游标只前进</b>：<see cref="TryReadNext"/> 只在真取到字节后才把
/// <see cref="FromFrameIndex"/> 推到 <c>foundFrameIndex + 1</c>；NotFound、BufferTooSmall、
/// 失败都不动游标（内核在 BufferTooSmall 时明确不消费条目，重试是同一条）。</para>
/// <para><b>环被清空时游标必须复位</b>：内核在 seek、解码器重建、设备恢复、关闭这些
/// flush 点上把环整个丢掉，因为 presentation index 从那里重来——留着旧游标的话，
/// "帧号 ≥ 游标"这个条件会在一段新的、更小的帧号区间上恒不成立，表现成"环永远空"。
/// 调用契约：宿主在 <c>Seek</c>/<c>SeekFrame</c>/<c>Stop</c>/换文件之后调
/// <see cref="Reset"/>（<see cref="ColorTransformPump.OnStreamRestart"/> 就是这件事的封装）。</para>
/// <para>返回的 <see cref="ArraySegment{byte}"/> 指向内部复用缓冲，只在下一次
/// <see cref="TryReadNext"/> 之前有效——要留下就得当场拷走。</para>
/// </summary>
public sealed class FrameSideDataCursor
{
    private readonly IFrameSideDataRing _ring;
    private byte[]? _buffer;

    /// <summary>游标的真身：<b>高 32 位是复位世代，低 32 位是"帧号 ≥"下界</b>，
    /// 两者必须一起原子更新，所以打包在一个 long 里。
    /// <para>为什么不直接对帧号做 CAS：复位把下界写回 0，而探测时的下界本来可能就是 0
    /// （从头播、或刚复位过一次）——那时"值没变"与"被人复位过"在单值 CAS 上长得一模一样，
    /// 旧时间线的条目照样会被发出去并把游标顶回高帧号。世代计数器正是这两者的区分量。</para></summary>
    private long _state;

    /// <summary>低 32 位取回下界（presentation index 恒 &ge; 0 且远小于 2^31）。</summary>
    private static long FloorOf(long state) => (int)state;

    public FrameSideDataCursor(IFrameSideDataRing ring)
    {
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
    }

    /// <summary>下一次查询的"帧号 ≥"下界（含）。0 表示从头开始。
    /// <para>读它必须是原子的：宿主在 seek/Stop/换文件时从 UI 线程调 <see cref="Reset"/>，
    /// 而泵线程同时在读——两边都是普通字段读写时，CLR 不保证泵看得见复位。</para></summary>
    public long FromFrameIndex => FloorOf(Volatile.Read(ref _state));

    /// <summary>环当前复用缓冲的容量（字节），只按内核报过的最大尺寸增长。</summary>
    public int BufferCapacity => _buffer?.Length ?? 0;

    /// <summary>复位游标：见类型注释里的"环被清空时游标必须复位"契约。
    /// 只动游标、不动复用缓冲（尺寸复用没有语义）。
    /// <para>下界归零**并且**世代 +1：世代是"在飞的读取该不该作废"的判据。</para></summary>
    public void Reset()
    {
        long before, after;
        do
        {
            before = Volatile.Read(ref _state);
            after = ((before >> 32) + 1) << 32;
        }
        while (Interlocked.CompareExchange(ref _state, after, before) != before);
    }

    /// <summary>取下一条边数据。返回 false 表示这一趟没有可注入的新条目
    /// （含正常回答 NotFound），不表示故障。</summary>
    public bool TryReadNext(uint sideDataType, out long frameIndex, out ArraySegment<byte> bytes)
    {
        frameIndex = -1;
        bytes = default;

        // 一次取回整个状态：它既是这次探测用的下界，也是稍后前进时的合法性凭据。
        var state0 = Volatile.Read(ref _state);
        var probeFloor = FloorOf(state0);

        // 第一趟：buffer=null 只问尺寸。内核在这条路上对"找到了"返回成功并写 requiredSize，
        // 容量不足分支走不到（空缓冲时它先答尺寸），但语义上仍要按可能遇到处理。
        var probe = _ring.CopyFrameSideDataAfter(sideDataType, probeFloor, null,
            out var required, out var found);
        if (probe != SideDataReadOutcome.Found) return false;   // NotFound / Failed / 别的
        if (required == 0 || found < 0) return false;

        // 第二趟起可以重试：BufferTooSmall 时条目未被消费，按内核报出的尺寸换大缓冲再取一次。
        for (var attempt = 0; attempt < 2; attempt++)
        {
            EnsureCapacity(required);
            var take = _ring.CopyFrameSideDataAfter(sideDataType, found, _buffer,
                out var written, out var takenIndex);
            switch (take)
            {
                case SideDataReadOutcome.Found:
                    if (written == 0 || takenIndex < 0) return false;
                    // 游标只前进：found 是上一条已消费条目的下一格。
                    // 探测与取字节之间可能夹进一次 Reset()（seek/Stop/换文件都在 UI 线程上）。
                    // 世代+下界一起 CAS：对不上就说明这条条目属于已经作废的时间线 ⇒ 当场丢弃，
                    // 不把带旧帧号的表交给内核（那会让画面停在旧色调，而且毫无失败痕迹）。
                    var next = (state0 & unchecked((long)0xFFFF_FFFF_0000_0000UL))
                               | (uint)(takenIndex + 1);
                    if (Interlocked.CompareExchange(ref _state, next, state0) != state0)
                        return false;
                    frameIndex = takenIndex;
                    bytes = new ArraySegment<byte>(_buffer!, 0, (int)written);
                    return true;

                case SideDataReadOutcome.BufferTooSmall:
                    required = written;          // 按报告尺寸重试（跨趟之间条目理论上不变，
                    continue;                     // 这条分支是契约要求的兜底，不是常规路径）

                default:
                    return false;                // NotFound：条目在两次调用之间被挤出环
            }
        }
        return false;
    }

    private void EnsureCapacity(uint required)
    {
        if (_buffer is not null && _buffer.Length >= required) return;
        // 一次到位按报告尺寸分配；内核单块上限 16 KiB，所以这里不会长成大块。
        _buffer = new byte[Math.Max(required, 16 * 1024)];
    }
}
