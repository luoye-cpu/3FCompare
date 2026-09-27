using System;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using _3FCompare.App;
using _3FCompare.App.Capture;

namespace _3FCompare.Controls;

/// <summary>时间轴拖动缩略图预览弹窗（WinForms ThumbnailPopup 对应）：
/// 无边框置顶、250ms 未刷新自动隐藏、220×130 等比缩放显示捕获帧。
/// SkiaSharp 合成路径：GDI 位图 → WriteableBitmap（renderbench 基准：位图合成收益最大）。</summary>
public sealed class ThumbnailPopup : Window
{
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _hint = new();
    private readonly DispatcherTimer _hideTimer;
    private WriteableBitmap? _writeable;

    public ThumbnailPopup()
    {
        // Avalonia 12：WindowDecorations 是新 API；旧 SystemDecorations 已废弃。
        // 此处直接给依赖属性赋值（WindowDecorationsProperty），避开废弃的 CLR 包装器（CS0618）。
        SetCurrentValue(WindowDecorationsProperty, global::Avalonia.Controls.WindowDecorations.None);
        ShowActivated = false;
        // Z 序靠 **Owner**，不用 Topmost（与 LayoutOverlayWindow / FloatingTransportWindow 同口径）：
        // Topmost 会让缩略图在切到别的程序之后仍浮在最上层，而这里真正需要的只是"高于主窗口"。
        // Owner 由创建方显式设置（MainWindow.Playback 里 `new ThumbnailPopup { Owner = this }`）。
        Topmost = false;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        Width = 220; Height = 130;
        ThemePalette.SetBrush(this, BackgroundProperty, "CanvasDarkBrush");

        var border = new Border
        {
            BorderThickness = new global::Avalonia.Thickness(1),
            Padding = new global::Avalonia.Thickness(2),
            Child = _image,
        };
        ThemePalette.SetBrush(border, Border.BorderBrushProperty, "BorderBrush");
        ThemePalette.SetBrush(_hint, TextBlock.ForegroundProperty, "TextMutedBrush");
        _hint.FontSize = 11;
        _hint.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
        _hint.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
        Content = new Panel { Children = { border, _hint } };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    /// <summary>设置宿主窗口。Z 序靠 Owner 而非 Topmost（见构造函数注释），
    /// 但 <c>Owner</c> 是受保护成员、外部无法赋值 ⇒ 必须由本类型自己暴露入口。</summary>
    public void SetOwner(Window owner)
    {
        if (owner is null) return;
        try { Owner = owner; }
        catch (InvalidOperationException) { /* 已显示/已关闭时不允许改 Owner，保持现状即可 */ }
    }

    /// <summary>在屏幕坐标 (x,y) 显示；bmp 为 null 时显示拖动提示。
    /// 复用 WriteableBitmap（同尺寸时仅拷贝像素，不重新分配 GPU 纹理）。
    /// <para><b>生命周期</b>：bmp 由本方法负责释放。像素一旦复制进 WriteableBitmap，
    /// 原 GDI 位图就不再需要；调用方<b>不得</b>再 Dispose 传入的位图。</para></summary>
    public void ShowAt(PixelPoint position, System.Drawing.Bitmap? bmp)
    {
        _hint.Text = LanguageManager.T("Thumbnail_Hint");
        _hint.IsVisible = bmp is null;
        _image.IsVisible = bmp is not null;

        if (bmp is not null)
        {
            // P0-5 修复：过去这里只复制像素、从不释放 GDI 位图，而调用方在拖动（scrubbing）
            // 分支里也不会释放，导致每 150ms（一个 scrub tick）泄漏一张 480px 宽的 GDI 位图。
            // GDI 句柄上限约 1 万，长时间拖动时间轴会耗尽句柄，使整个进程的界面绘制失败。
            try { UpdateBitmap(bmp); }
            finally { bmp.Dispose(); }
        }

        Position = new PixelPoint(position.X - 110, position.Y - (int)Height - 6);
        if (!IsVisible) Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>3FCompare 优化项⑤：把抓帧结果缩放到预览宽度（GDI StretchBlt 一次完成，
    /// 避免全尺寸 4K 位图跨线程传输与 GPU 上传）。</summary>
    public static System.Drawing.Bitmap? ScaleTo(System.Drawing.Bitmap src, int maxWidth)
    {
        try
        {
            if (src.Width <= maxWidth) return new System.Drawing.Bitmap(src);
            var w = maxWidth;
            var h = Math.Max(1, (int)Math.Round((double)src.Height * w / src.Width));
            var dst = new System.Drawing.Bitmap(w, h);
            using var g = System.Drawing.Graphics.FromImage(dst);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, w, h));
            return dst;
        }
        catch { return null; }
    }

    /// <summary>复用 WriteableBitmap：同尺寸直接锁写像素，不同尺寸才重新分配。</summary>
    private unsafe void UpdateBitmap(System.Drawing.Bitmap src)
    {
        var data = src.LockBits(new System.Drawing.Rectangle(0, 0, src.Width, src.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            if (_writeable is null || _writeable.PixelSize.Width != src.Width || _writeable.PixelSize.Height != src.Height)
            {
                _writeable?.Dispose();
                _writeable = new WriteableBitmap(new PixelSize(src.Width, src.Height), new Vector(96, 96),
                    global::Avalonia.Platform.PixelFormat.Bgra8888);
            }
                        using var l = _writeable.Lock();
            // GDI+ **底向上**位图的 Stride 是负数。原先 Math.Min(l.RowBytes, data.Stride)
            // 会取到这个负数，而 Buffer.MemoryCopy 的长度参数是 nuint ⇒ 负数被当成
            // 一个接近 ulong.MaxValue 的无符号巨值 ⇒ 越界拷贝、进程直接崩（不可 catch）。
            // 行字节数取**绝对值**；行首地址仍按带符号的 data.Stride 计算（底向上时向上走）。
            var absStride = Math.Abs(data.Stride);
            for (var y = 0; y < src.Height; y++)
            unsafe
            {
                var rowBytes = Math.Min(l.RowBytes, absStride);
                if (rowBytes <= 0) break; // 尺寸/格式异常时宁可不画，也不要越界写
                global::System.Buffer.MemoryCopy(
                    (void*)(data.Scan0 + y * data.Stride),
                    (void*)(l.Address + y * l.RowBytes),
                    rowBytes, rowBytes);
            }
            _image.Source = _writeable;
        }
        finally
        {
            src.UnlockBits(data);
        }
    }

    public new void Hide()
    {
        _hideTimer.Stop();
        base.Hide();
    }

    /// <summary>彻底关闭并释放预览窗口持有的资源。
    /// <para><b>注意</b>：Hide() 只是隐藏，Window 与其原生资源仍然存活。
    /// 宿主窗口关闭时必须调用本方法，否则进程里会残留一个永不销毁的顶层窗口，
    /// 且 Avalonia 会因仍有存活 Window 而不退出消息循环。</para></summary>
    public void CloseAndDispose()
    {
        _hideTimer.Stop();
        _image.Source = null;
        _writeable?.Dispose();
        _writeable = null;
        Close();
    }

}
