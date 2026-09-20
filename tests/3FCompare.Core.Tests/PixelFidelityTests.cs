using System.Globalization;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Imaging;
using Xunit;

namespace _3FCompare.Core.Tests;

/// <summary>
/// PixelFidelity（dest 矩形 vs 源分辨率 → 能否宣称像素级）测试。
///
/// <para><b>期望值一律独立推导</b>，不照抄被测实现：每个用例的档位由"缩放比是否为 1 / 是否为同一整数倍"
/// 手工判定，缩放比由 dest÷source 手算后写死（如 1920/1280 = 1.5、1920/960 = 2.0）。</para>
///
/// <para><b>核心事实</b>（已核实）：内核放大恒为双线性，任何缩放都会插值，
/// 故只有 1:1 才是 <see cref="PixelFidelityTier.PixelExact"/>；整数倍放大可对齐位置但值已插值，
/// 归 <see cref="PixelFidelityTier.IntegerScaled"/>；缩小同样是重采样，归
/// <see cref="PixelFidelityTier.Interpolated"/>。</para>
///
/// <para><b>判定容差</b>取「绝对 <see cref="PixelFidelity.PixelTolerance"/> 像素」与
/// 「倍数 × 源尺寸 × <see cref="PixelFidelity.RelativeTolerance"/>」的较小者，
/// 因此极小源上 2.6× 不会被误判成 3×（见本文件"极小源"一节）。</para>
/// </summary>
public sealed class PixelFidelityTests
{
    /// <summary>与实现约定的 1:1 绝对容差（像素）；这里按规格独立取 0.5，不引用被测常量。</summary>
    private const double Tolerance = 0.5;

    /// <summary>把当前线程的区域固定为不变区域。
    /// <para>生产代码用当前区域格式化倍数（<c>{factor:0.0}</c>）：在 <c>,</c> 作小数分隔符的机器上
    /// 会产出 <c>"2,0"</c>，硬断言字面量 <c>"2.0"</c> 就会误报失败。这里显式固定区域，
    /// 让断言与机器设置无关。</para></summary>
    private static IDisposable InvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        return new CultureRestore(previous);
    }

    private sealed class CultureRestore(CultureInfo previous) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentCulture = previous;
    }

    private static void AssertRatio(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) <= 1e-9,
            $"期望缩放比 {expected:R}，实际 {actual:R}");

    private static PixelFidelityResult Rt(uint destW, uint destH, int srcW, int srcH)
        => PixelFidelity.FromRenderTarget(
            new RenderTargetInfo(
                SwapWidth: destW, SwapHeight: destH,
                ClientWidth: destW, ClientHeight: destH,
                DestX: 0, DestY: 0, DestWidth: destW, DestHeight: destH,
                OutputBitDepth: 8, Hdr: false),
            srcW, srcH);

    // ------------------------------------------------------------ 1:1 → PixelExact

    [Theory]
    [InlineData(1920, 1080)]   // 1920/1920 = 1.0，1080/1080 = 1.0
    [InlineData(3840, 2160)]   // 3840/3840 = 1.0，2160/2160 = 1.0
    [InlineData(1280, 720)]    // 1280/1280 = 1.0
    [InlineData(1, 1)]         // 退化但合法的 1×1 源
    public void OneToOne_IsPixelExact(int w, int h)
    {
        var r = PixelFidelity.Evaluate(w, h, w, h);

        Assert.Equal(PixelFidelityTier.PixelExact, r.Tier);
        Assert.True(r.CanClaimPixelExact);
        Assert.True(r.IsUsable);
        AssertRatio(1d, r.RatioX);
        AssertRatio(1d, r.RatioY);
    }

    // ------------------------------------------------------------ 0.5 像素容差边界

    [Theory]
    [InlineData(1920.5, 1080.0)]   // |1920.5-1920| = 0.5 ≤ 0.5 → 仍 1:1
    [InlineData(1919.5, 1080.0)]   // |1919.5-1920| = 0.5 ≤ 0.5 → 仍 1:1
    [InlineData(1920.0, 1080.5)]   // 纵向 0.5 → 仍 1:1
    [InlineData(1920.0, 1079.5)]   // 纵向 -0.5 → 仍 1:1
    public void WithinHalfPixel_IsStillPixelExact(double destW, double destH)
    {
        var r = PixelFidelity.Evaluate(destW, destH, 1920, 1080);

        Assert.Equal(PixelFidelityTier.PixelExact, r.Tier);
        Assert.True(r.CanClaimPixelExact);
    }

    [Theory]
    [InlineData(1920.6, 1080.0)]   // |1920.6-1920| = 0.6 > 0.5 → 不再是 1:1
    [InlineData(1920.0, 1080.51)]  // 0.51 > 0.5 → 不再是 1:1
    [InlineData(1921.0, 1080.0)]   // 整 1 像素偏差 → 不再是 1:1
    [InlineData(1918.0, 1080.0)]   // 差 2 像素 → 不再是 1:1
    public void BeyondHalfPixel_IsNotPixelExact(double destW, double destH)
    {
        var r = PixelFidelity.Evaluate(destW, destH, 1920, 1080);

        Assert.NotEqual(PixelFidelityTier.PixelExact, r.Tier);
        Assert.False(r.CanClaimPixelExact);
    }

    [Fact]
    public void NormalSize_AbsoluteToleranceStillGoverns()
    {
        // 源 ≥ 50px 时相对上限不起作用（1920×1% = 19.2 ≫ 0.5），行为与引入相对容差前逐位一致：
        // 恰好 0.5px 仍算 1:1，再多一点点就不算。
        Assert.Equal(PixelFidelityTier.PixelExact,
            PixelFidelity.Evaluate(1920 + Tolerance, 1080, 1920, 1080).Tier);
        Assert.NotEqual(PixelFidelityTier.PixelExact,
            PixelFidelity.Evaluate(1920 + Tolerance + 1e-6, 1080, 1920, 1080).Tier);
    }

    // ------------------------------------------- 极小源：绝对容差不得退化成"倍数同级"

    [Theory]
    // 真实倍率 2.6×：旧规则 |2.6 − 3×1| = 0.4 ≤ 0.5 被判 IntegerScaled 且文案写 "3.0×"
    [InlineData(2.6, 2.6, 1, 1)]
    [InlineData(3.4, 3.4, 1, 1)]   // |3.4 − 3| = 0.4
    [InlineData(5.4, 5.4, 1, 1)]   // |5.4 − 5| = 0.4
    [InlineData(1.4, 1.4, 1, 1)]   // 旧规则会落进 1:1 容差（|1.4 − 1| = 0.4 ≤ 0.5）
    [InlineData(6.4, 6.4, 2, 2)]   // 真实 3.2×，源 2px：旧规则 |6.4 − 3×2| = 0.4 ≤ 0.5 会判成 3×
    public void TinySource_NonIntegerScale_IsInterpolated(double destW, double destH, int srcW, int srcH)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.Interpolated, r.Tier);
        Assert.False(r.CanClaimPixelExact);
    }

    [Theory]
    [InlineData(2.0, 2.0, 1, 1, 2)]
    [InlineData(3.0, 3.0, 1, 1, 3)]
    [InlineData(10.0, 10.0, 2, 2, 5)]
    [InlineData(4.0, 4.0, 2, 2, 2)]
    public void TinySource_ExactIntegerScale_IsStillIntegerScaled(
        double destW, double destH, int srcW, int srcH, double expectedRatio)
    {
        // 收紧相对容差不能把"真·整数倍"也一起否掉
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.IntegerScaled, r.Tier);
        AssertRatio(expectedRatio, r.RatioX);
        AssertRatio(expectedRatio, r.RatioY);
    }

    [Fact]
    public void TinySource_NonIntegerUpscale_DescriptionShowsTrueRatio()
    {
        // 回归：文案过去由 Math.Round(2.6) = 3 得来，会写 "3.0×"，与真实 2.6× 不符
        using var _ = InvariantCulture();
        var r = PixelFidelity.Evaluate(2.6, 2.6, 1, 1);

        Assert.Equal(PixelFidelityTier.Interpolated, r.Tier);
        Assert.Contains("2.60", r.Description);
        Assert.DoesNotContain("3.0", r.Description);
    }

    // ------------------------------------------------------------ 整数倍放大 → IntegerScaled

    [Theory]
    [InlineData(960, 540, 1920, 1080, 2)]    // 1920/960 = 2.0，1080/540 = 2.0
    [InlineData(640, 360, 1920, 1080, 3)]    // 1920/640 = 3.0，1080/360 = 3.0
    [InlineData(480, 270, 1920, 1080, 4)]    // 1920/480 = 4.0，1080/270 = 4.0
    [InlineData(100, 100, 200, 200, 2)]      // 200/100 = 2.0
    [InlineData(50, 50, 150, 150, 3)]        // 150/50 = 3.0
    public void IntegerUpscale_IsIntegerScaled(
        int srcW, int srcH, int destW, int destH, int factor)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.IntegerScaled, r.Tier);
        Assert.False(r.CanClaimPixelExact);   // 位置可对齐但取样已插值，不能宣称像素级
        Assert.True(r.IsUsable);
        AssertRatio(factor, r.RatioX);
        AssertRatio(factor, r.RatioY);
    }

    // ------------------------------------------------------------ 非整数倍 → Interpolated

    [Theory]
    [InlineData(1280, 720, 1920, 1080, 1.5)]     // 1920/1280 = 1.5
    [InlineData(640, 360, 1440, 810, 2.25)]      // 1440/640 = 2.25
    [InlineData(1920, 1080, 2560, 1440, 4d / 3d)] // 2560/1920 = 1.333…
    [InlineData(1000, 1000, 1500, 1500, 1.5)]    // 1500/1000 = 1.5
    public void NonIntegerUpscale_IsInterpolated(
        int srcW, int srcH, int destW, int destH, double expectedRatio)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.Interpolated, r.Tier);
        Assert.False(r.CanClaimPixelExact);
        AssertRatio(expectedRatio, r.RatioX);
        AssertRatio(expectedRatio, r.RatioY);
    }

    // ------------------------------------------------------------ 横纵比不一致 → Interpolated

    [Theory]
    [InlineData(100, 100, 200, 150, 2.0, 1.5)]   // X=2.0，Y=1.5
    [InlineData(1920, 1080, 3840, 1080, 2.0, 1.0)] // X=2.0，Y=1.0（都"整数"但不相等）
    [InlineData(100, 100, 300, 200, 3.0, 2.0)]   // X=3.0，Y=2.0，各向异性
    public void AnisotropicScale_IsInterpolated(
        int srcW, int srcH, int destW, int destH, double expectedX, double expectedY)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.Interpolated, r.Tier);
        Assert.False(r.CanClaimPixelExact);
        AssertRatio(expectedX, r.RatioX);
        AssertRatio(expectedY, r.RatioY);
    }

    // ------------------------------------------------------------ 缩小 → Interpolated（缩小也是重采样）

    [Theory]
    [InlineData(1920, 1080, 960, 540, 0.5)]      // 960/1920 = 0.5
    [InlineData(1920, 1080, 1280, 720, 2d / 3d)] // 1280/1920 = 0.666…
    [InlineData(100, 100, 50, 50, 0.5)]          // 干净的 1/2 倍率也不得算 IntegerScaled
    [InlineData(4000, 3000, 1000, 750, 0.25)]    // 1000/4000 = 0.25
    public void Downscale_IsInterpolated(
        int srcW, int srcH, int destW, int destH, double expectedRatio)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.Interpolated, r.Tier);
        Assert.False(r.CanClaimPixelExact);
        AssertRatio(expectedRatio, r.RatioX);
    }

    // ------------------------------------------------------------ 边界：0 / 负数 / NaN / Inf → Unknown

    [Theory]
    [InlineData(0, 1080, 1920, 1080)]      // dest 宽为 0
    [InlineData(1920, 0, 1920, 1080)]      // dest 高为 0
    [InlineData(1920, 1080, 0, 1080)]      // 源宽为 0
    [InlineData(1920, 1080, 1920, 0)]      // 源高为 0
    [InlineData(-1920, 1080, 1920, 1080)]  // dest 为负
    [InlineData(1920, 1080, -1920, 1080)]  // 源为负
    public void ZeroOrNegative_IsUnknown(int destW, int destH, int srcW, int srcH)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.Unknown, r.Tier);
        Assert.False(r.IsUsable);
        Assert.False(r.CanClaimPixelExact);
        // 不可用时不把 NaN 写进缩放比，避免污染下游比较
        Assert.False(double.IsNaN(r.RatioX));
        Assert.False(double.IsNaN(r.RatioY));
        Assert.Equal(0d, r.RatioX);
        Assert.Equal(0d, r.RatioY);
    }

    [Theory]
    [InlineData(double.NaN, 1080d, 1920d, 1080d)]              // dest 宽 NaN
    [InlineData(1920d, double.NaN, 1920d, 1080d)]              // dest 高 NaN
    [InlineData(1920d, 1080d, double.NaN, 1080d)]              // 源宽 NaN
    [InlineData(1920d, 1080d, 1920d, double.PositiveInfinity)] // 源高 +Inf
    [InlineData(double.PositiveInfinity, 1080d, 1920d, 1080d)] // dest 宽 +Inf
    [InlineData(double.NegativeInfinity, 1080d, 1920d, 1080d)] // dest 宽 -Inf
    public void NaNOrInfinity_IsUnknown(double destW, double destH, double srcW, double srcH)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.Equal(PixelFidelityTier.Unknown, r.Tier);
        Assert.False(r.IsUsable);
        Assert.Equal(0d, r.RatioX);
        Assert.Equal(0d, r.RatioY);
        Assert.False(string.IsNullOrWhiteSpace(r.Description));
    }

    // ------------------------------------------------------------ 展示文案

    [Fact]
    public void PixelExact_Description_MentionsPixelLevel()
    {
        var r = PixelFidelity.Evaluate(1920, 1080, 1920, 1080);

        Assert.False(string.IsNullOrWhiteSpace(r.Description));
        Assert.Contains("像素级", r.Description);
        Assert.Contains("1:1", r.Description);
    }

    [Fact]
    public void IntegerScaled_Description_ShowsFactorAndDeniesPixelLevel()
    {
        using var _ = InvariantCulture();
        var r = PixelFidelity.Evaluate(1920, 1080, 960, 540);   // 2× 放大

        Assert.False(string.IsNullOrWhiteSpace(r.Description));
        Assert.Contains("非像素级", r.Description);
        Assert.Contains("2.0", r.Description);   // 档位需带上倍数信息
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]   // 1920/1280 = 1.5× 非整数倍放大
    [InlineData(1440, 810, 640, 360)]     // 1440/640 = 2.25× 非整数倍放大
    public void Interpolated_Description_IsNonEmptyAndDeniesPixelLevel(
        int destW, int destH, int srcW, int srcH)
    {
        var r = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        Assert.False(string.IsNullOrWhiteSpace(r.Description));
        Assert.Contains("非像素级", r.Description);
    }

    [Fact]
    public void Downscaled_Description_MentionsShrink()
    {
        var r = PixelFidelity.Evaluate(960, 540, 1920, 1080);   // 0.5×

        Assert.Equal(PixelFidelityTier.Interpolated, r.Tier);
        Assert.Contains("缩小", r.Description);
        Assert.Contains("非像素级", r.Description);
    }

    [Fact]
    public void Anisotropic_Description_MentionsBothAxes()
    {
        using var _ = InvariantCulture();
        var r = PixelFidelity.Evaluate(200, 150, 100, 100);   // X=2.0，Y=1.5

        Assert.Contains("非等比", r.Description);
        Assert.Contains("2.00", r.Description);
        Assert.Contains("1.50", r.Description);
    }

    [Fact]
    public void Unknown_Description_IsNonEmpty()
    {
        var r = PixelFidelity.Evaluate(0, 0, 1920, 1080);

        Assert.False(string.IsNullOrWhiteSpace(r.Description));
    }

    // ------------------------------------------------------------ RenderTargetInfo 便捷重载

    [Fact]
    public void FromRenderTarget_OneToOne_IsPixelExact()
    {
        var r = Rt(1920, 1080, 1920, 1080);

        Assert.Equal(PixelFidelityTier.PixelExact, r.Tier);
        Assert.True(r.CanClaimPixelExact);
    }

    [Fact]
    public void FromRenderTarget_IntegerUpscale_IsIntegerScaled()
    {
        // dest 3840×2160 vs 源 1920×1080 → 2× 整数倍
        var r = Rt(3840, 2160, 1920, 1080);

        Assert.Equal(PixelFidelityTier.IntegerScaled, r.Tier);
        AssertRatio(2d, r.RatioX);
        AssertRatio(2d, r.RatioY);
    }

    [Fact]
    public void FromRenderTarget_ZeroDest_IsUnknown()
    {
        // 未布局 / 未渲染：内核上报 dest = 0
        var r = Rt(0, 0, 1920, 1080);

        Assert.Equal(PixelFidelityTier.Unknown, r.Tier);
        Assert.False(r.IsUsable);
    }

    [Fact]
    public void FromRenderTarget_MatchesScalarOverload()
    {
        var viaInfo = Rt(2560, 1440, 1920, 1080);
        var viaScalars = PixelFidelity.Evaluate(2560, 1440, 1920, 1080);

        Assert.Equal(viaScalars, viaInfo);
    }

    // ------------------------------------------------------------ 结果契约

    [Fact]
    public void CanClaimPixelExact_OnlyForPixelExactTier()
    {
        Assert.True(PixelFidelity.Evaluate(1920, 1080, 1920, 1080).CanClaimPixelExact);
        Assert.False(PixelFidelity.Evaluate(1920, 1080, 960, 540).CanClaimPixelExact);
        Assert.False(PixelFidelity.Evaluate(1920, 1080, 1280, 720).CanClaimPixelExact);
        Assert.False(PixelFidelity.Evaluate(0, 0, 1920, 1080).CanClaimPixelExact);
    }

    [Fact]
    public void RatioX_IsDestOverSource()
    {
        // 1920/640 = 3.0；1080/360 = 3.0 —— 手工推导
        var r = PixelFidelity.Evaluate(1920, 1080, 640, 360);

        AssertRatio(3d, r.RatioX);
        AssertRatio(3d, r.RatioY);
    }
}
