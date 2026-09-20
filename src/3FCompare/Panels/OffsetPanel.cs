using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Controls;

namespace _3FCompare.Panels;

/// <summary>偏移校准面板（WinForms OffsetPanel 对应）：相对第 1 路的 ±帧/±100ms/对齐/归零。</summary>
public sealed class OffsetPanel : StackPanel
{
    private readonly TextBlock _current = new TextBlock
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
    }.Themed("TextSecondaryBrush");

    public event EventHandler? AlignRequested;
    public event Action<long>? OffsetNudge;   // delta100ns
    public event EventHandler? OffsetReset;

    private long _frameTicks = (long)(TimeSpan.TicksPerSecond / 24.0); // 24fps 缺省

    public OffsetPanel()
    {
        Margin = new global::Avalonia.Thickness(10);
        Spacing = 8;

        // 图标 + 文本（docs/31 阶段 4 第二批）：字形 ◀ ▶ ◎ ↺ 在缺字字体下渲染成豆腐块，
        // 改为 AppIcons 的矢量几何；按钮文字保持可见，故图标不是唯一语义来源。
        // 排版沿用原样：减号按钮箭头在左、加号按钮箭头在右（原来就是 "◀ 1帧" / "1帧 ▶"，
        // 一对按钮的箭头朝外）。图标取 13px —— 与 ProbePanel 的复制按钮同级，26px 高的按钮里不撑边。
        var mk = (string key, string iconKey, bool iconFirst, Action click) =>
        {
            var label = new TextBlock
            {
                Text = LanguageManager.T(key), VerticalAlignment = VerticalAlignment.Center,
            }.Themed("TextPrimaryBrush");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (iconFirst) content.Children.Add(AppIcons.Create(iconKey, 13));
            content.Children.Add(label);
            if (!iconFirst) content.Children.Add(AppIcons.Create(iconKey, 13));
            var b = new Button
            {
                Content = content, Height = 26, HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            b.Click += (_, _) => click();
            return b;
        };

        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row1.Children.Add(mk("Offset_FrameMinus", "NudgeBack", true, () => OffsetNudge?.Invoke(-_frameTicks)));
        row1.Children.Add(mk("Offset_FramePlus", "NudgeForward", false, () => OffsetNudge?.Invoke(_frameTicks)));

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(mk("Offset_MsMinus", "NudgeBack", true, () => OffsetNudge?.Invoke(-TimeSpan.FromMilliseconds(100).Ticks)));
        row2.Children.Add(mk("Offset_MsPlus", "NudgeForward", false, () => OffsetNudge?.Invoke(TimeSpan.FromMilliseconds(100).Ticks)));

        var align = mk("Offset_Align", "Align", true, () => AlignRequested?.Invoke(this, EventArgs.Empty));
        var reset = mk("Offset_Reset", "Reset", true, () => OffsetReset?.Invoke(this, EventArgs.Empty));

        Children.Add(new TextBlock
        {
            Text = LanguageManager.T("Offset_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        Children.Add(_current);
        Children.Add(row1);
        Children.Add(row2);
        Children.Add(align);
        Children.Add(reset);
    }

    public void SetFps(double fps)
    {
        // fps 极大（>1e7，元数据异常或除零产生的脏值）时 TicksPerSecond/fps 会舍入到 0，
        // 于是下面 offset100ns / _frameTicks 得 Infinity / NaN 并显示成 "∞" ——
        // 一帧至少 1 tick，钳到 1 保证换算始终有界。
        if (fps > 0) _frameTicks = Math.Max(1, (long)(TimeSpan.TicksPerSecond / fps));
    }

    /// <summary>刷新偏移显示（fpsText 如 "24"）。</summary>
    public void SetOffset(long offset100ns, double fps)
    {
        var ms = offset100ns / (double)TimeSpan.TicksPerMillisecond;
        // 这里再钳一次：_frameTicks 可能在 SetFps 之前就被用到（会话未就绪时仍是缺省值）
        var frames = offset100ns / (double)Math.Max(1, _frameTicks);
        _current.Text = LanguageManager.Tf("Offset_ValueFmt", (int)Math.Round(ms), frames, fps.ToString("0.##"));
    }

    public void SetPlaceholder() => _current.Text = LanguageManager.T("Offset_NotSelected");
}
