using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using _3FCompare.Core.Settings;

namespace _3FCompare;

/// <summary>主窗口的「工具侧栏几何」部分：三态（展开 / 图标栏 / 完全隐藏）、宽度记忆、
/// 拖拽回写、响应式自动折叠。
/// <para>自 MainWindow.axaml.cs 拆出（该类已约 1780 行，仍远超 08 计划的 1500 行目标）；
/// 拆分样板见 MainWindow.Capture.cs 与 MainWindow.SelfTest.cs。</para></summary>
public partial class MainWindow
{
    /// <summary>把任意入口（菜单面板项 / P / F6 快捷键）统一送到"展开并激活该面板"。
    /// 三态下这里**不需要**额外判断当前是不是 Hidden：<see cref="Controls.ToolsSidebar.Expand"/>
    /// 会把 Hidden 一并还原成 Expanded，随后的 ModeChanged 会把列宽与分割条同步过来。</summary>
    private void ShowSidebar() => _sidebar.Expand();

    // ══════════ 侧栏几何（三态 / 宽度记忆 / 拖拽回写 / 响应式自动折叠）══════════

    /// <summary>低于此宽度自动折叠侧栏，优先保障中央对比画面。</summary>
    private const double AutoCollapseThreshold = 1100;

    /// <summary>用户显式折叠/展开过之后，不再自动接管（避免与用户意图打架）。</summary>
    private bool _autoCollapseEnabled = true;

    /// <summary>当前折叠是否由窄窗自动触发。只有"我们收起的"才由我们展开，
    /// 否则会把用户在宽屏下主动折叠（并已持久化）的偏好强行还原。</summary>
    private bool _autoCollapsedByUs;

    /// <summary>正在从设置恢复侧栏几何，或正在由本文件程序化改写列宽。
    /// 恢复动作会触发 <c>ModeChanged</c>，若不屏蔽就会把刚读出来的值原样写回去——
    /// 每次启动白白落盘一次，且任何恢复期的中间态都可能污染用户配置。
    /// 同理，程序化写出的 48 / 0 若被当成"用户拖出来的宽度"回写成展开宽度，
    /// 下次展开就会只剩 48px 或直接消失。</summary>
    private bool _restoringSidebar;

    /// <summary>三态 → 列 0 宽度的唯一映射。写在一处，避免"某个入口忘了处理 Hidden"。</summary>
    private double SidebarWidthFor(SidebarMode mode) => mode switch
    {
        SidebarMode.Expanded => _sidebar.ExpandedWidth,
        SidebarMode.Rail => Controls.ToolsSidebar.RailWidth,
        _ => 0,
    };

    /// <summary>把三态落到列宽与两处可见性上（侧栏宿主 / 分割条）。</summary>
    private void ApplySidebarMode()
    {
        var mode = _sidebar.Mode;
        MainArea.ColumnDefinitions[0].Width =
            new GridLength(SidebarWidthFor(mode), GridUnitType.Pixel);
        // Hidden：侧栏与分割条都不留。Rail：侧栏（只剩图标栏）留着，但分割条要收 ——
        // 48px 的图标栏再挂一条可拖的分割条，拖起来只会把状态机拖进无效区间。
        SidebarHost.IsVisible = mode != SidebarMode.Hidden;
        SidebarSplitter.IsVisible = mode == SidebarMode.Expanded;
    }

    private void OnSidebarModeChanged(SidebarMode mode)
    {
        ApplySidebarMode();
        // 用户手动操作（折叠按钮 / 图标导航栏 / 菜单 / 快捷键）后交还控制权
        if (_sidebar.LastToggleByUser) _autoCollapseEnabled = false;
        if (_selfTestMode || _restoringSidebar) return;
        _settings.SidebarMode = mode;
        // 兼容字段同步维护：老版本读到的 SidebarCollapsed 仍是对的（Rail/Hidden 都算"已折叠"）
        _settings.SidebarCollapsed = mode != SidebarMode.Expanded;
        if (mode == SidebarMode.Expanded) _settings.SidebarWidth = (int)_sidebar.ExpandedWidth;
        SettingsStore.Save(_settings);
    }

    // ══════════ 拖拽回写（D2）══════════

    /// <summary>接上分割条的拖拽结束事件。挂 <c>Thumb.DragCompleted</c> 而不是监听
    /// <c>ColumnDefinition.Width</c> 的变化，是因为后者分不清"用户拖的"与"我们自己写的"：
    /// 每次切到 Rail/Hidden 都会写 48/0，一旦被当成拖动结果就会把展开宽度污染成 48 或 0。
    /// 拖拽结束事件只在真实拖动时触发，从结构上排除了这一类误判（另加 _restoringSidebar 兜底）。</summary>
    private void WireSidebarSplitter() =>
        SidebarSplitter.AddHandler(Thumb.DragCompletedEvent, OnSidebarSplitterDragCompleted);

    private void OnSidebarSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_restoringSidebar) return;

        var col = MainArea.ColumnDefinitions[0];
        // 取"请求宽度"而不是 ActualWidth：GridSplitter 拖拽写的就是一个像素型 GridLength，
        // 而 ActualWidth 要等一次布局才刷新（拖动刚结束的当帧还是旧值）。
        var width = col.Width.IsAbsolute ? col.Width.Value : col.ActualWidth;

        if (width < Controls.ToolsSidebar.PanelMinWidth)
        {
            // 拖到比"面板能用的最小宽度"还窄 ⇒ 用户要的是完全收起，而不是一条残条。
            // 这是"完全折叠"最自然的入口（往左一拖到底）。
            _sidebar.SetMode(SidebarMode.Hidden, byUser: true);
            return;
        }

        _sidebar.UpdateExpandedWidth(width);
        // 往回拖（同一次拖拽里又拖过阈值）⇒ 恢复 Expanded，宽度取拖动值
        _sidebar.SetMode(SidebarMode.Expanded, byUser: true);

        // SetMode 若因"已经就是 Expanded"而没有触发 ModeChanged，就不会落盘 ⇒ 这里补一次。
        if (_selfTestMode) return;
        _settings.SidebarMode = SidebarMode.Expanded;
        _settings.SidebarCollapsed = false;
        _settings.SidebarWidth = (int)width;
        SettingsStore.Save(_settings);
    }

    // ══════════ 菜单三项 + 快捷键循环 ══════════

    /// <summary>「视图 → 侧栏」三项的统一入口，写法与 <see cref="OnCompareModePreset"/> 一致
    ///（<c>Click</c> + <c>Tag</c> 字符串，不用 Command），保持本菜单风格统一。</summary>
    private void OnSidebarModePreset(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string preset }) return;
        var mode = preset switch
        {
            "rail" => SidebarMode.Rail,
            "hidden" => SidebarMode.Hidden,
            _ => SidebarMode.Expanded,
        };
        _sidebar.SetMode(mode, byUser: true);
        // 勾选一律以真实状态为准（Click 已把 IsChecked 取反，见 RefreshBottomBarChecks 的注释）
        RefreshSidebarChecks();
    }

    /// <summary>刷新「视图 → 侧栏」三项的勾选。菜单展开时（覆盖快捷键改过的情况）与
    /// 点击处理末尾都要调 —— 理由与 <see cref="RefreshBottomBarChecks"/> 完全相同。</summary>
    private void RefreshSidebarChecks()
    {
        var mode = _sidebar.Mode;
        MenuSidebarExpanded.IsChecked = mode == SidebarMode.Expanded;
        MenuSidebarRail.IsChecked = mode == SidebarMode.Rail;
        MenuSidebarHidden.IsChecked = mode == SidebarMode.Hidden;
    }

    /// <summary>「视图 → 侧栏」子菜单展开时刷新勾选。</summary>
    private void OnSidebarMenuOpened(object? sender, RoutedEventArgs e) => RefreshSidebarChecks();

    /// <summary>快捷键（Ctrl+B）循环三态：展开 → 图标栏 → 完全隐藏 → 展开。</summary>
    private void CycleSidebarMode()
    {
        _sidebar.CycleMode();
        RefreshSidebarChecks();
    }

    // ══════════ 自动折叠 ══════════

    private void AutoCollapseSidebar()
    {
        if (_sidebar is null || !_autoCollapseEnabled) return;
        // 布局未完成时不判定：首次 Bounds 变更时 Width 可能还是 0，
        // 会被当成"窄窗"先折叠一次，布局完成后再展开 —— 一次可见的抖动。
        if (Bounds.Width <= 0) return;
        var narrow = Bounds.Width < AutoCollapseThreshold;
        // 自动折叠只到 Rail（保留图标入口），**不**到 Hidden：窄窗下把工具入口整个抹掉
        // 会让用户无从发现侧栏还能回来；Hidden 只由用户的显式操作产生。
        if (narrow && _sidebar.Mode == SidebarMode.Expanded)
        {
            _sidebar.SetMode(SidebarMode.Rail);
            _autoCollapsedByUs = true;
        }
        else if (!narrow && _sidebar.Mode == SidebarMode.Rail && _autoCollapsedByUs)
        {
            _sidebar.SetMode(SidebarMode.Expanded);
            _autoCollapsedByUs = false;
        }
    }

    /// <summary>从设置恢复侧栏几何（展开宽度 + 三态），由构造函数调用。
    /// 主流工具侧栏均为「可拖拽 + 可记忆」；此前宽度写死 264、折叠态不落盘，
    /// 每次启动都要重新拖一次。</summary>
    internal void RestoreSidebarGeometry()
    {
        if (_settings.SidebarWidth is > (int)Controls.ToolsSidebar.RailWidth)
            _sidebar.UpdateExpandedWidth(_settings.SidebarWidth.Value);
        // 老文件没有 SidebarMode（null）⇒ 由 SidebarCollapsed 推导，不丢既有偏好。
        // 这一步是"可空枚举"设计的全部意义所在：null 与"显式 Expanded"必须可区分。
        var mode = _settings.SidebarMode
                   ?? (_settings.SidebarCollapsed ? SidebarMode.Rail : SidebarMode.Expanded);
        // 恢复动作会触发 ModeChanged → OnSidebarModeChanged，
        // 必须屏蔽其回写，否则刚读出来的值又被原样写回磁盘（每次启动一次无谓落盘）。
        _restoringSidebar = true;
        try
        {
            _sidebar.SetMode(mode);
            // SetMode 在"值未变"时不会触发 ModeChanged ⇒ 列宽得自己落一次
            ApplySidebarMode();
        }
        finally { _restoringSidebar = false; }
    }
}
