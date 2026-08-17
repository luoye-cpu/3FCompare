using System.ComponentModel;
using _3FCompare.Core.Backend;

namespace _3FCompare.App.Controls;

/// <summary>单路视频表面（每会话一个）。
/// - 模拟模式（无真实 DLL）：自绘合成画面（渐变 + 帧号 + 时间码 + 文件名）；
/// - 真实模式：创建子窗口 HWND，交给 3FP 会话作为输出窗口，D3D11 直接渲染（本控件不做绘制）。</summary>
public sealed class PlayerSurface : Control
{
    private readonly int _index;
    private IPlayerSession? _session;
    private readonly bool _realMode;
    private bool _selected;
    private bool _failed;
    private string _error = string.Empty;
    private string _fileName = string.Empty;
    private EngineSnapshot? _lastSnapshot;

    public int Index => _index;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsFailed
    {
        get => _failed;
        set { _failed = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ErrorText
    {
        get => _error;
        set { _error = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string FileName
    {
        get => _fileName;
        set { _fileName = value; Invalidate(); }
    }

    public event EventHandler? SurfaceClicked;

    public PlayerSurface(int index, bool realMode)
    {
        _index = index;
        _realMode = realMode;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(18, 18, 20);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
    }

    /// <summary>绑定会话（真实模式：会话创建时已把 Handle 作为输出窗口；此处仅记录并刷新）。</summary>
    public void AttachSession(IPlayerSession session)
    {
        _session = session;
        Invalidate();
    }

    public void DetachSession()
    {
        _session = null;
        Invalidate();
    }

    public void UpdateSnapshot(EngineSnapshot? snapshot)
    {
        _lastSnapshot = snapshot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = ClientRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        if (_realMode && _session is not null && IsHandleCreated)
        {
            // 真实模式：D3D 输出到本窗口 HWND，这里只画边框与信息层
            PaintBorder(g, rect);
            PaintOverlayInfo(g, rect);
            return;
        }

        PaintSimulatedContent(g, rect);
        PaintBorder(g, rect);
        PaintOverlayInfo(g, rect);
    }

    private void PaintSimulatedContent(Graphics g, Rectangle rect)
    {
        if (_failed)
        {
            using var bg = new SolidBrush(Color.FromArgb(30, 30, 34));
            g.FillRectangle(bg, rect);
            return;
        }

        // 合成渐变背景（按 Index 变化色相）
        var hue = (_index * 47) % 360;
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect,
            ColorFromHsv(hue, 0.55f, 0.35f),
            ColorFromHsv((hue + 60) % 360, 0.65f, 0.18f),
            System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, rect);

        // 中心大字：帧号
        var frame = _lastSnapshot?.FrameIndex ?? 0;
        var pos = _lastSnapshot?.Position100ns ?? 0;
        var frameText = $"FRAME {frame:D6}";
        using var bigFont = new Font("Consolas", Math.Max(14f, rect.Width / 22f), FontStyle.Bold);
        using var bigBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        var size = g.MeasureString(frameText, bigFont);
        g.DrawString(frameText, bigFont, bigBrush,
            (rect.Width - size.Width) / 2f, (rect.Height - size.Height) / 2f);

        // 时间码
        var time = TimeSpan.FromTicks(pos);
        var timeText = time.ToString(@"hh\:mm\:ss\.fff");
        using var timeFont = new Font("Consolas", Math.Max(10f, rect.Width / 40f), FontStyle.Regular);
        using var timeBrush = new SolidBrush(Color.FromArgb(180, 255, 255, 255));
        g.DrawString(timeText, timeFont, timeBrush, (rect.Width - g.MeasureString(timeText, timeFont).Width) / 2f, rect.Height * 0.55f);
    }

    private void PaintBorder(Graphics g, Rectangle rect)
    {
        var color = _selected ? Color.FromArgb(255, 200, 64) : Color.FromArgb(60, 60, 66);
        using var pen = new Pen(color, _selected ? 3f : 1f);
        var r = Rectangle.Inflate(rect, -1, -1);
        g.DrawRectangle(pen, r);
    }

    private void PaintOverlayInfo(Graphics g, Rectangle rect)
    {
        // 左上角：路号 + 文件名；右上角：错误
        using var font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

        if (_failed)
        {
            using var red = new SolidBrush(Color.FromArgb(255, 120, 120));
            g.DrawString($"✖ {_error}", font, red, new RectangleF(8, 8, rect.Width - 16, 60));
        }
        else
        {
            using var white = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            using var dark = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            var label = $"[{_index + 1}] {_fileName}";
            g.DrawString(label, font, dark, new PointF(10, 10));
            g.DrawString(label, font, white, new PointF(9, 9));
        }

        if (_realMode)
        {
            using var tagBrush = new SolidBrush(Color.FromArgb(160, 0, 120, 0));
            g.FillRectangle(tagBrush, new Rectangle(rect.Right - 44, 6, 38, 18));
            using var tagFont = new Font("Segoe UI", 8f);
            g.DrawString("D3D11", tagFont, Brushes.White, rect.Right - 42, 8);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        SurfaceClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 真实模式：通知会话输出窗口（3FP 支持窗口创建后补绑）
        if (_realMode && _session is not null)
        {
            // 会话创建时已传 HWND；这里确保窗口句柄有效后重设
        }
    }

    private static Color ColorFromHsv(float h, float s, float v)
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