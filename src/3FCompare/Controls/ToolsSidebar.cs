using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Panels;
// 本工程开了 ImplicitUsings，System.IO.Path 在作用域内 ⇒ 必须起别名，否则 Path 二义
using Path = Avalonia.Controls.Shapes.Path;

namespace _3FCompare.Controls;

/// <summary>Fluent 风格工具侧栏：纵向导航、常驻放大镜开关与可折叠内容区。</summary>
public sealed class ToolsSidebar : UserControl
{
    // 配色一律经令牌解析（不能 static readonly：会把首次解析结果固化，切主题后停在旧值）。
    // 静态外观用 DynamicResource 绑定（自动跟随）；随状态切换的前景/背景在
    // ActualThemeVariantChanged 里重跑 Activate/SyncRailMagnifier。
    private IBrush Token(string key) => ThemePalette.Brush(this, key);

    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };

    /// <summary>折叠按钮的箭头（docs/31 阶段 4）：展开态指向左、折叠态指向右，只换几何不换控件。</summary>
    private readonly Path _collapseIcon = AppIcons.Create("ChevronLeft", 12, "TextSecondaryBrush");

    private readonly Button _collapseButton = new()
    {
        Width = 32, // 折叠态可视宽度 32（RailWidth 48 - 边距 16），保证不溢出
        Height = 32,
        Content = null, // 实例字段初始化器不能引用另一个实例字段 ⇒ 图标在构造函数里装配
        HorizontalAlignment = HorizontalAlignment.Right,
    };
    private readonly StackPanel _navigation = new() { Spacing = 4 };
    private readonly CheckBox _magnifierCheck = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _magnifierHost;
    private readonly ContentControl _content = new();
    private readonly Button _railMagnifier;
    /// <summary>折叠态图标导航栏（icon rail）。主流工具（VS Code 活动栏 48px、
    /// Figma / Resolve 折叠面板）折叠后仍保留工具入口；此前折叠只留一个 44px 空白条，
    /// 既浪费横向空间又让用户失去一键进入各面板的能力。</summary>
    private readonly StackPanel _rail = new() { Spacing = 4 };
    private readonly List<(string Key, Button Tab, Button Rail, Control Panel)> _tabs = new();
    private Control? _active;

    public const double DefaultExpandedWidth = 264;
    /// <summary>折叠态导航栏宽度（VS Code 活动栏同为 48）。</summary>
    public const double RailWidth = 48;
    private double _expandedWidth = DefaultExpandedWidth;
    public double ExpandedWidth => _expandedWidth;

    public ProbePanel Probe { get; }
    public BookmarkPanel Bookmarks { get; }
    public OffsetPanel Offset { get; }
    public MediaInfoPanel Media { get; }
    public AudioPanel Audio { get; }

    public bool MagnifierOn => _magnifierCheck.IsChecked == true;
    public event EventHandler? MagnifierToggled;

    public bool Collapsed { get; private set; }
    public event Action<bool>? CollapsedChanged;

    /// <summary>本次折叠/展开是否由用户交互触发（折叠按钮或图标导航栏）。
    /// 程序化恢复（SetCollapsed）为 false，供主窗口判断是否停用窄窗自动折叠。</summary>
    public bool LastToggleByUser { get; private set; }

    public ToolsSidebar(ProbePanel probe, BookmarkPanel bookmarks, OffsetPanel offset, MediaInfoPanel media, AudioPanel audio)
    {
        Probe = probe;
        Bookmarks = bookmarks;
        Offset = offset;
        Media = media;
        Audio = audio;

        _collapseButton.Content = _collapseIcon;
        ToolTip.SetTip(_collapseButton, "折叠/展开");
        _collapseButton.Click += (_, _) => ToggleCollapse();

        foreach (var (key, panel) in new[]
                 {
                     ("Tab_Probe", (Control)probe), ("Tab_Bookmarks", bookmarks),
                     ("Tab_Offset", offset), ("Tab_Media", media), ("Tab_Audio", audio),
                 })
        {
            var tab = new Button
            {
                Height = 38,
                FontSize = 13,
                Padding = new Thickness(12, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(6),
                Content = LanguageManager.T(key),
            };
            tab.Click += (_, _) => Activate(panel);

            // 折叠态：图标按钮（点一下 = 展开并激活该面板）
            // 宽度不写死：折叠后可视宽度 = RailWidth(48) - 布局边距(16) = 32，
            // 让 StackPanel 自动撑满，边距调整时不会溢出。
            // 图标是矢量几何（docs/31 阶段 4）：不再用"探/签/偏…"字形，面板名靠 ToolTip 兜底
            // （ApplyLanguage 里设置），中文首字/英文两字母方案随之退场。
            var rail = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 38,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(6),
                Content = AppIcons.Create(IconKey(key), 18, "TextSecondaryBrush"),
            };
            var captured = panel; // 闭包捕获
            rail.Click += (_, _) => { Expand(); Activate(captured); };

            _tabs.Add((key, tab, rail, panel));
            _navigation.Children.Add(tab);
            _rail.Children.Add(rail);
        }

        _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier");
        _magnifierCheck.IsCheckedChanged += (_, _) =>
        {
            SyncRailMagnifier();
            MagnifierToggled?.Invoke(this, EventArgs.Empty);
        };

        // 折叠态的放大镜开关（与展开态 CheckBox 共享同一状态）
        _railMagnifier = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 38,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Content = AppIcons.Create("Magnifier", 18, "TextSecondaryBrush"),
        };
        _railMagnifier.Click += (_, _) =>
        {
            _magnifierCheck.IsChecked = _magnifierCheck.IsChecked != true;
            SyncRailMagnifier();
        };
        _magnifierHost = new Border
        {
            Background = Token("CardBgBrush"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Child = _magnifierCheck,
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(4, 2, 4, 10),
        };
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_collapseButton, 1);
        header.Children.Add(_title);
        header.Children.Add(_collapseButton);

        var contentHost = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Child = _content,
        };

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(8),
        };
        // 折叠时 _navigation 隐藏、_rail 显示（同一行互斥，行高自动切换）
        _rail.Children.Add(_railMagnifier);
        Grid.SetRow(_navigation, 1);
        Grid.SetRow(_rail, 1);
        Grid.SetRow(contentHost, 2);
        Grid.SetRow(_magnifierHost, 3);
        layout.Children.Add(header);
        layout.Children.Add(_navigation);
        layout.Children.Add(_rail);
        layout.Children.Add(contentHost);
        layout.Children.Add(_magnifierHost);

        Content = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = layout,
        };

        ThemePalette.SetBrush((Border)Content, Border.BorderBrushProperty, "DividerBrush");
        ThemePalette.SetBrush(contentHost, Border.BorderBrushProperty, "DividerBrush");
        // 主题切换后：状态色是命令式赋值的，不会自动跟随 ⇒ 重跑一遍
        ActualThemeVariantChanged += (_, _) =>
        {
            if (_active is not null) Activate(_active);
            SyncRailMagnifier();
        };

        ApplyCollapsedState();
        Activate(probe);
        ApplyLanguage();
        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, s =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                s.ApplyLanguage();
                s.ApplyCollapsedState();
            }));
    }

    public void ToggleCollapse()
    {
        // 折叠前记下当前实际宽度（用户可能已拖拽分隔条），展开时原样还原
        if (!Collapsed) UpdateExpandedWidth(Bounds.Width);
        Collapsed = !Collapsed;
        LastToggleByUser = true;
        ApplyCollapsedState();
        CollapsedChanged?.Invoke(Collapsed);
    }

    public void Expand()
    {
        if (!Collapsed) return;
        Collapsed = false;
        LastToggleByUser = true;
        ApplyCollapsedState();
        CollapsedChanged?.Invoke(false);
    }

    /// <summary>由外部（持久化恢复 / 窄窗自动折叠）直接设定折叠状态，不触发反转。</summary>
    public void SetCollapsed(bool collapsed)
    {
        LastToggleByUser = false;
        if (Collapsed == collapsed) { ApplyCollapsedState(); return; }
        Collapsed = collapsed;
        ApplyCollapsedState();
        CollapsedChanged?.Invoke(Collapsed);
    }

    public void UpdateExpandedWidth(double width)
    {
        if (width >= PanelMinWidth) _expandedWidth = width;
    }

    private const double PanelMinWidth = 160;

    public void Activate(Control panel)
    {
        _active = panel;
        _content.Content = panel;
        foreach (var (_, tab, rail, item) in _tabs)
        {
            var active = ReferenceEquals(item, panel);
            tab.Background = active ? Token("AccentSubtleBrush") : null;
            tab.Foreground = active ? Token("AccentBrush") : Token("TextSecondaryBrush");
            tab.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
            rail.Background = active ? Token("AccentSubtleBrush") : null;
            rail.Foreground = active ? Token("AccentBrush") : Token("TextSecondaryBrush");
            // 图标是 Shape，不吃 Foreground ⇒ 单独改绑 Fill（同优先级绑定互相替换）。
            // 本方法在主题切换时会被重跑（ActualThemeVariantChanged），故这里不用再管主题。
            if (rail.Content is Path railIcon)
                ThemePalette.SetBrush(railIcon, Path.FillProperty,
                    active ? "AccentBrush" : "TextSecondaryBrush");
        }
    }

    private void ApplyCollapsedState()
    {
        _title.IsVisible = !Collapsed;
        _navigation.IsVisible = !Collapsed;
        _content.IsVisible = !Collapsed;
        _magnifierHost.IsVisible = !Collapsed;
        _rail.IsVisible = Collapsed;
        _collapseIcon.Data = AppIcons.Get(Collapsed ? "ChevronRight" : "ChevronLeft");
        SyncRailMagnifier();
    }

    private void SyncRailMagnifier()
    {
        var on = _magnifierCheck.IsChecked == true;
        _railMagnifier.Background = on ? Token("AccentSubtleBrush") : null;
        _railMagnifier.Foreground = on ? Token("AccentBrush") : Token("TextSecondaryBrush");
        // 图标是 Shape，不吃 Foreground ⇒ 单独改绑 Fill（同优先级绑定互相替换）。
        // DynamicResource 绑定本身会跟随主题切换，这里只需处理"状态"变化。
        if (_railMagnifier.Content is Path icon)
            ThemePalette.SetBrush(icon, Path.FillProperty, on ? "AccentBrush" : "TextSecondaryBrush");
    }

    /// <summary>侧栏入口的本地化键 → <see cref="AppIcons"/> 图标键。
    /// 键写错是编程错误，直接抛（不返回"?"占位：占位会变成"按钮上一个问号"，比图标画歪更难排查）。</summary>
    private static string IconKey(string key) => key switch
    {
        "Tab_Probe" => "Probe",
        "Tab_Bookmarks" => "Bookmarks",
        "Tab_Offset" => "Offset",
        "Tab_Media" => "Media",
        "Tab_Audio" => "Audio",
        "Mag_Magnifier" => "Magnifier",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "未登记的侧栏图标键"),
    };

    private void ApplyLanguage()
    {
        _title.Text = LanguageManager.T("Sidebar_Title");
        _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier");
        foreach (var (key, tab, rail, _) in _tabs)
        {
            var name = LanguageManager.T(key);
            tab.Content = name;
            ToolTip.SetTip(tab, name);
            ToolTip.SetTip(rail, name);
        }
        ToolTip.SetTip(_railMagnifier, LanguageManager.T("Mag_Magnifier"));
    }

    public void ActivateProbe() => Activate(Probe);
    public void ActivateBookmarks() => Activate(Bookmarks);
    public void ActivateOffset() => Activate(Offset);
    public void ActivateMedia() => Activate(Media);
    public void ActivateAudio() => Activate(Audio);

    public Control? Active => _active;
}
