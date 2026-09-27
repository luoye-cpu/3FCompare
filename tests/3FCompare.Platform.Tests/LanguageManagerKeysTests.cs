using _3FCompare.App;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// <see cref="LanguageManager.FindMissingKeys"/> / <c>T</c> / <c>Tf</c> 的<b>缺键契约</b>
/// （docs/41 §4.5 #3）。
///
/// <para><b>为什么必须测</b>：缺键是本项目唯一"不报错的缺陷"——<c>T()</c> 不抛异常、
/// 不打日志，只是把 key 原样返回，界面上直接出现 <c>Status_ColorModeUnified</c> 这种裸键名
/// （docs/41 #19 的原始缺陷就是这么漏出去的）。所以"两张表键集合一致"这条必须常驻守卫。</para>
///
/// <para><b>与 LocalizationTests 的分工</b>：那边测的是<b>状态栏拼装</b>（BuildStatusInfoLine
/// 的中英文案）；本文件测的是 <b>资源查找原语</b>（缺键返回什么、占位符是否被替换）。</para>
///
/// <para><b>期望值来源（独立推算）</b>：① <c>FindMissingKeys</c> 期望空集合 —— 契约即
/// "无差异"；② 未知键的期望值是测试自己传进去的那个<b>字面量键名</b>（不是从语言表读的）；
/// ③ <c>Tf</c> 的期望值是人工把语言表里的格式串（如 <c>"{0}模式 | 路数 {1}/9"</c>）
/// 与实参代入后手算出的字面量，且只用整数/整数值参数，规避区域设置差异。</para>
///
/// <para><b>为什么整类串行化</b>：<see cref="LanguageManager"/> 的语言是<b>静态可变状态</b>，
/// 而本文件必须切换语言才能断言两套文案。xunit 默认让不同测试类并行 ⇒ 与 LocalizationTests
/// 同时切语言会互相踩（表现为随机判红）。用 <c>DisableParallelization</c> 的集合把本类
/// 单独跑，是<b>不改动既有测试文件</b>的最小修法。</para>
/// </summary>
[Collection(LanguageStateCollection.Name)]
public class LanguageManagerKeysTests
{
    /// <summary>中文表与英文表的键集合必须完全一致 —— 任何"只加了一张表"的新键都在此判红。
    /// 期望空列表即契约本身。</summary>
    [Fact]
    public void 两张语言表键集合一致时返回空列表()
    {
        var missing = LanguageManager.FindMissingKeys();

        Assert.Empty(missing);
    }

    /// <summary>未登记的键由 <c>T</c> 原样返回（两种语言下都是），且不得抛异常。
    /// 期望值就是本用例自己构造的键名字面量。</summary>
    [Fact]
    public void 未登记的键由T原样返回()
    {
        const string unknownKey = "This_Key_Does_Not_Exist_3fc";

        var saved = LanguageManager.CurrentLanguage;
        try
        {
            foreach (var lang in new[] { 0, 1 })
            {
                LanguageManager.SetLanguage(lang);
                Assert.Equal(unknownKey, LanguageManager.T(unknownKey));
            }
        }
        finally
        {
            LanguageManager.SetLanguage(saved);
        }
    }

    /// <summary><c>Tf</c> 必须替换全部占位符。期望值 = 语言表格式串 + 实参的人工代入结果：
    /// zh <c>"{0}模式 | 路数 {1}/9"</c> 代入 ("网格", 4) ⇒ <c>"网格模式 | 路数 4/9"</c>；
    /// zh <c>" | {0} 路失败"</c> 代入 4 ⇒ <c>" | 4 路失败"</c>；
    /// en <c>"{0} mode | {1}/9 routes"</c> 代入 ("Grid", 4) ⇒ <c>"Grid mode | 4/9 routes"</c>。</summary>
    [Fact]
    public void Tf替换全部占位符()
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            LanguageManager.SetLanguage(0);
            Assert.Equal("网格模式 | 路数 4/9", LanguageManager.Tf("Status_ModeRoutesFmt", "网格", 4));
            Assert.Equal(" | 4 路失败", LanguageManager.Tf("Status_FailedRoutesFmt", 4));

            LanguageManager.SetLanguage(1);
            Assert.Equal("Grid mode | 4/9 routes", LanguageManager.Tf("Status_ModeRoutesFmt", "Grid", 4));
            Assert.Equal(" | 4 route(s) failed", LanguageManager.Tf("Status_FailedRoutesFmt", 4));
        }
        finally
        {
            LanguageManager.SetLanguage(saved);
        }
    }

    /// <summary>数值占位符按格式说明符渲染：zh <c>" | {0}: {1}帧/{2:0.#}秒"</c>
    /// 代入 ("步进", 1, 1.0) ⇒ <c>" | 步进: 1帧/1秒"</c>。
    /// 参数刻意只取整数值，避免不同区域设置的小数点差异污染期望值。</summary>
    [Fact]
    public void Tf按格式说明符渲染数值占位符()
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            LanguageManager.SetLanguage(0);
            Assert.Equal(" | 步进: 1帧/1秒", LanguageManager.Tf("Status_StepsFmt", "步进", 1, 1.0));

            LanguageManager.SetLanguage(1);
            Assert.Equal(" | Step: 1 frames/1s", LanguageManager.Tf("Status_StepsFmt", "Step", 1, 1.0));
        }
        finally
        {
            LanguageManager.SetLanguage(saved);
        }
    }

    /// <summary>键缺失时 <c>Tf</c> 返回键名本身（<c>T</c> 已返回键名，其中无占位符 ⇒
    /// <c>string.Format</c> 原样输出）。期望值 = 传入的字面量键名。</summary>
    [Fact]
    public void 键缺失时Tf返回键名本身()
    {
        const string unknownKey = "No_Such_Format_Key_3fc";

        Assert.Equal(unknownKey, LanguageManager.Tf(unknownKey, 1, 2));
    }
}

/// <summary>把语言相关的用例串成"不与其他集合并行"的一组，见
/// <see cref="LanguageManagerKeysTests"/> 的类注释。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LanguageStateCollection
{
    public const string Name = "3FCompare_LanguageState";
}
