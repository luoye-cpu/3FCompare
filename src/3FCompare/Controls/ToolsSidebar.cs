using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Core.Settings;
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
    /// <summary>内容区外框（上边框 + 上内边距）。三态里它是**整块**随状态隐藏的对象 ——
    /// 只藏 <c>_content</c> 会把它那 1px 上边框与 10+10 内边距留在原地，
    /// 于是折叠后图标栏下方残留一条横线加约 41px 空白（D3）。</summary>
    private readonly Border _contentHost;
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

    /// <summary>当前三态（展开 / 图标栏 / 完全隐藏）。</summary>
    public SidebarMode Mode { get; private set; } = SidebarMode.Expanded;

    /// <summary>是否处于"非展开"态。保留这个派生属性而不是让调用方各写一遍
    /// <c>Mode != Expanded</c>：既有调用点（窄窗自动折叠、布局断言、菜单"仅网格"）问的都是
    /// "侧栏是不是收起来了"，与收成图标栏还是完全隐藏无关。</summary>
    public bool Collapsed => Mode != SidebarMode.Expanded;

    /// <summary>三态变更通知（与旧的 <c>CollapsedChanged</c> 合并：三态下 bool 承载不了信息）。</summary>
    public event Action<SidebarMode>? ModeChanged;

    /// <summary>本次变更是否由用户交互触发（折叠按钮 / 图标导航栏 / 菜单 / 快捷键）。
    /// 程序化恢复（<see cref="SetMode"/> 不带 byUser）为 false，供主窗口判断是否停用窄窗自动折叠。</summary>
    public bool LastToggleByUser { get; private set; }

    // ══════════ 自测探针（internal：只对同程序集的 MainWindow.SelfTest 开放）══════════

    /// <summary>图标导航栏是否可见（三态断言用）。</summary>
    internal bool IsRailVisible => _rail.IsVisible;

    /// <summary>内容区整块（含上边框）是否可见 —— D3 的回归判据。</summary>
    internal bool IsContentHostVisible => _contentHost.IsVisible;

    /// <summary>取某面板在图标导航栏里的入口按钮，供自测驱动真实 Click 路径。</summary>
    internal Button? RailButtonFor(Control panel)
    {
        foreach (var (_, _, rail, item) in _tabs)
            if (ReferenceEquals(item, panel)) return rail;
        return null;
    }

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

        _contentHost = new Border
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
        Grid.SetRow(_contentHost, 2);
        Grid.SetRow(_magnifierHost, 3);
        layout.Children.Add(header);
        layout.Children.Add(_navigation);
        layout.Children.Add(_rail);
        layout.Children.Add(_contentHost);
        layout.Children.Add(_magnifierHost);

        Content = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = layout,
        };

        ThemePalette.SetBrush((Border)Content, Border.BorderBrushProperty, "DividerBrush");
        ThemePalette.SetBrush(_contentHost, Border.BorderBrushProperty, "DividerBrush");
        // 主题切换后：状态色是命令式赋值的，不会自动跟随 ⇒ 重跑一遍
        ActualThemeVariantChanged += (_, _) =>
        {
            if (_active is not null) Activate(_active);
            SyncRailMagnifier();
        };

        ApplyMode();
        Activate(probe);
        ApplyLanguage();
        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, s =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                s.ApplyLanguage();
                s.ApplyMode();
            }));
    }

    /// <summary>折叠按钮：Expanded ↔ Rail（保留图标入口，符合主流工具习惯）。
    /// **不经过 Hidden** —— 完全折叠是拖分隔条 / 菜单 / 快捷键的职责，
    /// 让一个按钮承担"展开↔图标栏↔全隐"三跳会让每次点击的结果无法预测。</summary>
    public void ToggleCollapse() =>
        SetMode(Mode == SidebarMode.Expanded ? SidebarMode.Rail : SidebarMode.Expanded, byUser: true);

    public void Expand() => SetMode(SidebarMode.Expanded, byUser: true);

    /// <summary>快捷键循环三态：展开 → 图标栏 → 完全隐藏 → 展开。
    /// 固定顺序（而不是"切换上一个"）让连按的结果可预测，也与菜单三项的排列一致。</summary>
    public void CycleMode() =>
        SetMode(Mode switch
        {
            SidebarMode.Expanded => SidebarMode.Rail,
            SidebarMode.Rail => SidebarMode.Hidden,
            _ => SidebarMode.Expanded,
        }, byUser: true);

    /// <summary>设定三态。程序化恢复（持久化 / 窄窗自动折叠）用 <c>byUser: false</c>，
    /// 使主窗口不至于把"我们收起的"当成用户意图而停用自动折叠。
    ///
    /// <para><b>离开展开态前顺手记下当前宽度</b>：用户可能刚拖过分隔条，
    /// 展开时应当还原他拖出来的宽度而不是启动时的旧值。
    /// 反方向（Rail/Hidden → Expanded）时 <c>Bounds.Width</c> 只有 48 或 0，
    /// 低于 <see cref="PanelMinWidth"/> 会被 <see cref="UpdateExpandedWidth"/> 自动忽略，
    /// 因此不需要在这里判断方向。</para></summary>
    public void SetMode(SidebarMode mode, bool byUser = false)
    {
        LastToggleByUser = byUser;
        if (Mode == mode) { ApplyMode(); return; }
        if (mode != SidebarMode.Hidden) UpdateExpandedWidth(Bounds.Width);
        Mode = mode;
        ApplyMode();
        ModeChanged?.Invoke(Mode);
    }

    public void UpdateExpandedWidth(double width)
    {
        if (width >= PanelMinWidth) _expandedWidth = width;
    }

    /// <summary>侧栏的"最小可用宽度"。低于它就没有任何面板能正常排版了 ——
    /// 主窗口据此把"拖到比这更窄"解读成"用户想完全收起"（切 Hidden），
    /// 而不是留下一个连标题都显示不全的残条。</summary>
    public const double PanelMinWidth = 160;

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

    /// <summary>把三态落到可见性上。
    ///
    /// <para><b>内容区整块隐藏</b>（<c>_contentHost</c>，不是只藏 <c>_content</c>）：
    /// 只藏内层会把外框的 1px 上边框与上下各 10px 内边距留下，折叠后图标栏下方多出一条横线
    /// 和约 41px 空白（D3），同时把图标栏的行高从 206 撑到 248（D6 的行高跳变）。</para>
    ///
    /// <para><b>绝不把 <c>_content.Content</c> 置 null</b>：五个面板是共享实例，
    /// 外部（探针读数、书签删除、偏移校准）仍会调用它们；摘掉内容会让面板停止参与布局、
    /// 状态（滚动位置、输入框内容）与定时器订阅一并失效（D4）。
    /// 三态只改"看得见看不见"，不改"挂没挂上"。</para></summary>
    private void ApplyMode()
    {
        var expanded = Mode == SidebarMode.Expanded;
        _title.IsVisible = expanded;
        _navigation.IsVisible = expanded;
        _contentHost.IsVisible = expanded;
        _magnifierHost.IsVisible = expanded;
        _rail.IsVisible = Mode == SidebarMode.Rail;
        _collapseIcon.Data = AppIcons.Get(expanded ? "ChevronLeft" : "ChevronRight");
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
