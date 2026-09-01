using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using _3FCompare.Core.Backend;

namespace _3FCompare.Controls;

/// <summary>放大镜覆盖层（WinForms MagnifierOverlay 对应）：
/// 160×120 十字线/对齐网格/4x 坐标注记 + 光标邻域像素放大内容；
/// 随光标定位，鼠标不命中（IsHitTestVisible=false）。</summary>
public sealed class MagnifierOverlay : Control
{
    private Point _position = new(-500, -500);
    private bool _visible;
    private IPlayerSession? _session;
    private float[]? _pixelGrid = new float[ZoomGrid * ZoomGrid * 4]; // CPU 邻域像素 (backbuffer space)

    private const int Zoom = 4;
    private const int ZoomGrid = 12; // 12×12 采样网格 → 放大到 16px 块 = 192px

    public double WidthPx => 160;
    public double HeightPx => 120;
    public const float ZoomFactor = 4f;

    public MagnifierOverlay()
    {
        IsHitTestVisible = false;
        Width = 192; Height = 144;
        IsVisible = false;
    }

    /// <summary>绑定当前选中会话（探针移动时用于读像素）。</summary>
    public void AttachSession(IPlayerSession? session) => _session = session;

    /// <summary>定位到（相对父容器的）光标位置并显示。
    /// cursor 为"中心点"（放大镜覆盖在光标旁）。</summary>
    public void UpdateAt(Point cursor)
    {
        _position = new Point(cursor.X + 16, cursor.Y + 16);
        _visible = true;
        IsVisible = true;
        // 钳制在父容器内
        if (Parent is Control p)
        {
            if (_position.X + Width > p.Bounds.Width) _position = new Point(cursor.X - Width - 8, _position.Y);
            if (_position.Y + Height > p.Bounds.Height) _position = new Point(_position.X, cursor.Y - Height - 8);
        }
        RefreshPixels(cursor);
        InvalidateVisual();
    }

    private void RefreshPixels(Point cursor)
    {
        if (_session is null || !_session.ReadRenderTargetInfo(out var rt) ||
            rt.SwapWidth == 0 || rt.SwapHeight == 0 || _pixelGrid is null)
        {
            _pixelGrid = null;
            return;
        }
        try
        {
            // 取光标邻域（backbuffer 像素空间）。点 = DIP*RenderScaling (本类不知道 scaling，用 1 兜底)
            // 放大镜中心 = 光标位置 → backbuffer 坐标
            var centerX = (int)(cursor.X);
            var centerY = (int)(cursor.Y);
            // 采样网格宽 ZoomGrid 像素，以中心为参考
            var half = ZoomGrid / 2;
            var gx = Math.Clamp(centerX - half, 0, (int)rt.SwapWidth - ZoomGrid);
            var gy = Math.Clamp(centerY - half, 0, (int)rt.SwapHeight - ZoomGrid);
            var buffer = new float[ZoomGrid * ZoomGrid * 4];
            if (!_session.TryReadPixelRegion(gx, gy, ZoomGrid, ZoomGrid, buffer, out _))
            {
                _pixelGrid = null;
                return;
            }
            _pixelGrid = buffer;
        }
        catch
        {
            _pixelGrid = null; // 引擎未就绪时放大镜只画框架
        }
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
        var rect = new Rect(_position.X, _position.Y, Width, Height);

        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(200, 10, 10, 12)), null, rect);
        var accent = new SolidColorBrush(Color.FromRgb(255, 200, 64));
        dc.DrawRectangle(null, new Pen(accent, 2), rect);

        // 像素内容：把采样网格最近邻放大成 M×M 块
        if (_pixelGrid is not null)
        {
            var cellW = rect.Width / ZoomGrid;
            var cellH = rect.Height / ZoomGrid;
            for (var gy = 0; gy < ZoomGrid; gy++)
            {
                for (var gx = 0; gx < ZoomGrid; gx++)
                {
                    var i = (gy * ZoomGrid + gx) * 4;
                    var color = Color.FromArgb(
                        (byte)To8(_pixelGrid[i + 3]), (byte)To8(_pixelGrid[i]), (byte)To8(_pixelGrid[i + 1]), (byte)To8(_pixelGrid[i + 2]));
                    dc.DrawRectangle(new SolidColorBrush(color), null,
                        new Rect(rect.X + gx * cellW, rect.Y + gy * cellH, cellW, cellH));
                }
            }
        }

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

    private static int To8(float v) => Math.Clamp((int)Math.Round(v * 255f), 0, 255);
}
