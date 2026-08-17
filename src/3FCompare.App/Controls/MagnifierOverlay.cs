namespace _3FCompare.App.Controls;

/// <summary>放大镜（F17）：在视频面上跟随鼠标显示放大局部区域。
/// 依附于 PlayerSurface 的覆盖层，通过图片取色放大（模拟模式直接放大自绘；真实模式依赖像素探针回读）。</summary>
public sealed class MagnifierOverlay : Control
{
    private Point _mousePos = new(-1000, -1000);
    private int _zoom = 4;
    private bool _mouseInside;

    public MagnifierOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(160, 120); // 放大镜窗口
        Visible = false;
        Enabled = false; // 不拦截鼠标
    }

    /// <summary>设置鼠标位置（Surface 坐标），展开放大镜。</summary>
    public void UpdateMagnifier(Point surfacePoint)
    {
        _mousePos = surfacePoint;
        PositionNearMouse(surfacePoint);
        if (!Visible) Visible = true;
        Invalidate();
    }

    public void HideMagnifier()
    {
        Visible = false;
        _mouseInside = false;
    }

    private void PositionNearMouse(Point p)
    {
        var x = p.X + 16;
        var y = p.Y + 16;
        if (Parent is not null)
        {
            x = Math.Min(x, Parent.ClientSize.Width - Width - 8);
            y = Math.Min(y, Parent.ClientSize.Height - Height - 8);
        }
        Location = new Point(Math.Max(0, x), Math.Max(0, y));
    }

    /// <summary>放大镜外观：边框 + 内容由父级画笔填充（这里画网格辅助线）。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Color.FromArgb(220, 16, 16, 20));

        using var border = new Pen(Color.FromArgb(255, 200, 64), 2);
        g.DrawRectangle(border, new Rectangle(0, 0, Width - 1, Height - 1));

        // 中心十字
        using var crossPen = new Pen(Color.FromArgb(120, 255, 255, 255));
        g.DrawLine(crossPen, Width / 2 - 14, Height / 2, Width / 2 + 14, Height / 2);
        g.DrawLine(crossPen, Width / 2, Height / 2 - 14, Width / 2, Height / 2 + 14);

        // 放大网格辅助线（示意格子，方便对齐像素）
        using var gridPen = new Pen(Color.FromArgb(30, 255, 255, 255));
        for (var i = 1; i < _zoom; i++)
        {
            var step = Height / (double)_zoom;
            g.DrawLine(gridPen, 0, (float)(i * step), Width, (float)(i * step));
        }

        // 顶部角标：当前缩放
        using var font = new Font("Consolas", 8f);
        g.DrawString($"{_zoom}x @({_mousePos.X},{_mousePos.Y})", font, Brushes.White, 4, 2);
    }
}