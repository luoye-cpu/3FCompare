using Avalonia.Styling;

namespace _3FCompare;

/// <summary>主题偏好（设置窗口「外观」节）。</summary>
public enum ThemePreference
{
    /// <summary>跟随系统（读取 <c>IPlatformSettings</c> 的深浅色，并监听其变化）。</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>一条语义颜色令牌：语义名 + 资源键 + 深浅两组取值。
/// <para><b>为什么同时保留语义名和资源键</b>：语义名（<c>Surface.Background</c>）是后续 UI 重构
/// 讨论与新增令牌时的命名规范；资源键（<c>Bg</c>）是既有 XAML <c>{DynamicResource BgBrush}</c>
/// 与代码装配已经在用的键名，改键名会波及 MainWindow.axaml 等大量文件却换不来任何收益。
/// 两者一一对应，新增令牌请两边都写。</para></summary>
public readonly record struct ThemeToken(string Semantic, string Key, string Dark, string Light);

/// <summary>全部颜色令牌（单一真源）。
/// <para>深色取值与改造前的硬编码值逐一对应（保留用户认可的金黄强调色），
/// 浅色取值为新增。</para>
/// <para>令牌分为两类：</para>
/// <list type="bullet">
/// <item>随主题变化的应用表面/文字/边框/标记色 —— 深浅两值不同。</item>
/// <item>覆盖在视频画面之上的元素（放大镜背板、网格线、手柄描边环、滑块握把）——
/// 深浅两值<b>相同</b>：它们压在视频像素上而非应用表面上，对比度需求与主题无关，
/// 跟着主题翻转反而会在浅色下把画面糊掉。仍走令牌是为了消除硬编码。</item>
/// </list></summary>
public static class ThemeTokens
{
    /// <summary><b>以下为预留令牌，暂无引用，勿删</b>（审计确认当前引用计数为 0）：
    /// <c>Canvas</c>、<c>ControlBgLight</c>、<c>InputBg</c>、<c>InputBgAlt</c>、<c>Warning</c>、
    /// <c>SelectedBorder</c>、<c>UnselectedBorder</c>、<c>ButtonSecondary</c>、<c>MarkerA</c>、
    /// <c>MarkerB</c>。它们已随本表进入两套变体字典的装配（<see cref="ThemeResources"/> 会为
    /// 每条同时写入颜色键与派生的 <c>xxxBrush</c> 键），是后续 UI 重构要用的语义位；
    /// 删掉会让"单一真源"出现缺口，且 XAML 里可能已有 <c>DynamicResource</c> 指向它们。</summary>
    public static readonly ThemeToken[] All =
    {
        // 语义名                        资源键              深色        浅色
        new("Surface.Background",      "Bg",               "#18181C", "#F5F6F8"),
        new("Surface.Panel",           "Panel",            "#1E1E24", "#FFFFFF"),
        new("Surface.Canvas",          "Canvas",           "#121214", "#E9EBEF"),
        new("Surface.CanvasDeep",      "CanvasDark",       "#0A0A0C", "#DCDFE5"),
        new("Surface.Control",         "ControlBg",        "#28282E", "#EFF1F4"),
        new("Surface.ControlHover",    "ControlBgLight",   "#2D2D34", "#E5E8ED"),
        new("Surface.Input",           "InputBg",          "#24242A", "#FFFFFF"),
        new("Surface.InputAlt",        "InputBgAlt",       "#32323A", "#EAECF0"),
        new("Surface.Card",            "CardBg",           "#26262D", "#F7F8FA"),

        new("Text.Primary",            "TextPrimary",      "#FFFFFF", "#1A1A1E"),
        new("Text.Secondary",          "TextSecondary",    "#C8C8D2", "#46464E"),
        new("Text.Muted",              "TextMuted",        "#8C8C96", "#6B6B75"),

        new("Accent.Default",          "Accent",           "#FFC840", "#A87800"),
        new("Accent.Text",             "AccentText",       "#FFC840", "#8A6200"),
        new("Accent.Subtle",           "AccentSubtle",     "#24FFC840", "#1FA87800"),

        new("Status.Success",          "Success",          "#64C864", "#1E8E3E"),
        new("Status.Warning",          "Warning",          "#FFB432", "#B26A00"),
        new("Status.Error",            "Error",            "#FF6464", "#CC3B3B"),

        new("Border.Default",          "Border",           "#50505A", "#C4C8D0"),
        new("Border.Divider",          "Divider",          "#3E3E46", "#E2E5EA"),
        new("Border.Selected",         "SelectedBorder",   "#40A0FF", "#1A73E8"),
        new("Border.Unselected",       "UnselectedBorder", "#3C3C46", "#C8CCD4"),

        new("Button.Active",           "ButtonActive",     "#3C5A3C", "#CFE8CF"),
        new("Button.Secondary",        "ButtonSecondary",  "#3C3C42", "#E4E6EA"),

        new("Marker.A",                "MarkerA",          "#FF6464", "#D93A3A"),
        new("Marker.B",                "MarkerB",          "#6464FF", "#3A3AD9"),

        new("Playback.LoopFill",       "LoopFill",         "#4664C864", "#331E8E3E"),

        // ---- 以下为"压在视频画面上"的元素：深浅同值（见类注释）----
        new("Overlay.Scrim",           "OverlayScrim",     "#C80A0A0C", "#C80A0A0C"),
        new("Overlay.Grid",            "OverlayGrid",      "#8CFFFFFF", "#8CFFFFFF"),
        new("Overlay.Halo",            "OverlayHalo",      "#96000000", "#96000000"),
        new("Overlay.Ring",            "OverlayRing",      "#DC141418", "#DC141418"),
        new("Overlay.Grip",            "OverlayGrip",      "#FFFFFF",   "#FFFFFF"),
    };

    /// <summary>资源键 → 令牌的索引。同时登记 <c>Accent</c> 与派生的 <c>AccentBrush</c>：
    /// XAML 与自绘控件用的都是后者（<see cref="ThemeResources"/> 里 <c>dict[t.Key + "Brush"]</c>），
    /// 调用点因此不需要自己拼后缀。静态字典而非每次线性扫描 <see cref="All"/>（32 条 × 每帧调用）。
    /// <para>字段声明顺序有意放在 <see cref="All"/> 之后：静态初始化器按文本顺序执行，
    /// <see cref="BuildIndex"/> 必须能读到已赋值的 <see cref="All"/>。</para>
    /// <para>AOT 友好：纯字面量构建，不触碰反射。</para></summary>
    private static readonly Dictionary<string, ThemeToken> ByKey = BuildIndex();

    private static Dictionary<string, ThemeToken> BuildIndex()
    {
        var map = new Dictionary<string, ThemeToken>(All.Length * 2, StringComparer.Ordinal);
        foreach (var t in All)
        {
            map[t.Key] = t;
            map[t.Key + "Brush"] = t;
        }
        return map;
    }

    /// <summary>按资源键取令牌（<c>Accent</c> 与 <c>AccentBrush</c> 都认）。
    /// <para><b>未命中时抛出而不是返回 <c>default</c></b>：<c>default(ThemeToken)</c> 的字段全是
    /// 空串，后续 <c>Color.Parse("")</c> 会抛出更难定位的异常（甚至可能被 <c>catch</c> 吞掉而
    /// 画成透明）；键写错属编程错误，越早、越清楚地暴露越好。消息里带上键名与文件位置。</para></summary>
    /// <exception cref="KeyNotFoundException">该键未在 <see cref="All"/> 中登记。</exception>
    public static ThemeToken Get(string key)
        => ByKey.TryGetValue(key, out var token)
            ? token
            : throw new KeyNotFoundException(
                $"未定义的主题令牌：\"{key}\"。请在 Themes/ThemeTokens.cs 的 ThemeTokens.All 中登记" +
                "（注意自绘控件传的是派生的 xxxBrush 形式，如 \"AccentBrush\"）。");

    /// <summary>按主题变体取该令牌的取值：<see cref="ThemeVariant.Light"/> 返回 <c>Light</c>，
    /// 其余（含 <c>null</c> / <c>Default</c> / 未知变体）一律返回 <c>Dark</c> —— 本应用的默认外观是深色。</summary>
    public static string Value(string key, ThemeVariant variant)
    {
        var token = Get(key);
        return variant == ThemeVariant.Light ? token.Light : token.Dark;
    }
}
