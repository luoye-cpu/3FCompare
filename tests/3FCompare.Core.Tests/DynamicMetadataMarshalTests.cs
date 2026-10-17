using _3FCompare.Core.Backend;
using _3FCompare.Core.Backend.Interop;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 通用动态元数据注入通道的托管 → 原生扁平映射（<see cref="Fff3FpEngine.Fff3FpSession"/>
/// 的三个 internal static marshal 助手，照 <see cref="MapSnapshot"/> 先例开放给测试）。
///
/// <para>这些测试钉的是内核契约在托管侧的算术：15 锚点上限、3 窗口上限、
/// 缺位零补齐、FrameIndex/目标峰值原样透传、Size/Version 自描述头。
/// 语义源头是内核 <c>FFF3FPHdrDynamicMetadataEntry</c>/<c>FFF3FPHdrVividMetadataPayload</c>
/// 的注释与 static_assert（见 DynamicMetadataLayoutTests 的布局镜像）。</para>
/// </summary>
public class DynamicMetadataMarshalTests
{
    private static HdrDynamicMetadataWindow MakeWindow(float tag, int anchorCount)
    {
        var anchors = new float[anchorCount];
        for (var i = 0; i < anchorCount; i++) anchors[i] = tag + i;
        return new HdrDynamicMetadataWindow
        {
            UpperLeftX = tag * 100,
            UpperLeftY = tag * 100 + 1,
            LowerRightX = tag * 100 + 2,
            LowerRightY = tag * 100 + 3,
            MaxSclR = tag + 0.1f,
            MaxSclG = tag + 0.2f,
            MaxSclB = tag + 0.3f,
            AverageMaxRgb = tag + 0.4f,
            FractionBrightPixels = tag + 0.5f,
            ToneMappingFlag = 7,
            KneePointX = tag + 0.6f,
            KneePointY = tag + 0.7f,
            CurveAnchors = anchors,
        };
    }

    [Fact]
    public void 窗口映射_15个锚点全部按序落位()
    {
        var w = Fff3FpEngine.Fff3FpSession.MarshalWindow(MakeWindow(tag: 1f, anchorCount: 15));

        Assert.Equal(15u, w.NumCurveAnchors);
        Assert.Equal(100f, w.UpperLeftX);
        Assert.Equal(103f, w.LowerRightY);
        Assert.Equal(1.1f, w.MaxSclR);
        Assert.Equal(1.7f, w.KneePointY);
        Assert.Equal(7u, w.ToneMappingFlag);
        // 锚点按序落在扁平字段上（tag+i）
        Assert.Equal(1f, w.CurveAnchors0);
        Assert.Equal(8f, w.CurveAnchors7);
        Assert.Equal(15f, w.CurveAnchors14);
    }

    [Fact]
    public void 窗口映射_锚点超过15个截断且序号钳在15()
    {
        var w = Fff3FpEngine.Fff3FpSession.MarshalWindow(MakeWindow(tag: 1f, anchorCount: 20));

        Assert.Equal(15u, w.NumCurveAnchors);
        Assert.Equal(1f, w.CurveAnchors0);
        Assert.Equal(15f, w.CurveAnchors14);   // 第 16 个锚点（16）被丢弃
    }

    [Fact]
    public void 窗口映射_锚点少于15个时剩余字段零补齐()
    {
        var w = Fff3FpEngine.Fff3FpSession.MarshalWindow(MakeWindow(tag: 1f, anchorCount: 2));

        Assert.Equal(2u, w.NumCurveAnchors);
        Assert.Equal(1f, w.CurveAnchors0);
        Assert.Equal(2f, w.CurveAnchors1);
        Assert.Equal(0f, w.CurveAnchors2);
        Assert.Equal(0f, w.CurveAnchors14);
    }

    [Fact]
    public void 条目映射_窗口超3个截断_帧号与目标峰值透传()
    {
        var windows = Enumerable.Range(0, 5).Select(i => MakeWindow(tag: i + 1, anchorCount: 3)).ToArray();
        var e = Fff3FpEngine.Fff3FpSession.MarshalEntry(new HdrDynamicMetadataEntry
        {
            FrameIndex = 30,
            Windows = windows,
            TargetedDisplayNits = 1400f,
        });

        Assert.Equal(3u, e.NumWindows);         // 内核只认 1..3
        Assert.Equal(30L, e.FrameIndex);
        Assert.Equal(1400f, e.TargetedDisplayNits);
        Assert.Equal((uint)System.Runtime.InteropServices.Marshal.SizeOf<Fff3FpHdrDynamicMetadataEntry>(), e.Size);
        Assert.Equal(1u, e.Version);
        // 前三个窗口按序落位（tag 1/2/3），第 4/5 个被丢弃
        Assert.Equal(100f, e.Window0.UpperLeftX);
        Assert.Equal(200f, e.Window1.UpperLeftX);
        Assert.Equal(300f, e.Window2.UpperLeftX);
    }

    [Fact]
    public void 条目映射_零窗口钳到1且窗口为零值()
    {
        var e = Fff3FpEngine.Fff3FpSession.MarshalEntry(new HdrDynamicMetadataEntry
        {
            FrameIndex = 0,
            Windows = Array.Empty<HdrDynamicMetadataWindow>(),
            TargetedDisplayNits = 0f,
        });

        // 与 MarshalVividPayload 同口径：内核要求 numWindows ∈ [1,3]，
        // 钳到 1 比把 0 送进去让整批被拒更符合"托管层先自整形"的约定。
        Assert.Equal(1u, e.NumWindows);
        Assert.Equal(default, e.Window0);
    }

    [Fact]
    public void Vivid载荷映射_窗口超3个截断_目标峰值透传()
    {
        var windows = Enumerable.Range(0, 4)
            .Select(i => ((float)i + 0.5f, (float)i + 0.6f, (float)i + 0.7f, (float)i + 0.8f))
            .ToArray();
        var p = Fff3FpEngine.Fff3FpSession.MarshalVividPayload(new HdrVividMetadataPayload
        {
            Windows = windows,
            TargetedDisplayNits = 2000f,
        });

        Assert.Equal(3u, p.NumWindows);
        Assert.Equal(0.5f, p.W0MaxRGB0);
        Assert.Equal(0.6f, p.W0MaxRGB1);
        Assert.Equal(0.7f, p.W0MaxRGB2);
        Assert.Equal(0.8f, p.W0AverageMaxRGB);
        Assert.Equal(1.5f, p.W1MaxRGB0);
        Assert.Equal(2.5f, p.W2MaxRGB0);
        Assert.Equal(2.8f, p.W2AverageMaxRGB);
        Assert.Equal(2000f, p.TargetedDisplayNits);
        Assert.Equal(68u, p.Size);              // 与内核 static_assert 同数
        Assert.Equal(1u, p.Version);
    }

    [Fact]
    public void Vivid载荷映射_零窗口钳到1且全零()
    {
        var p = Fff3FpEngine.Fff3FpSession.MarshalVividPayload(new HdrVividMetadataPayload
        {
            Windows = Array.Empty<(float, float, float, float)>(),
            TargetedDisplayNits = 0f,
        });

        Assert.Equal(1u, p.NumWindows);
        Assert.Equal(0f, p.W0MaxRGB0);
        Assert.Equal(0f, p.W2AverageMaxRGB);
        Assert.Equal(0u, p.Reserved);
    }
}
