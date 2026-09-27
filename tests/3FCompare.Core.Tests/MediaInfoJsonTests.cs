using _3FCompare.Core.Backend;

namespace _3FCompare.Core.Tests;

/// <summary>
/// <c>Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson</c>（docs/41 §4.5 #2）。
///
/// <para><b>为什么必须测</b>：它是 3FP 内核 JSON → <see cref="EngineMediaInfo"/> 的<b>唯一</b>
/// 解析入口，媒体信息面板的全部数据都来自它；此前是 private，零覆盖（docs/41 §4.4 A 类）。
/// 解析失败只记一条 Warn 并返回 null ⇒ 界面上表现为"信息面板全空"，靠肉眼无法归因。</para>
///
/// <para><b>字段名从实现读出，不靠猜</b>：字段名逐条对照 Fff3FpEngine.cs:474-562
/// （<c>path</c>/<c>filename</c>、<c>streams[].type</c>=<c>video</c>/<c>audio</c>、
/// <c>nominalFrameRateNumerator</c>/<c>Denominator</c>、<c>hdrFormat</c>、<c>fieldOrder</c>、
/// <c>decoderBitDepth</c>…）。猜字段名会让 JSON 全被忽略而断言依旧全绿 —— 那是本文件最要避免的失败模式。</para>
///
/// <para><b>期望值来源（独立推算）</b>：① 字符串/整数取 JSON 里写死的字面量（人工抄写）；
/// ② 帧率用<b>手算除法</b>的结果写成十进制字面量（如 30000/1001 = 29.97002997…），
/// 并配 <c>Assert.Equal(expected, actual, precision)</c>；③ 越界钳零的边界取自实现注释
/// "异常值视为未知"与区间 <c>[1.0, 480.0]</c>。全程不调用被测函数或其兄弟函数。</para>
/// </summary>
public class MediaInfoJsonTests
{
    // ------------------------------------------------------------------
    // 1. 全字段解析（视频 / 音频 / 容器）
    // ------------------------------------------------------------------

    /// <summary>一条含全部字段的 JSON 必须逐字段落到 EngineMediaInfo 上。
    /// 这里同时覆盖了容器级字段与音频流的字段回退（<c>sampleRate</c> 主键）。</summary>
    [Fact]
    public void 完整JSON的容器视频音频字段全部落到DTO()
    {
        const string json = """
        {
          "path": "/media/clip.mkv",
          "format": "matroska",
          "duration100ns": 1234567890,
          "bitRate": 8000000,
          "fileSize": 987654321,
          "streams": [
            {
              "type": "video",
              "width": 3840,
              "height": 2160,
              "codec": "hevc",
              "nominalFrameRateNumerator": 30000,
              "nominalFrameRateDenominator": 1001,
              "hdr": true,
              "hdrFormat": "HDR10",
              "lossless": false,
              "decoderBitDepth": 10,
              "pixelFormat": "yuv420p10le",
              "chromaSubsampling": "4:2:0",
              "colorPrimaries": "bt2020",
              "colorTransfer": "smpte2084",
              "colorSpace": "bt2020nc",
              "frames": 1440,
              "fieldOrder": 0
            },
            {
              "type": "audio",
              "codec": "aac",
              "channels": 2,
              "sampleRate": 48000
            }
          ]
        }
        """;

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal("/media/clip.mkv", info!.Path);
        Assert.Equal("matroska", info.Format);
        Assert.Equal(1234567890L, info.Duration100ns);
        Assert.Equal(8000000L, info.BitRate);
        Assert.Equal(987654321L, info.FileSize);

        Assert.Equal(3840, info.VideoWidth);
        Assert.Equal(2160, info.VideoHeight);
        Assert.Equal("hevc", info.Codec);
        Assert.Equal(10, info.BitDepth);
        Assert.Equal("yuv420p10le", info.PixelFormat);
        Assert.Equal("4:2:0", info.ChromaSubsampling);
        Assert.Equal("bt2020", info.ColorPrimaries);
        Assert.Equal("smpte2084", info.ColorTransfer);
        Assert.Equal("bt2020nc", info.ColorSpace);
        Assert.Equal("HDR10", info.HdrFormat);
        Assert.True(info.IsHdr);
        Assert.False(info.IsLossless);
        Assert.False(info.Interlaced);
        Assert.Equal(1440L, info.FrameCount);

        Assert.Equal("aac", info.AudioCodec);
        Assert.Equal(2, info.AudioChannels);
        Assert.Equal(48000, info.AudioSampleRate);
    }

    // ------------------------------------------------------------------
    // 2. 帧率：num/den → fps
    // ------------------------------------------------------------------

    /// <summary>NTSC 帧率必须按 num/den 相除得出。
    /// 期望值 29.97003 = 手算 30000 ÷ 1001 = 29.970029970…，四舍五入到 5 位小数，
    /// 用 <c>precision: 5</c> 断言（不是用被测代码再算一遍）。</summary>
    [Fact]
    public void 帧率由分子除以分母得出()
    {
        const string json = """
        { "streams": [ { "type": "video", "nominalFrameRateNumerator": 30000,
                         "nominalFrameRateDenominator": 1001 } ] }
        """;

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(29.97003, info!.FrameRate, 5);
    }

    /// <summary>帧率区间边界：合法值原样保留，越界值一律钳成 0（= 未知）。
    /// 期望值人工推算：25/1=25、1/1=1.0、480/1=480 都在闭区间 [1.0, 480.0] 内；
    /// 481/1=481 &gt; 480、1/2=0.5 &lt; 1.0 越界 ⇒ 0；分子为 0 时实现直接跳过 ⇒ 0。
    /// 全部为整数或可精确表示的二进制小数，故用 precision: 9 断言不引入舍入争议。</summary>
    [Theory]
    [InlineData(25, 1, 25.0)]     // 合法
    [InlineData(1, 1, 1.0)]       // 下边界（含）
    [InlineData(480, 1, 480.0)]   // 上边界（含）
    [InlineData(481, 1, 0.0)]     // 上界外
    [InlineData(15360, 1, 0.0)]   // 时间基混入 fps 位（实现注释点名的场景）
    [InlineData(1, 2, 0.0)]       // 下界外
    [InlineData(0, 1, 0.0)]       // 分子为 0 = 未知
    public void 帧率越界时钳为零(int num, int den, double expected)
    {
        var json = $$"""
        { "streams": [ { "type": "video", "nominalFrameRateNumerator": {{num}},
                         "nominalFrameRateDenominator": {{den}} } ] }
        """;

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.FrameRate, 9);
    }

    /// <summary>nominal 缺失时回退到 averageFrameRate*（Fff3FpEngine.cs:520-521）。
    /// 期望值 50.0 = 手算 50000 ÷ 1000。</summary>
    [Fact]
    public void 帧率在nominal缺失时回退到average()
    {
        const string json = """
        { "streams": [ { "type": "video", "averageFrameRateNumerator": 50000,
                         "averageFrameRateDenominator": 1000 } ] }
        """;

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(50.0, info!.FrameRate, 9);
    }

    // ------------------------------------------------------------------
    // 3. HDR 判定
    // ------------------------------------------------------------------

    /// <summary>IsHdr 的四个触发条件：<c>hdr</c> 为真、或 hdrFormat 命中
    /// HDR10 / HDR10+ / HLG（大小写不敏感）。SDR 与缺失都必须为 false。
    /// 期望值来自实现分支的直读，不来自任何计算。</summary>
    [Theory]
    [InlineData("""{ "type": "video", "hdr": true }""", true)]
    [InlineData("""{ "type": "video", "hdrFormat": "HDR10" }""", true)]
    [InlineData("""{ "type": "video", "hdrFormat": "hdr10+" }""", true)]  // 大小写不敏感
    [InlineData("""{ "type": "video", "hdrFormat": "HLG" }""", true)]
    [InlineData("""{ "type": "video", "hdrFormat": "SDR" }""", false)]
    [InlineData("""{ "type": "video" }""", false)]
    public void HDR判定跟随hdr布尔与hdrFormat(string videoObject, bool expected)
    {
        var json = $$"""{ "streams": [ {{videoObject}} ] }""";

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.IsHdr);
    }

    // ------------------------------------------------------------------
    // 4. 交错判定：fieldOrder
    // ------------------------------------------------------------------

    /// <summary>fieldOrder = -1 或 &gt; 0 视为交错；0 与非数字缺失视为非交错
    /// （Fff3FpEngine.cs:543-544）。-1 是内核的"未知/非渐进"哨兵值。</summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void fieldOrder为负或正数时判定为交错(int fieldOrder, bool expected)
    {
        var json = $$"""
        { "streams": [ { "type": "video", "fieldOrder": {{fieldOrder}} } ] }
        """;

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.Interlaced);
    }

    /// <summary>fieldOrder 字段整体缺失（老内核）⇒ 非交错，不得抛。</summary>
    [Fact]
    public void fieldOrder缺失时判定为非交错()
    {
        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson("""{ "streams": [ { "type": "video" } ] }""");

        Assert.NotNull(info);
        Assert.False(info!.Interlaced);
    }

    // ------------------------------------------------------------------
    // 5. 坏输入
    // ------------------------------------------------------------------

    /// <summary>空、纯空白、语法错误三类输入都必须返回 null 而不是抛异常
    /// （解析失败只记 Warn，见 Fff3FpEngine.cs:591-599）。
    /// 期望值 null 是契约本身，非计算所得。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("""{"streams": [""")]
    [InlineData("""{"streams": [ { "type": "video" }""")]  // 缺右括号
    public void 坏JSON返回null(string json)
    {
        Assert.Null(Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json));
    }

    /// <summary>合法 JSON 但顶层不是对象（TryGetProperty 在非对象上抛
    /// InvalidOperationException）⇒ 同样由 catch 兜住返回 null，不得把异常漏出去。</summary>
    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("42")]
    public void 顶层非对象的合法JSON返回null(string json)
    {
        Assert.Null(Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json));
    }

    // ------------------------------------------------------------------
    // 6. 回退链与默认值
    // ------------------------------------------------------------------

    /// <summary>codec 回退链：<c>codec</c> → <c>codec_name</c> → 字面量 "unknown"。</summary>
    [Theory]
    [InlineData("""{ "type": "video", "codec": "h264", "codec_name": "ignored" }""", "h264")]
    [InlineData("""{ "type": "video", "codec_name": "av1" }""", "av1")]
    [InlineData("""{ "type": "video" }""", "unknown")]
    public void 编码回退链为codec再到codec_name再到unknown(string videoObject, string expected)
    {
        var json = $$"""{ "streams": [ {{videoObject}} ] }""";

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.Codec);
    }

    /// <summary>位深回退链：<c>decoderBitDepth</c> → <c>bitDepth</c> → 8（DTO 默认值）。</summary>
    [Theory]
    [InlineData("""{ "type": "video", "decoderBitDepth": 10, "bitDepth": 99 }""", 10)]
    [InlineData("""{ "type": "video", "bitDepth": 12 }""", 12)]
    [InlineData("""{ "type": "video" }""", 8)]
    public void 位深回退链为decoderBitDepth再到bitDepth再到8(string videoObject, int expected)
    {
        var json = $$"""{ "streams": [ {{videoObject}} ] }""";

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.BitDepth);
    }

    /// <summary>只有音频流（无 video 流）时：视频字段全部保持初值，
    /// Codec 落成 "unknown"、BitDepth 落成 8，且音频字段仍被解析。</summary>
    [Fact]
    public void 无视频流时视频字段取默认而音频仍解析()
    {
        const string json = """
        {
          "path": "/media/audioonly.m4a",
          "streams": [ { "type": "audio", "codec_name": "flac", "channelCount": 6,
                         "sample_rate": 96000 } ]
        }
        """;

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(0, info!.VideoWidth);
        Assert.Equal(0, info.VideoHeight);
        Assert.Equal(0.0, info.FrameRate, 9);
        Assert.Equal("unknown", info.Codec);
        Assert.Equal(8, info.BitDepth);
        Assert.Null(info.PixelFormat);
        Assert.False(info.IsHdr);
        Assert.False(info.Interlaced);

        // 音频回退链：codec_name / channelCount / sample_rate
        Assert.Equal("flac", info.AudioCodec);
        Assert.Equal(6, info.AudioChannels);
        Assert.Equal(96000, info.AudioSampleRate);
    }

    /// <summary>顶层 <c>path</c> 缺失时回退 <c>filename</c>；两者都缺则为空串
    /// （Fff3FpEngine.cs:474）。</summary>
    [Theory]
    [InlineData("""{ "filename": "/media/only-name.mp4" }""", "/media/only-name.mp4")]
    [InlineData("""{ "path": "/media/full.mp4", "filename": "ignored.mp4" }""", "/media/full.mp4")]
    [InlineData("""{ }""", "")]
    public void 路径回退链为path再到filename再到空串(string json, string expected)
    {
        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.Path);
    }

    /// <summary>视频流不必是 streams[0]：混在音频之后仍必须被选中
    /// （Fff3FpEngine.cs:482-490 逐个匹配 type=="video" 后 break）。
    /// 期望值 1280/720 为 JSON 字面量。</summary>
    [Fact]
    public void 视频流不在首位时仍被选中()
    {
        const string json = """
        {
          "streams": [
            { "type": "audio", "codec": "aac", "channels": 2, "sampleRate": 44100 },
            { "type": "video", "width": 1280, "height": 720, "codec": "vp9" }
          ]
        }
        """;

        var info = Fff3FpEngine.Fff3FpSession.ParseMediaInfoJson(json);

        Assert.NotNull(info);
        Assert.Equal(1280, info!.VideoWidth);
        Assert.Equal(720, info.VideoHeight);
        Assert.Equal("vp9", info.Codec);
        Assert.Equal("aac", info.AudioCodec);
    }
}
