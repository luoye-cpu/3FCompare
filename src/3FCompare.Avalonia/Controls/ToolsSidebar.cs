using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Avalonia.Panels;

namespace _3FCompare.Avalonia.Controls;

/// <summary>右侧工具侧栏（WinForms VerticalDockHost 对应）：
/// 顶部折叠按钮 + 5 页签（探针/书签/偏移/媒体/音频）+ 放大镜常驻开关 + 内容区。</summary>
public sealed class ToolsSidebar : UserControl
{
    private readonly CheckBox _magnifierCheck = new() { FontSize = 11 };
    private readonly ContentControl _content = new();
    private readonly List<(Button Tab, Control Panel)> _tabs = new();
    private Control? _active;

    /// <summary>激活页签变化（MainWindow 据此刷新面板会话绑定）。</summary>
    public event EventHandler? ActiveToolChanged;

    public ProbePanel Probe { get; }
    public BookmarkPanel Bookmarks { get; }
    public OffsetPanel Offset { get; }
    public MediaInfoPanel Media { get; }
    public AudioPanel Audio { get; }

    public bool MagnifierOn => _magnifierCheck.IsChecked == true;
    public event EventHandler? MagnifierToggled;

    public bool Collapsed { get; private set; }
    public event EventHandler? CollapsedChanged;

    public ToolsSidebar(ProbePanel probe, BookmarkPanel bookmarks, OffsetPanel offset, MediaInfoPanel media, AudioPanel audio)
    {
        Probe = probe; Bookmarks = bookmarks; Offset = offset; Media = media; Audio = audio;

        var collapse = new Button { Height = 20, FontSize = 10, Content = "▶", HorizontalAlignment = HorizontalAlignment.Right };
        collapse.Click += (_, _) => ToggleCollapse();

        var tabsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new global::Avalonia.Thickness(4, 2) };
        foreach (var (key, panel) in new[]
                 {
                     ("Tab_Probe", (Control)probe), ("Tab_Bookmarks", bookmarks),
                     ("Tab_Offset", offset), ("Tab_Media", media), ("Tab_Audio", audio),
                 })
        {
            var tab = new Button { Height = 24, FontSize = 11, Padding = new global::Avalonia.Thickness(8, 0), Content = LanguageManager.T(key) };
            tab.Click += (_, _) => Activate(panel);
            _tabs.Add((tab, panel));
            tabsRow.Children.Add(tab);
        }

        _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier");
        _magnifierCheck.Margin = new global::Avalonia.Thickness(6, 4);
        _magnifierCheck.IsCheckedChanged += (_, _) => MagnifierToggled?.Invoke(this, EventArgs.Empty);

        var top = new StackPanel { Orientation = Orientation.Vertical };
        top.Children.Add(collapse);
        top.Children.Add(tabsRow);
        top.Children.Add(_magnifierCheck);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_content);
        Content = root;

        Activate(probe);
        LanguageManager.LanguageChanged += (_, _) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier"));
    }

    private void ToggleCollapse()
    {
        Collapsed = !Collapsed;
        CollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Activate(Control panel)
    {
        _active = panel;
        _content.Content = panel;
        foreach (var (tab, p) in _tabs)
            tab.Background = ReferenceEquals(p, panel)
                ? new SolidColorBrush(Color.FromRgb(255, 200, 64))
                : null;
        ActiveToolChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ActivateProbe() => Activate(Probe);
    public void ActivateBookmarks() => Activate(Bookmarks);
    public void ActivateOffset() => Activate(Offset);
    public void ActivateMedia() => Activate(Media);
    public void ActivateAudio() => Activate(Audio);

    public Control? Active => _active;
}
