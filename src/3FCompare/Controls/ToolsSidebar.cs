using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Panels;

namespace _3FCompare.Controls;

/// <summary>右侧工具侧栏（WinForms VerticalDockHost 对应）：
/// 顶部折叠按钮 + 5 页签（探针/书签/偏移/媒体/音频）+ 放大镜常驻开关 + 内容区。
/// 折叠/展开时通过 CollapsedChanged 事件告知宿主（MainWindow）调整 Grid 列宽，
/// 自身记录展开宽度（默认 240px），避免用 Bounds.Width（折叠后为 24px）导致展开回不去。</summary>
public sealed class ToolsSidebar : UserControl
{
    private readonly CheckBox _magnifierCheck = new() { FontSize = 11 };
    private readonly ContentControl _content = new();
    private readonly List<(Button Tab, Control Panel)> _tabs = new();
    private Control? _active;
    /// <summary>展开时的宽度（像素），折叠后记录此值供恢复。</summary>
    public const double DefaultExpandedWidth = 240;
    private double _expandedWidth = DefaultExpandedWidth;
    /// <summary>当前展开宽度（只读，供宿主在折叠展开时恢复）。</summary>
    public double ExpandedWidth => _expandedWidth;

    public ProbePanel Probe { get; }
    public BookmarkPanel Bookmarks { get; }
    public OffsetPanel Offset { get; }
    public MediaInfoPanel Media { get; }
    public AudioPanel Audio { get; }

    public bool MagnifierOn => _magnifierCheck.IsChecked == true;
    public event EventHandler? MagnifierToggled;

    public bool Collapsed { get; private set; }
    /// <summary>折叠/展开状态变化时触发，宿主应据此调整 Grid 列宽。</summary>
    public event Action<bool>? CollapsedChanged;

    public ToolsSidebar(ProbePanel probe, BookmarkPanel bookmarks, OffsetPanel offset, MediaInfoPanel media, AudioPanel audio)
    {
        Probe = probe; Bookmarks = bookmarks; Offset = offset; Media = media; Audio = audio;

        var collapse = new Button { Height = 20, FontSize = 10, Content = "◀", HorizontalAlignment = HorizontalAlignment.Right };
        ToolTip.SetTip(collapse, "折叠/展开");
        collapse.Click += (_, _) => ToggleCollapse();

        var tabsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        foreach (var (key, panel) in new[]
                 {
                     ("Tab_Probe", (Control)probe), ("Tab_Bookmarks", bookmarks),
                     ("Tab_Offset", offset), ("Tab_Media", media), ("Tab_Audio", audio),
                 })
        {
            var tab = new Button { Height = 24, FontSize = 11, Padding = new Thickness(8, 0), Content = LanguageManager.T(key) };
            tab.Click += (_, _) => Activate(panel);
            _tabs.Add((tab, panel));
            tabsRow.Children.Add(tab);
        }

        _magnifierCheck.Content = LanguageManager.T("Mag_Magnifier");
        _magnifierCheck.Margin = new Thickness(6, 4);
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

    /// <summary>切换折叠/展开状态。宿主应监听 CollapsedChanged 事件调整列宽。</summary>
    public void ToggleCollapse()
    {
        Collapsed = !Collapsed;
        CollapsedChanged?.Invoke(Collapsed);
    }

    /// <summary>展开侧栏（恢复展开宽度）。</summary>
    public void Expand()
    {
        if (!Collapsed) return;
        Collapsed = false;
        CollapsedChanged?.Invoke(false);
    }

    /// <summary>设置展开宽度（由宿主在首次布局或拖动分隔条后更新）。</summary>
    public void UpdateExpandedWidth(double width)
    {
        if (width >= PanelMinWidth) _expandedWidth = width;
    }

    private const double PanelMinWidth = 100; // 侧栏最小宽度（避免与 Layoutable.MinWidth 属性同名隐藏 CS0108）

    public void Activate(Control panel)
    {
        _active = panel;
        _content.Content = panel;
foreach (var (tab, p) in _tabs)
	            tab.Background = ReferenceEquals(p, panel)
	                ? new SolidColorBrush(Color.FromRgb(255, 200, 64))
	                : null;
	    }

    public void ActivateProbe() => Activate(Probe);
    public void ActivateBookmarks() => Activate(Bookmarks);
    public void ActivateOffset() => Activate(Offset);
    public void ActivateMedia() => Activate(Media);
    public void ActivateAudio() => Activate(Audio);

    public Control? Active => _active;
}