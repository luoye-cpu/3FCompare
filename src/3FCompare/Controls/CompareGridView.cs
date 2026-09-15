using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using _3FCompare.App;
using _3FCompare.Core.Display;

namespace _3FCompare.Controls;

/// <summary>多路对比网格容器（WinForms CompareGridView 对应）。
/// 1~9 路 PlayerSurface 等分布局；布局解析复用 Core.Display.GridLayout；
/// 单屏模式只显示选中路；空态绘制本地化提示。</summary>
public sealed class CompareGridView : Control
{
    private readonly List<PlayerSurface> _surfaces = new();
    private readonly TextBlock _hint;
    private bool _singleView;
    private string _preset = "auto";
    private int _selectedIndex = -1;
    private int? _presetCols, _presetRows;

    public event EventHandler? SelectionChanged;
    /// <summary>新表面创建时触发（MainWindow 订阅以绑定鼠标/缩放事件）。</summary>
    public event Action<PlayerSurface>? SurfaceCreated;

    public IReadOnlyList<PlayerSurface> Surfaces => _surfaces;
    public int Count => _surfaces.Count;
    public bool SingleView
    {
        get => _singleView;
        set { _singleView = value; InvalidateVisual(); Relayout(); }
    }
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            if (_selectedIndex >= 0 && _selectedIndex < _surfaces.Count)
                _surfaces[_selectedIndex].Selected = false;
            _selectedIndex = value;
            if (_selectedIndex >= 0 && _selectedIndex < _surfaces.Count)
                _surfaces[_selectedIndex].Selected = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }

    public CompareGridView()
    {
        _hint = new TextBlock
        {
            Text = LanguageManager.T("Grid_Empty"),
            Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 150)),
            FontSize = 16,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        VisualChildren.Add(_hint);
        LogicalChildren.Add(_hint);
        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, g => g.OnLanguageChanged(null, EventArgs.Empty));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _hint.Text = LanguageManager.T("Grid_Empty"));

    /// <summary>设置路数（增删 PlayerSurface；新增路继承 RealMode）。</summary>
    public void SetCount(int count, bool realMode)
    {
        count = Math.Clamp(count, 0, 9);
        while (_surfaces.Count < count)
        {
            var s = new PlayerSurface(_surfaces.Count, realMode)
            {
                Width = double.NaN, Height = double.NaN,
            };
                        // 选中由 MainWindow 统一处理
            SurfaceCreated?.Invoke(s);
            _surfaces.Add(s);
            VisualChildren.Add(s);
            LogicalChildren.Add(s);
        }
        while (_surfaces.Count > count)
        {
            var s = _surfaces[^1];
            s.DetachSession();
            _surfaces.RemoveAt(_surfaces.Count - 1);
            VisualChildren.Remove(s);
            LogicalChildren.Remove(s);
            if (_selectedIndex >= _surfaces.Count) SelectedIndex = _surfaces.Count - 1;
        }
        if (_selectedIndex < 0 && _surfaces.Count > 0) SelectedIndex = 0;
        _hint.IsVisible = _surfaces.Count == 0;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public PlayerSurface? GetSurface(int i) => i >= 0 && i < _surfaces.Count ? _surfaces[i] : null;

    /// <summary>当前网格预设名（"2x1"/"2x2"/"3x3"/"auto"）。
    /// 供自测断言"会话还原是否真的应用了预设"——只看 SingleView 抓不到 2x2/3x3 的还原缺失。</summary>
    internal string Preset => _preset;

    /// <summary>设置网格预设（"2x1"/"2x2"/"3x3"/"auto"）。</summary>
    public void SetGridLayout(string preset)
    {
        // 复用 Core 的双向映射，不在 UI 再写一份 switch——
        // 两份 switch 迟早漂移（docs/14 §1.1：PresetOf 漏了 "2x1" 就是这么来的）。
        var (cols, rows) = GridLayout.OverrideOf(preset);
        _presetCols = cols > 0 ? cols : null;
        _presetRows = rows > 0 ? rows : null;
        _preset = GridLayout.PresetOf(GridLayout.CodeFromPreset(preset, false));
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 计算每格尺寸（而不是用整体 availableSize），避免 DesiredSize 膨胀导致布局异常
        var (cols, rows) = GridLayout.ResolveGrid(_surfaces.Count, _singleView,
            _presetCols ?? 0, _presetRows ?? 0);
        if (_singleView) (cols, rows) = (1, 1);
        var cellW = cols > 0 ? availableSize.Width / cols : availableSize.Width;
        var cellH = rows > 0 ? availableSize.Height / rows : availableSize.Height;
        var cellSize = new Size(Math.Max(0, cellW), Math.Max(0, cellH));

        foreach (var s in _surfaces)
            s.Measure(cellSize);
        _hint.Measure(availableSize);

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _hint.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        if (_surfaces.Count == 0) return finalSize;

        var (cols, rows) = GridLayout.ResolveGrid(_surfaces.Count, _singleView,
            _presetCols ?? 0, _presetRows ?? 0);
        if (_singleView) (cols, rows) = (1, 1);

        var cw = finalSize.Width / cols;
        var ch = finalSize.Height / rows;
        for (var i = 0; i < _surfaces.Count; i++)
        {
            var visible = !_singleView || i == _selectedIndex;
            var s = _surfaces[i];
            s.IsVisible = visible;
            if (!visible) { s.Arrange(new Rect(0, 0, 0, 0)); continue; }
            var visibleIndex = _singleView ? 0 : i;
            var col = visibleIndex % cols;
            var row = visibleIndex / cols;
            // 每格留 1px 缝隙（选中边框不互相覆盖）
            var r = new Rect(col * cw + 1, row * ch + 1, cw - 2, ch - 2);
            s.Arrange(r);
        }
        return finalSize;
    }

    private void Relayout() => InvalidateMeasure();
}
