using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Tests;

/// <summary>
/// kind=3 注入的"伴报表"记账（<c>Fff3FpSession</c> 里那两个纯函数）。
///
/// <para>被钉住的三件事：<b>①</b> 本泵自己发出的那一批（含 kind=3）不能被当成"宿主的表"
/// 记下来，否则每次注入都会把伴报表再套一层，越记越长；<b>②</b> 用户点了"清除注入"之后
/// 伴报表必须同步掉，否则下一次注入会把刚清掉的表原样装回去（点了没效果）；
/// <b>③</b> "全被清掉"与"宿主压根没载入过"必须同形（都答 null），否则泵的注入会
/// 因为多带一个空列表而与"没有伴报"走上不同的分支。</para>
/// </summary>
public class ColorTransformCompanionTests
{
    private static DynamicMetadataPacket Vivid(long frame) => new()
    {
        Kind = DynamicMetadataKind.HdrVivid,
        FrameIndex = frame,
        Payload = new HdrVividMetadataPayload
        {
            Windows = new[] { (0.9f, 0.8f, 0.7f, 0.6f) },
            TargetedDisplayNits = 1000f,
        },
    };

    private static DynamicMetadataPacket Hdr10Plus(long frame) => new()
    {
        Kind = DynamicMetadataKind.Hdr10Plus,
        FrameIndex = frame,
        Payload = new HdrDynamicMetadataEntry
        {
            FrameIndex = frame,
            Windows = Array.Empty<HdrDynamicMetadataWindow>(),
            TargetedDisplayNits = 0f,
        },
    };

    private static DynamicMetadataPacket External(long frame) => new()
    {
        Kind = DynamicMetadataKind.ExternalColorTransform,
        FrameIndex = frame,
        Payload = ColorTransformTable.Identity(),
    };

    [Fact]
    public void 宿主载入的表不含kind3()
    {
        var host = new[] { Vivid(0), Hdr10Plus(3) };
        Assert.False(Fff3FpEngine.Fff3FpSession.ContainsExternalTransform(host));
    }

    [Fact]
    public void 本泵发出的那批含kind3()
    {
        // 形状就是"伴报表 + 末尾一条 kind=3"。
        var batch = new[] { Vivid(0), Hdr10Plus(3), External(7) };
        Assert.True(Fff3FpEngine.Fff3FpSession.ContainsExternalTransform(batch));
        Assert.True(Fff3FpEngine.Fff3FpSession.ContainsExternalTransform(new[] { External(7) }));
    }

    [Fact]
    public void 清全部kind后伴报表为空()
    {
        var current = new[] { Vivid(0), Hdr10Plus(3) };
        Assert.Null(Fff3FpEngine.Fff3FpSession.KeepExcept(current, DynamicMetadataKind.None));
    }

    [Fact]
    public void 只清一种kind时其余留下()
    {
        var current = new[] { Vivid(0), Hdr10Plus(3), Vivid(5) };
        var kept = Fff3FpEngine.Fff3FpSession.KeepExcept(current, DynamicMetadataKind.Hdr10Plus);
        Assert.NotNull(kept);
        Assert.Equal(2, kept!.Count);
        Assert.All(kept, p => Assert.Equal(DynamicMetadataKind.HdrVivid, p.Kind));
        Assert.Equal(new long[] { 0, 5 }, kept.Select(p => p.FrameIndex));
    }

    [Fact]
    public void 全部条目都属于被清的kind时答null()
    {
        // "清完之后没有表"必须与"从来没载入过"同形，不能留一个空列表。
        var current = new[] { Hdr10Plus(3), Hdr10Plus(9) };
        Assert.Null(Fff3FpEngine.Fff3FpSession.KeepExcept(
            current, DynamicMetadataKind.Hdr10Plus));
    }

    [Fact]
    public void 空表与null一律答null()
    {
        Assert.Null(Fff3FpEngine.Fff3FpSession.KeepExcept(null, DynamicMetadataKind.None));
        Assert.Null(Fff3FpEngine.Fff3FpSession.KeepExcept(
            Array.Empty<DynamicMetadataPacket>(), DynamicMetadataKind.HdrVivid));
    }
}
