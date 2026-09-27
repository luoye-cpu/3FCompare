using _3FCompare.Core.Backend;
using _3FCompare.Core.Imaging;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// ColorNormalizer（回读像素 → 线性光域，1.0 = SDR 参考白）测试。
///
/// <para><b>期望值一律独立推导</b>，不照抄被测实现（docs/15 教训）：</para>
/// <list type="bullet">
/// <item><description>SDR 侧用 IEC 61966-2-1 的 sRGB EOTF 分段式手算，例如
/// <c>((0.5+0.055)/1.055)^2.4 = 0.214041140</c>；</description></item>
/// <item><description>HDR 侧用内核契约 <c>1.0 = 80 nits</c>（docs/19 §3）与参考白
/// 203 nits 手算，例如 <c>2.6641 * 80 / 203 = 1.049891626</c>。</description></item>
/// </list>
///
/// <para><b>域由 <c>hdr</c> 标志决定</b>（位深只在标志缺失时兜底），因此每个用例都显式传
/// <c>hdr:</c>，不依赖任何隐含推断。</para>
/// </summary>
public sealed class ColorNormalizerTests
{
    /// <summary>与默认值一致的参考白，写死以便期望值可手算对照。</summary>
    private const float PaperWhite = 203f;

    /// <summary>浮点比较容差：被测实现全程 float，标准公式按 double 推导，留出 Pow 的舍入余量。</summary>
    private static void AssertClose(float expected, float actual, float tolerance = 1e-5f)
        => Assert.True(MathF.Abs(expected - actual) <= tolerance,
            $"期望 {expected:R}，实际 {actual:R}（容差 {tolerance:R}）");

    private static PixelSample Sdr(float r, float g, float b, float a = 1f, uint bitDepth = 8)
        => new(r, g, b, a, bitDepth);

    private static PixelSample Hdr(float r, float g, float b, float a = 1f)
        => new(r, g, b, a, 16);

    // ---------------------------------------------------------------- SDR

    [Theory]
    [InlineData(0.0f, 0.0f)]              // 黑 → 0
    [InlineData(1.0f, 1.0f)]              // 白 → 1
    [InlineData(0.5f, 0.214041140f)]      // ((0.5+0.055)/1.055)^2.4
    [InlineData(0.25f, 0.050876088f)]     // ((0.305)/1.055)^2.4
    [InlineData(0.75f, 0.522521554f)]     // ((0.805)/1.055)^2.4
    [InlineData(0.04045f, 0.003130805f)]  // 分段端点：0.04045 / 12.92
    public void SrgbToLinear_KnownPoints(float encoded, float expected)
        => AssertClose(expected, ColorNormalizer.SrgbToLinear(encoded));

    [Fact]
    public void SrgbToLinear_NegativeInput_KeepsSign()
    {
        // 线性段覆盖负值（超色域），符号必须保留：-0.1 / 12.92
        AssertClose(-0.007739938f, ColorNormalizer.SrgbToLinear(-0.1f));
    }

    [Fact]
    public void SrgbToLinear_AboveOne_IsNotClamped()
    {
        // 1.5 外推：((1.5+0.055)/1.055)^2.4 = 2.537155239 > 1
        var linear = ColorNormalizer.SrgbToLinear(1.5f);
        AssertClose(2.537155239f, linear);
        Assert.True(linear > 1f, "线性值 >1 时不得截断");
    }

    [Fact]
    public void SrgbToLinear_NaN_ReturnsZero()
    {
        // 无 NaN 契约：NaN 若原样扩散，下游阈值比较会静默全变 false
        Assert.Equal(0f, ColorNormalizer.SrgbToLinear(float.NaN));
        Assert.Equal(0f, ColorNormalizer.LinearToSrgb(float.NaN));
    }

    [Fact]
    public void Sdr_Sample_ConvertsGammaToLinear_AndPassesAlpha()
    {
        var linear = ColorNormalizer.ToLinear(Sdr(0.5f, 0.25f, 0.75f, 0.7f), hdr: false);

        AssertClose(0.214041140f, linear.R);
        AssertClose(0.050876088f, linear.G);
        AssertClose(0.522521554f, linear.B);
        Assert.Equal(0.7f, linear.A);   // A 通道不做转换，逐位相等
    }

    [Fact]
    public void TenBit_UsesSameSrgbEotf_AsEightBit()
    {
        // 位深只决定编码域，不决定曲线：10-bit RGB10A2 与 8-bit 走同一条 sRGB EOTF
        var eight = ColorNormalizer.ToLinear(Sdr(0.5f, 0.25f, 0.75f, 1f, bitDepth: 8), hdr: false);
        var ten = ColorNormalizer.ToLinear(Sdr(0.5f, 0.25f, 0.75f, 1f, bitDepth: 10), hdr: false);

        Assert.Equal(eight, ten);
    }

    [Fact]
    public void Sdr_IgnoresPaperWhite()
    {
        // 参考白只参与 HDR→相对值的换算；SDR 分支不依赖它
        var a = ColorNormalizer.ToLinear(Sdr(0.5f, 0.5f, 0.5f), hdr: false, PaperWhite);
        var b = ColorNormalizer.ToLinear(Sdr(0.5f, 0.5f, 0.5f), hdr: false, 100f);

        Assert.Equal(a, b);
    }

    // ---------------------------------------------------------------- HDR

    [Fact]
    public void Hdr_One_MapsTo_EightyOverPaperWhite()
    {
        // scRGB 1.0 = 80 nits；相对 203 nits 参考白 = 80/203 = 0.394088670
        var linear = ColorNormalizer.ToLinear(Hdr(1f, 1f, 1f), hdr: true);

        AssertClose(80f / 203f, linear.R);
        AssertClose(0.394088670f, linear.R);
    }

    [Fact]
    public void Hdr_AboveOne_IsNotClamped()
    {
        // docs/19 实测到的 HDR 蓝通道 2.6641：2.6641*80/203 = 1.049891626
        var linear = ColorNormalizer.ToLinear(Hdr(0.8979f, 0.4021f, 2.6641f), hdr: true);

        AssertClose(2.6641f * 80f / 203f, linear.B);
        AssertClose(1.049891626f, linear.B);
        Assert.True(linear.B > 1f, "HDR 高光 >1 不得截断");
        // 三个通道各自按同一比例缩放，通道间关系不变
        AssertClose(0.8979f * 80f / 203f, linear.R);
    }

    [Fact]
    public void Hdr_UsesProvidedPaperWhite()
    {
        // 参考白 100 nits：1.0 → 80/100 = 0.8；2.6641 → 2.13128
        var white = ColorNormalizer.ToLinear(Hdr(1f, 1f, 1f), hdr: true, 100f);
        AssertClose(0.8f, white.R);

        var highlight = ColorNormalizer.ToLinear(Hdr(2.6641f, 2.6641f, 2.6641f), hdr: true, 100f);
        AssertClose(2.6641f * 80f / 100f, highlight.R);
        AssertClose(2.13128f, highlight.R);
    }

    [Fact]
    public void Hdr_Alpha_PassesThrough()
    {
        var linear = ColorNormalizer.ToLinear(Hdr(1f, 1f, 1f, a: 0.25f), hdr: true);

        Assert.Equal(0.25f, linear.A);
    }

    [Fact]
    public void Hdr_Zero_MapsToZero()
    {
        var linear = ColorNormalizer.ToLinear(Hdr(0f, 0f, 0f), hdr: true);

        Assert.Equal(0f, linear.R);
        Assert.Equal(0f, linear.G);
        Assert.Equal(0f, linear.B);
    }

    // ---------------------------------------------------------------- 域判定

    [Theory]
    [InlineData(8u, false, false)]
    [InlineData(10u, false, false)]
    [InlineData(16u, false, true)]   // 标志缺失时的格式兜底：16 位只可能是 R16G16B16A16_FLOAT
    [InlineData(0u, false, false)]
    [InlineData(32u, false, false)]
    [InlineData(8u, true, true)]     // 标志是权威判据
    [InlineData(10u, true, true)]    // 10-bit HDR 链路：只看位深会走错曲线
    [InlineData(16u, true, true)]
    public void IsHdrDomain_TrustsHdrFlag_AndFallsBackToSixteen(uint bitDepth, bool hdr, bool expected)
        => Assert.Equal(expected, ColorNormalizer.IsHdrDomain(bitDepth, hdr));

    [Fact]
    public void TenBit_WithHdrFlag_UsesScRgbBranch()
    {
        // 核心回归：位深 10 + hdr=true 必须走线性 scRGB 分支。
        // 若只看 bitDepth，线性值会被当成 gamma 编码值再解一次 gamma——正是本类要消除的错色。
        var sample = new PixelSample(1f, 1f, 1f, 1f, 10);

        var withFlag = ColorNormalizer.ToLinear(sample, hdr: true);
        var withoutFlag = ColorNormalizer.ToLinear(sample, hdr: false);

        AssertClose(80f / 203f, withFlag.R);   // 1.0 scRGB → 80/203
        AssertClose(1f, withoutFlag.R);        // SrgbToLinear(1.0) = 1.0
        Assert.NotEqual(withFlag, withoutFlag);
    }

    [Fact]
    public void SixteenBit_WithoutHdrFlag_StillUsesScRgbBranch()
    {
        var linear = ColorNormalizer.ToLinear(Hdr(1f, 1f, 1f), hdr: false);

        AssertClose(80f / 203f, linear.R);
    }

    // ---------------------------------------------------------------- NaN

    [Fact]
    public void NaN_Channel_IsSanitizedToZero_BothDomains()
    {
        var sdr = ColorNormalizer.ToLinear(Sdr(float.NaN, 0.5f, 0.5f), hdr: false);
        Assert.Equal(0f, sdr.R);
        AssertClose(0.214041140f, sdr.G);      // 其余通道不受影响

        var hdr = ColorNormalizer.ToLinear(Hdr(float.NaN, 1f, 1f), hdr: true);
        Assert.Equal(0f, hdr.R);
        AssertClose(80f / 203f, hdr.G);

        // 逆变换同样不得把 NaN 传回去
        var back = ColorNormalizer.FromLinear(
            new LinearRgba(float.NaN, 0.5f, 0.5f, 1f), bitDepth: 16, hdr: true);
        Assert.Equal(0f, back.R);
    }

    // ---------------------------------------------------------------- 未知位深兜底

    [Theory]
    [InlineData(0u)]    // 内核未上报
    [InlineData(7u)]
    [InlineData(12u)]
    [InlineData(32u)]
    public void UnknownBitDepth_FallsBackToSdr(uint bitDepth)
    {
        // 兜底策略：未知位深按 SDR（sRGB EOTF）处理，结果与 8-bit 逐位一致
        var unknown = ColorNormalizer.ToLinear(Sdr(0.5f, 0.25f, 0.75f, 1f, bitDepth), hdr: false);
        var eight = ColorNormalizer.ToLinear(Sdr(0.5f, 0.25f, 0.75f, 1f, bitDepth: 8), hdr: false);

        Assert.Equal(eight, unknown);
        AssertClose(0.214041140f, unknown.R);
    }

    // ---------------------------------------------------------------- 往返一致性

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.04045f)]
    [InlineData(0.25f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    [InlineData(1.0f)]
    public void Sdr_RoundTrips_EncodedValues(float encoded)
    {
        var linear = ColorNormalizer.ToLinear(Sdr(encoded, encoded, encoded), hdr: false, PaperWhite);
        var back = ColorNormalizer.FromLinear(linear, bitDepth: 8, hdr: false, PaperWhite);

        AssertClose(encoded, back.R, 1e-4f);
        Assert.Equal(8u, back.BitDepth);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    [InlineData(2.6641f)]
    public void Hdr_RoundTrips_ScRgbValues(float scrgb)
    {
        var linear = ColorNormalizer.ToLinear(Hdr(scrgb, scrgb, scrgb), hdr: true, PaperWhite);
        var back = ColorNormalizer.FromLinear(linear, bitDepth: 16, hdr: true, PaperWhite);

        AssertClose(scrgb, back.R, 1e-4f);
        Assert.Equal(16u, back.BitDepth);
    }

    [Fact]
    public void RoundTrip_PreservesAlpha_AndBitDepth()
    {
        var linear = ColorNormalizer.ToLinear(
            Sdr(0.5f, 0.5f, 0.5f, 0.3f, bitDepth: 10), hdr: false);
        var back = ColorNormalizer.FromLinear(linear, bitDepth: 10, hdr: false, PaperWhite);

        Assert.Equal(0.3f, back.A);
        Assert.Equal(10u, back.BitDepth);
        AssertClose(0.5f, back.R, 1e-4f);
    }

    // ---------------------------------------------------------------- 接口一致性 / 参数校验

    [Fact]
    public void OutOverload_MatchesReturningOverload()
    {
        var sample = Hdr(0.8979f, 0.4021f, 2.6641f, 1f);

        ColorNormalizer.ToLinear(sample, hdr: true, out var viaOut, PaperWhite);
        var viaReturn = ColorNormalizer.ToLinear(sample, hdr: true, PaperWhite);

        Assert.Equal(viaReturn, viaOut);
    }

    [Fact]
    public void RawChannelOverload_MatchesSampleOverload()
    {
        var viaRaw = ColorNormalizer.ToLinear(
            0.5f, 0.25f, 0.75f, 1f, bitDepth: 10, hdr: false, PaperWhite);
        var viaSample = ColorNormalizer.ToLinear(
            Sdr(0.5f, 0.25f, 0.75f, 1f, bitDepth: 10), hdr: false, PaperWhite);

        Assert.Equal(viaSample, viaRaw);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidPaperWhite_Throws_OnHdrBranch(float paperWhiteNits)
    {
        // 0/负数会产出 Inf 或反号的缩放因子；NaN 会一路污染差异值——HDR 分支必须立刻报错
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ColorNormalizer.ToLinear(Hdr(0.5f, 0.5f, 0.5f), hdr: true, paperWhiteNits));

        var linear = new LinearRgba(0.5f, 0.5f, 0.5f, 1f);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ColorNormalizer.FromLinear(linear, bitDepth: 16, hdr: true, paperWhiteNits));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidPaperWhite_IsIgnored_OnSdrBranch(float paperWhiteNits)
    {
        // SDR 分支根本不用参考白 ⇒ 不该在逐像素热路径上为它抛异常（结果与传 203 完全一致）
        var sdr = ColorNormalizer.ToLinear(Sdr(0.5f, 0.5f, 0.5f), hdr: false, paperWhiteNits);
        AssertClose(0.214041140f, sdr.R);

        // 逆变换：LinearToSrgb(0.5) = 1.055 * 0.5^(1/2.4) - 0.055 = 0.735356983
        var back = ColorNormalizer.FromLinear(
            new LinearRgba(0.5f, 0.5f, 0.5f, 1f), bitDepth: 8, hdr: false, paperWhiteNits);
        AssertClose(0.735356983f, back.R, 1e-4f);
    }

    [Fact]
    public void DefaultPaperWhite_IsTwoHundredThreeNits()
        => Assert.Equal(203f, ColorNormalizer.DefaultPaperWhiteNits);

    // ── ToDisplay8Bit：8-bit 显示码值（docs/45 P0-4 的接线判据）────────────────
    //
    // 期望值一律独立手算（float32 语义），不照抄实现：
    //   SDR：回读本就是 gamma 编码 ⇒ 直接量化，round(v*255) 后钳到 0..255。
    //   HDR：v → 线性（× 80/203）→ sRGB OETF → ×255。例 v=1.0：
    //        1.0*80/203 = 0.39408866；1.055*0.39408866^(1/2.4)-0.055 = 0.6606…
    //        ×255 = 168.47 → 168。

    [Theory]
    [InlineData(0.0f, 0)]
    [InlineData(0.25f, 64)]
    [InlineData(0.5f, 128)]
    [InlineData(1.0f, 255)]
    public void Display8Bit_Sdr_IsDirectQuantize(float v, int expected)
    {
        ColorNormalizer.ToDisplay8Bit(v, v, v, 1f, bitDepth: 8, hdr: false,
            out var r8, out var g8, out var b8, out _);
        Assert.Equal(expected, r8);
        Assert.Equal(expected, g8);
        Assert.Equal(expected, b8);
    }

    [Theory]
    // 旧实现 v*255 会给出 64 / 128 / 255 / 255，与这里的期望相差 24 / 5 / 87 / 25
    // ⇒ 断言对"HDR 真的走了线性光口径"这件事有牙齿，不是恒等式复述。
    [InlineData(0.25f, 88)]
    [InlineData(0.5f, 123)]
    [InlineData(1.0f, 168)]
    [InlineData(2.0f, 230)]
    public void Display8Bit_Hdr_MapsViaLinearLight(float v, int expected)
    {
        ColorNormalizer.ToDisplay8Bit(v, v, v, 1f, bitDepth: 16, hdr: true,
            out var r8, out _, out _, out _);
        Assert.Equal(expected, r8);
    }

    [Fact]
    public void Display8Bit_Hdr_HighlightSaturates_ButLinearDomainKeepsIt()
    {
        // 8-bit 显示饱和到 255 只是显示上限；线性域必须仍保留 >1 的高光供跨路比较
        ColorNormalizer.ToDisplay8Bit(2.6641f, 2.6641f, 2.6641f, 1f,
            bitDepth: 16, hdr: true, out var r8, out _, out _, out _);
        Assert.Equal(255, r8);

        var lin = ColorNormalizer.ToLinear(2.6641f, 0f, 0f, 1f, bitDepth: 16, hdr: true);
        Assert.True(lin.R > 1f, "线性域不得截断 HDR 高光");
    }

    [Fact]
    public void Display8Bit_ClampsInsteadOfWrapping_AndMapsNaNToZero()
    {
        ColorNormalizer.ToDisplay8Bit(1.5f, -0.1f, 0f, 1f, bitDepth: 8, hdr: false,
            out var r8, out var g8, out var b8, out _);
        Assert.Equal(255, r8);  // 上溢钳到 255（不环绕成黑）
        Assert.Equal(0, g8);    // 下溢钳到 0（不环绕成白）
        Assert.Equal(0, b8);

        ColorNormalizer.ToDisplay8Bit(float.NaN, 0f, 0f, 1f, bitDepth: 8, hdr: false,
            out var nr, out _, out _, out _);
        Assert.Equal(0, nr);    // 无 NaN 契约
    }

    [Fact]
    public void Display8Bit_SdrPath_IsBitIdenticalToLegacy_V255()
    {
        // 回归护栏：SDR 下必须与旧的 v*255 口径逐值一致（实测 0..255 全 256 级零偏差），
        // 否则就等于把原本正确的 SDR 显示改坏了。
        for (var i = 0; i <= 255; i++)
        {
            var v = i / 255f;
            ColorNormalizer.ToDisplay8Bit(v, v, v, 1f, bitDepth: 8, hdr: false,
                out var r8, out _, out _, out _);
            var raw = (int)Math.Round(v * 255f);
            var legacy = raw < 0 ? 0 : raw > 255 ? 255 : raw;
            Assert.Equal(legacy, r8);
        }
    }

    [Fact]
    public void Display8Bit_PaperWhiteAffectsHdrMapping()
    {
        // 参考白越高，同一 scRGB 值相对越暗：203 nits → 168；100 nits → 231
        ColorNormalizer.ToDisplay8Bit(1f, 1f, 1f, 1f, bitDepth: 16, hdr: true,
            out var at203, out _, out _, out _, 203f);
        ColorNormalizer.ToDisplay8Bit(1f, 1f, 1f, 1f, bitDepth: 16, hdr: true,
            out var at100, out _, out _, out _, 100f);
        Assert.Equal(168, at203);
        Assert.Equal(231, at100);
        Assert.True(at100 > at203);
    }
}
