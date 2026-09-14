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
        // 全局兜底（审查 R3）：UI 线程未处理异常 / 未观察任务异常记入 AppLog。
        // async void（如 OpenFiles）漏网异常原本会静默击穿进程，至少留下诊断痕迹。
        // 不置 e.Handled，不改变既有崩溃语义。
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
            _3FCompare.Core.Diagnostics.AppLog.Warn("Global",
                $"UI 未处理异常: {e.Exception.GetType().Name}: {e.Exception.Message}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            _3FCompare.Core.Diagnostics.AppLog.Warn("Global",
                $"未观察任务异常: {e.Exception.GetType().Name}: {e.Exception.Message}");
        };
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
