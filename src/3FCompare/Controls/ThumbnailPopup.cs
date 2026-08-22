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
    private global::Avalonia.Media.Imaging.Bitmap? _current;
    private WriteableBitmap? _writeable;

    public ThumbnailPopup()
    {
        SystemDecorations = SystemDecorations.None;
        ShowActivated = false;
        Topmost = true;
        ShowInTaskbar = false;
        IsHitTestVisible = false;
        Width = 220; Height = 130;
        Background = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(16, 16, 18));

        var border = new Border
        {
            BorderBrush = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(80, 80, 90)),
            BorderThickness = new global::Avalonia.Thickness(1),
            Padding = new global::Avalonia.Thickness(2),
            Child = _image,
        };
        _hint.Foreground = new SolidColorBrush(global::Avalonia.Media.Color.FromRgb(140, 140, 150));
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

    /// <summary>在屏幕坐标 (x,y) 显示；bmp 为 null 时显示拖动提示。</summary>
    public void ShowAt(PixelPoint position, System.Drawing.Bitmap? bmp)
    {
        _hint.Text = LanguageManager.T("Thumbnail_Hint");
        _hint.IsVisible = bmp is null;
        _image.IsVisible = bmp is not null;

        if (bmp is not null)
        {
            var av = Convert(bmp);
            _current?.Dispose();
            _current = av;
            _image.Source = av;
        }

        Position = new PixelPoint(position.X - 110, position.Y - (int)Height - 6);
        if (!IsVisible) Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public new void Hide()
    {
        _hideTimer.Stop();
        base.Hide();
    }

    /// <summary>GDI 位图 → Avalonia 位图（BGRA 直拷）。</summary>
    private static global::Avalonia.Media.Imaging.Bitmap Convert(System.Drawing.Bitmap src)
    {
        var data = src.LockBits(new System.Drawing.Rectangle(0, 0, src.Width, src.Height),
            ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var wb = new WriteableBitmap(new PixelSize(src.Width, src.Height), new Vector(96, 96),
                global::Avalonia.Platform.PixelFormat.Bgra8888);
            using (var l = wb.Lock())
            {
                for (var y = 0; y < src.Height; y++)
                unsafe
                {
                    var rowBytes = src.Width * 4;
                    global::System.Buffer.MemoryCopy(
                        (void*)(data.Scan0 + y * data.Stride),
                        (void*)(l.Address + y * l.RowBytes),
                        rowBytes, rowBytes);
                }
            }
            return wb;
        }
        finally
        {
            src.UnlockBits(data);
        }
    }
}
