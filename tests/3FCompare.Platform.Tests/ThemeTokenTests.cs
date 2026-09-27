using Avalonia.Styling;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="ThemeTokens.Get"/> / <see cref="ThemeTokens.Value"/>（docs/41 §4.5 #4）。
///
/// <para><b>为什么必须测</b>：<see cref="ThemeTokens.All"/> 是全部颜色令牌的"单一真源"，
/// 两套主题变体字典都由它装配。两处静默失败模式：① 键名写错时 <c>Get</c> 抛
/// <c>KeyNotFoundException</c> —— 但若某天被改成返回 <c>default</c>，
/// 后续 <c>Color.Parse("")</c> 会把界面画成透明且极难定位；② <c>All</c> 里出现重复键时
/// <c>BuildIndex</c> 的后写覆盖先写，令牌<b>静默失效</b>（编译期无任何提示）。</para>
///
/// <para><b>期望值来源（独立推算）</b>：① 令牌取值（<c>#18181C</c> 等）是人工从
/// ThemeTokens.cs:42 的令牌表逐字抄下来的<b>字面量</b>，不是用 <c>Get</c>/<c>Value</c> 现算；
/// ② 变体分支规则（仅 Light 取浅色，null/Default 一律深色）来自 ThemeTokens.cs:116-117 的注释；
/// ③ 重复键检查用 <c>Distinct</c> 这一独立手段，与 <c>BuildIndex</c> 的字典行为无关。</para>
/// </summary>
public class ThemeTokenTests
{
    /// <summary>按资源键命中，且四个字段与令牌表中该行完全一致。</summary>
    [Fact]
    public void 按资源键命中令牌且字段与令牌表一致()
    {
        var token = ThemeTokens.Get("Bg");

        Assert.Equal("Surface.Background", token.Semantic);
        Assert.Equal("Bg", token.Key);
        Assert.Equal("#18181C", token.Dark);
        Assert.Equal("#F5F6F8", token.Light);
    }

    /// <summary>派生的 <c>xxxBrush</c> 键必须命中<b>同一条</b>令牌
    /// （自绘控件传的就是这种形式，见 ThemeTokens.cs:99）。</summary>
    [Fact]
    public void 派生的Brush键命中同一条令牌()
    {
        var plain = ThemeTokens.Get("Accent");
        var brush = ThemeTokens.Get("AccentBrush");

        Assert.Equal(plain, brush);           // ThemeToken 是 record struct ⇒ 逐字段相等
        Assert.Equal("Accent", brush.Key);
        Assert.Equal("Accent.Default", brush.Semantic); // 语义名取自 ThemeTokens.cs:56
    }

    /// <summary>未登记的键必须抛 <see cref="KeyNotFoundException"/>（而不是返回 default），
    /// 且消息里带上键名以便定位。</summary>
    [Fact]
    public void 未登记的键抛出KeyNotFoundException()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => ThemeTokens.Get("NoSuchToken_3fc"));

        Assert.Contains("NoSuchToken_3fc", ex.Message);
    }

    /// <summary>取值按变体分支：Light 取浅色，Dark 取深色，
    /// Default（以及"其余一切"）同样取深色 —— 本应用默认外观是深色。</summary>
    [Fact]
    public void 取值仅在Light变体下取浅色其余取深色()
    {
        Assert.Equal("#F5F6F8", ThemeTokens.Value("Bg", ThemeVariant.Light));
        Assert.Equal("#18181C", ThemeTokens.Value("Bg", ThemeVariant.Dark));
        Assert.Equal("#18181C", ThemeTokens.Value("Bg", ThemeVariant.Default));

        // 派生键走同一条分支，不得因为少了 Brush 后缀处理而抛
        Assert.Equal("#F5F6F8", ThemeTokens.Value("BgBrush", ThemeVariant.Light));
        Assert.Equal("#18181C", ThemeTokens.Value("BgBrush", ThemeVariant.Dark));
    }

    /// <summary>压在视频画面上的令牌深浅同值（ThemeTokens.cs:77-83）：
    /// 这类令牌跟着主题翻转会在浅色下把画面糊掉。期望值 = 深浅两侧均为同一字面量。</summary>
    [Fact]
    public void 叠加层令牌的深浅取值相同()
    {
        foreach (var key in new[] { "OverlayScrim", "OverlayGrid", "OverlayHalo", "OverlayRing", "OverlayGrip" })
        {
            var token = ThemeTokens.Get(key);
            Assert.Equal(token.Dark, token.Light);
        }
    }

    /// <summary><c>All</c> 无重复键（含语义名），且没有键以 <c>Brush</c> 结尾 ——
    /// 后者会与 <c>BuildIndex</c> 派生的 <c>xxxBrush</c> 键在同一个字典里互相覆盖。
    /// 这三条都是用 <c>Distinct</c>/<c>EndsWith</c> 独立判定，与索引实现无关。</summary>
    [Fact]
    public void 令牌表的键与语义名均无重复()
    {
        Assert.NotEmpty(ThemeTokens.All);

        var keys = ThemeTokens.All.Select(t => t.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());

        var semantics = ThemeTokens.All.Select(t => t.Semantic).ToArray();
        Assert.Equal(semantics.Length, semantics.Distinct(StringComparer.Ordinal).Count());

        Assert.DoesNotContain(keys, k => k.EndsWith("Brush", StringComparison.Ordinal));
    }
}
