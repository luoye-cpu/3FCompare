using System.ComponentModel;
using _3FCompare.App;

namespace _3FCompare.Localization;

/// <summary>本地化绑定源。
/// XAML 用 <c>{loc:Loc Key}</c> 或 <c>{Binding [Key], Source={x:Static loc:Loc.I}}</c>；
/// 语言切换时触发 Item[] 通知，全部绑定自动刷新——替代 WinForms 的 ~100 键 ApplyLanguage 手动链。</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    private Loc()
    {
        LanguageManager.LanguageChanged += (_, _) => Refresh();
    }

    /// <summary>索引器：key → 当前语言文本（缺失键返回 key 本身，与 WinForms T() 一致）。</summary>
    public string this[string key] => LanguageManager.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>通知所有索引器绑定刷新（语言切换后调用）。</summary>
    public void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}
