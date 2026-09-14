using Avalonia.Controls;

namespace _3FCompare;

/// <summary>主窗口的「工具侧栏几何」部分：宽度记忆 / 折叠 / 响应式自动折叠。
/// <para>自 MainWindow.axaml.cs 拆出（该类已约 1780 行，仍远超 08 计划的 1500 行目标）；
/// 拆分样板见 MainWindow.Capture.cs 与 MainWindow.SelfTest.cs。</para></summary>
public partial class MainWindow
{
    private void ShowSidebar()
    {
        _sidebar.Expand();
        SidebarSplitter.IsVisible = true;
    }

    // ══════════ 侧栏几何（宽度记忆 / 折叠 / 响应式自动折叠）══════════

    /// <summary>低于此宽度自动折叠侧栏，优先保障中央对比画面。</summary>
    private const double AutoCollapseThreshold = 1100;

    /// <summary>用户显式折叠/展开过之后，不再自动接管（避免与用户意图打架）。</summary>
    private bool _autoCollapseEnabled = true;

    /// <summary>当前折叠是否由窄窗自动触发。只有"我们收起的"才由我们展开，
    /// 否则会把用户在宽屏下主动折叠（并已持久化）的偏好强行还原。</summary>
    private bool _autoCollapsedByUs;

    /// <summary>正在从设置恢复侧栏几何。
    /// 恢复动作会触发 <c>CollapsedChanged</c>，若不屏蔽就会把刚读出来的值原样写回去——
    /// 每次启动白白落盘一次，且任何恢复期的中间态都可能污染用户配置。</summary>
    private bool _restoringSidebar;

    private void ApplySidebarWidth(bool collapsed) =>
        MainArea.ColumnDefinitions[0].Width = new GridLength(
            collapsed ? Controls.ToolsSidebar.RailWidth : _sidebar.ExpandedWidth, GridUnitType.Pixel);

    private void OnSidebarCollapsedChanged(bool collapsed)
    {
        ApplySidebarWidth(collapsed);
        SidebarSplitter.IsVisible = !collapsed;
        // 用户手动操作（折叠按钮 / 图标导航栏）后交还控制权
        if (_sidebar.LastToggleByUser) _autoCollapseEnabled = false;
        if (_selfTestMode || _restoringSidebar) return;
        _settings.SidebarCollapsed = collapsed;
        if (!collapsed) _settings.SidebarWidth = (int)_sidebar.ExpandedWidth;
        _3FCompare.Core.Settings.SettingsStore.Save(_settings);
    }

    private void AutoCollapseSidebar()
    {
        if (_sidebar is null || !_autoCollapseEnabled) return;
        // 布局未完成时不判定：首次 Bounds 变更时 Width 可能还是 0，
        // 会被当成"窄窗"先折叠一次，布局完成后再展开 —— 一次可见的抖动。
        if (Bounds.Width <= 0) return;
        var narrow = Bounds.Width < AutoCollapseThreshold;
        if (narrow && !_sidebar.Collapsed)
        {
            _sidebar.SetCollapsed(true);
            _autoCollapsedByUs = true;
        }
        else if (!narrow && _sidebar.Collapsed && _autoCollapsedByUs)
        {
            _sidebar.SetCollapsed(false);
            _autoCollapsedByUs = false;
        }
    }

    /// <summary>从设置恢复侧栏几何（展开宽度 + 折叠态），由构造函数调用。
    /// 主流工具侧栏均为「可拖拽 + 可记忆」；此前宽度写死 264、折叠态不落盘，
    /// 每次启动都要重新拖一次。</summary>
    internal void RestoreSidebarGeometry()
    {
        if (_settings.SidebarWidth is > (int)Controls.ToolsSidebar.RailWidth)
            _sidebar.UpdateExpandedWidth(_settings.SidebarWidth.Value);
        // 恢复动作会触发 CollapsedChanged → OnSidebarCollapsedChanged，
        // 必须屏蔽其回写，否则刚读出来的值又被原样写回磁盘（每次启动一次无谓落盘）。
        _restoringSidebar = true;
        try
        {
            ApplySidebarWidth(_settings.SidebarCollapsed);
            _sidebar.SetCollapsed(_settings.SidebarCollapsed);
        }
        finally { _restoringSidebar = false; }
    }
}
