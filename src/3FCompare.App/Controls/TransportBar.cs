using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>传输栏：播放/暂停、双步进（帧/秒）、循环、速度、路数控制、HDR/SDR 切换、时间显示。</summary>
public sealed class TransportBar : Control
{
    private readonly Button _btnPlay;
    private readonly Button _btnStop;
    private readonly Button _btnFramePrev;
    private readonly Button _btnFrameNext;
    private readonly Button _btnSecPrev;
    private readonly Button _btnSecNext;
    private readonly Button _btnLoop;
    private readonly Button _btnAdd;
    private readonly Button _btnRemove;
    private readonly ComboBox _cmbColorMode;
    private readonly Label _timeLabel;
    private readonly Label _infoLabel;
    private readonly ComboBox _speedBox;
    private readonly ToolTip _toolTip;

    private bool _playing;
    private bool _loopEnabled;

    /// <summary>集中管理的按钮提示文本（避免散落的 magic string）。</summary>
    private static class Tooltips
    {
        public const string Play = "播放 (Space)";
        public const string Pause = "暂停 (Space)";
        public const string Stop = "停止";
        public const string FramePrev = "后退一帧 (←)";
        public const string FrameNext = "前进一帧 (→)";
        public const string SecPrev = "后退一秒 (Shift+←)";
        public const string SecNext = "前进一秒 (Shift+→)";
        public const string LoopOn = "循环: 开";
        public const string LoopOff = "循环: 关";
        public const string Speed = "播放速度";
        public const string Add = "加路";
        public const string Remove = "减路";
        public const string ColorMode = "SDR: 标准动态范围输出 | HDR: 高动态范围输出（自动检测显示器能力）";
    }

    public event EventHandler? PlayPauseClicked;
    public event EventHandler? StopClicked;
    public event EventHandler<int>? FrameStepClicked;
    public event EventHandler<double>? SecondsStepClicked;
    public event EventHandler? LoopToggled;
    public event EventHandler? AddClicked;
    public event EventHandler? RemoveClicked;
    public event EventHandler<double>? SpeedChanged;
    /// <summary>色彩模式切换（0=MapToSdr, 1=RawHdrAsSdr, 2=MapToHdr）。</summary>
    public event EventHandler<int>? ColorModeChanged;

    public TransportBar()
    {
        Height = 44;
        Dock = DockStyle.Bottom;
        _toolTip = new ToolTip { AutoPopDelay = 5000, InitialDelay = 300, ReshowDelay = 100 };

        _btnPlay = MakeButton("▶", new Point(8, 6), 40, Tooltips.Play);
        _btnPlay.Click += (_, _) => PlayPauseClicked?.Invoke(this, EventArgs.Empty);

        _btnStop = MakeButton("■", new Point(52, 6), 32, Tooltips.Stop);
        _btnStop.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);

        _btnFramePrev = MakeButton("◀◀", new Point(96, 6), 36, Tooltips.FramePrev);
        _btnFramePrev.Click += (_, _) => FrameStepClicked?.Invoke(this, -1);

        _btnFrameNext = MakeButton("▶▶", new Point(136, 6), 36, Tooltips.FrameNext);
        _btnFrameNext.Click += (_, _) => FrameStepClicked?.Invoke(this, +1);

        _btnSecPrev = MakeButton("◀", new Point(184, 6), 28, Tooltips.SecPrev);
        _btnSecPrev.Click += (_, _) => SecondsStepClicked?.Invoke(this, -1);

        _btnSecNext = MakeButton("▶", new Point(216, 6), 28, Tooltips.SecNext);
        _btnSecNext.Click += (_, _) => SecondsStepClicked?.Invoke(this, +1);

        _btnLoop = MakeButton("🔁", new Point(256, 6), 36, Tooltips.LoopOff);
        _btnLoop.Click += (_, _) => LoopToggled?.Invoke(this, EventArgs.Empty);

        _speedBox = new ComboBox
        {
            Location = new Point(300, 7),
            Size = new Size(60, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _speedBox.Items.AddRange(new object[] { "0.5x", "1.0x", "2.0x", "4.0x" });
        _speedBox.SelectedIndex = 1;
        _toolTip.SetToolTip(_speedBox, Tooltips.Speed);
        _speedBox.SelectedIndexChanged += (_, _) =>
        {
            if (_speedBox.SelectedItem is string s)
                SpeedChanged?.Invoke(this, double.Parse(s.TrimEnd('x')));
        };

        _btnAdd = MakeButton("+", new Point(368, 6), 28, Tooltips.Add);
        _btnAdd.Click += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);

        _btnRemove = MakeButton("−", new Point(400, 6), 28, Tooltips.Remove);
        _btnRemove.Click += (_, _) => RemoveClicked?.Invoke(this, EventArgs.Empty);

        // HDR/SDR 色彩模式切换（紧凑下拉）
        _cmbColorMode = new ComboBox
        {
            Location = new Point(436, 7),
            Size = new Size(72, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbColorMode.Items.AddRange(new object[] { "SDR", "HDR" });
        _cmbColorMode.SelectedIndex = 1;
        _toolTip.SetToolTip(_cmbColorMode, Tooltips.ColorMode);
        _cmbColorMode.SelectedIndexChanged += (_, _) =>
            ColorModeChanged?.Invoke(this, _cmbColorMode.SelectedIndex);

        _timeLabel = new Label
        {
            Location = new Point(520, 10),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.MonospaceMediumFont,
            Text = "00:00:00.000 / 00:00:00.000",
        };

        _infoLabel = new Label
        {
            Location = new Point(780, 10),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextMuted,
            Font = AppTheme.Fonts.BodyFont,
            Text = string.Empty,
        };

        Controls.AddRange(new Control[]
        {
            _btnPlay, _btnStop, _btnFramePrev, _btnFrameNext, _btnSecPrev, _btnSecNext,
            _btnLoop, _speedBox, _btnAdd, _btnRemove, _cmbColorMode, _timeLabel, _infoLabel,
        });
    }

    public void SetColorMode(int index) => _cmbColorMode.SelectedIndex = index;

    public int CurrentColorMode => _cmbColorMode.SelectedIndex;

    private Button MakeButton(string text, Point location, int width, string tooltip)
    {
        var btn = new Button
        {
            Text = text,
            Location = location,
            Size = new Size(width, 30),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = AppTheme.Colors.Border, BorderSize = 1 },
            BackColor = AppTheme.Colors.ControlBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.BodyFont,
        };
        _toolTip.SetToolTip(btn, tooltip);
        return btn;
    }

    public void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        _playing = playing;
        _btnPlay.Text = playing ? "⏸" : "▶";
        _toolTip.SetToolTip(_btnPlay, playing ? Tooltips.Pause : Tooltips.Play);
    }

    public void SetLoop(bool enabled)
    {
        if (_loopEnabled == enabled) return;
        _loopEnabled = enabled;
        _btnLoop.BackColor = enabled ? AppTheme.Colors.ButtonActive : AppTheme.Colors.ControlBackground;
        _toolTip.SetToolTip(_btnLoop, enabled ? Tooltips.LoopOn : Tooltips.LoopOff);
    }

    public void SetTime(TimeSpan position, TimeSpan duration)
    {
        _timeLabel.Text = $"{position:hh\\:mm\\:ss\\.fff} / {duration:hh\\:mm\\:ss\\.fff}";
    }

    public void SetInfo(string info) => _infoLabel.Text = info;

    public double CurrentSpeed
    {
        get
        {
            return _speedBox.SelectedItem is string s && s.EndsWith("x")
                ? double.Parse(s.TrimEnd('x'))
                : 1.0;
        }
    }
}