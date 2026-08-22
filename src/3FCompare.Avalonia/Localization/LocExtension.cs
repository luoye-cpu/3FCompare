using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace _3FCompare.Avalonia.Localization;

/// <summary>XAML 标记扩展：<c>{loc:Loc Menu_File}</c> → 绑定 Loc.I[Menu_File]。
/// 返回 Binding 而非即时字符串，语言切换时随 Loc.Refresh() 自动更新。</summary>
public class LocExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = Loc.I,
            Mode = BindingMode.OneWay,
        };
}
