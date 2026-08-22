using Avalonia.Data;
using Avalonia.Markup.Xaml;
using _3FCompare.App;

namespace _3FCompare.Avalonia.Localization;

/// <summary>XAML 标记扩展：<c>{loc:Loc Menu_File}</c> → 返回即时字符串。
/// AOT 安全（避免 Binding 构造函数的 RequiresUnreferencedCode 警告）。
/// 如需运行时语言切换即时刷新，控件应订阅 LanguageManager.LanguageChanged 事件
/// 在代码中刷新文本（与 WinForms ApplyLanguage 模式等价）。</summary>
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => LanguageManager.T(Key);
}
