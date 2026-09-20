using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
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
    private bool _dragged; // 预留：拖动状态标记（当前由 _scrubbing 承担，保留供后续扩展）

    public event Action<long>? SeekRequested;
    public event Action<long, bool>? AbPointSet;   // (position100ns, isA)
    public event Action<long>? ScrubPreview;

    public bool IsScrubbing => _scrubbing;

    public TimelineView()
    {
        Focusable = true;
        ClipToBounds = true;
        ContextMenu = BuildContextMenu();
        // 主题切换后必须重绘：本控件全部走 Render 自绘，属性没变时 Avalonia 不会自动失效
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, v => v.RefreshMenuTexts());
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

    public void SetDuration(long duration100ns)
    {
        duration100ns = Math.Max(0, duration100ns);
        // 同值短路：播放中 duration 恒定，避免每 tick 无效重绘（PollSnapshots 4Hz × 9 路）
        if (_duration100ns == duration100ns) return;
        _duration100ns = duration100ns;
        InvalidateVisual();
    }

    public void SetPosition(long position100ns)
    {
        // 同值短路：仅在位置真正变化时重绘
        if (_position100ns == position100ns) return;
        _position100ns = position100ns;
        InvalidateVisual();
    }

    public void SetPreviewPosition(long position100ns)
    {
        // 相同预览位置（如拖动停止后复点）不触发重绘
        if (_preview100ns == position100ns) return;
        _preview100ns = position100ns;
        InvalidateVisual();
    }
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
        _dragged = false; // 纯点击未拖动：松手时跳过 Seek（位置未变，无需解码跳转）
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
        var dragged = _dragged;
        e.Pointer.Capture(null);
        // 松手才真正 Seek（拖动期间只做预览，不触发解码跳转）。
        // 纯点击（未拖动）位置未变，跳过 Seek 避免一次无谓的解码器 flush。
        if (dragged)
            SeekRequested?.Invoke(PositionFromX(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // ALT+TAB 等导致捕获丢失时复位 scrubbing，否则轮询不再更新播放头位置
        if (_scrubbing)
        {
            _scrubbing = false;
            InvalidateVisual();
        }
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

    // ---- 渲染资源：颜色全部取自主题令牌 ----
    // 不能做成 static readonly：那会把首次解析到的画刷固化下来，切换主题后时间轴
    // 仍停在旧配色（浅色下深色轨道 + 浅色刻度标签 = 几乎看不见）。
    // 令牌只在主题变体变化时重取（见 EnsureTheme），不是每帧。
    private static readonly Typeface TypeConsolas = new("Consolas");

    // 字段初值取令牌表的深色值：首次 EnsureTheme 会按当前变体覆盖，运行时不生效，
    // 只为消除硬编码副本（单一真源见 ThemeTokens）。
    private IBrush _trackBg = new SolidColorBrush(Color.Parse(ThemeTokens.Get("ControlBgBrush").Dark));
    private IBrush _loopFill = new SolidColorBrush(Color.Parse(ThemeTokens.Get("LoopFillBrush").Dark));
    private IBrush _loopLine = new SolidColorBrush(Color.Parse(ThemeTokens.Get("SuccessBrush").Dark));
    private IBrush _tickColor = new SolidColorBrush(Color.Parse(ThemeTokens.Get("BorderBrush").Dark));
    private IBrush _tickLabelBrush = new SolidColorBrush(Color.Parse(ThemeTokens.Get("TextMutedBrush").Dark));
    private IBrush _playhead = new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark));
    private IBrush _timestamp = new SolidColorBrush(Color.Parse(ThemeTokens.Get("TextSecondaryBrush").Dark));
    private Pen _penLoopLine = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("SuccessBrush").Dark)), 1.5);
    private Pen _penTick = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("BorderBrush").Dark)), 1);
    private Pen _penPlayhead = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark)), 2);
    private ThemeVariant? _themeTag;

    /// <summary>主题变体变化时重取令牌并丢弃握着旧画笔的文本缓存。</summary>
    private void EnsureTheme()
    {
        if (_themeTag == ActualThemeVariant) return;
        _themeTag = ActualThemeVariant;
        _trackBg = ThemePalette.Brush(this, "ControlBgBrush");
        _loopFill = ThemePalette.Brush(this, "LoopFillBrush");
        _loopLine = ThemePalette.Brush(this, "SuccessBrush");
        _tickColor = ThemePalette.Brush(this, "BorderBrush");
        _tickLabelBrush = ThemePalette.Brush(this, "TextMutedBrush");
        _playhead = ThemePalette.Brush(this, "AccentBrush");
        _timestamp = ThemePalette.Brush(this, "TextSecondaryBrush");
        _penLoopLine = new Pen(_loopLine, 1.5);
        _penTick = new Pen(_tickColor, 1);
        _penPlayhead = new Pen(_playhead, 2);
        // FormattedText 在构造时就绑定了画笔，主题变了必须重建（_tickLabelSrc 一并清空以触发重建）
        Array.Clear(_tickLabel);
        Array.Clear(_tickLabelSrc);
        _stampText = null;
        _stampSrc = string.Empty;
    }

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        EnsureTheme();
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 轨道底（缓存画刷）
        var trackRect = new Rect(0, h * 0.30, w, h * 0.40);
        dc.DrawFill(_trackBg, trackRect);

        // A-B 循环区间（半透明绿 + 两端竖线）
        if (_loopEnabled && _duration100ns > 0 && _loopEnd > _loopStart)
        {
            var ax = (double)_loopStart / _duration100ns * w;
            var bx = (double)_loopEnd / _duration100ns * w;
            dc.DrawFill(_loopFill, new Rect(ax, 0, bx - ax, h));
            dc.DrawLine(_penLoopLine, new Point(ax, 0), new Point(ax, h));
            dc.DrawLine(_penLoopLine, new Point(bx, 0), new Point(bx, h));
        }

        // 11 个刻度 + hh:mm:ss 标签
        for (var i = 0; i <= 10; i++)
        {
            var x = w * i / 10.0;
            var tickTop = i % 5 == 0 ? h * 0.12 : h * 0.2;
            dc.DrawLine(_penTick, new Point(x, tickTop), new Point(x, h * 0.3));
            if (i % 5 == 0 && _duration100ns > 0)
            {
                var slot = i / 5; // 0/5/10 → 三个标签位
                var label = Ts((long)(_duration100ns * i / 10.0));
                // 按内容缓存：时长不变时（暂停盯帧的常态）三个标签复用同一批 FormattedText。
                // 失效条件：时长变化 / 切语言（格式由 hh\:mm\:ss 决定，不随语言变）/ 切主题（见 EnsureTheme）。
                if (!string.Equals(_tickLabelSrc[slot], label, StringComparison.Ordinal))
                {
                    _tickLabelSrc[slot] = label;
                    _tickLabel[slot] = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, TypeConsolas, 10, _tickLabelBrush);
                }
                var ft = _tickLabel[slot]!;
                dc.DrawText(ft, new Point(Math.Min(x + 2, w - ft.Width), h - ft.Height - 1));
            }
        }

        // 播放头（拖动预览时显示预览位置）
        var pos = _scrubbing ? _preview100ns : _position100ns;
        if (_duration100ns > 0)
        {
            // 必须钳制：预览位置可能来自拖到控件外的指针（<0 或 >duration），
            // 未钳制会把播放头画到轨道外甚至 Bounds 之外（用户看到"播放头消失了"）。
            var px = Math.Clamp((double)pos / _duration100ns * w, 0, w);
            dc.DrawLine(_penPlayhead, new Point(px, 0), new Point(px, h));
            // 三角形几何只建一次（x 基准为 0），靠平移矩阵挪到播放头位置。
            // Matrix 是**值类型**，PushTransform 记录的是当时的矩阵值，
            // 不存在"可变对象被延迟回放覆盖"的隐患（画刷同理，见 _playhead）。
            _playheadGeometry ??= BuildPlayheadTriangle();
            using (dc.PushTransform(new Matrix(1, 0, 0, 1, px, 0)))
                dc.DrawGeometry(_playhead, null, _playheadGeometry);
        }

        // 当前时间戳（左上）
        var stamp = TimeSpan.FromTicks(_scrubbing ? _preview100ns : _position100ns).ToString(@"hh\:mm\:ss\.fff");
        // 同样按内容缓存：暂停时时间戳不变 ⇒ 命中缓存；播放中每帧重建（内容确实变了）。
        if (!string.Equals(_stampSrc, stamp, StringComparison.Ordinal))
        {
            _stampSrc = stamp;
            _stampText = new FormattedText(stamp, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, TypeConsolas, 11, _timestamp);
        }
        dc.DrawText(_stampText!, new Point(4, 2));
    }

    /// <summary>刻度标签缓存（3 个：0/5/10 号刻度）。内容不变则复用，避免每帧 3 次字形排版。</summary>
    private readonly FormattedText?[] _tickLabel = new FormattedText?[3];
    private readonly string[] _tickLabelSrc = new string[3];

    /// <summary>时间戳文本缓存（内容不变则复用）。</summary>
    private FormattedText? _stampText;
    private string _stampSrc = string.Empty;

    /// <summary>播放头三角形几何（x 基准为 0，靠平移矩阵定位），只建一次。</summary>
    private StreamGeometry? _playheadGeometry;

    private static StreamGeometry BuildPlayheadTriangle()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(-5, 0), true);
            ctx.LineTo(new Point(5, 0));
            ctx.LineTo(new Point(0, 7));
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
