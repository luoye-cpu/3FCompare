using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;
using _3FCompare.App;
using _3FCompare.Core.Settings;

namespace _3FCompare;

public partial class AppEntry : Application
{
    public override void Initialize()
    {
        // 主题在代码中装配：App.axaml 内嵌 Resources/Styles 会触发本机 .NET 11 预览
        // 运行时的编译 XAML 查找异常（M1 排障结论，详见 docs/07 决策记录）
        var theme = new ThemeResources();
        Resources.MergedDictionaries.Add(theme);
        Styles.Add(new FluentTheme());
        foreach (var s in ThemeResources.BuildBaseStyles(theme))
            Styles.Add(s);

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = SettingsStore.Load();
            LanguageManager.SetLanguage(settings.Language);
            var win = new MainWindow(settings);
            if (Program.AutodemoFiles is { Length: > 0 })
                win.AutoOpenFiles(Program.AutodemoFiles);
            desktop.MainWindow = win;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
