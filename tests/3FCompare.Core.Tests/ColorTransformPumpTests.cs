using _3FCompare.Core.Backend;
using _3FCompare.Core.Tests.Infrastructure;

namespace _3FCompare.Core.Tests;

/// <summary>
/// "环 → 换算 → 注入 kind=3"这条编排（<see cref="ColorTransformPump"/>）。
///
/// <para>真模块只能从盘上的 <c>FFF.HDRdp.Test.dll</c> 装载出来，所以这里用泵的
/// 内部接缝把换算器换成脚本：被钉住的是编排本身——游标只前进、NoMetadata 不算故障、
/// <b>注入是整表替换所以伴报表必须一并重发</b>、内核整批拒绝时不记成功、
/// seek/flush 后游标复位。换算器自己那条真路径不在本仓能覆盖的范围（见报告）。</para>
/// </summary>
public class ColorTransformPumpTests
{
    private static FakeSession SessionWithRing(params long[] frameIndices)
    {
        var session = new FakeSession(new EngineSessionOptions { OutputWindow = nint.Zero });
        foreach (var index in frameIndices)
            session.SideDataRing[index] = Enumerable.Repeat((byte)7, 64).ToArray();
        return session;
    }

    /// <summary>只对指定帧号出表的换算器；其它帧答"NoMetadata"（false + error 留空）。
    /// 出表时用一张真恒等表，顺带把表→包的那一步也走通。</summary>
    private static ColorTransformPump.TryAnalyzeSideData AnalyzerForFrames(params long[] injected)
    {
        return (ReadOnlySpan<byte> sideData, long frameIndex,
                out ColorTransformTable? table, out string? error) =>
        {
            table = null;
            error = null;
            if (Array.IndexOf(injected, frameIndex) < 0) return false;
            if (sideData.Length == 0) { error = "空边数据不该进换算器"; return false; }
            table = ColorTransformTable.Identity();
            return true;
        };
    }

    [Fact]
    public void 泵按帧注入kind3且游标只前进()
    {
        var session = SessionWithRing(1, 2);
        var pump = new ColorTransformPump(session, AnalyzerForFrames(2),
            ColorTransformPump.SideDataTypeDoviMetadata);

        Assert.True(pump.IsSupported);

        // 第一帧：环里有、换算器答 NoMetadata ⇒ 跳过，不是故障。
        Assert.False(pump.PumpOnce(out var error1));
        Assert.Null(error1);
        Assert.Equal(1u, pump.SkippedFrames);
        Assert.Equal(0u, pump.InjectedFrames);
        Assert.Empty(session.DynamicMetadataPackets);
        Assert.Equal(2L, pump.CursorFromFrameIndex);   // 条目已消费，游标推到 found+1

        // 第二帧：拿到表 ⇒ 注入一条 kind=3，帧号就是环报的 presentation index。
        Assert.True(pump.PumpOnce(out var error2));
        Assert.Null(error2);
        Assert.Equal(3L, pump.CursorFromFrameIndex);
        Assert.Equal(2L, pump.LastInjectedFrameIndex);
        Assert.Equal(1u, pump.InjectedFrames);
        var packet = Assert.Single(session.DynamicMetadataPackets);
        Assert.Equal(DynamicMetadataKind.ExternalColorTransform, packet.Kind);
        Assert.Equal(2L, packet.FrameIndex);
        Assert.IsType<ColorTransformTable>(packet.Payload);

        // 环空了：正常回答，计数与游标都不动。
        Assert.False(pump.PumpOnce(out var error3));
        Assert.Null(error3);
        Assert.Equal(3L, pump.CursorFromFrameIndex);
        Assert.Equal(1u, pump.InjectedFrames);
    }

    [Fact]
    public void 注入是整表替换_伴报表必须一并重发()
    {
        // 内核 InstallInjectedTable 用这一批整体换掉现有表：只发 kind=3 那一条，
        // 宿主先前从 JSON 装载的 HDR10+/Vivid 表会被悄悄清空。
        var session = SessionWithRing(5);
        var companion = new[]
        {
            new DynamicMetadataPacket
            {
                Kind = DynamicMetadataKind.HdrVivid,
                FrameIndex = 0L,
                Payload = new HdrVividMetadataPayload
                {
                    Windows = new[] { (0.9f, 0.8f, 0.7f, 0.6f) },
                    TargetedDisplayNits = 1000f,
                },
            },
            new DynamicMetadataPacket
            {
                Kind = DynamicMetadataKind.Hdr10Plus,
                FrameIndex = 3L,
                Payload = new HdrDynamicMetadataEntry
                {
                    FrameIndex = 3,
                    Windows = Array.Empty<HdrDynamicMetadataWindow>(),
                    TargetedDisplayNits = 0f,
                },
            },
        };
        var pump = new ColorTransformPump(session, AnalyzerForFrames(5),
            ColorTransformPump.SideDataTypeDoviMetadata)
        {
            CompanionPackets = companion,
        };

        Assert.True(pump.PumpOnce(out _));
        Assert.Equal(3, session.DynamicMetadataPackets.Count);
        Assert.Equal(DynamicMetadataKind.HdrVivid, session.DynamicMetadataPackets[0].Kind);
        Assert.Equal(DynamicMetadataKind.Hdr10Plus, session.DynamicMetadataPackets[1].Kind);
        Assert.Equal(DynamicMetadataKind.ExternalColorTransform, session.DynamicMetadataPackets[2].Kind);
        Assert.Equal(5L, session.DynamicMetadataPackets[2].FrameIndex);
    }

    [Fact]
    public void 内核整批拒绝时不记注入成功()
    {
        var session = SessionWithRing(9);
        session.SetDynamicMetadataResult = false;   // 旧内核不支持 / 形状被拒，旧表续任
        var pump = new ColorTransformPump(session, AnalyzerForFrames(9),
            ColorTransformPump.SideDataTypeDoviMetadata);

        Assert.False(pump.PumpOnce(out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(0u, pump.InjectedFrames);
        Assert.Equal(1u, pump.SkippedFrames);
        Assert.Equal(-1L, pump.LastInjectedFrameIndex);
    }

    [Fact]
    public void seek后泵复位游标并叫换算器的reset()
    {
        var session = SessionWithRing(1, 2);
        var resetCalls = 0;
        var pump = new ColorTransformPump(session, AnalyzerForFrames(1, 2),
            ColorTransformPump.SideDataTypeHdr10Plus, () => resetCalls++);

        Assert.True(pump.PumpOnce(out _));
        Assert.Equal(2L, pump.CursorFromFrameIndex);
        Assert.Equal(1, session.SideDataCalls.Count(x => !x.Probe));

        pump.OnStreamRestart();
        Assert.Equal(0L, pump.CursorFromFrameIndex);   // presentation index 重来 ⇒ 游标归零
        Assert.Equal(1, resetCalls);                   // 模块的逐流状态也丢了
        Assert.Equal(-1L, pump.LastInjectedFrameIndex);

        // 复位后同一段环可以再走一遍（不清游标的表现是"通路静默停摆"，这里正好反证）。
        Assert.True(pump.PumpOnce(out _));
        Assert.Equal(2u, pump.InjectedFrames);
    }

    [Fact]
    public void 换算器报真失败时把原因透出来()
    {
        var session = SessionWithRing(1);
        ColorTransformPump.TryAnalyzeSideData broken =
            (ReadOnlySpan<byte> sideData, long frameIndex,
             out ColorTransformTable? table, out string? error) =>
            {
                table = null;
                error = "模块不支持这帧的元数据修订";
                return false;
            };
        var pump = new ColorTransformPump(session, broken, ColorTransformPump.SideDataTypeDoviMetadata);

        Assert.False(pump.PumpOnce(out var error));
        Assert.Equal("模块不支持这帧的元数据修订", error);
        Assert.Equal(1u, pump.SkippedFrames);
        Assert.Empty(session.DynamicMetadataPackets);
    }

    [Fact]
    public void 空环不消耗换算器()
    {
        var session = SessionWithRing();   // 默认空环
        var calls = 0;
        var pump = new ColorTransformPump(session,
            (ReadOnlySpan<byte> sideData, long frameIndex,
             out ColorTransformTable? table, out string? error) =>
            {
                calls++;
                table = ColorTransformTable.Identity();
                error = null;
                return true;
            },
            ColorTransformPump.SideDataTypeDoviMetadata);

        Assert.False(pump.PumpOnce(out var error));
        Assert.Null(error);
        Assert.Equal(0, calls);
        Assert.Equal(0L, pump.CursorFromFrameIndex);
    }

    [Fact]
    public void 模块缺席时泵不支持且不抛()
    {
        // 会话提供环（FakeSession 实现了 IFrameSideDataRing），但没有模块 ⇒ 没有换算器。
        // 游标建得起来、通路不走通：这正是"不装模块 = 今天的行为"的形状。
        var session = SessionWithRing(1);
        var pump = new ColorTransformPump(session, (ColorTransformModule?)null);
        Assert.False(pump.IsSupported);
        Assert.Equal(0L, pump.CursorFromFrameIndex);
        Assert.False(pump.PumpOnce(out var error));
        Assert.Null(error);
        Assert.Empty(session.DynamicMetadataPackets);
        Assert.Empty(session.SideDataCalls);   // 连一次环查询都没发出去
    }

    [Fact]
    public void 内容未变的连续条目不重发()
    {
        // 载荷 431296 字节：按帧重发等于每秒几十兆的大对象分配，而"内核里已经躺着
        // 内容相同的一条 ≤ 当前帧"时重发在语义上是恒等的。开 SkipUnchangedPayload 后
        // 应只见一次注入，且内核侧仍是那一条（不重发 ≠ 清掉）。
        var session = SessionWithRing(1, 2);
        var pump = new ColorTransformPump(session, AnalyzerForFrames(1, 2),
            ColorTransformPump.SideDataTypeDoviMetadata)
        { SkipUnchangedPayload = true };

        Assert.True(pump.PumpOnce(out _));
        Assert.Equal(1u, pump.InjectedFrames);
        Assert.Equal(0u, pump.UnchangedFrames);

        Assert.False(pump.PumpOnce(out _));           // 同一张恒等表 ⇒ 不重发
        Assert.Equal(1u, pump.InjectedFrames);
        Assert.Equal(1u, pump.UnchangedFrames);
        Assert.Equal(0u, pump.SkippedFrames);         // 不是"没换算出表"，是"不用重发"
        var packet = Assert.Single(session.DynamicMetadataPackets);
        Assert.Equal(1L, packet.FrameIndex);          // 内核留着的是第一帧那条
    }

    [Fact]
    public void 内容变了才重发()
    {
        var session = SessionWithRing(1, 2);
        // 第一帧恒等表、第二帧通道置换表 ⇒ 内容不同 ⇒ 两条都发。
        ColorTransformPump.TryAnalyzeSideData analyzer =
            (ReadOnlySpan<byte> sideData, long frameIndex,
             out ColorTransformTable? table, out string? error) =>
            {
                error = null;
                table = frameIndex == 1
                    ? ColorTransformTable.Identity()
                    : ColorTransformTable.ChannelPermutation(2, 1, 0);
                return true;
            };
        var pump = new ColorTransformPump(session, analyzer,
            ColorTransformPump.SideDataTypeDoviMetadata)
        { SkipUnchangedPayload = true };

        Assert.True(pump.PumpOnce(out _));
        Assert.True(pump.PumpOnce(out _));
        Assert.Equal(2u, pump.InjectedFrames);
        Assert.Equal(0u, pump.UnchangedFrames);
        Assert.Equal(2, session.DynamicMetadataPackets.Count);
    }

    [Fact]
    public void 重启后即使内容相同也必须重发一次()
    {
        // 注入是**整表替换**，内核按"最后一条 ≤ 当前帧"匹配。seek 之后 presentation index
        // 从 0 重来，而内核里留着的那条带的是旧帧号 ⇒ 落在"未来"、永远选不中 ⇒ 画面会掉回
        // 基底，直到下一次内容变化。故重启必须清掉"上次的表"，让第一帧强制重发。
        var session = SessionWithRing(1, 2);
        var pump = new ColorTransformPump(session, AnalyzerForFrames(1, 2),
            ColorTransformPump.SideDataTypeDoviMetadata)
        { SkipUnchangedPayload = true };

        Assert.True(pump.PumpOnce(out _));
        Assert.False(pump.PumpOnce(out _));       // 未变 ⇒ 不重发
        Assert.Equal(1u, pump.InjectedFrames);

        pump.OnStreamRestart();
        Assert.True(pump.PumpOnce(out _));        // 同内容也要重发
        Assert.Equal(2u, pump.InjectedFrames);
        Assert.Equal(2, session.DynamicMetadataPackets.Count);
    }

    [Fact]
    public void 边数据类型常量与内核实测值同水()
    {
        // 这几个数是 FFmpeg AVFrameSideDataType 的枚举值（本机对 bundled 头实测，
        // 见 Fff3FpNative 的注释与 3fp/tools/sidedata_size_probe.cpp）。
        // 传错数字不会报错：内核只按数值匹配，答"环里没有"，通路静默空转。
        Assert.Equal(17u, ColorTransformPump.SideDataTypeHdr10Plus);
        Assert.Equal(23u, ColorTransformPump.SideDataTypeDoviRpuBuffer);
        Assert.Equal(24u, ColorTransformPump.SideDataTypeDoviMetadata);
        Assert.Equal(25u, ColorTransformPump.SideDataTypeHdrVivid);
    }

    /// <summary>环替身：只在"取字节"那一趟之前把游标复位一次，用来打下面那一格。
    /// 它刻意**仍然照旧答复**——要钉的是游标层：哪怕内核慢一拍把旧条目还回来了，
    /// 游标也不许把带旧帧号的条目交给换算器与内核。</summary>
    private sealed class ResettingRing : IFrameSideDataRing
    {
        public const long HeldFrameIndex = 40;
        public FrameSideDataCursor? Cursor;
        public int Takes;

        public SideDataReadOutcome CopyFrameSideDataAfter(uint sideDataType, long fromFrameIndex,
            byte[]? buffer, out uint requiredSize, out long foundFrameIndex)
        {
            requiredSize = 64;
            foundFrameIndex = HeldFrameIndex;
            if (buffer is null or { Length: 0 }) return SideDataReadOutcome.Found;   // 探路那趟
            Takes++;
            Cursor!.Reset();                       // 复位正好插在探测与取字节之间
            for (var i = 0; i < 64; i++) buffer[i] = 7;
            return SideDataReadOutcome.Found;
        }
    }

    [Fact]
    public void 复位插在探测与取字节之间时旧时间线的条目不得交出()
    {
        // seek / Stop / 换文件都在 UI 线程上复位游标，而泵线程在同一毫秒里读环。
        // 没有"世代+下界"一起原子更新时，这一格是红的：读-改-写会把游标顶回 41，
        // 而 presentation index 已经从 0 重来 ⇒ 之后每一趟都答 NotFound，
        // 注入**静默停摆**（本文件顶部与 FrameSideDataCursor 注释都警告过这个形状）。
        var ring = new ResettingRing();
        var cursor = new FrameSideDataCursor(ring);
        ring.Cursor = cursor;

        Assert.False(cursor.TryReadNext(ColorTransformPump.SideDataTypeDoviMetadata,
            out var frameIndex, out var bytes));
        Assert.Equal(1, ring.Takes);                 // 确实走到了取字节那一趟
        Assert.Equal(-1L, frameIndex);               // 条目被当场丢弃
        Assert.Equal(0, bytes.Count);
        Assert.Equal(0L, cursor.FromFrameIndex);     // 复位没有被旧帧号顶回去
    }

    [Fact]
    public void 没有并发复位时游标照常只前进()
    {
        // 上面那格的对照臂：同一套替身但不插复位 ⇒ 必须真的交出条目并前进。
        // 少了这条对照，"返回 false"可能只是替身压根没配合成功（恒假的判据）。
        var ring = new PlainRing();
        var cursor = new FrameSideDataCursor(ring);

        Assert.True(cursor.TryReadNext(ColorTransformPump.SideDataTypeDoviMetadata,
            out var frameIndex, out var bytes));
        Assert.Equal(ResettingRing.HeldFrameIndex, frameIndex);
        Assert.Equal(64, bytes.Count);
        Assert.Equal(ResettingRing.HeldFrameIndex + 1, cursor.FromFrameIndex);
    }

    /// <summary>不插复位的普通环：与 <see cref="ResettingRing"/> 同尺寸同帧号。</summary>
    private sealed class PlainRing : IFrameSideDataRing
    {
        public SideDataReadOutcome CopyFrameSideDataAfter(uint sideDataType, long fromFrameIndex,
            byte[]? buffer, out uint requiredSize, out long foundFrameIndex)
        {
            requiredSize = 64;
            foundFrameIndex = ResettingRing.HeldFrameIndex;
            if (buffer is null or { Length: 0 }) return SideDataReadOutcome.Found;
            for (var i = 0; i < 64; i++) buffer[i] = 7;
            return SideDataReadOutcome.Found;
        }
    }
}
