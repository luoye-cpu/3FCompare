using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using _3FCompare.App;
using _3FCompare.Core.Backend;

namespace _3FCompare.Avalonia.Controls;

/// <summary>差异热力图（WinForms DiffOverlayView 对应）：
/// 对两路会话做 96×N 网格像素采样（颜色管理前码值），归一化 |ΔRGB| 热力着色。
/// 点击重采样。</summary>
public sealed class DiffOverlayView : Control
{
    private const int CellsX = 96;
    private float[] _heat = Array.Empty<float>();
    private int _cellsY;
    private int _aIndex, _bIndex = 1;
    private double _diffRatio;

    public int AIndex { get => _aIndex; set { _aIndex = value; InvalidateVisual(); } }
    public int BIndex { get => _bIndex; set { _bIndex = value; InvalidateVisual(); } }

    public void SetPair(int a, int b)
    {
        _aIndex = a; _bIndex = b;
        Resample();
    }

    public DiffOverlayView()
    {
        ClipToBounds = true;
        PointerPressed += (_, _) => Resample();
    }

    /// <summary>重新采样并重绘（会话由外部提供）。</summary>
    public void Resample(Func<int, IPlayerSession?>? sessionAt = null)
    {
        _sessionAt = sessionAt ?? _sessionAt;
        if (_sessionAt is null) return;
        var sa = _sessionAt(_aIndex);
        var sb = _sessionAt(_bIndex);
        if (sa is null || sb is null) return;

        try
        {
            var media = sa.ReadMediaInfo();
            var w = media?.VideoWidth ?? 1280;
            var h = media?.VideoHeight ?? 720;
            _cellsY = Math.Max(8, (int)Math.Round((double)CellsX * h / w / 2)); // 2:1 采样密度
            var heat = new float[CellsX * _cellsY];
            var diffCount = 0;

            for (var y = 0; y < _cellsY; y++)
            {
                for (var x = 0; x < CellsX; x++)
                {
                    var px = (int)((x + 0.5) / CellsX * w);
                    var py = (int)((y + 0.5) / _cellsY * h);
                    if (!sa.TryReadPixel(px, py, out var a) || !sb.TryReadPixel(px, py, out var b))
                    {
                        heat[y * CellsX + x] = -1f;
                        continue;
                    }
                    var d = (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3f;
                    heat[y * CellsX + x] = d;
                    if (d >= 0.02f) diffCount++;
                }
            }
            _heat = heat;
            _diffRatio = diffCount * 100.0 / (CellsX * _cellsY);
            InvalidateVisual();
        }
        catch
        {
            _heat = Array.Empty<float>();
            InvalidateVisual();
        }
    }

    private Func<int, IPlayerSession?>? _sessionAt;

    /// <summary>设置会话取用器（MainWindow 注入）。</summary>
    public void SetSessionProvider(Func<int, IPlayerSession?> provider) => _sessionAt = provider;

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // 头部
        var header = LanguageManager.Tf("Diff_HeaderFmt", _aIndex, _bIndex);
        dc.DrawText(new FormattedText(header, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 13,
            new SolidColorBrush(Color.FromRgb(255, 255, 255))), new Point(10, 8));

        var top = 36.0;
        var bottom = h - 28;
        if (_heat.Length == 0)
        {
            dc.DrawText(new FormattedText(LanguageManager.T("Diff_SampleFail"), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12,
                new SolidColorBrush(Color.FromRgb(140, 140, 150))), new Point(10, top + 4));
            return;
        }

        // 热力网格：青 → 黄 → 红
        var cw = (w - 20) / CellsX;
        var chh = (bottom - top) / _cellsY;
        for (var y = 0; y < _cellsY; y++)
        {
            for (var x = 0; x < CellsX; x++)
            {
                var v = _heat[y * CellsX + x];
                if (v < 0) continue;          // 采样失败跳过
                if (v < 0.02f) continue;      // 弱差异不画（WinForms 同阈值）
                var color = HeatColor(Math.Min(1f, v));
                dc.DrawRectangle(new SolidColorBrush(color), null,
                    new Rect(10 + x * cw, top + y * chh, Math.Max(1, cw - 0.5), Math.Max(1, chh - 0.5)));
            }
        }

        // 图例 + 差异百分比
        var legendY = h - 20;
        for (var i = 0; i < 60; i++)
            dc.DrawRectangle(new SolidColorBrush(HeatColor(i / 60f)), null, new Rect(10 + i * 2, legendY, 2, 8));
        var legend = $"{LanguageManager.T("Diff_LegendWeak")} → {LanguageManager.T("Diff_LegendStrong")}";
        dc.DrawText(new FormattedText(legend, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 10,
            new SolidColorBrush(Color.FromRgb(200, 200, 210))), new Point(140, legendY - 2));
        var pct = LanguageManager.Tf("Diff_PercentFmt",
            (int)Math.Round(_diffRatio * CellsX * _cellsY / 100.0), CellsX * _cellsY, _diffRatio);
        dc.DrawText(new FormattedText(pct, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Consolas"), 11,
            new SolidColorBrush(Color.FromRgb(255, 200, 64))), new Point(10, legendY - 16));
    }

    private static Color HeatColor(float t)
    {
        // 青(0) → 黄(0.5) → 红(1)
        return t < 0.5f
            ? Color.FromRgb(0, (byte)(180 + 75 * t * 2), (byte)(200 * (1 - t * 2)))
            : Color.FromRgb((byte)(255), (byte)(255 * (1 - (t - 0.5f) * 2)), 0);
    }
}
