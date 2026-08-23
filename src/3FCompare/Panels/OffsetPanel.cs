using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;

namespace _3FCompare.Panels;

/// <summary>偏移校准面板（WinForms OffsetPanel 对应）：相对第 1 路的 ±帧/±100ms/对齐/归零。</summary>
public sealed class OffsetPanel : StackPanel
{
    private readonly TextBlock _current = new()
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 12,
        Foreground = new SolidColorBrush(Color.Parse("#FFC8C8D2")), TextWrapping = TextWrapping.Wrap,
    };

    public event EventHandler? AlignRequested;
    public event Action<long>? OffsetNudge;   // delta100ns
    public event EventHandler? OffsetReset;

    private long _frameTicks = (long)(TimeSpan.TicksPerSecond / 24.0); // 24fps 缺省

    public OffsetPanel()
    {
        Margin = new global::Avalonia.Thickness(10);
        Spacing = 8;

        var mk = (string text, string key, Action click) =>
        {
            var b = new Button { Content = LanguageManager.T(key), Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(b, text);
            b.Click += (_, _) => click();
            return b;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(mk("", "Offset_FrameMinus", () => OffsetNudge?.Invoke(-_frameTicks)));
        row1.Children.Add(mk("", "Offset_FramePlus", () => OffsetNudge?.Invoke(_frameTicks)));

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(mk("", "Offset_MsMinus", () => OffsetNudge?.Invoke(-TimeSpan.FromMilliseconds(100).Ticks)));
        row2.Children.Add(mk("", "Offset_MsPlus", () => OffsetNudge?.Invoke(TimeSpan.FromMilliseconds(100).Ticks)));

        var align = mk("", "Offset_Align", () => AlignRequested?.Invoke(this, EventArgs.Empty));
        var reset = mk("", "Offset_Reset", () => OffsetReset?.Invoke(this, EventArgs.Empty));

        Children.Add(new TextBlock
        {
            Text = LanguageManager.T("Offset_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")), TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(_current);
        Children.Add(row1);
        Children.Add(row2);
        Children.Add(align);
        Children.Add(reset);
    }

    public void SetFps(double fps)
    {
        if (fps > 0) _frameTicks = (long)(TimeSpan.TicksPerSecond / fps);
    }

    /// <summary>刷新偏移显示（fpsText 如 "24"）。</summary>
    public void SetOffset(long offset100ns, double fps)
    {
        var ms = offset100ns / (double)TimeSpan.TicksPerMillisecond;
        var frames = offset100ns / (double)_frameTicks;
        _current.Text = LanguageManager.Tf("Offset_ValueFmt", (int)Math.Round(ms), frames, fps.ToString("0.##"));
    }

    public void SetPlaceholder() => _current.Text = LanguageManager.T("Offset_NotSelected");
}
