using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace _3FCompare;

/// <summary>主题切换入口（跟随系统 / 浅色 / 深色）。
/// <para>机制：只改 <see cref="Application.RequestedThemeVariant"/>，由 Avalonia 重新解析
/// <c>ThemeDictionaries</c> 与全部 <c>DynamicResource</c> —— 不重建 ResourceDictionary、
/// 不重建窗口（docs/31 阶段 0 实测通过）。</para></summary>
public static class ThemeManager
{
    private static ThemePreference _preference = ThemePreference.System;
    private static bool _hooked;

    /// <summary>当前偏好（不是"实际生效的变体"；跟随系统时实际变体由平台决定）。</summary>
    public static ThemePreference Preference => _preference;

    /// <summary>应用主题偏好。<paramref name="persist"/> = false 用于"实时预览后取消"的还原。</summary>
    public static void Apply(ThemePreference pref, bool persist = true)
    {
        _preference = pref;
        var app = Application.Current;
        if (app is not null)
        {
            HookPlatform(app);
            app.RequestedThemeVariant = ResolveVariant(pref, app);
        }
        if (persist) Store.Save(pref);
    }

    /// <summary>启动时应用上次保存的偏好（不写回，避免把默认值当成用户选择落盘）。</summary>
    public static void ApplySaved() => Apply(Store.Load(), persist: false);

    private static ThemeVariant ResolveVariant(ThemePreference pref, Application app) => pref switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => SystemVariant(app),
    };

    /// <summary>跟随系统：把平台深浅色<b>显式</b>解析成 Light/Dark。
    /// <para>不能直接把 <c>RequestedThemeVariant</c> 设成 <see cref="ThemeVariant.Default"/>：
    /// 实测此时 <c>ActualThemeVariant</c> 为空值，<c>ThemeDictionaries</c> 根本不会被查询，
    /// 全部令牌解析失败（窗口背景与文字一起消失）。</para>
    /// <para>平台返回 null（无深浅色概念）时兜底为 Dark —— 本应用的默认外观是深色。</para></summary>
    private static ThemeVariant SystemVariant(Application app)
        => app.PlatformSettings?.GetColorValues().ThemeVariant switch
        {
            Avalonia.Platform.PlatformThemeVariant.Light => ThemeVariant.Light,
            Avalonia.Platform.PlatformThemeVariant.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Dark,
        };

    /// <summary>订阅系统深浅色变化（仅在跟随系统时生效）。只需挂一次。</summary>
    private static void HookPlatform(Application app)
    {
        if (_hooked || app.PlatformSettings is not { } ps) return;
        _hooked = true;
        ps.ColorValuesChanged += (_, _) =>
        {
            if (_preference != ThemePreference.System) return;
            // 平台事件未必在 UI 线程；RequestedThemeVariant 必须在 UI 线程改
            Dispatcher.UIThread.Post(() =>
            {
                if (_preference == ThemePreference.System)
                    Application.Current!.RequestedThemeVariant = SystemVariant(Application.Current);
            });
        };
    }

    /// <summary>偏好持久化。
    /// <para>为什么不放进 Core 的 <c>AppSettings</c>：Core 有独立单测且正被并行修改，
    /// 本次改造约定不触碰。故 UI 侧单开一个小文件，路径规则与
    /// <c>SettingsStore</c> 保持一致（应用目录优先以支持便携部署，不可写才回退用户目录）。</para>
    /// <para>内容是一个单词（<c>system</c>/<c>light</c>/<c>dark</c>）而不是 JSON：
    /// 本工程 <c>PublishAot=true</c>，单值走纯文本可完全避开反射式序列化。</para></summary>
    private static class Store
    {
        private static string? _path;

        private static string Path
        {
            get
            {
                if (_path is not null) return _path;
                var portable = System.IO.Path.Combine(AppContext.BaseDirectory, "theme.txt");
                if (CanWrite(AppContext.BaseDirectory)) return _path = portable;
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "3FCompare");
                return _path = CanWrite(dir) ? System.IO.Path.Combine(dir, "theme.txt") : portable;
            }
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = System.IO.Path.Combine(dir, ".theme_write_probe");
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static ThemePreference Load()
        {
            try
            {
                if (!File.Exists(Path)) return ThemePreference.System;
                return File.ReadAllText(Path).Trim().ToLowerInvariant() switch
                {
                    "light" => ThemePreference.Light,
                    "dark" => ThemePreference.Dark,
                    _ => ThemePreference.System,
                };
            }
            catch { return ThemePreference.System; }
        }

        public static void Save(ThemePreference pref)
        {
            try
            {
                File.WriteAllText(Path, pref switch
                {
                    ThemePreference.Light => "light",
                    ThemePreference.Dark => "dark",
                    _ => "system",
                });
            }
            catch { /* 存不下不影响本次会话内的主题 */ }
        }
    }
}

/// <summary>自绘控件取令牌的桥：从资源宿主按当前主题变体解析画刷/颜色。
/// <para>为什么自绘控件不用 <c>DynamicResource</c>：<c>Render(DrawingContext)</c> 里要的是
/// 具体的 <see cref="IBrush"/>/<see cref="Color"/> 值，不是可绑定的依赖属性。</para>
/// <para>取不到（控件尚未接入可视树 / 字典尚未装配）时，回落到 <see cref="ThemeTokens"/>
/// 里该键<b>在当前变体下</b>的取值 —— 令牌表是颜色单一真源，调用点不再自带十六进制缺省值。
/// 键不在令牌表里属编程错误，由 <see cref="ThemeTokens.Get"/> 抛出清晰异常（不再静默画错色）。</para></summary>
public static class ThemePalette
{
    public static IBrush Brush(StyledElement owner, string key)
        => owner.TryFindResource(key, owner.ActualThemeVariant, out var v) && v is IBrush b
            ? b
            : new Avalonia.Media.Immutable.ImmutableSolidColorBrush(
                Avalonia.Media.Color.Parse(ThemeTokens.Value(key, owner.ActualThemeVariant)));

    /// <summary>显式指定回落色。仅当确需一个"不属于任何令牌"的颜色时才用
    /// （例如热力色那种编码数据而非主题的取值）；主题色一律走两参重载。</summary>
    public static IBrush Brush(StyledElement owner, string key, string fallback)
        => owner.TryFindResource(key, owner.ActualThemeVariant, out var v) && v is IBrush b
            ? b
            : new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Avalonia.Media.Color.Parse(fallback));

    /// <summary>把一个画笔属性绑到令牌上（等价于 XAML 的 <c>{DynamicResource XxxBrush}</c>）。
    /// <para>代码构建的控件（面板/设置窗口等）用它代替"构造时 new 一个画刷"：
    /// 后者在主题切换后不会更新。可重复调用（同优先级绑定互相替换），
    /// 适合"状态变化时换一个令牌"的场景（如 FFmpeg 校验状态由红转绿）。</para></summary>
    public static void SetBrush(AvaloniaObject target, AvaloniaProperty property, string key)
        => target.Bind(property, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(key));

    /// <summary>把 TextBlock 的前景绑到令牌并原样返回，便于字段初始化器 / 对象初始化器内联使用
    /// （<c>new TextBlock { Foreground = ... }</c> 无法调用方法）。</summary>
    public static TextBlock Themed(this TextBlock target, string key)
    {
        SetBrush(target, TextBlock.ForegroundProperty, key);
        return target;
    }
}
