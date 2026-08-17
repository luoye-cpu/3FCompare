using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>偏移校准面板（F9）：对选中路调整相对 master 的媒体时间偏移。
/// 支持 ±1帧 / ±100ms / ±1s 微调与「对齐此处」快速设置。</summary>
public sealed class OffsetPanel : Panel
{
    private readonly Label _info;
    private readonly Label _current;
    private readonly Button _btnFrameMinus;
    private readonly Button _btnFramePlus;
    private readonly Button _btnMsMinus;
    private readonly Button _btnMsPlus;
    private readonly Button _btnAlign;
    private readonly Button _btnReset;

    public event EventHandler? AlignRequested;
    public event EventHandler<long>? OffsetNudge; // 参数：增量(100ns)
    public event EventHandler? OffsetReset;

    /// <summary>当前选中路的偏移（100ns）显示。</summary>
    public long CurrentOffset { get; private set; }

    public OffsetPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = AppTheme.Colors.PanelBackground;
        Padding = AppTheme.Spacing.Standard;

        _info = new Label
        {
            Text = "偏移校准（相对第 1 路）",
            Dock = DockStyle.Top,
            Height = 26,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        _current = new Label
        {
            Text = "偏移: 0ms (0帧@24fps)",
            Dock = DockStyle.Top,
            Height = 24,
            Font = AppTheme.Fonts.MonospaceMediumFont,
            ForeColor = AppTheme.Colors.Accent,
        };

        _btnAlign = MakeButton("◎ 对齐于此帧");
        _btnAlign.Click += (_, _) => AlignRequested?.Invoke(this, EventArgs.Empty);

        _btnMsMinus = MakeButton("◀ 100ms");
        _btnMsMinus.Click += (_, _) => OffsetNudge?.Invoke(this, -TimeSpan.TicksPerMillisecond * 100);
        _btnMsPlus = MakeButton("100ms ▶");
        _btnMsPlus.Click += (_, _) => OffsetNudge?.Invoke(this, TimeSpan.TicksPerMillisecond * 100);

        _btnFrameMinus = MakeButton("◀ 1帧");
        _btnFrameMinus.Click += (_, _) => OffsetNudge?.Invoke(this, -_frameTicks);
        _btnFramePlus = MakeButton("1帧 ▶");
        _btnFramePlus.Click += (_, _) => OffsetNudge?.Invoke(this, _frameTicks);

        _btnReset = MakeButton("↺ 归零");
        _btnReset.Click += (_, _) => OffsetReset?.Invoke(this, EventArgs.Empty);

        // 布局：用 FlowLayout 简化
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 2, 0, 2) };
        row1.Controls.Add(_btnFrameMinus); row1.Controls.Add(_btnFramePlus);
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 2, 0, 2) };
        row2.Controls.Add(_btnMsMinus); row2.Controls.Add(_btnMsPlus);
        var row3 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 2, 0, 2) };
        row3.Controls.Add(_btnAlign); row3.Controls.Add(_btnReset);

        foreach (var b in new[] { _btnFrameMinus, _btnFramePlus, _btnMsMinus, _btnMsPlus, _btnAlign, _btnReset })
        {
            b.Size = new Size(86, 30);
        }

        Controls.Add(_current);
        Controls.Add(row3);
        Controls.Add(row2);
        Controls.Add(row1);
        Controls.Add(_info);
    }

    private long _frameTicks = TimeSpan.TicksPerSecond / 24;

    public void SetFps(double fps)
    {
        if (fps > 0) _frameTicks = (long)(TimeSpan.TicksPerSecond / fps);
        Refresh();
    }

    /// <summary>更新显示与帧时长上下文。</summary>
    public void SetOffset(long offset100ns, double fps)
    {
        CurrentOffset = offset100ns;
        if (fps > 0) _frameTicks = (long)(TimeSpan.TicksPerSecond / fps);
        var ms = offset100ns / (double)TimeSpan.TicksPerMillisecond;
        _current.Text = $"偏移: {ms:0.0}ms ({(double)offset100ns / _frameTicks:0.0}帧@{(fps > 0 ? fps.ToString("0.##") : "--")}fps)";
    }

    public void SetPlaceholder(string text)
    {
        _current.Text = text;
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderColor = AppTheme.Colors.Border, BorderSize = 1 },
        BackColor = AppTheme.Colors.ControlBackgroundLight,
        ForeColor = AppTheme.Colors.TextPrimary,
        Font = AppTheme.Fonts.CaptionFont,
        Margin = new Padding(2, 0, 2, 0),
    };
}