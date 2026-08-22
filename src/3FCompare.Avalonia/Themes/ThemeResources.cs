using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Styling;

namespace _3FCompare.Avalonia;

/// <summary>主题资源（代码装配；对应 WinForms AppTheme.Colors/Fonts/Sizes）。
/// 注：App.axaml 内嵌 Resources/Styles 在本机 .NET 11 预览运行时触发
/// 「No precompiled XAML found for App」查找异常（M1 排障结论，见 docs/07），
/// 故主题一律经代码装配；Theme.axaml 仅作设计参考不参与编译。</summary>
public sealed class ThemeResources : global::Avalonia.Controls.ResourceDictionary
{
    public ThemeResources()
    {
        AddColor("BgColor", 24, 24, 28);
        AddColor("PanelColor", 30, 30, 36);
        AddColor("TextPrimaryColor", 255, 255, 255);
        AddColor("TextSecondaryColor", 200, 200, 210);
        AddColor("AccentColor", 255, 200, 64);
        AddColor("SuccessColor", 100, 200, 100);
        AddColor("WarningColor", 255, 180, 50);
        AddColor("ErrorColor", 255, 100, 100);
        AddColor("CanvasColor", 18, 18, 20);
        AddColor("CanvasDarkColor", 10, 10, 12);
        AddColor("ControlBgColor", 40, 40, 46);
        AddColor("ControlBgLightColor", 45, 45, 52);
        AddColor("InputBgColor", 36, 36, 42);
        AddColor("InputBgAltColor", 50, 50, 58);
        AddColor("ButtonActiveColor", 60, 90, 60);
        AddColor("ButtonSecondaryColor", 60, 60, 66);
        AddColor("BorderColor", 80, 80, 90);
        AddColor("TextMutedColor", 140, 140, 150);
        AddColor("SelectedBorderColor", 64, 160, 255);
        AddColor("UnselectedBorderColor", 60, 60, 70);
        AddColor("MarkerAColor", 255, 100, 100);
        AddColor("MarkerBColor", 100, 100, 255);

        // 派生画笔（每个 Color 键派生同名 Brush 后缀键）
        foreach (var key in Keys.ToList())
        {
            if (key is string s && this[key] is Color c)
                this[s + "Brush"] = new SolidColorBrush(c);
        }

        this["UiFont"] = new FontFamily("Microsoft YaHei UI");
        this["UiBoldFont"] = new FontFamily("Microsoft YaHei UI");
        this["MonoFont"] = new FontFamily("Consolas, Microsoft YaHei UI");

        this["ToolbarHeight"] = 44.0;
        this["TimelineHeight"] = 34.0;
        this["StatusBarHeight"] = 24.0;
        this["ToolsPanelWidth"] = 240.0;
        this["ToolsPanelMinWidth"] = 200.0;
        this["ToolsPanelMaxWidth"] = 400.0;
    }

    private void AddColor(string key, byte r, byte g, byte b) => this[key] = Color.FromArgb(255, r, g, b);

    /// <summary>全局基础样式（对应 WinForms 默认外观：暗面板菜单/上下文菜单、正文前景色）。</summary>
    public static global::Avalonia.Styling.Styles BuildBaseStyles(global::Avalonia.Controls.ResourceDictionary res) => new()
    {
        new Style(s => s.OfType<global::Avalonia.Controls.TextBlock>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.TextBlock.ForegroundProperty, res["TextPrimaryBrush"]),
                new Setter(global::Avalonia.Controls.TextBlock.FontFamilyProperty, res["UiFont"]),
            },
        },
        new Style(s => s.OfType<global::Avalonia.Controls.Menu>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.Menu.BackgroundProperty, res["PanelBrush"]),
                new Setter(global::Avalonia.Controls.Menu.ForegroundProperty, res["TextPrimaryBrush"]),
            },
        },
        new Style(s => s.OfType<global::Avalonia.Controls.MenuItem>())
        {
            Setters = { new Setter(global::Avalonia.Controls.MenuItem.ForegroundProperty, res["TextPrimaryBrush"]) },
        },
        new Style(s => s.OfType<global::Avalonia.Controls.ContextMenu>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.ContextMenu.BackgroundProperty, res["PanelBrush"]),
                new Setter(global::Avalonia.Controls.ContextMenu.ForegroundProperty, res["TextPrimaryBrush"]),
            },
        },
    };
}
