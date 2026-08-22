using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>悬浮缩略图预览窗：无边框置顶小窗，绘制在进度条上方、跟随鼠标，
/// 用于时间轴拖动时的关键帧预览。尺寸约 220x130，半透明深色背景 + 缩放适配的图像。</summary>
public sealed class ThumbnailPopup : Form
{
    private Bitmap? _frame;
    private readonly System.Windows.Forms.Timer _hideTimer;

    public ThumbnailPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(220, 130);
        BackColor = AppTheme.Colors.ControlBackground;
        DoubleBuffered = true;
        // 拖动停止/移出窗口后短暂隐藏
        _hideTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _hideTimer.Tick += (_, _) => ApplyVisibility(false);
        _hideTimer.Start();
    }

    public void ShowAt(Point screenPos, Bitmap? frame)
    {
        // 接管 Bitmap 所有权：旧帧由本窗体释放，避免调用方 Dispose 后出现悬挂引用
        // （调用方 PerformScrubCapture 每次抓帧后立即 Dispose(bmp)，若直接保存引用，
        //  下次 Invalidate 触发 OnPaint 时读取 _frame.Width 会抛 ArgumentException）
        if (!ReferenceEquals(_frame, frame))
        {
            _frame?.Dispose();
            _frame = frame;
        }
        Location = new Point(screenPos.X - Width / 2, Math.Max(0, screenPos.Y - Height - 8));
        ApplyVisibility(_frame is not null);
    }

    public void HidePopup()
    {
        _hideTimer.Stop();
        ApplyVisibility(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame?.Dispose();
            _frame = null;
            _hideTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ApplyVisibility(bool visible)
    {
        if (visible != Visible)
        {
            Visible = visible;
            Invalidate();
        }
        else if (visible)
        {
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var rect = ClientRectangle;

        // 深色底 + 边框
        using var bg = new SolidBrush(AppTheme.Colors.ControlBackground);
        g.FillRectangle(bg, rect);
        using var border = new Pen(AppTheme.Colors.Border, 1);
        g.DrawRectangle(border, 0, 0, rect.Width - 1, rect.Height - 1);

        if (_frame is null)
        {
            using var f = new Font("Microsoft YaHei UI", 9f);
            using var b = new SolidBrush(AppTheme.Colors.TextMuted);
            var s = LanguageManager.T("Thumbnail_Hint");
            var sz = g.MeasureString(s, f);
            g.DrawString(s, f, b, (rect.Width - sz.Width) / 2, (rect.Height - sz.Height) / 2);
            return;
        }

        // 缩放适配绘制（保持宽高比，居中）
        var imgW = _frame.Width;
        var imgH = _frame.Height;
        if (imgW <= 0 || imgH <= 0) return;
        var scale = Math.Min((rect.Width - 8) / (float)imgW, (rect.Height - 8) / (float)imgH);
        var w = (int)(imgW * scale);
        var h = (int)(imgH * scale);
        var x = (rect.Width - w) / 2;
        var y = (rect.Height - h) / 2;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(_frame, x, y, w, h);
    }
}