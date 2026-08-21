using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>时间轴：单轨道（master）+ 播放头 + A/B 区间标记 + 点击/拖动 Seek + A/B 打点。
/// 交互：左键拖拽 Seek（拖动期间不跳转，仅 OnMouseUp 触发一次 Seek，避免高频性能损耗）；
/// 拖动时触发 ScrubPreview（节流 >10ms）供外部抓帧做悬浮缩略图；按下「A」键设 A 点，
/// 按「B」键设 B 点（或右键设 A、中键设 B）。支持设置 A/B 循环区间（F11）。</summary>
public sealed class TimelineView : Control
{
    private long _duration100ns;
    private long _position100ns;
    private long _loopStart = -1;
    private long _loopEnd = -1;
    private bool _dragging;
    private bool _scrubbing;          // 拖动中（预览模式，播放头可被外部 SetPreviewPosition 覆盖）
    private long _lastPreviewTicks;   // ScrubPreview 节流基线
    private long _lastDragPos;        // 拖动最后一次位置

    public event EventHandler<long>? SeekRequested;

    /// <summary>A/B 点被打点（参数 = 100ns 位置；负表示清除）。</summary>
    public event EventHandler<(long position, bool isA)>? AbPointSet;

    /// <summary>拖动暂停在某个位置（参数 = 100ns 位置）。外部借此抓帧做悬浮缩略图预览。</summary>
    public event EventHandler<long>? ScrubPreview;

    /// <summary>是否正在拖拽（供外部跳过普通 SetPosition 以保留预览位置）。</summary>
    public bool IsScrubbing => _scrubbing;

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
        var setA = new ToolStripMenuItem(LanguageManager.T("Timeline_SetA"), null, (_, _) => AbPointSet?.Invoke(this, (PositionFromX(PointToClient(MousePosition).X), true)));
        var setB = new ToolStripMenuItem(LanguageManager.T("Timeline_SetB"), null, (_, _) => AbPointSet?.Invoke(this, (PositionFromX(PointToClient(MousePosition).X), false)));
        menu.Items.AddRange(new ToolStripItem[] { setA, setB });
        return menu;
    }

    /// <summary>语言切换后刷新右键菜单文本。</summary>
    public void ApplyLanguage()
    {
        if (ContextMenuStrip?.Items is not { Count: >= 2 }) return;
        ContextMenuStrip.Items[0].Text = LanguageManager.T("Timeline_SetA");
        ContextMenuStrip.Items[1].Text = LanguageManager.T("Timeline_SetB");
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

    /// <summary>外部注入预览用位置（拖动悬浮缩略图时由外部回调设置，可反映预览位置）。</summary>
    public void SetPreviewPosition(long position100ns)
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
            _scrubbing = true;
            _lastDragPos = PositionFromX(e.X);
            _lastPreviewTicks = 0;
            // 拖动开始：立即触发一次预览，但不 Seek（避免拖动起步就跳）
            ScrubPreview?.Invoke(this, _lastDragPos);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var pos = PositionFromX(e.X);
        _lastDragPos = pos;
        // 拖动期间不 Seek；节流（>10ms）触发 ScrubPreview
        var now = Environment.TickCount64;
        if (now - _lastPreviewTicks >= 10)
        {
            _lastPreviewTicks = now;
            ScrubPreview?.Invoke(this, pos);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false;
            _scrubbing = false;
            // 只在松手时触发一次 Seek（到最终位置），避免拖动高频 Seek 占用性能
            SeekRequested?.Invoke(this, _lastDragPos);
        }
    }
}