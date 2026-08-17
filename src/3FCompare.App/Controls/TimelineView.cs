using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>时间轴：单轨道（master）+ 播放头 + A/B 区间标记 + 点击/拖动 Seek + A/B 打点。
/// 交互：左键拖拽 Seek；按下「A」键设 A 点，按「B」键设 B 点（或右键设 A、中键设 B）。
/// 支持设置 A/B 循环区间（F11）。</summary>
public sealed class TimelineView : Control
{
    private long _duration100ns;
    private long _position100ns;
    private long _loopStart = -1;
    private long _loopEnd = -1;
    private bool _dragging;

    public event EventHandler<long>? SeekRequested;

    /// <summary>A/B 点被打点（参数 = 100ns 位置；负表示清除）。</summary>
    public event EventHandler<(long position, bool isA)>? AbPointSet;

    public TimelineView()
    {
        Height = 34;
        Dock = DockStyle.Bottom;
        BackColor = AppTheme.Colors.Background;
        DoubleBuffered = true;
        ContextMenuStrip = BuildContextMenu();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        var setA = new ToolStripMenuItem("设为 A 点", null, (_, _) => AbPointSet?.Invoke(this, (PositionFromX(PointToClient(MousePosition).X), true)));
        var setB = new ToolStripMenuItem("设为 B 点", null, (_, _) => AbPointSet?.Invoke(this, (PositionFromX(PointToClient(MousePosition).X), false)));
        menu.Items.AddRange(new ToolStripItem[] { setA, setB });
        return menu;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.A)
        {
            AbPointSet?.Invoke(this, (_position100ns, true));
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.B)
        {
            AbPointSet?.Invoke(this, (_position100ns, false));
            e.Handled = true;
        }
    }

    public void SetDuration(long duration100ns)
    {
        _duration100ns = Math.Max(0, duration100ns);
        Invalidate();
    }

    public void SetPosition(long position100ns)
    {
        _position100ns = Math.Clamp(position100ns, 0, Math.Max(0, _duration100ns));
        Invalidate();
    }

    public void SetLoopRange(long start, long end)
    {
        _loopStart = start;
        _loopEnd = end;
        Invalidate();
    }

    public long PositionFromX(int x)
    {
        if (_duration100ns <= 0 || Width <= 0) return 0;
        var ratio = Math.Clamp((double)x / Width, 0, 1);
        return (long)(ratio * _duration100ns);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var rect = ClientRectangle;

        // 背景轨
        using var trackBrush = new SolidBrush(AppTheme.Colors.ControlBackgroundLight);
        g.FillRectangle(trackBrush, new Rectangle(0, rect.Height / 2 - 5, rect.Width, 10));

        // A/B 区间高亮
        if (_loopStart >= 0 && _loopEnd > _loopStart && _duration100ns > 0)
        {
            var x1 = (int)(_loopStart / (double)_duration100ns * rect.Width);
            var x2 = (int)(_loopEnd / (double)_duration100ns * rect.Width);
            using var loopBrush = new SolidBrush(Color.FromArgb(70, 80, 180, 80));
            g.FillRectangle(loopBrush, x1, rect.Height / 2 - 6, Math.Max(1, x2 - x1), 12);
            using var abFont = new Font("Segoe UI", 8f, FontStyle.Bold);
            g.DrawString("A", abFont, Brushes.GreenYellow, x1 + 2, 0);
            g.DrawString("B", abFont, Brushes.Orange, x2 - 14, 0);
            using var markPen = new Pen(Color.FromArgb(120, 200, 120), 1);
            g.DrawLine(markPen, x1, 0, x1, rect.Height);
            g.DrawLine(markPen, x2, 0, x2, rect.Height);
        }

        // 播放头
        if (_duration100ns > 0)
        {
            var x = (int)(_position100ns / (double)_duration100ns * rect.Width);
            using var headPen = new Pen(AppTheme.Colors.Accent, 2);
            g.DrawLine(headPen, x, 0, x, rect.Height);
            // 头部三角
            using var headBrush = new SolidBrush(AppTheme.Colors.Accent);
            g.FillPolygon(headBrush, new[]
            {
                new Point(x - 5, 0), new Point(x + 5, 0), new Point(x, 8),
            });
        }

        // 时间刻度
        using var font = new Font("Consolas", 8f);
        using var textBrush = new SolidBrush(AppTheme.Colors.TextMuted);
        for (var i = 0; i <= 10; i++)
        {
            var x = (int)(i / 10.0 * rect.Width);
            g.DrawLine(Pens.DimGray, x, rect.Height - 10, x, rect.Height);
            var t = TimeSpan.FromTicks((long)(i / 10.0 * _duration100ns));
            g.DrawString(t.ToString(@"hh\:mm\:ss"), font, textBrush, x + 2, rect.Height - 14);
        }

        // 时间戳文字
        using var timeFont = new Font("Consolas", 9f);
        using var timeBrush = new SolidBrush(Color.White);
        var posText = TimeSpan.FromTicks(_position100ns).ToString(@"hh\:mm\:ss\.fff");
        g.DrawString(posText, timeFont, timeBrush, 6, 3);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            SeekRequested?.Invoke(this, PositionFromX(e.X));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) SeekRequested?.Invoke(this, PositionFromX(e.X));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }
}