using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.Core.Display;
using _3FCompare.Localization;

namespace _3FCompare.Views;

/// <summary>「选择路」对话框（二级窗口，模态）：指定拉动各条带<b>显示哪几路、按什么顺序</b>。
///
/// <para><b>它解决什么</b>：用户要求"手动指定多路情况"。拉动默认是"第 i 条带 = 第 i 路"（恒等），
/// 但用户常常只想对比其中几路（例如 5 路素材里只看第 1、3、5 路），
/// 或想让某一路<b>重复出现</b>在两条带上做细节对照。</para>
///
/// <para><b>允许重复，且允许不相邻</b>（用户决策，见 docs/51 §八）：同一路可以出现在多条带上。
/// 相邻重复时该路的区域是连通并集；<b>不相邻重复</b>需要镜像会话才能同时呈现两处
/// （一个子窗口只能在一个位置）—— 本对话框不阻止用户这么选，但会在确认时给出提示，
/// 让"看起来配好了、实际只显示一处"这种落差不会静默发生。</para>
///
/// <para><b>为什么是普通模态窗</b>：与布局编辑器同一理由（见
/// <see cref="MosaicLayoutEditorWindow"/> 的类注释）——普通模态窗是**已被日常使用验证**的路径，
/// 而覆盖窗口上的控件（<c>WS_EX_NOACTIVATE</c> 无边框窗）在本项目从未实测（docs/30:189 P6）。
/// 注意边界：<c>--selftest</c> 探针不合成指针事件、不抢焦点，因此它只证明"控件存在 +
/// 生产处理器可被驱动"，<b>不</b>声称覆盖"真人用鼠标点中按钮"——那一条仍需人工目视。</para>
/// </summary>
public sealed class RoutePickerWindow : Window
{
    private readonly int _laneCount;
    private readonly int _routeCount;

    /// <summary>每行一个"这条带显示第几路"的下拉框（行号 = 带号）。</summary>
    private readonly List<ComboBox> _rows = new();

    private readonly StackPanel _rowsHost = new() { Spacing = 6 };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    /// <summary>确定后的带→路映射（取消时为 null）。</summary>
    public int[]? Result { get; private set; }

    public bool Accepted { get; private set; }

    private static string T(string key) => LanguageManager.T(key);

    /// <param name="current">当前带→路映射（null = 恒等）。</param>
    /// <param name="laneCount">当前带数（= 要配置的行数）。</param>
    /// <param name="routeCount">可用路数（决定下拉项数）。</param>
    public RoutePickerWindow(int[]? current, int laneCount, int routeCount)
    {
        _laneCount = Math.Clamp(laneCount, 1, WipeBands.MaxBands);
        _routeCount = Math.Max(1, routeCount);

        Title = T("Routes_Title");
        Width = 460;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        ShowInTaskbar = false;

        Content = BuildUi(current);
        UpdateStatus();
    }

    private Control BuildUi(int[]? current)
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // ── 底部按钮 ──
        var ok = new Button { Content = T("Layout_Confirm"), MinWidth = 88 };
        var cancel = new Button { Content = T("Layout_Cancel"), MinWidth = 88 };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // ── 顶部说明 + 带数增减 ──
        var top = new StackPanel { Spacing = 6 };
        top.Children.Add(new TextBlock
        {
            Text = T("Routes_Hint"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var countRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        countRow.Children.Add(new TextBlock
        {
            Text = T("Routes_BandCount"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var addBtn = new Button { Content = "+" , MinWidth = 36 };
        var delBtn = new Button { Content = "−", MinWidth = 36 };
        addBtn.Click += (_, _) => AddRow();
        delBtn.Click += (_, _) => RemoveRow();
        countRow.Children.Add(addBtn);
        countRow.Children.Add(delBtn);
        top.Children.Add(countRow);

        var resetBtn = new Button { Content = T("Routes_ResetIdentity") };
        resetBtn.Click += (_, _) => ResetIdentity();
        countRow.Children.Add(resetBtn);

        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // ── 中间：每带一行下拉 ──
        for (var i = 0; i < _laneCount; i++) AppendRow(i, current);
        root.Children.Add(new ScrollViewer { Content = _rowsHost });

        // 状态行放底部按钮之上
        var statusHost = new Border { Margin = new Thickness(0, 8, 0, 0), Child = _status };
        DockPanel.SetDock(statusHost, Dock.Bottom);
        root.Children.Add(statusHost);

        return root;
    }

    private void AppendRow(int index, int[]? current)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        // 带号显示用 1 起（与"第 N 条带"的用户口径一致）
        row.Children.Add(new TextBlock
        {
            Text = LanguageManager.IsEnglish ? $"Band {index + 1}:" : $"第 {index + 1} 条带：",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 92,
        });

        var combo = new ComboBox { MinWidth = 160 };
        for (var r = 0; r < _routeCount; r++)
            combo.Items.Add(LanguageManager.IsEnglish ? $"Route {r + 1}" : $"第 {r + 1} 路");

        // 初值：用户已有的映射优先；否则恒等（第 i 带 = 第 i 路）
        var initial = current is { Length: > 0 } && index < current.Length ? current[index] : index;
        combo.SelectedIndex = Math.Clamp(initial, 0, _routeCount - 1);
        combo.SelectionChanged += (_, _) => UpdateStatus();
        row.Children.Add(combo);

        _rowsHost.Children.Add(row);
        _rows.Add(combo);
    }

    private void AddRow()
    {
        if (_rows.Count >= WipeBands.MaxBands)
        {
            SetStatus(LanguageManager.IsEnglish
                ? $"At most {WipeBands.MaxBands} bands."
                : $"最多 {WipeBands.MaxBands} 条带。");
            return;
        }
        AppendRow(_rows.Count, null);
        UpdateStatus();
    }

    private void RemoveRow()
    {
        if (_rows.Count <= 1)
        {
            SetStatus(LanguageManager.IsEnglish ? "At least one band." : "至少要有一条带。");
            return;
        }
        _rowsHost.Children.RemoveAt(_rows.Count - 1);
        _rows.RemoveAt(_rows.Count - 1);
        UpdateStatus();
    }

    private void ResetIdentity()
    {
        for (var i = 0; i < _rows.Count; i++)
            _rows[i].SelectedIndex = Math.Clamp(i, 0, _routeCount - 1);
        UpdateStatus();
    }

    /// <summary>当前映射（供确认与自测读取）。</summary>
    internal int[] CurrentRoutes()
    {
        var r = new int[_rows.Count];
        for (var i = 0; i < _rows.Count; i++) r[i] = Math.Clamp(_rows[i].SelectedIndex, 0, _routeCount - 1);
        return r;
    }

    /// <summary>状态行：写清"哪几路参与、是否有重复、是否不相邻"。
    ///
    /// <para><b>为什么必须写出来</b>：重复路与不相邻重复在拉动下的呈现后果不同
    /// （不相邻需要镜像，本切片尚未接入 ⇒ 只会显示一处）。若不说，用户配好之后
    /// 看到"只出来一处"会以为是 bug。这与本项目"不静默"的一贯口径一致。</para></summary>
    private void UpdateStatus()
    {
        var routes = CurrentRoutes();
        var distinct = new List<int>();
        foreach (var r in routes) if (!distinct.Contains(r)) distinct.Add(r);

        var parts = new List<string>
        {
            LanguageManager.IsEnglish
                ? $"{routes.Length} bands, {distinct.Count} route(s) involved."
                : $"{routes.Length} 条带，{distinct.Count} 路参与。",
        };

        // 是否有重复
        var dup = routes.Length != distinct.Count;
        if (dup)
            parts.Add(LanguageManager.IsEnglish
                ? "Some routes appear more than once."
                : "有路重复出现。");

        if (dup && HasNonAdjacentRepeat(routes))
            parts.Add(LanguageManager.IsEnglish
                ? "⚠ A route appears in non-adjacent bands: only one of them can be shown for now."
                : "⚠ 有路出现在不相邻的带上：目前只能显示其中一处。");

        SetStatus(string.Join(" ", parts));
    }

    /// <summary>是否存在"同一路出现在不相邻的带上"。</summary>
    private static bool HasNonAdjacentRepeat(int[] routes)
    {
        var first = new Dictionary<int, int>();
        var last = new Dictionary<int, int>();
        for (var i = 0; i < routes.Length; i++)
        {
            if (!first.ContainsKey(routes[i])) first[routes[i]] = i;
            last[routes[i]] = i;
        }
        foreach (var kv in first)
        {
            var f = kv.Value;
            var l = last[kv.Key];
            if (l - f <= 1) continue;                    // 相邻（或只出现一次）
            for (var i = f + 1; i < l; i++)
                if (routes[i] != kv.Key) return true;    // 中间夹了别的路 ⇒ 不相邻
        }
        return false;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void Accept()
    {
        var routes = CurrentRoutes();
        if (routes.Length == 0)
        {
            SetStatus(LanguageManager.IsEnglish ? "No bands configured." : "没有配置任何条带。");
            return;
        }
        Result = routes;
        Accepted = true;
        Close();
    }

    // ────────── 自测探针 ──────────

    internal int RowCountForTest => _rows.Count;
    internal string StatusTextForTest => _status.Text ?? string.Empty;

    internal void SetRowForTest(int index, int route)
    {
        if (index < 0 || index >= _rows.Count) return;
        _rows[index].SelectedIndex = Math.Clamp(route, 0, _routeCount - 1);
        UpdateStatus();
    }

    internal void AddRowForTest() => AddRow();
    internal void AcceptForTest() => Accept();
}
