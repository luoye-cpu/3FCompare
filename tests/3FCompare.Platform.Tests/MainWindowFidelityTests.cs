using _3FCompare.Core.Backend;
using _3FCompare.Core.Imaging;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// D1 硬约束（"档位不是 <see cref="PixelFidelityTier.PixelExact"/> 时，界面上绝不能出现'像素级'字样"）
/// 与 B2（"读不到 RenderTargetInfo 时可信度必须回落到 <see cref="PixelFidelityTier.Unknown"/>，
/// 不得沿用上一路的值"）的守门测试。
///
/// <para><b>为什么可以直接调用</b>：被测的 <c>MainWindow.DescribeFidelity</c> / <c>WithoutPixelClaim</c> /
/// <c>ResolveFidelity</c> 是 <c>internal static</c> 纯函数（只做值运算与字符串拼接，不碰 Avalonia、
/// 不碰内核），而 UI 工程已对 <c>3FCompare.Platform.Tests</c> 开 <c>InternalsVisibleTo</c>
/// （见 <c>src/3FCompare/3FCompare.csproj</c>）⇒ 这里调用的是<b>生产代码本身</b>，
/// 不是测试里重写的一份等价逻辑，因此这些断言有真实鉴别力。
/// （早先版本因为误以为"UI 工程未开 InternalsVisibleTo"而改用反射，现已去掉：
/// 反射会把方法名变成字符串、改名/改签名只在运行时才炸，编译器帮不上忙。）</para>
///
/// <para><b>覆盖边界（诚实说明）</b>：本文件钉住的是"规则"本身，即
/// <c>ResolveFidelity(readOk: false, …) ⇒ Unknown</c>。调用点是否真的在 <c>else</c> 分支里调用了它
/// （B2 的原始缺陷是"没有 else 分支"）需要活的窗口与会话，无法在单测中覆盖，
/// 只能由"<c>ResolveFidelity</c> 是唯一生产者"这一结构来保证。</para>
/// </summary>
public class MainWindowFidelityTests
{
    // ─────────────────────────── 生产代码入口 ───────────────────────────

    private static PixelFidelityResult ResolveFidelity(bool readOk, RenderTargetInfo rt, EngineMediaInfo? media)
        => global::_3FCompare.MainWindow.ResolveFidelity(readOk, rt, media);

    private static string DescribeFidelity(PixelFidelityResult f)
        => global::_3FCompare.MainWindow.DescribeFidelity(f);

    private static string WithoutPixelClaim(string? text)
        => global::_3FCompare.MainWindow.WithoutPixelClaim(text!);

    private static RenderTargetInfo MakeRt(uint destW, uint destH)
        => new(SwapWidth: destW, SwapHeight: destH,
               ClientWidth: destW, ClientHeight: destH,
               DestX: 0, DestY: 0, DestWidth: destW, DestHeight: destH,
               OutputBitDepth: 8, Hdr: false);

    private static EngineMediaInfo MakeMedia(int width, int height)
        => new() { Path = "t.mp4", Codec = "h264", VideoWidth = width, VideoHeight = height };

    // ─────────────────── D1：非 PixelExact 档位不得出现"像素级" ───────────────────

    [Theory]
    // destW, destH, srcW, srcH, 期望档位
    [InlineData(3840, 2160, 1920, 1080, PixelFidelityTier.IntegerScaled)] // 2× 整数倍放大
    [InlineData(5760, 3240, 1920, 1080, PixelFidelityTier.IntegerScaled)] // 3× 整数倍放大
    [InlineData(2560, 1440, 1920, 1080, PixelFidelityTier.Interpolated)]  // 1.33× 非整数倍放大
    [InlineData(1280, 720, 1920, 1080, PixelFidelityTier.Interpolated)]   // 0.67× 缩小（重采样）
    [InlineData(2560, 1080, 1920, 1080, PixelFidelityTier.Interpolated)]  // 非等比缩放
    [InlineData(1921, 1080, 1920, 1080, PixelFidelityTier.Interpolated)]  // 单轴差 1px，超 0.5px 容差
    public void DescribeFidelity_NonPixelExactTier_NeverShowsPixelExactWording(
        int destW, int destH, int srcW, int srcH, PixelFidelityTier expectedTier)
    {
        var fidelity = PixelFidelity.Evaluate(destW, destH, srcW, srcH);

        // 先钉死档位：若用例实际落进了 PixelExact，下面的断言会变成真空转（"假覆盖"）。
        Assert.Equal(expectedTier, fidelity.Tier);
        Assert.False(fidelity.CanClaimPixelExact);

        var text = DescribeFidelity(fidelity);

        Assert.NotEmpty(text);
        // D1 硬约束：字面不得出现该词——包括源码里那种"，非像素级"的否定式写法。
        Assert.DoesNotContain("像素级", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFidelity_PixelExactTier_IsTheOnlyTierAllowedToClaimPixelExact()
    {
        var fidelity = PixelFidelity.Evaluate(1920, 1080, 1920, 1080);
        Assert.Equal(PixelFidelityTier.PixelExact, fidelity.Tier);

        var text = DescribeFidelity(fidelity);

        // 反向钉住：防止有人靠"全局删掉这个词"让上面那组用例假通过（那样就丢了 1:1 的正当宣称）。
        Assert.Contains("像素级", text, StringComparison.Ordinal);
    }

    // ─────────────────── D1：WithoutPixelClaim 的直接覆盖 ───────────────────

    [Theory]
    [InlineData("1:1 原生分辨率，可像素级比对")]                          // PixelExact 文案（异常入参，兜底也要清掉）
    [InlineData("已放大 2.0×（整数倍插值），非像素级")]                    // IntegerScaled
    [InlineData("已放大 1.33×（插值），非像素级")]                         // Interpolated 放大
    [InlineData("已缩小 0.67×（重采样），非像素级")]                       // Interpolated 缩小
    [InlineData("非等比缩放 X 1.33× / Y 1.00×（插值），非像素级")]         // Interpolated 非等比
    [InlineData("像素级像素级像素级")]                                     // 连续出现：兜底替换必须全量生效
    [InlineData("前缀像素级后缀")]                                         // 词在中间
    public void WithoutPixelClaim_RemovesEveryOccurrence(string input)
    {
        var text = WithoutPixelClaim(input);

        Assert.DoesNotContain("像素级", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithoutPixelClaim_NullOrEmpty_ReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, WithoutPixelClaim(input));
    }

    [Fact]
    public void WithoutPixelClaim_DoesNotTouchUnrelatedText()
    {
        const string input = "已放大 1.33×（插值），非像素级";
        // 否定后缀整段移除，而不是只删"像素级"三个字——后者会留下"，非"这种断句。
        Assert.Equal("已放大 1.33×（插值）", WithoutPixelClaim(input));
    }

    // ─────────────────── B2：读取失败必须回落到 Unknown ───────────────────

    [Fact]
    public void ResolveFidelity_ReadFailed_IsUnknown_EvenWhenArgsDescribeAPixelExactLane()
    {
        // 入参刻意给一条"看起来就是像素级"的路（1920×1080 → 1920×1080）。
        // 若实现写成"读失败就沿用上次的值"（B2 的原始缺陷），这里必然返回 PixelExact。
        var rt = MakeRt(1920, 1080);
        var media = MakeMedia(1920, 1080);

        var fidelity = ResolveFidelity(readOk: false, rt, media);

        Assert.Equal(PixelFidelityTier.Unknown, fidelity.Tier);
        Assert.False(fidelity.IsUsable);
        Assert.False(fidelity.CanClaimPixelExact);
        // 无 NaN 契约：不可用时比值记 0，绝不把上一路的数字留下来
        Assert.Equal(0d, fidelity.RatioX);
        Assert.Equal(0d, fidelity.RatioY);
    }

    [Fact]
    public void ResolveFidelity_ReadOkButNoMedia_IsUnknown()
    {
        // 源分辨率未知 ⇒ 无从判定。不得因为 dest 恰好是 1920×1080 就猜它是 1:1。
        var fidelity = ResolveFidelity(readOk: true, MakeRt(1920, 1080), media: null);

        Assert.Equal(PixelFidelityTier.Unknown, fidelity.Tier);
    }

    [Fact]
    public void ResolveFidelity_ReadOk_DelegatesToPixelFidelity()
    {
        var rt = MakeRt(3840, 2160);
        var fidelity = ResolveFidelity(readOk: true, rt, MakeMedia(1920, 1080));

        Assert.Equal(PixelFidelityTier.IntegerScaled, fidelity.Tier);
        Assert.Equal(PixelFidelity.FromRenderTarget(rt, 1920, 1080), fidelity);
    }
}
