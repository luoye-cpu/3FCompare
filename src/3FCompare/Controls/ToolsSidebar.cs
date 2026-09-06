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
        Width = 36,
        Height = 32,
        FontSize = 12,
        Content = "◀",
        HorizontalAlignment = HorizontalAlignment.Right,
    };
    private readonly StackPanel _navigation = new() { Spacing = 4 };
    private readonly CheckBox _magnifierCheck = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _magnifierHost;
    private readonly ContentControl _content = new();
    private readonly List<(string Key, Button Tab, Control Panel)> _tabs = new();
    private Control? _active;

    public const double DefaultExpandedWidth = 264;
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
            _tabs.Add((key, tab, panel));
            _navigation.Children.Add(tab);
        }

        _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier");
        _magnifierCheck.IsCheckedChanged += (_, _) => MagnifierToggled?.Invoke(this, EventArgs.Empty);
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
        Grid.SetRow(_navigation, 1);
        Grid.SetRow(contentHost, 2);
        Grid.SetRow(_magnifierHost, 3);
        layout.Children.Add(header);
        layout.Children.Add(_navigation);
        layout.Children.Add(contentHost);
        layout.Children.Add(_magnifierHost);

        Content = new Border
        {
            Background = PanelBackground,
            BorderBrush = Divider,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = layout,
        };

        Activate(probe);
        ApplyLanguage();
        LanguageManager.LanguageChanged += (_, _) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(ApplyLanguage);
    }

    public void ToggleCollapse()
    {
        Collapsed = !Collapsed;
        ApplyCollapsedState();
        CollapsedChanged?.Invoke(Collapsed);
    }

    public void Expand()
    {
        if (!Collapsed) return;
        Collapsed = false;
        ApplyCollapsedState();
        CollapsedChanged?.Invoke(false);
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
        foreach (var (_, tab, item) in _tabs)
        {
            var active = ReferenceEquals(item, panel);
            tab.Background = active ? ActiveBackground : null;
            tab.Foreground = active ? Accent : SecondaryText;
            tab.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    private void ApplyCollapsedState()
    {
        _title.IsVisible = !Collapsed;
        _navigation.IsVisible = !Collapsed;
        _content.IsVisible = !Collapsed;
        _magnifierHost.IsVisible = !Collapsed;
        _collapseButton.Content = Collapsed ? "▶" : "◀";
    }

    private void ApplyLanguage()
    {
        _title.Text = LanguageManager.T("Sidebar_Title");
        _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier");
        foreach (var (key, tab, _) in _tabs)
            tab.Content = LanguageManager.T(key);
    }

    public void ActivateProbe() => Activate(Probe);
    public void ActivateBookmarks() => Activate(Bookmarks);
    public void ActivateOffset() => Activate(Offset);
    public void ActivateMedia() => Activate(Media);
    public void ActivateAudio() => Activate(Audio);

    public Control? Active => _active;
}
