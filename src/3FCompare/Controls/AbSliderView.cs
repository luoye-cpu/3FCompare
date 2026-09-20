using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using _3FCompare.App;

namespace _3FCompare.Controls;

/// <summary>A-B 滑块对比视图（WinForms AbSliderView 对应）。
/// 左 A 右 B 以可拖动分割线分屏；真实画面为子 HWND 无法合成进本视图（与 WinForms
/// 一致使用合成渐变占位 + 路号标签）；拖动分割线触发 SliderChanged(0..1)。</summary>
public sealed class AbSliderView : Control
{
    private double _slider = 0.5;
    private int _aIndex, _bIndex = 1;

    public double Slider
    {
        get => _slider;
        set { _slider = Math.Clamp(value, 0, 1); InvalidateVisual(); SliderChanged?.Invoke(_slider); }
    }

    public int AIndex { get => _aIndex; set { _aIndex = value; InvalidateVisual(); } }
    public int BIndex { get => _bIndex; set { _bIndex = value; InvalidateVisual(); } }

    public event Action<double>? SliderChanged;

    public AbSliderView()
    {
        ClipToBounds = true;
        // 自绘控件不会因主题变化自动失效，必须显式重绘
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    // ---- 渲染资源静态化：这些画刷/画笔/字体的取值与控件尺寸无关，
    // 原先每帧 new（渐变刷 2 个 + Pen 1 个 + SolidColorBrush 1 个 + Typeface 2 个），
    // 拖动分割线时每帧十几二十次分配，纯属浪费。----
    // 渐变用 **相对点坐标**（RelativeUnit.Relative），因此同一个实例可以复用于任意尺寸矩形，
    // 不需要随 Bounds 变化重建 —— 这是能做成 static readonly 的前提。
    private static readonly LinearGradientBrush BrushLaneA = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(30, 80, 160), 0),
            new GradientStop(Color.FromRgb(12, 12, 16), 1),
        },
    };

    private static readonly LinearGradientBrush BrushLaneB = new()
    {
        StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(140, 40, 120), 0),
            new GradientStop(Color.FromRgb(12, 12, 16), 1),
        },
    };

    // 上面两条车道渐变是**合成占位画面**（真实 D3D 内容不合成于此），语义等同视频内容，
    // 与主题无关 ⇒ 保持固定深色。下面分割线/拖柄属覆盖层 ⇒ 取令牌。
    private static readonly Typeface TypeConsolas = new("Consolas");

    // 字段初值取令牌表的深色值：它们会在首次 EnsureTheme 时被按当前变体重取的画刷覆盖，
    // 运行时不生效，只是为了让"颜色"不出现在本文件里（单一真源见 ThemeTokens）。
    private IBrush _accent = new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark));
    private Pen _penAccent = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark)), 2);
    private Pen _penGrip = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayGripBrush").Dark)), 2);
    private IBrush _gripBg = new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayRingBrush").Dark));
    private Avalonia.Styling.ThemeVariant? _themeTag;

    private void EnsureTheme()
    {
        if (_themeTag == ActualThemeVariant) return;
        _themeTag = ActualThemeVariant;
        _accent = ThemePalette.Brush(this, "AccentBrush");
        _penAccent = new Pen(_accent, 2);
        _penGrip = new Pen(ThemePalette.Brush(this, "OverlayGripBrush"), 2);
        _gripBg = ThemePalette.Brush(this, "OverlayRingBrush");
        // 角标签缓存握着旧画笔 ⇒ 清空源串触发重建
        _aText = null; _aSrc = string.Empty;
        _bText = null; _bSrc = string.Empty;
    }

    /// <summary>角标签文本缓存：内容只有 A/B 路号 + 语言两种变量，
    /// 按内容缓存后拖动分割线（每帧重绘）时完全不重建 FormattedText。
    /// 失效条件：路号变化或切换语言 ⇒ 字符串比较自动失效。</summary>
    private FormattedText? _aText; private string _aSrc = string.Empty;
    private FormattedText? _bText; private string _bSrc = string.Empty;

    private FormattedText Reuse(FormattedText? cached, string? cachedSrc, string src, double size)
        => cached is not null && string.Equals(cachedSrc, src, StringComparison.Ordinal)
            ? cached
            : new FormattedText(src, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, TypeConsolas, size, ThemePalette.Brush(this, "OverlayGripBrush"));

    public void SetPair(int a, int b)
    {
        _aIndex = a; _bIndex = b;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Slider = e.GetPosition(this).X / Math.Max(1, Bounds.Width);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Slider = e.GetPosition(this).X / Math.Max(1, Bounds.Width);
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        EnsureTheme();

        // 左右合成渐变占位（WinForms 同语义：真实 D3D 内容不合成于此）
        dc.DrawRectangle(BrushLaneA, null, new Rect(0, 0, w * _slider, h));
        dc.DrawRectangle(BrushLaneB, null, new Rect(w * _slider, 0, w * (1 - _slider), h));

        // 分割线 + 拖柄（3 条白线）
        dc.DrawLine(_penAccent, new Point(w * _slider, 0), new Point(w * _slider, h));
        var gripY = h / 2;
        dc.FillRectangle(_gripBg, new Rect(w * _slider - 10, gripY - 14, 20, 28));
        for (var i = -1; i <= 1; i++)
            dc.DrawLine(_penGrip,
                new Point(w * _slider + i * 5 - 1.5, gripY - 7),
                new Point(w * _slider + i * 5 - 1.5, gripY + 7));

        // 角标签 A [n] / B [n]
        var aLabel = $"A {LanguageManager.Tf("AbSlider_LaneFmt", _aIndex + 1)}";
        _aText = Reuse(_aText, _aSrc, aLabel, 16);
        _aSrc = aLabel;
        dc.DrawText(_aText, new Point(10, 10));
        var bLabel = $"B {LanguageManager.Tf("AbSlider_LaneFmt", _bIndex + 1)}";
        _bText = Reuse(_bText, _bSrc, bLabel, 16);
        _bSrc = bLabel;
        dc.DrawText(_bText, new Point(w - _bText.Width - 10, 10));
    }
}
