using System;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace _3FCompare;

/// <summary>主题资源（代码装配；对应 WinForms AppTheme.Colors/Fonts/Sizes）。
/// 注：App.axaml 内嵌 Resources/Styles 在本机 .NET 11 预览运行时触发
/// 「No precompiled XAML found for App」查找异常（M1 排障结论，见 docs/07），
/// 故主题一律经代码装配；Theme.axaml 仅作设计参考不参与编译。
/// <para><b>深浅两套值放 <see cref="Avalonia.Controls.ResourceDictionary.ThemeDictionaries"/></b>：
/// 令牌定义见 <see cref="ThemeTokens"/>，每个令牌在每个变体字典里同时写入
/// 「颜色键」与派生的「<c>xxxBrush</c> 画笔键」。切换 <c>RequestedThemeVariant</c> 时
/// Avalonia 会自动重解析全部 <c>DynamicResource</c>，不需要重建字典或窗口
/// （可行性已实测：docs/31 阶段 0）。</para></summary>
public sealed class ThemeResources : global::Avalonia.Controls.ResourceDictionary
{
    public ThemeResources()
    {
        ThemeDictionaries[ThemeVariant.Dark] = BuildVariant(light: false);
        ThemeDictionaries[ThemeVariant.Light] = BuildVariant(light: true);

        // 与主题无关的资源留在顶层字典（字体 / 尺寸）
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

    /// <summary>按令牌表装配某一变体的资源字典（颜色 + 自动派生 <c>xxxBrush</c>）。</summary>
    private static global::Avalonia.Controls.ResourceDictionary BuildVariant(bool light)
    {
        var dict = new global::Avalonia.Controls.ResourceDictionary();
        foreach (var t in ThemeTokens.All)
        {
            var color = Color.Parse(light ? t.Light : t.Dark);
            dict[t.Key] = color;
            dict[t.Key + "Brush"] = new SolidColorBrush(color);
        }
        return dict;
    }

    /// <summary>全局基础样式（对应 WinForms 默认外观：面板菜单/上下文菜单背景、正文前景色）。
    /// <para>取值必须用 <c>DynamicResource</c> 而不是构造时取出的画刷实例：后者在主题切换后
    /// 仍指向旧变体的对象，全应用正文颜色会停在上一套主题（实测见 docs/31 阶段 0）。</para></summary>
    public static Styles BuildBaseStyles() => new()
    {
        new Style(s => s.OfType<global::Avalonia.Controls.TextBlock>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.TextBlock.ForegroundProperty,
                    new DynamicResourceExtension("TextPrimaryBrush")),
                new Setter(global::Avalonia.Controls.TextBlock.FontFamilyProperty, new FontFamily("Microsoft YaHei UI")),
            },
        },
        new Style(s => s.OfType<global::Avalonia.Controls.Menu>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.Menu.BackgroundProperty,
                    new DynamicResourceExtension("PanelBrush")),
                new Setter(global::Avalonia.Controls.Menu.ForegroundProperty,
                    new DynamicResourceExtension("TextPrimaryBrush")),
            },
        },
        new Style(s => s.OfType<global::Avalonia.Controls.MenuItem>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.MenuItem.ForegroundProperty,
                    new DynamicResourceExtension("TextPrimaryBrush")),
            },
        },
        new Style(s => s.OfType<global::Avalonia.Controls.ContextMenu>())
        {
            Setters =
            {
                new Setter(global::Avalonia.Controls.ContextMenu.BackgroundProperty,
                    new DynamicResourceExtension("PanelBrush")),
                new Setter(global::Avalonia.Controls.ContextMenu.ForegroundProperty,
                    new DynamicResourceExtension("TextPrimaryBrush")),
            },
        },
    };
}
