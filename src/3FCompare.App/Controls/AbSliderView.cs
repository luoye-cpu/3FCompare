using System.ComponentModel;
using _3FCompare.Core.Backend;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>A-B 滑块（F15，ICAT 招牌）：两路视频同区域并排渲染，
/// 中央滑块决定「左侧显示多少路A/路B」——拖动时两侧内容互补，逐区域观察差异。
/// 实现：单个 Surface 上绘制两路放大画面 + 滑块分隔线（模拟模式直接自绘合成）。</summary>
public sealed class AbSliderView : Control
{
    private readonly CompareGridView _grid;
    private double _slider = 0.5; // 0..1，A 路占左侧比例
    private bool _dragging;
    private int _aIndex;
    private int _bIndex = 1;

    public event EventHandler? SliderChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Slider
    {
        get => _slider;
        set { _slider = Math.Clamp(value, 0, 1); Invalidate(); SliderChanged?.Invoke(this, EventArgs.Empty); }
    }

    public AbSliderView(CompareGridView grid)
    {
        _grid = grid;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = AppTheme.Colors.CanvasBackground;
        DoubleBuffered = true;
        Height = 480;
        Cursor = Cursors.VSplit;
    }

    public void SetPair(int aIndex, int bIndex)
    {
        _aIndex = aIndex;
        _bIndex = bIndex;
        Invalidate();
    }

    public int AIndex => _aIndex;
    public int BIndex => _bIndex;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var rect = ClientRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var splitX = (int)(rect.Width * _slider);

        // 左半：A 路（合成模拟画面）
        PaintSlot(g, _aIndex, new Rectangle(0, 0, splitX, rect.Height));
        // 右半：B 路
        PaintSlot(g, _bIndex, new Rectangle(splitX, 0, rect.Width - splitX, rect.Height));

        // 分隔滑块（垂直）
        using var sliderPen = new Pen(AppTheme.Colors.Accent, 3);
        g.DrawLine(sliderPen, splitX, 0, splitX, rect.Height);
        // 滑块手柄
        using var handleBrush = new SolidBrush(AppTheme.Colors.Accent);
        var handleRect = new Rectangle(splitX - 14, rect.Height / 2 - 20, 28, 40);
        g.FillRectangle(handleBrush, handleRect);
        using var whitePen = new Pen(Color.White, 2);
        g.DrawLine(whitePen, splitX - 5, rect.Height / 2 - 8, splitX - 5, rect.Height / 2 + 8);
        g.DrawLine(whitePen, splitX, rect.Height / 2 - 8, splitX, rect.Height / 2 + 8);
        g.DrawLine(whitePen, splitX + 5, rect.Height / 2 - 8, splitX + 5, rect.Height / 2 + 8);

        // 角标
        using var tagFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        g.DrawString($"A [{(char)('1' + _aIndex)}]", tagFont, Brushes.LightGreen, new PointF(8, 8));
        g.DrawString($"B [{(char)('1' + _bIndex)}]", tagFont, Brushes.Orange, new PointF(splitX + 8, 8));
    }

    private void PaintSlot(Graphics g, int surfaceIndex, Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var surface = _grid.GetSurface(surfaceIndex);
        if (surface is null)
        {
            using var empty = new SolidBrush(AppTheme.Colors.PanelBackground);
            g.FillRectangle(empty, rect);
            return;
        }

        // 用路本身的 snapshot 信息合成代表画面（模拟模式可用；真实模式建议后续接入 D3D 截图）
        var hue = (surfaceIndex * 47) % 360;
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect,
            HsvToColor(hue, 0.6f, 0.5f),
            HsvToColor((hue + 90) % 360, 0.7f, 0.2f),
            System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, rect);

        // 帧号
        if (surface is PlayerSurface ps)
        {
            // 通过刷新轮询拿到接近真值（低精度占位）
            using var font = new Font("Consolas", 14f, FontStyle.Bold);
            using var textBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
            var label = $"路 {surfaceIndex + 1}";
            var size = g.MeasureString(label, font);
            g.DrawString(label, font, textBrush,
                rect.X + (rect.Width - size.Width) / 2f,
                rect.Y + (rect.Height - size.Height) / 2f);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            Slider = e.X / (double)Math.Max(1, ClientSize.Width);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Slider = e.X / (double)Math.Max(1, ClientSize.Width);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    private static Color HsvToColor(float h, float s, float v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        var m = v - c;
        (float r, float g, float b) = h switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}