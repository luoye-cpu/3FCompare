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
        // 颜色名与 XAML 画笔键名 {DynamicResource XxxBrush} 对应 ——
        // AddColor 存入颜色 + 派生画笔 (s+"Brush")
        AddColor("Bg", 24, 24, 28);             // → BgBrush
        AddColor("Panel", 30, 30, 36);           // → PanelBrush
        AddColor("TextPrimary", 255, 255, 255);  // → TextPrimaryBrush
        AddColor("TextSecondary", 200, 200, 210); // → TextSecondaryBrush
        AddColor("Accent", 255, 200, 64);         // → AccentBrush
        AddColor("Success", 100, 200, 100);       // → SuccessBrush
        AddColor("Warning", 255, 180, 50);        // → WarningBrush
        AddColor("Error", 255, 100, 100);         // → ErrorBrush
        AddColor("Canvas", 18, 18, 20);           // → CanvasBrush
        AddColor("CanvasDark", 10, 10, 12);       // → CanvasDarkBrush
        AddColor("ControlBg", 40, 40, 46);        // → ControlBgBrush
        AddColor("ControlBgLight", 45, 45, 52);   // → ControlBgLightBrush
        AddColor("InputBg", 36, 36, 42);          // → InputBgBrush
        AddColor("InputBgAlt", 50, 50, 58);       // → InputBgAltBrush
        AddColor("ButtonActive", 60, 90, 60);     // → ButtonActiveBrush
        AddColor("ButtonSecondary", 60, 60, 66);  // → ButtonSecondaryBrush
        AddColor("Border", 80, 80, 90);           // → BorderBrush
        AddColor("TextMuted", 140, 140, 150);     // → TextMutedBrush
        AddColor("SelectedBorder", 64, 160, 255); // → SelectedBorderBrush
        AddColor("UnselectedBorder", 60, 60, 70); // → UnselectedBorderBrush
        AddColor("MarkerA", 255, 100, 100);       // → MarkerABrush
        AddColor("MarkerB", 100, 100, 255);       // → MarkerBBrush

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
