using _3FCompare.Core.Backend;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>差异叠加视图（F20，可选）：两路同屏画面的逐像素差异热力图。
/// 原理：将两路 Surface 截图缩小到网格采样，逐像素计算 |ΔRGB| 并叠加显示。
/// 注意：真实模式受色彩模式一致性与缩放路径影响，作为参考工具而非测量工具（见 01 需求 F20 备注）。</summary>
public sealed class DiffOverlayView : Control
{
    private readonly CompareGridView _grid;
    private int _aIndex;
    private int _bIndex = 1;
    private const int SampleWidth = 96; // 采样网格宽（每 8 像素 1 点 → 结果 ~12px 星盘）

    public event EventHandler? PairChanged;

    public DiffOverlayView(CompareGridView grid)
    {
        _grid = grid;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = AppTheme.Colors.CanvasBackgroundDark;
        DoubleBuffered = true;
    }

    public void SetPair(int aIndex, int bIndex)
    {
        _aIndex = aIndex;
        _bIndex = bIndex;
        PairChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public int AIndex => _aIndex;
    public int BIndex => _bIndex;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var rect = ClientRectangle;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // 背景
        g.Clear(AppTheme.Colors.CanvasBackgroundDark);

        var a = _grid.GetSurface(_aIndex);
        var b = _grid.GetSurface(_bIndex);
        using var headerFont = new Font("Microsoft YaHei UI", 9f);
        using var headerBrush = new SolidBrush(Color.FromArgb(200, 255, 255, 255));

        if (a is null || b is null)
        {
            g.DrawString(LanguageManager.T("Diff_Need2"), headerFont, headerBrush, 10, 10);
            return;
        }

        g.DrawString(LanguageManager.Tf("Diff_HeaderFmt", _aIndex + 1, _bIndex + 1), headerFont, headerBrush, 10, 10);

        // 采样两路画面
        using var bmpA = CaptureSurface(a);
        using var bmpB = CaptureSurface(b);
        if (bmpA is null || bmpB is null)
        {
            g.DrawString(LanguageManager.T("Diff_SampleFail"), headerFont, Brushes.OrangeRed, 10, 30);
            return;
        }

        // 计算差异网格
        var cell = rect.Width / SampleWidth;
        var cellsX = Math.Min(SampleWidth, rect.Width / Math.Max(1, cell));
        var height = rect.Height - 40;
        var cellsY = Math.Max(1, height / Math.Max(1, cell));

        var diffCount = 0;
        for (var cy = 0; cy < cellsY; cy++)
        {
            for (var cx = 0; cx < cellsX; cx++)
            {
                var sx = (int)(cx / (double)cellsX * bmpA.Width);
                var sy = (int)(cy / (double)cellsY * bmpA.Height);
                var cA = bmpA.GetPixel(sx, sy);
                var cB = bmpB.GetPixel(sx, sy);

                // |Δ| 归一化
                var diff = Math.Abs(cA.R - cB.R) + Math.Abs(cA.G - cB.G) + Math.Abs(cA.B - cB.B);
                var mag = Math.Min(1.0, diff / (3.0 * 255.0));
                if (mag < 0.02) continue; // 低于阈值忽略

                diffCount++;
                // 热力色：低→青蓝，高→红白
                var color = HeatColor(mag);
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, cx * cell, 40 + cy * cell, cell, cell);
            }
        }

        using var infoFont = new Font("Microsoft YaHei UI", 8f);
        using var infoBrush = new SolidBrush(AppTheme.Colors.TextSecondary);
        g.DrawString(LanguageManager.Tf("Diff_PercentFmt", diffCount, cellsX * cellsY, (double)diffCount / Math.Max(1, cellsX * cellsY) * 100), infoFont, infoBrush, 10, rect.Height - 18);

        // 图例
        using var legendFont = new Font("Consolas", 8f);
        g.DrawString(LanguageManager.T("Diff_LegendWeak"), legendFont, Brushes.Cyan, 10, rect.Height - 32);
        g.DrawString(LanguageManager.T("Diff_LegendStrong"), legendFont, Brushes.Red, 70, rect.Height - 32);
    }

    /// <summary>语言切换后重绘（动态文本在 OnPaint 中读取资源）。</summary>
    public void ApplyLanguage() => Invalidate();

    private static Bitmap? CaptureSurface(PlayerSurface surface)
    {
        try
        {
            // 缩小采样以加速（目标宽固定 320）
            var targetW = Math.Min(surface.Width, 320);
            var targetH = surface.Height == 0 ? 0 : (int)(targetW / (double)surface.Width * surface.Height);
            targetH = Math.Max(1, targetH);
            var bmp = new Bitmap(targetW, targetH);
            using var g = Graphics.FromImage(bmp);
            // 先把源绘制到临时位图，再缩采样
            var temp = new Bitmap(Math.Max(1, surface.Width), Math.Max(1, surface.Height));
            surface.DrawToBitmap(temp, new Rectangle(0, 0, temp.Width, temp.Height));
            g.DrawImage(temp, new Rectangle(0, 0, targetW, targetH));
            g.Dispose();
            temp.Dispose();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static Color HeatColor(double mag)
    {
        // 低→青(0,255,255)，中→黄(255,255,0)，高→红(255,0,0)
        if (mag < 0.5)
        {
            var t = mag / 0.5;
            return Color.FromArgb((int)(t * 255), 255, (int)(255 * (1 - t)));
        }
        var t2 = (mag - 0.5) / 0.5;
        return Color.FromArgb(255, (int)(255 * (1 - t2)), 0);
    }
}