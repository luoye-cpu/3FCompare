using System.ComponentModel;
using _3FCompare.Core.Backend;

namespace _3FCompare.App.Controls;

/// <summary>对比网格（1~9 路，1x1~3x3 布局，点击选中，单屏模式）。</summary>
public sealed class CompareGridView : Control
{
    private readonly List<PlayerSurface> _surfaces = new();
    private int _selectedIndex = -1;
    private bool _singleView; // 单屏（只显示选中路）

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
        BackColor = Color.FromArgb(10, 10, 12);
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
            surface.SurfaceClicked += (_, _) => { if (_surfaces.IndexOf(surface) is var i && i >= 0) SelectedIndex = i; };
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

    /// <summary>布局计算：根据路数与单屏状态确定行列。</summary>
    public static (int cols, int rows) ComputeGrid(int count, bool singleView)
    {
        if (singleView) return (1, 1);
        return count switch
        {
            1 => (1, 1),
            2 => (2, 1),
            3 => (3, 1),
            4 => (2, 2),
            5 => (3, 2),
            6 => (3, 2),
            _ => (3, 3), // 7,8,9
        };
    }

    public void LayoutSurfaces()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var (cols, rows) = ComputeGrid(_surfaces.Count, _singleView);

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

        var cellW = ClientSize.Width / cols;
        var cellH = ClientSize.Height / rows;
        for (var i = 0; i < _surfaces.Count; i++)
        {
            _surfaces[i].Visible = true;
            var r = i / cols;
            var c = i % cols;
            _surfaces[i].Bounds = new Rectangle(c * cellW, r * cellH, cellW, cellH);
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
            using var brush = new SolidBrush(Color.FromArgb(40, 40, 46));
            using var font = new Font("Microsoft YaHei UI", 14f);
            var text = "点击「打开视频」或拖拽文件到此处\n支持 1~9 路对比";
            e.Graphics.DrawString(text, font, brush, ClientRectangle,
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
    }
}