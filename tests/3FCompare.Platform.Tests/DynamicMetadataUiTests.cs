using _3FCompare.Core.Backend;
using _3FCompare.Localization;

namespace _3FCompare.Platform.Tests;

/// <summary>动态元数据注入的 UI 侧纯函数（媒体信息面板的来源文案 + 状态栏注入结果文案）。
///
/// <para><b>为什么能直接调用 internal static</b>：UI 工程已通过
/// <c>InternalsVisibleTo("3FCompare.Platform.Tests")</c> 对本工程开放（3FCompare.csproj），
/// 而这两个函数都只做"读枚举/读整数 + 查语言表"，不碰 Avalonia 控件、不碰内核，
/// 因此本工程不必启动 Avalonia 应用（全工程零 <c>AppBuilder</c> 引用，见既有用例）——
/// 与 <c>MainWindowFidelityTests</c> 覆盖 <c>MainWindow.ResolveFidelity</c> 是同一手法。</para>
/// </summary>
public class DynamicMetadataUiTests
{
    private static string SourceText(DynamicMetadataSource? source)
        => global::_3FCompare.Panels.MediaInfoPanel.FormatDynamicMetadataSource(source);

    [Fact]
    public void 来源为位流时显示位流文案()
    {
        var text = SourceText(DynamicMetadataSource.Bitstream);
        // 缺键时 LanguageManager.T 会原样返回 key，界面上就会露出裸键名——这条断言把它钉住。
        Assert.NotEqual("MediaInfo_DmBitstream", text);
        Assert.Equal(LanguageManager.T("MediaInfo_DmBitstream"), text);
    }

    [Fact]
    public void 来源为注入时显示注入文案()
    {
        var text = SourceText(DynamicMetadataSource.Injected);
        Assert.NotEqual("MediaInfo_DmInjected", text);
        Assert.Equal(LanguageManager.T("MediaInfo_DmInjected"), text);
    }

    /// <summary>null 的语义是"内核没有 FFF3FP_GetHdrMetadataSource 这个导出"（旧版 DLL），
    /// 不是"当前没有元数据"——后者内核会明确回 Bitstream。文案必须区分这两者。</summary>
    [Fact]
    public void 来源为空时显示内核不支持且不同于另外两种()
    {
        var text = SourceText(null);
        Assert.NotEqual("MediaInfo_DmUnsupported", text);
        Assert.Equal(LanguageManager.T("MediaInfo_DmUnsupported"), text);
        Assert.NotEqual(SourceText(DynamicMetadataSource.Bitstream), text);
        Assert.NotEqual(SourceText(DynamicMetadataSource.Injected), text);
    }

    [Fact]
    public void 三态文案两两不同()
    {
        var all = new[]
        {
            SourceText(DynamicMetadataSource.Bitstream),
            SourceText(DynamicMetadataSource.Injected),
            SourceText(null),
        };
        Assert.Equal(3, all.Distinct(StringComparer.Ordinal).Count());
    }

    private static string Result(int applied, int total, int entries)
        => global::_3FCompare.MainWindow.DescribeInjectionResult(applied, total, entries);

    /// <summary>全部路成功：必须同时说清"多少条"和"几分之几路"，缺一半用户会误解。</summary>
    [Fact]
    public void 注入全成功时文案带条目数与全路数()
    {
        var text = Result(3, 3, 24);
        Assert.Contains("24", text);
        Assert.Contains("3/3", text);
    }

    [Fact]
    public void 注入部分成功时文案带实际成功路数()
    {
        var text = Result(1, 3, 24);
        Assert.Contains("1/3", text);
        Assert.DoesNotContain("3/3", text);
    }

    /// <summary>全失败最常见的原因是"旧内核没有这个导出"，文案必须点出这一点，
    /// 否则用户只会看到一个干巴巴的失败。措辞在 Status_DmLoadedNone，这里只钉结构。</summary>
    [Fact]
    public void 注入全失败时文案带零成功路数()
    {
        var text = Result(0, 3, 24);
        Assert.Contains("0/3", text);
    }

    [Fact]
    public void 三种结果文案两两不同()
    {
        var all = new[] { Result(3, 3, 24), Result(1, 3, 24), Result(0, 3, 24) };
        Assert.Equal(3, all.Distinct(StringComparer.Ordinal).Count());
    }
}
