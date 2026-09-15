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

    /// <summary>自测钩子：采样缓冲是否已就绪（C3 回归断言用。</summary>
    internal bool HasPixelGrid => _pixelGrid is not null;

    /// <summary>自测钩子：把采样缓冲置回"读回失败"状态。
    /// 用于复现"换路前读回失败 → 缓冲为 null → 换路后永久空框"这一路径。</summary>
    internal void SimulatePixelReadFailureForSelfTest() => _pixelGrid = null;

    /// <summary>绑定当前选中会话（探针移动时用于读像素）。</summary>
    public void AttachSession(IPlayerSession? session)
    {
        _session = session;
        // 恢复采样缓冲：换路 / 重开时必须重建。若沿用换路前的 null，
        // RefreshPixels 会永久走"引擎未就绪"分支，放大镜只剩空框且再也不恢复。
        _pixelGrid ??= new float[ZoomGrid * ZoomGrid * 4];
    }

    /// <summary>定位到（相对父容器的）光标位置并显示。
    /// cursor 为"中心点"（放大镜覆盖在光标旁）。</summary>
    /// <summary>更新放大镜位置与采样。
    ///
    /// <paramref name="localInSurface"/> 必须是**相对该路 PlayerSurface** 的坐标
    /// （<c>e.GetPosition(surface)</c>），**不能**传 CenterPanel 的全局坐标——
    /// 后者在多格布局下会把"第 3 格的坐标"当成"第 1 格后台缓冲的坐标"，
    /// 再加上未乘 RenderScaling、未排 letterbox，显示的就完全不是光标下的内容
    /// （docs/15 §2.2）。<paramref name="renderScaling"/> 用于 DIP → 物理像素。
    /// </summary>
    public void UpdateAt(Point localInSurface, double renderScaling)
    {
        // 浮窗仍按 CenterPanel 坐标摆放：需要把"surface 内坐标"换算回去。
        // （放大镜本体跟随光标，采样点才用 surface 内坐标。）
        var cursor = localInSurface;
        _position = new Point(cursor.X + 16, cursor.Y + 16);
        _visible = true;
        IsVisible = true;
        // 钳制在父容器内
        if (Parent is Control p)
        {
            if (_position.X + Width > p.Bounds.Width) _position = new Point(cursor.X - Width - 8, _position.Y);
            if (_position.Y + Height > p.Bounds.Height) _position = new Point(_position.X, cursor.Y - Height - 8);
        }
        RefreshPixels(localInSurface, renderScaling);
        InvalidateVisual();
    }

    private void RefreshPixels(Point localInSurface, double renderScaling)
    {
        // 注意：判据里不能含 "_pixelGrid is null"。它一旦被置 null（读回失败 / 引擎未就绪），
        // 就会永远命中同一判据提前返回，再也进不到下面重新赋值的分支 —— 永久只剩空框。
        if (_session is null || !_session.ReadRenderTargetInfo(out var rt) ||
            rt.SwapWidth == 0 || rt.SwapHeight == 0)
        {
            _pixelGrid = null;
            return;
        }
        try
        {
            // DIP → 物理像素：DPI 非 100% 时漏掉这步会整体偏移一个缩放倍数
            // （原实现注释自认"本类不知道 scaling，用 1 兜底"，即永远漏乘）。
            var scale = renderScaling > 0 ? renderScaling : 1.0;
            var centerX = (int)(localInSurface.X * scale);
            var centerY = (int)(localInSurface.Y * scale);

            // 采样网格宽 ZoomGrid 像素，以中心为参考。
            // 后台缓冲尺寸可能小于 ZoomGrid（极小窗口），此时上界会变负 → 归零。
            var half = ZoomGrid / 2;
            var maxX = Math.Max(0, (int)rt.SwapWidth - ZoomGrid);
            var maxY = Math.Max(0, (int)rt.SwapHeight - ZoomGrid);
            var gx = Math.Clamp(centerX - half, 0, maxX);
            var gy = Math.Clamp(centerY - half, 0, maxY);
            // 复用缓冲：原先每次指针移动都 new float[576]（指针热路径上的无谓分配）。
            // 仅在失败时保留"置 null"的语义——自测的 HasPixelGrid 依赖它，
            // 且失败是少数路径，不值得为省一次分配去动既有语义。
            var buffer = _pixelGrid ?? new float[ZoomGrid * ZoomGrid * 4];
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

    // 固定颜色的画刷/画笔做成静态：原先每帧 new，纯属浪费（docs/15 六章）
    private static readonly SolidColorBrush BackdropBrush = new(Color.FromArgb(200, 10, 10, 12));
    private static readonly Pen AccentPen = new(new SolidColorBrush(Color.FromRgb(255, 200, 64)), 2);
    private static readonly Pen GridLinePen = new(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1);
    private static readonly SolidColorBrush CaptionBrush = new(Color.FromRgb(200, 200, 210));

    // 格子颜色随像素变化，无法静态化，但可以复用**同一个**实例改 Color：
    // Avalonia 的 DrawingContext 是立即模式，DrawRectangle 返回后画刷即可再改，
    // 不必为 144 个格子各建一个画刷。
    private readonly SolidColorBrush _cellBrush = new(Colors.Black);

    // 字幕只在缩放倍率变化时才需要重建
    private FormattedText? _captionText;
    private double _captionZoom = -1;

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        if (!_visible) return;
        var rect = new Rect(_position.X, _position.Y, Width, Height);

        dc.DrawRectangle(BackdropBrush, null, rect);
        dc.DrawRectangle(null, AccentPen, rect);

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
                    _cellBrush.Color = color;
                    dc.DrawRectangle(_cellBrush, null,
                        new Rect(rect.X + gx * cellW, rect.Y + gy * cellH, cellW, cellH));
                }
            }
        }

        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var line = GridLinePen;
        // 十字线
        dc.DrawLine(line, new Point(cx, rect.Y), new Point(cx, rect.Bottom));
        dc.DrawLine(line, new Point(rect.X, cy), new Point(rect.Right, cy));
        // 对齐网格（1/4 分割）
        for (var i = 1; i < 4; i++)
        {
            dc.DrawLine(line, new Point(rect.X + rect.Width * i / 4, rect.Y), new Point(rect.X + rect.Width * i / 4, rect.Bottom));
            dc.DrawLine(line, new Point(rect.X, rect.Y + rect.Height * i / 4), new Point(rect.Right, rect.Y + rect.Height * i / 4));
        }

        if (_captionText is null || Math.Abs(_captionZoom - Zoom) > 0.01)
        {
            _captionText = new FormattedText($"{Zoom:0}x", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 10, CaptionBrush);
            _captionZoom = Zoom;
        }
        dc.DrawText(_captionText, new Point(rect.X + 4, rect.Bottom - _captionText.Height - 2));
    }

    private static int To8(float v) => Math.Clamp((int)Math.Round(v * 255f), 0, 255);
}
