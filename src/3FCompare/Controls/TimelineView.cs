using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using _3FCompare.App;

namespace _3FCompare.Controls;

/// <summary>主时间轴（WinForms TimelineView 对应，Avalonia DrawingContext 自绘）。
/// 刻度/播放头/A-B 循环区间/当前时间戳；左键拖动 = ScrubPreview（10ms 节流、松手 Seek）；
/// 右键菜单设 A/B 点；A/B 键（本控件聚焦时）。</summary>
public sealed class TimelineView : Control
{
    private long _duration100ns, _position100ns, _preview100ns;
    private long _loopStart, _loopEnd;
    private bool _loopEnabled;
    private bool _scrubbing;
    private DateTime _lastScrubEmit = DateTime.MinValue;
    private bool _dragged;

    public event Action<long>? SeekRequested;
    public event Action<long, bool>? AbPointSet;   // (position100ns, isA)
    public event Action<long>? ScrubPreview;

    public bool IsScrubbing => _scrubbing;

    public TimelineView()
    {
        Focusable = true;
        ClipToBounds = true;
        ContextMenu = BuildContextMenu();
        LanguageManager.LanguageChanged += (_, _) => RefreshMenuTexts();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var setA = new MenuItem();
        var setB = new MenuItem();
        setA.Click += (_, _) => AbPointSet?.Invoke(_position100ns, true);
        setB.Click += (_, _) => AbPointSet?.Invoke(_position100ns, false);
        menu.Items.Add(setA);
        menu.Items.Add(setB);
        menu.Tag = (setA, setB);
        RefreshMenuTexts(menu);
        return menu;
    }

    private void RefreshMenuTexts(ContextMenu? menu = null)
    {
        menu ??= ContextMenu;
        if (menu?.Tag is not (MenuItem setA, MenuItem setB)) return;
        var a = setA; var b = setB;
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            a.Header = LanguageManager.T("Timeline_SetA");
            b.Header = LanguageManager.T("Timeline_SetB");
        });
    }

    // ---------- 状态注入 ----------

    public void SetDuration(long duration100ns) { _duration100ns = Math.Max(0, duration100ns); InvalidateVisual(); }
    public void SetPosition(long position100ns) { _position100ns = position100ns; InvalidateVisual(); }
    public void SetPreviewPosition(long position100ns) { _preview100ns = position100ns; InvalidateVisual(); }
    public void SetLoopRange(long start100ns, long end100ns, bool enabled)
    {
        _loopStart = start100ns; _loopEnd = end100ns; _loopEnabled = enabled;
        InvalidateVisual();
    }
    public void EndScrub() { _scrubbing = false; InvalidateVisual(); }

    // ---------- 交互 ----------

    private long PositionFromX(double x)
    {
        if (Bounds.Width <= 0 || _duration100ns <= 0) return 0;
        var ratio = Math.Clamp(x / Bounds.Width, 0.0, 1.0);
        return (long)(ratio * _duration100ns);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        _scrubbing = true;
        _dragged = false;
        e.Pointer.Capture(this);
        ScrubThrottled(PositionFromX(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_scrubbing) return;
        _dragged = true;
        ScrubThrottled(PositionFromX(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_scrubbing) return;
        _scrubbing = false;
        e.Pointer.Capture(null);
        // 松手才真正 Seek（拖动期间只做预览，不触发解码跳转）
        SeekRequested?.Invoke(PositionFromX(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.A) { AbPointSet?.Invoke(_position100ns, true); e.Handled = true; }
        else if (e.Key == Key.B) { AbPointSet?.Invoke(_position100ns, false); e.Handled = true; }
    }

    private void ScrubThrottled(long pos)
    {
        _preview100ns = pos;
        InvalidateVisual();
        var now = DateTime.UtcNow;
        if ((now - _lastScrubEmit).TotalMilliseconds >= 10)
        {
            _lastScrubEmit = now;
            ScrubPreview?.Invoke(pos);
        }
    }

    // ---------- 绘制 ----------

    private static string Ts(long ticks) => TimeSpan.FromTicks(ticks).ToString(@"hh\:mm\:ss");

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 轨道底
        var trackRect = new Rect(0, h * 0.30, w, h * 0.40);
        dc.DrawFill(new SolidColorBrush(Color.FromRgb(40, 40, 46)), trackRect);

        // A-B 循环区间（半透明绿 + 两端竖线）
        if (_loopEnabled && _duration100ns > 0 && _loopEnd > _loopStart)
        {
            var ax = (double)_loopStart / _duration100ns * w;
            var bx = (double)_loopEnd / _duration100ns * w;
            dc.DrawFill(new SolidColorBrush(Color.FromArgb(70, 100, 200, 100)),
                new Rect(ax, 0, bx - ax, h));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(100, 200, 100)), 1.5),
                new Point(ax, 0), new Point(ax, h));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(100, 200, 100)), 1.5),
                new Point(bx, 0), new Point(bx, h));
        }

        // 11 个刻度 + hh:mm:ss 标签
        var tickPen = new Pen(new SolidColorBrush(Color.FromRgb(80, 80, 90)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(140, 140, 150));
        for (var i = 0; i <= 10; i++)
        {
            var x = w * i / 10.0;
            var tickTop = i % 5 == 0 ? h * 0.12 : h * 0.2;
            dc.DrawLine(tickPen, new Point(x, tickTop), new Point(x, h * 0.3));
            if (i % 5 == 0 && _duration100ns > 0)
            {
                var label = Ts((long)(_duration100ns * i / 10.0));
                var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Consolas"), 10, labelBrush);
                dc.DrawText(ft, new Point(Math.Min(x + 2, w - ft.Width), h - ft.Height - 1));
            }
        }

        // 播放头（拖动预览时显示预览位置）
        var pos = _scrubbing ? _preview100ns : _position100ns;
        if (_duration100ns > 0)
        {
            var px = (double)pos / _duration100ns * w;
            var accent = new SolidColorBrush(Color.FromRgb(255, 200, 64));
            dc.DrawLine(new Pen(accent, 2), new Point(px, 0), new Point(px, h));
            dc.DrawGeometry(accent, null, Triangle(px));
        }

        // 当前时间戳（左上）
        var stamp = TimeSpan.FromTicks(_scrubbing ? _preview100ns : _position100ns).ToString(@"hh\:mm\:ss\.fff");
        var stampText = new FormattedText(stamp, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Consolas"), 11,
            new SolidColorBrush(Color.FromRgb(200, 200, 210)));
        dc.DrawText(stampText, new Point(4, 2));
    }

    private static Geometry Triangle(double px)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(px - 5, 0), true);
            ctx.LineTo(new Point(px + 5, 0));
            ctx.LineTo(new Point(px, 7));
            ctx.EndFigure(true);
        }
        return g;
    }
}

file static class DrawingContextExtensions
{
    public static void DrawFill(this DrawingContext dc, IBrush brush, Rect rect) =>
        dc.DrawRectangle(brush, null, rect);
}
