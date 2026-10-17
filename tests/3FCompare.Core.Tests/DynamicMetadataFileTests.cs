using System.Text;
using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Tests;

/// <summary>
/// 动态元数据交换文件（JSON v1）的载入侧：<see cref="DynamicMetadataFile"/> 的解析与校验。
/// </summary>
/// <para>这里钉的是**内核拒绝口径在托管侧的镜像**：内核 <c>HdrProcessor.cpp</c> 的
/// <c>BuildInjectedWindow</c>(:496) / <c>BuildInjectedState</c>(:620) /
/// <c>BuildInjectedStateVivid</c>(:572) 一旦判定某条非法就<b>整批拒绝且旧表续任</b>，
/// 调用方只拿到一个 false。解析器必须让这些表在载入时就带着中文的、带字段路径的原因失败。</para>
/// <para>另外钉住两个"不做"的约定：不排序（<c>InstallInjectedTable</c>:715 自己排）、
/// 不去重（同一个帧号出现两次是文件作者的意图，内核的"最后一条 ≤ 当前帧"会给出确定结果）。</para>
/// <para>不写落临时文件的用例：当前沙箱拒写 %TEMP%，那类失败是环境问题而非产品缺陷。</para>
public class DynamicMetadataFileTests
{
    // ---------------- 通过路径 ----------------

    [Fact]
    public void 最小合法Hdr10Plus_单窗口无曲线_逐字段核对()
    {
        var json = Doc("Hdr10Plus", Entry(7, Hdr10PlusWindow(), nits: "1000"));

        var packets = ParseOk(json);
        var packet = Assert.Single(packets);
        Assert.Equal(DynamicMetadataKind.Hdr10Plus, packet.Kind);
        Assert.Equal(7L, packet.FrameIndex);

        var entry = Assert.IsType<HdrDynamicMetadataEntry>(packet.Payload);
        Assert.Equal(7L, entry.FrameIndex);          // 包与载荷填同一个帧号
        Assert.Equal(1000f, entry.TargetedDisplayNits);

        var window = Assert.Single(entry.Windows);
        Assert.Equal(0f, window.UpperLeftX);
        Assert.Equal(0f, window.UpperLeftY);
        Assert.Equal(1f, window.LowerRightX);
        Assert.Equal(1f, window.LowerRightY);
        Assert.Equal(0.8f, window.MaxSclR);
        Assert.Equal(0.75f, window.MaxSclG);
        Assert.Equal(0.7f, window.MaxSclB);
        Assert.Equal(0.5f, window.AverageMaxRgb);
        Assert.Equal(0f, window.FractionBrightPixels);   // 省略 → 0
        Assert.Equal(0u, window.ToneMappingFlag);
        Assert.Equal(0f, window.KneePointX);
        Assert.Equal(0f, window.KneePointY);
        Assert.Empty(window.CurveAnchors);
    }

    [Fact]
    public void 最小合法HdrVivid_窗口四值核对()
    {
        var json = Doc("HdrVivid", Entry(3, VividWindow(maxRgb: "[0.8,0.75,0.7]", averageMaxRgb: "0.5"), nits: "1000"));

        var packets = ParseOk(json);
        var packet = Assert.Single(packets);
        Assert.Equal(DynamicMetadataKind.HdrVivid, packet.Kind);
        Assert.Equal(3L, packet.FrameIndex);

        var payload = Assert.IsType<HdrVividMetadataPayload>(packet.Payload);
        Assert.Equal(1000f, payload.TargetedDisplayNits);
        var window = Assert.Single(payload.Windows);
        Assert.Equal(0.8f, window.MaxR);
        Assert.Equal(0.75f, window.MaxG);
        Assert.Equal(0.7f, window.MaxB);
        Assert.Equal(0.5f, window.AverageMaxRgb);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void 多窗口带完整曲线_锚点顺序原样保留(int windowCount)
    {
        // 三个窗口各自带 4 个锚点：验证"逐窗独立校验"与"锚点不被排序/截断"。
        var windows = string.Join(',', Enumerable.Range(0, windowCount)
            .Select(i => Hdr10PlusWindow(
                upperLeft: $"[{i * 0.1},0]", lowerRight: "[1,1]",
                maxScl: "[0.8,0.75,0.7]", averageMaxRgb: "0.5",
                toneMappingFlag: "1",
                kneePoint: "[0.65,0.65]",
                curveAnchors: $"[{0.7 + i * 0.01},0.85,0.95,1.0]")));
        var json = Doc("Hdr10Plus", Entry(11, windows));

        var entry = Assert.IsType<HdrDynamicMetadataEntry>(Assert.Single(ParseOk(json)).Payload);
        Assert.Equal(windowCount, entry.Windows.Count);

        var first = entry.Windows[0];
        Assert.Equal(0.7f, first.CurveAnchors[0], 5);
        Assert.Equal(0.85f, first.CurveAnchors[1], 5);
        Assert.Equal(0.95f, first.CurveAnchors[2], 5);
        Assert.Equal(1.0f, first.CurveAnchors[3], 5);
        // 第二个窗口随 tag 位移，证明锚点是逐窗读取而非共用
        if (windowCount > 1)
            Assert.Equal(0.71f, entry.Windows[1].CurveAnchors[0], 5);
        Assert.Equal(0.65f, first.KneePointX, 5);
        Assert.Equal(0.65f, first.KneePointY, 5);
    }

    [Fact]
    public void 曲线锚点为空_toneMappingFlag为1时按内核降级处理不报错()
    {
        // 内核 :563-567 只是把窗口降级为 TooFewAnchors，不算错 ⇒ 解析器也不该拒。
        var json = Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.5,0.5]", curveAnchors: "[]")));

        var entry = Assert.IsType<HdrDynamicMetadataEntry>(Assert.Single(ParseOk(json)).Payload);
        Assert.Empty(Assert.Single(entry.Windows).CurveAnchors);
        Assert.Equal(1u, Assert.Single(entry.Windows).ToneMappingFlag);
    }

    [Fact]
    public void Vivid窗口的几何与曲线字段被忽略而不拒绝()
    {
        // 内核 BuildInjectedStateVivid 把几何硬写成全幅、曲线不解释 ⇒ 多余字段不携带语义。
        var json = Doc("HdrVivid", Entry(0, VividWindow(
            geometry: "\"upperLeft\":[0.2,0.2],\"lowerRight\":[0.3,0.3],\"kneePoint\":[0.5,0.5],\"curveAnchors\":[0.9]")));

        var packets = ParseOk(json);
        Assert.Equal(DynamicMetadataKind.HdrVivid, Assert.Single(packets).Kind);
    }

    [Fact]
    public void 字段名与kind值不区分大小写()
    {
        var json = "{\"FORMAT\":\"3FC-Dynamic-Metadata\",\"Version\":1,\"KIND\":\"hdr10plus\"," +
                   "\"ENTRIES\":[{\"FrameIndex\":5,\"TargetedDisplayNits\":1000,\"WINDOWS\":[" +
                   "{\"UpperLeft\":[0,0],\"LowerRight\":[1,1],\"MaxScl\":[0.8,0.75,0.7]," +
                   "\"AverageMaxRgb\":0.5,\"ToneMappingFlag\":0}]}]}";

        var packets = ParseOk(json);
        Assert.Equal(DynamicMetadataKind.Hdr10Plus, Assert.Single(packets).Kind);
        Assert.Equal(5L, Assert.Single(packets).FrameIndex);
    }

    [Fact]
    public void 条目顺序原样保留_不排序不去重()
    {
        // 内核 InstallInjectedTable(:715) 自己按 frameIndex 升序排序 ⇒ 解析器保持文件原序。
        var entries = string.Join(',', new[] { 30L, 20L, 10L }.Select(f => Entry(f, Hdr10PlusWindow())));
        var json = Doc("Hdr10Plus", entries);

        var packets = ParseOk(json);
        Assert.Equal(new[] { 30L, 20L, 10L }, packets.Select(p => p.FrameIndex).ToArray());
    }

    [Fact]
    public void 条目数上限_4096条成功_4097条失败且错误含上限()
    {
        var entry = Entry(0, Hdr10PlusWindow());

        var okJson = Doc("Hdr10Plus", string.Join(',', Enumerable.Repeat(entry, DynamicMetadataFile.MaxEntries)));
        Assert.Equal(DynamicMetadataFile.MaxEntries, ParseOk(okJson).Count);

        var bigJson = Doc("Hdr10Plus", string.Join(',', Enumerable.Repeat(entry, DynamicMetadataFile.MaxEntries + 1)));
        var error = Fail(bigJson);
        Assert.Contains("4096", error);
        Assert.Contains("4097", error);
    }

    // ---------------- 文件级拒绝 ----------------

    [Fact]
    public void format缺失_失败()
    {
        var error = Fail("{\"version\":1,\"kind\":\"Hdr10Plus\",\"entries\":[" + Entry(0, Hdr10PlusWindow()) + "]}");
        Assert.Contains("format", error);
    }

    [Fact]
    public void format错误_失败()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow())).Replace("3fc-dynamic-metadata", "hdr-metadata"));
        Assert.Contains("format", error);
    }

    [Fact]
    public void version为2_失败()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow())).Replace("\"version\":1", "\"version\":2"));
        Assert.Contains("version", error);
    }

    [Fact]
    public void kind未知_失败()
    {
        var error = Fail(Doc("DolbyVision", Entry(0, Hdr10PlusWindow())));
        Assert.Contains("kind", error);
    }

    [Fact]
    public void entries为空数组_失败()
    {
        var error = Fail(Doc("Hdr10Plus", ""));
        Assert.Contains("entries", error);
    }

    // ---------------- 逐条校验规则的拒绝（Hdr10Plus） ----------------

    [Fact]
    public void 窗口矩形退化或倒置_拒绝()
    {
        // 内核 :508-510：lowerRight <= upperLeft 的窗口选不中像素，曲线永远走不到。
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(upperLeft: "[0.5,0.5]", lowerRight: "[0.5,0.6]"))));
        Assert.Contains("lowerRight", error);
    }

    [Fact]
    public void upperLeft为负_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(upperLeft: "[-0.1,0]"))));
        Assert.Contains("upperLeft", error);
    }

    [Fact]
    public void maxScl长度不足_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(maxScl: "[0.5,0.5]"))));
        Assert.Contains("maxScl", error);
    }

    [Fact]
    public void maxScl越界_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(maxScl: "[0.5,1.5,0.5]"))));
        Assert.Contains("maxScl", error);
    }

    [Fact]
    public void averageMaxRgb越界_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(averageMaxRgb: "1.5"))));
        Assert.Contains("averageMaxRgb", error);
    }

    [Fact]
    public void toneMappingFlag非0或1_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(toneMappingFlag: "2"))));
        Assert.Contains("toneMappingFlag", error);
    }

    [Fact]
    public void kneePointY高于kneePointX_拒绝()
    {
        // 内核 :544：kneePointY > kneePointX + 1e-4 即拒。
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.5,0.9]"))));
        Assert.Contains("kneePoint", error);
    }

    [Fact]
    public void kneePointX为1_拒绝()
    {
        // 内核 :542：kneePointX >= 1 即拒（要求 0 ≤ x < 1）。
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[1.0,1.0]"))));
        Assert.Contains("kneePoint", error);
    }

    [Fact]
    public void 锚点数超过15_拒绝()
    {
        var anchors = "[" + string.Join(',', Enumerable.Repeat("0.95", 16)) + "]";
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.2,0.2]", curveAnchors: anchors))));
        Assert.Contains("curveAnchors", error);
        Assert.Contains("15", error);
    }

    [Fact]
    public void 锚点越界_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.2,0.2]", curveAnchors: "[0.5,1.5]"))));
        Assert.Contains("curveAnchors[1]", error);
    }

    [Fact]
    public void 首锚点小于kneePointY_拒绝()
    {
        // ⚠ 内核 :552 的 `previous` 初值是 kneePointY，所以首锚点也必须 ≥ 它。
        // 只查"锚点之间单调不减"会放过这种表（:558 的比较是从 kneePointY 起步的）。
        // kneePoint 取 (0.65,0.5)：先过第 13 条（y ≤ x+1e-4），这样命中点确实落在首锚点上。
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.65,0.5]", curveAnchors: "[0.4,0.9]"))));
        Assert.Contains("curveAnchors[0]", error);
        Assert.Contains("kneePointY", error);
    }

    [Fact]
    public void 首锚点等于kneePointY_接受()
    {
        // 内核用的是 `value < previous` 严格小于 ⇒ 相等合法（拐点即首个锚点是常见写法）。
        var json = Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.65,0.65]", curveAnchors: "[0.65,0.9]")));
        var entry = Assert.IsType<HdrDynamicMetadataEntry>(Assert.Single(ParseOk(json)).Payload);
        Assert.Equal(0.65f, Assert.Single(entry.Windows).CurveAnchors[0], 5);
    }

    [Fact]
    public void 交换格式示例里的kneePoint_0_65_0_85_按内核第13条被拒()
    {
        // ⚠ 规格文档顶部的示例写的是 "kneePoint":[0.65,0.85]，但它与第 13 条
        // （0 ≤ y ≤ 1 且 y ≤ x + 1e-4）自相矛盾：0.85 > 0.65 + 1e-4。
        // 内核 :544 会拒，故这里钉住"按第 13 条拒"，而不是照抄示例放行。
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.65,0.85]", curveAnchors: "[0.85,0.9,0.95,1.0]"))));
        Assert.Contains("kneePoint", error);
    }

    [Fact]
    public void 锚点非单调_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(0, Hdr10PlusWindow(
            toneMappingFlag: "1", kneePoint: "[0.2,0.2]", curveAnchors: "[0.5,0.4,0.6]"))));
        Assert.Contains("curveAnchors[1]", error);
        Assert.Contains("单调", error);
    }

    [Fact]
    public void 条目级frameIndex为负_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", Entry(-1, Hdr10PlusWindow())));
        Assert.Contains("frameIndex", error);
    }

    [Fact]
    public void 窗口数为0_拒绝()
    {
        var error = Fail(Doc("Hdr10Plus", "{\"frameIndex\":0,\"windows\":[]}"));
        Assert.Contains("windows", error);
    }

    [Fact]
    public void 窗口数为4_拒绝()
    {
        var windows = string.Join(',', Enumerable.Repeat(Hdr10PlusWindow(), 4));
        var error = Fail(Doc("Hdr10Plus", Entry(0, windows)));
        Assert.Contains("windows", error);
    }

    [Fact]
    public void Vivid窗口maxRgb越界_拒绝()
    {
        var error = Fail(Doc("HdrVivid", Entry(0, VividWindow(maxRgb: "[0.5,0.5,-0.1]"))));
        Assert.Contains("maxRgb", error);
    }

    [Fact]
    public void Vivid目标峰值为负_拒绝()
    {
        var error = Fail(Doc("HdrVivid", Entry(0, VividWindow(), nits: "-5")));
        Assert.Contains("targetedDisplayNits", error);
    }

    // ---------------- 异常边界 ----------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public void 非法输入_失败且不抛异常(string input)
    {
        var ex = Record.Exception(() => DynamicMetadataFile.TryParse(input, out _, out _));
        Assert.Null(ex);

        var error = Fail(input);        // 内含"失败时 packets 为 null"的断言
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryLoad_文件不存在_失败且不抛异常()
    {
        // 刻意不落临时文件：沙箱拒写 %TEMP%，只探测"读不到"这条分支。
        var missing = Path.Combine(Path.GetTempPath(), "3fc-no-such-dir", "no-such-metadata.json");

        var ex = Record.Exception(() => DynamicMetadataFile.TryLoad(missing, out _, out _));
        Assert.Null(ex);

        var ok = DynamicMetadataFile.TryLoad(missing, out var packets, out var error);
        Assert.False(ok);
        Assert.Null(packets);
        Assert.NotNull(error);
    }

    // ---------------- 组装用的小工具 ----------------

    /// <summary>断言解析成功并返回条目（失败时带上解析器的中文原因）。</summary>
    private static IReadOnlyList<DynamicMetadataPacket> ParseOk(string json)
    {
        var ok = DynamicMetadataFile.TryParse(json, out var packets, out var error);
        Assert.True(ok, error ?? string.Empty);
        Assert.Null(error);
        Assert.NotNull(packets);
        return packets;
    }

    /// <summary>断言解析失败并返回中文原因（同时钉住"失败时 packets 为 null"）。</summary>
    private static string Fail(string json)
    {
        var ok = DynamicMetadataFile.TryParse(json, out var packets, out var error);
        Assert.False(ok);
        Assert.Null(packets);
        Assert.NotNull(error);
        return error;
    }

    private static string Doc(string kind, string entries)
        => $"{{\"format\":\"3fc-dynamic-metadata\",\"version\":1,\"kind\":\"{kind}\",\"entries\":[{entries}]}}";

    /// <summary>一条条目（Hdr10Plus / HdrVivid 同外形，差异全在 windows 里）。</summary>
    private static string Entry(long frameIndex, string windows, string nits = "1000")
        => $"{{\"frameIndex\":{frameIndex},\"targetedDisplayNits\":{nits},\"windows\":[{windows}]}}";

    private static string Hdr10PlusWindow(
        string upperLeft = "[0,0]",
        string lowerRight = "[1,1]",
        string maxScl = "[0.8,0.75,0.7]",
        string averageMaxRgb = "0.5",
        string toneMappingFlag = "0",
        string? kneePoint = null,
        string? curveAnchors = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"upperLeft\":").Append(upperLeft)
          .Append(",\"lowerRight\":").Append(lowerRight)
          .Append(",\"maxScl\":").Append(maxScl)
          .Append(",\"averageMaxRgb\":").Append(averageMaxRgb)
          .Append(",\"toneMappingFlag\":").Append(toneMappingFlag);
        if (kneePoint != null) sb.Append(",\"kneePoint\":").Append(kneePoint);
        if (curveAnchors != null) sb.Append(",\"curveAnchors\":").Append(curveAnchors);
        return sb.Append('}').ToString();
    }

    /// <summary>Vivid 窗口；geometry 用于塞进"应被忽略"的几何/曲线字段。</summary>
    private static string VividWindow(string maxRgb = "[0.8,0.75,0.7]", string averageMaxRgb = "0.5", string? geometry = null)
    {
        var sb = new StringBuilder("{");
        if (geometry != null) sb.Append(geometry).Append(',');
        return sb.Append("\"maxRgb\":").Append(maxRgb)
                 .Append(",\"averageMaxRgb\":").Append(averageMaxRgb)
                 .Append('}').ToString();
    }
}
