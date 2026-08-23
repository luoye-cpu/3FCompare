using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    public AbSliderView() => ClipToBounds = true;

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

        // 左右合成渐变占位（WinForms 同语义：真实 D3D 内容不合成于此）
        dc.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(30, 80, 160), 0),
                new GradientStop(Color.FromRgb(12, 12, 16), 1),
            },
        }, null, new Rect(0, 0, w * _slider, h));
        dc.DrawRectangle(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(140, 40, 120), 0),
                new GradientStop(Color.FromRgb(12, 12, 16), 1),
            },
        }, null, new Rect(w * _slider, 0, w * (1 - _slider), h));

        // 分割线 + 拖柄（3 条白线）
        var accent = new SolidColorBrush(Color.FromRgb(255, 200, 64));
        dc.DrawLine(new Pen(accent, 2), new Point(w * _slider, 0), new Point(w * _slider, h));
        var gripY = h / 2;
        dc.FillRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 36)),
            new Rect(w * _slider - 10, gripY - 14, 20, 28));
        for (var i = -1; i <= 1; i++)
            dc.DrawLine(new Pen(Brushes.White, 2),
                new Point(w * _slider + i * 5 - 1.5, gripY - 7),
                new Point(w * _slider + i * 5 - 1.5, gripY + 7));

        // 角标签 A [n] / B [n]
        var aLabel = $"A {LanguageManager.Tf("AbSlider_LaneFmt", _aIndex + 1)}";
        var bLabel = $"B {LanguageManager.Tf("AbSlider_LaneFmt", _bIndex + 1)}";
        var font = new Typeface("Consolas");
        dc.DrawText(new FormattedText(aLabel, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, font, 16, Brushes.White), new Point(10, 10));
        var fb = new FormattedText(bLabel, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, font, 16, Brushes.White);
        dc.DrawText(fb, new Point(w - fb.Width - 10, 10));
    }
}
