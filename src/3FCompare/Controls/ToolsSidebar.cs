using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Panels;

namespace _3FCompare.Controls;

/// <summary>Fluent 风格工具侧栏：纵向导航、常驻放大镜开关与可折叠内容区。</summary>
public sealed class ToolsSidebar : UserControl
{
    private static readonly IBrush PanelBackground = new SolidColorBrush(Color.FromRgb(30, 30, 36));
    private static readonly IBrush CardBackground = new SolidColorBrush(Color.FromRgb(38, 38, 45));
    private static readonly IBrush ActiveBackground = new SolidColorBrush(Color.FromArgb(36, 255, 200, 64));
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(255, 200, 64));
    private static readonly IBrush SecondaryText = new SolidColorBrush(Color.FromRgb(200, 200, 210));
    private static readonly IBrush Divider = new SolidColorBrush(Color.FromRgb(62, 62, 70));

    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly Button _collapseButton = new()
    {
        Width = 32, // 折叠态可视宽度 32（RailWidth 48 - 边距 16），保证不溢出
        Height = 32,
        FontSize = 12,
        Content = "◀",
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
            var rail = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 38,
                FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(6),
                Content = RailGlyph(key),
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
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Content = RailGlyph("Mag_Magnifier"),
        };
        _railMagnifier.Click += (_, _) =>
        {
            _magnifierCheck.IsChecked = _magnifierCheck.IsChecked != true;
            SyncRailMagnifier();
        };
        _magnifierHost = new Border
        {
            Background = CardBackground,
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
            BorderBrush = Divider,
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
            Background = PanelBackground,
            BorderBrush = Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = layout,
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
            tab.Background = active ? ActiveBackground : null;
            tab.Foreground = active ? Accent : SecondaryText;
            tab.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
            rail.Background = active ? ActiveBackground : null;
            rail.Foreground = active ? Accent : SecondaryText;
        }
    }

    private void ApplyCollapsedState()
    {
        _title.IsVisible = !Collapsed;
        _navigation.IsVisible = !Collapsed;
        _content.IsVisible = !Collapsed;
        _magnifierHost.IsVisible = !Collapsed;
        _rail.IsVisible = Collapsed;
        _collapseButton.Content = Collapsed ? "▶" : "◀";
        SyncRailMagnifier();
    }

    private void SyncRailMagnifier()
    {
        var on = _magnifierCheck.IsChecked == true;
        _railMagnifier.Background = on ? ActiveBackground : null;
        _railMagnifier.Foreground = on ? Accent : SecondaryText;
    }

    /// <summary>图标导航栏字形：中文取名称首字，英文取两字母缩写。
    /// 刻意不用 Unicode 符号字体（Segoe UI Symbol 字形覆盖不稳，缺字会显示豆腐块）。</summary>
    private static string RailGlyph(string key)
    {
        if (LanguageManager.IsEnglish)
        {
            return key switch
            {
                "Tab_Probe" => "Pr",
                "Tab_Bookmarks" => "Bk",
                "Tab_Offset" => "Of",
                "Tab_Media" => "Md",
                "Tab_Audio" => "Au",
                "Mag_Magnifier" => "Mg",
                _ => "?",
            };
        }
        return key switch
        {
            "Tab_Probe" => "探",
            "Tab_Bookmarks" => "签",
            "Tab_Offset" => "偏",
            "Tab_Media" => "媒",
            "Tab_Audio" => "音",
            "Mag_Magnifier" => "镜",
            _ => "?",
        };
    }

    private void ApplyLanguage()
    {
        _title.Text = LanguageManager.T("Sidebar_Title");
        _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier");
        foreach (var (key, tab, rail, _) in _tabs)
        {
            var name = LanguageManager.T(key);
            tab.Content = name;
            rail.Content = RailGlyph(key);
            ToolTip.SetTip(tab, name);
            ToolTip.SetTip(rail, name);
        }
        _railMagnifier.Content = RailGlyph("Mag_Magnifier");
        ToolTip.SetTip(_railMagnifier, LanguageManager.T("Mag_Magnifier"));
    }

    public void ActivateProbe() => Activate(Probe);
    public void ActivateBookmarks() => Activate(Bookmarks);
    public void ActivateOffset() => Activate(Offset);
    public void ActivateMedia() => Activate(Media);
    public void ActivateAudio() => Activate(Audio);

    public Control? Active => _active;
}
