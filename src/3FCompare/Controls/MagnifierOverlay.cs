using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace _3FCompare.Controls;

/// <summary>放大镜覆盖层（WinForms MagnifierOverlay 对应）：
/// 160×120 十字线/对齐网格/4x 坐标注记；随光标定位，鼠标不命中
/// （IsHitTestVisible=false，替代 WinForms 的 WS_EX_TRANSPARENT hack）。</summary>
public sealed class MagnifierOverlay : Control
{
    private Point _position = new(-500, -500);
    private bool _visible;

    public double WidthPx => 160;
    public double HeightPx => 120;
    public const float Zoom = 4f;

    public MagnifierOverlay()
    {
        IsHitTestVisible = false;
        Width = 160; Height = 120;
        IsVisible = false;
    }

    /// <summary>定位到（相对父容器的）光标位置并显示。</summary>
    public void UpdateAt(Point cursor)
    {
        _position = new Point(cursor.X + 16, cursor.Y + 16);
        _visible = true;
        IsVisible = true;
        // 钳制在父容器内
        if (Parent is Control p)
        {
            if (_position.X + 160 > p.Bounds.Width) _position = new Point(cursor.X - 160 - 8, _position.Y);
            if (_position.Y + 120 > p.Bounds.Height) _position = new Point(_position.X, cursor.Y - 120 - 8);
        }
        InvalidateVisual();
    }

    public void HideOverlay()
    {
        _visible = false;
        IsVisible = false;
    }

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        if (!_visible) return;
        var rect = new Rect(_position.X, _position.Y, 160, 120);

        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 10, 10, 12)), null, rect);
        var accent = new SolidColorBrush(Color.FromRgb(255, 200, 64));
        dc.DrawRectangle(null, new Pen(accent, 2), rect);

        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var line = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1);
        // 十字线
        dc.DrawLine(line, new Point(cx, rect.Y), new Point(cx, rect.Bottom));
        dc.DrawLine(line, new Point(rect.X, cy), new Point(rect.Right, cy));
        // 对齐网格（1/4 分割）
        for (var i = 1; i < 4; i++)
        {
            dc.DrawLine(line, new Point(rect.X + rect.Width * i / 4, rect.Y), new Point(rect.X + rect.Width * i / 4, rect.Bottom));
            dc.DrawLine(line, new Point(rect.X, rect.Y + rect.Height * i / 4), new Point(rect.Right, rect.Y + rect.Height * i / 4));
        }

        var caption = $"{Zoom:0}x";
        var ft = new FormattedText(caption, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Consolas"), 10,
            new SolidColorBrush(Color.FromRgb(200, 200, 210)));
        dc.DrawText(ft, new Point(rect.X + 4, rect.Bottom - ft.Height - 2));
    }
}
