using _3FCompare.App;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 本地化键完整性 + 状态栏主信息条双语（docs/41 #19）。
///
/// <para><b>为什么需要这个文件</b>：<c>LanguageManager.T()</c> 缺键时<b>不抛异常</b>，
/// 而是把 key 原样返回 ⇒ 英文模式下界面上直接出现 <c>Status_ModeRoutesFmt</c> 这种裸键名，
/// 或者整条状态栏仍是中文（#19 的原始缺陷）。<c>FindMissingKeys()</c> 早就写好了，
/// 但在本次修复前<b>零调用点</b>（docs/41 R9 指出过），等于没有守卫 —— 这里把它接上。</para>
///
/// <para>本工程是 tests/ 下唯一引用 UI 工程的测试，<see cref="LanguageManager"/> 属 UI 层，
/// 所以这些用例放在这里而不是 Core.Tests。</para>
/// </summary>
[Collection(LanguageStateCollection.Name)]   // 会切语言 ⇒ 必须与其余语言用例串行（2026-09-27 补：
// 本类漏了这个标记多年，一直靠"用例小、时间窗窄"侥幸不撞；新加一个会切语言的类就当场撞红，
// 表现为本类"单跑绿、整轮红"。串行化才是正解，不是把断言放宽。
public class LocalizationTests
{
    /// <summary>中文表与英文表的键集合必须完全一致。任何"只加了一张表"的新键都会在这里判红。</summary>
    [Fact]
    public void 两张语言表的键集合完全一致()
    {
        var missing = LanguageManager.FindMissingKeys();
        Assert.True(missing.Count == 0,
            "语言表键集合不一致（缺键会原样显示键名）：" + string.Join(", ", missing));
    }

    /// <summary>#19 新增的三个状态栏键在 zh / en 下都必须能取到真实文本（不能取到键名本身）。</summary>
    [Theory]
    [InlineData("Status_ModeRoutesFmt")]
    [InlineData("Status_StepsFmt")]
    [InlineData("Status_FailedRoutesFmt")]
    public void 状态栏格式键在两种语言下都存在(string key)
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            foreach (var lang in new[] { 0, 1 })
            {
                LanguageManager.SetLanguage(lang);
                var text = LanguageManager.T(key);
                Assert.NotEqual(key, text);                     // 取到键名 = 缺键
                Assert.False(string.IsNullOrWhiteSpace(text));   // 空串同样是缺陷
            }
        }
        finally
        {
            LanguageManager.SetLanguage(saved);
        }
    }

    /// <summary>英文模式下状态栏主信息条不得残留中文（#19 的验收点）。
    /// 调的是<b>生产代码本身</b>（<c>MainWindow.BuildStatusInfoLine</c>，UI 工程已对
    /// 本测试工程开 InternalsVisibleTo），不是测试里重写的一份等价逻辑 ——
    /// 所以把该方法改回硬编码中文时，这条会判红。</summary>
    [Fact]
    public void 英文模式下状态栏主信息条不含中文()
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            LanguageManager.SetLanguage(1);
            var line = BuildLine();
            Assert.DoesNotContain("路", line);
            Assert.DoesNotContain("模式", line);
            Assert.DoesNotContain("帧", line);
            Assert.DoesNotContain("秒", line);
            Assert.DoesNotContain("{", line); // 占位符必须被全部替换
        }
        finally
        {
            LanguageManager.SetLanguage(saved);
        }
    }

    /// <summary>中文模式下的分段拼接结果与修复前逐字一致（防止"修双语时改坏中文"）。</summary>
    [Fact]
    public void 中文模式下状态栏主信息条与旧文案一致()
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            LanguageManager.SetLanguage(0);
            Assert.Equal("网格模式 | 路数 4/9 | 步进: 1帧/1秒 | 2 路失败", BuildLine());
        }
        finally
        {
            LanguageManager.SetLanguage(saved);
        }
    }

    /// <summary>失败路数为 0 时不显示该段（与原实现 <c>if (failed &gt; 0)</c> 一致）。</summary>
    [Fact]
    public void 失败路数为零时不追加该段()
    {
        var saved = LanguageManager.CurrentLanguage;
        try
        {
            LanguageManager.SetLanguage(0);
            var line = global::_3FCompare.MainWindow.BuildStatusInfoLine("网格", 4, 1, 1.0, 0);
            Assert.Equal("网格模式 | 路数 4/9 | 步进: 1帧/1秒", line);
        }
        finally
        {
            LanguageManager.SetLanguage(saved);
        }
    }

    /// <summary>与生产调用点同参数：mode 取语言表里的模式名。</summary>
    private static string BuildLine()
        => global::_3FCompare.MainWindow.BuildStatusInfoLine(
            LanguageManager.T("Status_GridMode"), 4, 1, 1.0, 2);
}
