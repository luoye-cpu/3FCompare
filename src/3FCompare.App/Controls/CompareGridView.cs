using System.ComponentModel;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>对比网格（1~9 路，1x1~3x3 布局，点击选中，单屏模式）。</summary>
public sealed class CompareGridView : Control
{
    private readonly List<PlayerSurface> _surfaces = new();
    private int _selectedIndex = -1;
    private bool _singleView; // 单屏（只显示选中路）
    private int _overrideCols; // 布局预设列（0 = 未设置，自动）
    private int _overrideRows;

    public event EventHandler? SelectionChanged;

    public bool RealMode { get; }

    public int Count => _surfaces.Count;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            foreach (var s in _surfaces) s.Selected = false;
            if (value >= 0 && value < _surfaces.Count) _surfaces[value].Selected = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            LayoutSurfaces();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SingleView
    {
        get => _singleView;
        set { _singleView = value; LayoutSurfaces(); }
    }

    public CompareGridView(bool realMode)
    {
        RealMode = realMode;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = AppTheme.Colors.CanvasBackgroundDark;
        DoubleBuffered = true;
    }

    /// <summary>设置路数（1~9）。多余 surface 复用/创建，多余移除。</summary>
    public void SetCount(int count)
    {
        count = Math.Clamp(count, 1, 9);
        while (_surfaces.Count > count)
        {
            var last = _surfaces[^1];
            Controls.Remove(last);
            last.Dispose();
            _surfaces.RemoveAt(_surfaces.Count - 1);
        }
        while (_surfaces.Count < count)
        {
            var surface = new PlayerSurface(_surfaces.Count, RealMode);
            surface.SurfaceClicked += (s, _) =>
            {
                var idx = _surfaces.IndexOf((PlayerSurface)s!);
                if (Geometry.IsValidIndex(idx, _surfaces.Count)) SelectedIndex = idx;
            };
            _surfaces.Add(surface);
            Controls.Add(surface);
        }
        if (_selectedIndex >= count) _selectedIndex = count - 1;
        foreach (var s in _surfaces) s.Selected = false;
        if (_selectedIndex >= 0) _surfaces[_selectedIndex].Selected = true;
        LayoutSurfaces();
    }

    public IReadOnlyList<PlayerSurface> Surfaces => _surfaces;

    public PlayerSurface? GetSurface(int index)
        => index >= 0 && index < _surfaces.Count ? _surfaces[index] : null;

    /// <summary>布局计算：根据路数与单屏状态确定行列（委托给 Core 纯逻辑，可单测）。</summary>
    public static (int cols, int rows) ComputeGrid(int count, bool singleView)
        => GridLayout.ComputeGrid(count, singleView);

    /// <summary>设置网格布局预设（列×行）。路数多于容量时自动回退到 ComputeGrid。</summary>
    public void SetGridLayout(int cols, int rows)
    {
        _overrideCols = Math.Max(1, cols);
        _overrideRows = Math.Max(1, rows);
        LayoutSurfaces();
    }

    /// <summary>清除布局预设，恢复自动布局。</summary>
    public void ResetGridLayout()
    {
        _overrideCols = 0;
        _overrideRows = 0;
        LayoutSurfaces();
    }

    /// <summary>解析最终布局：单屏 (1,1)；预设可用时用预设；否则自动计算。</summary>
    private (int cols, int rows) ResolveGrid(int count, bool singleView)
        => GridLayout.ResolveGrid(count, singleView, _overrideCols, _overrideRows);

    public void LayoutSurfaces()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var (cols, rows) = ResolveGrid(_surfaces.Count, _singleView);

        // 单屏：只显示选中路
        if (_singleView)
        {
            var sel = _selectedIndex >= 0 ? _selectedIndex : 0;
            for (var i = 0; i < _surfaces.Count; i++)
            {
                _surfaces[i].Visible = i == sel;
                if (i == sel) _surfaces[i].Bounds = ClientRectangle;
            }
            return;
        }

        for (var i = 0; i < _surfaces.Count; i++)
        {
            _surfaces[i].Visible = true;
            _surfaces[i].Bounds = Geometry.CellRect(i, cols, rows, ClientRectangle);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutSurfaces();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_surfaces.Count == 0)
        {
            using var brush = new SolidBrush(AppTheme.Colors.TextMuted);
            using var font = new Font("Microsoft YaHei UI", 14f);
            var text = LanguageManager.T("Grid_Empty");
            e.Graphics.DrawString(text, font, brush, ClientRectangle,
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
    }
}