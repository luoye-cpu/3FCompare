using Avalonia.Data;
using Avalonia.Markup.Xaml;
using _3FCompare.App;

namespace _3FCompare.Localization;

/// <summary>XAML 标记扩展：<c>{loc:Loc Menu_File}</c> → 返回即时字符串。
/// AOT 安全：返回静态字符串，避免 Binding 构造函数的 RequiresUnreferencedCode 裁剪警告。
/// 语言切换需重启生效（与 WinForms 版行为一致）。</summary>
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
