using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>传输栏：播放/暂停、双步进（帧/秒）、循环、速度、路数控制、HDR/SDR 切换、时间显示。</summary>
public sealed class TransportBar : Control
{
    /// <summary>集中管理的按钮提示文本（走 LanguageManager 资源，切换语言时刷新）。</summary>
    private static string Play => LanguageManager.T("Tb_Play");
    private static string Pause => LanguageManager.T("Tb_Pause");
    private static string Stop => LanguageManager.T("Tb_Stop");
    private static string FramePrev => LanguageManager.T("Tb_FramePrev");
    private static string FrameNext => LanguageManager.T("Tb_FrameNext");
    private static string SecPrev => LanguageManager.T("Tb_SecPrev");
    private static string SecNext => LanguageManager.T("Tb_SecNext");
    private static string LoopOn => LanguageManager.T("Tb_LoopOn");
    private static string LoopOff => LanguageManager.T("Tb_LoopOff");
    private static string Speed => LanguageManager.T("Tb_Speed");
    private static string Add => LanguageManager.T("Tb_Add");
    private static string Remove => LanguageManager.T("Tb_Remove");
    private static string ColorMode => LanguageManager.T("Tb_ColorMode");

    // 按钮字段（ApplyLanguage 需重新设 tooltip）
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

        // 流式布局：按钮/下拉/标签按顺序自动排布（WrapContents=false），
        // 高 DPI 下按钮被缩放后不会互相挤压溢出，超宽时 AutoScroll 兜底。
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(6, 6, 6, 0),
            BackColor = AppTheme.Colors.PanelBackground,
        };

        _btnPlay = MakeButton("▶", 40, Play);
        _btnPlay.Click += (_, _) => PlayPauseClicked?.Invoke(this, EventArgs.Empty);
        flow.Controls.Add(_btnPlay);

        _btnStop = MakeButton("■", 32, Stop);
        _btnStop.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);
        flow.Controls.Add(_btnStop);

        _btnFramePrev = MakeButton("◀◀", 36, FramePrev);
        _btnFramePrev.Click += (_, _) => FrameStepClicked?.Invoke(this, -1);
        flow.Controls.Add(_btnFramePrev);

        _btnFrameNext = MakeButton("▶▶", 36, FrameNext);
        _btnFrameNext.Click += (_, _) => FrameStepClicked?.Invoke(this, +1);
        flow.Controls.Add(_btnFrameNext);

        _btnSecPrev = MakeButton("◀", 28, SecPrev);
        _btnSecPrev.Click += (_, _) => SecondsStepClicked?.Invoke(this, -1);
        flow.Controls.Add(_btnSecPrev);

        _btnSecNext = MakeButton("▶", 28, SecNext);
        _btnSecNext.Click += (_, _) => SecondsStepClicked?.Invoke(this, +1);
        flow.Controls.Add(_btnSecNext);

        _btnLoop = MakeButton("🔁", 36, LoopOff);
        _btnLoop.Click += (_, _) => LoopToggled?.Invoke(this, EventArgs.Empty);
        flow.Controls.Add(_btnLoop);

        _speedBox = new ComboBox
        {
            Size = new Size(60, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _speedBox.Margin = new Padding(4, 2, 4, 2);
        _speedBox.Items.AddRange(new object[] { "0.5x", "1.0x", "2.0x", "4.0x" });
        _speedBox.SelectedIndex = 1;
        _toolTip.SetToolTip(_speedBox, Speed);
        _speedBox.SelectedIndexChanged += (_, _) =>
        {
            if (_speedBox.SelectedItem is string s)
                SpeedChanged?.Invoke(this, double.Parse(s.TrimEnd('x')));
        };
        flow.Controls.Add(_speedBox);

        _btnAdd = MakeButton("+", 28, Add);
        _btnAdd.Click += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);
        flow.Controls.Add(_btnAdd);

        _btnRemove = MakeButton("−", 28, Remove);
        _btnRemove.Click += (_, _) => RemoveClicked?.Invoke(this, EventArgs.Empty);
        flow.Controls.Add(_btnRemove);

        // HDR/SDR 色彩模式切换（紧凑下拉）
        _cmbColorMode = new ComboBox
        {
            Size = new Size(72, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbColorMode.Margin = new Padding(4, 2, 4, 2);
        _cmbColorMode.Items.AddRange(new object[] { "SDR", "HDR" });
        _cmbColorMode.SelectedIndex = 1;
        _toolTip.SetToolTip(_cmbColorMode, ColorMode);
        _cmbColorMode.SelectedIndexChanged += (_, _) =>
            ColorModeChanged?.Invoke(this, _cmbColorMode.SelectedIndex);
        flow.Controls.Add(_cmbColorMode);

        _timeLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(12, 6, 4, 0),
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.MonospaceMediumFont,
            Text = "00:00:00:00 / 00:00:00",
        };
        flow.Controls.Add(_timeLabel);

        _infoLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(12, 6, 4, 0),
            ForeColor = AppTheme.Colors.TextMuted,
            Font = AppTheme.Fonts.BodyFont,
            Text = string.Empty,
        };
        flow.Controls.Add(_infoLabel);

        Controls.Add(flow);
    }

    public void SetColorMode(int index) => _cmbColorMode.SelectedIndex = index;

    public int CurrentColorMode => _cmbColorMode.SelectedIndex;

    private Button MakeButton(string text, int width, string tooltip)
    {
        var btn = new Button
        {
            Text = text,
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
        _toolTip.SetToolTip(_btnPlay, playing ? Pause : Play);
    }

    public void SetLoop(bool enabled)
    {
        if (_loopEnabled == enabled) return;
        _loopEnabled = enabled;
        _btnLoop.BackColor = enabled ? AppTheme.Colors.ButtonActive : AppTheme.Colors.ControlBackground;
        _toolTip.SetToolTip(_btnLoop, enabled ? LoopOn : LoopOff);
    }

    /// <summary>更新时间码显示。PR 风格：HH:MM:SS:FF（小时:分:秒:当前秒内帧号）。</summary>
    /// <param name="position">当前播放位置。</param>
    /// <param name="duration">总时长。</param>
    /// <param name="frameInSecond">当前秒内的帧号（1 起，如 24fps 下为 1..24）。</param>
    public void SetTime(TimeSpan position, TimeSpan duration, int frameInSecond)
    {
        var p = $"{position:hh\\:mm\\:ss}:{frameInSecond:00}";
        var d = $"{duration:hh\\:mm\\:ss}";
        _timeLabel.Text = $"{p} / {d}";
    }

    public void SetInfo(string info) => _infoLabel.Text = info;

    /// <summary>语言切换后刷新所有按钮 Tooltip 文本。</summary>
    public void ApplyLanguage()
    {
        _toolTip.SetToolTip(_btnPlay, _playing ? Pause : Play);
        _toolTip.SetToolTip(_btnStop, Stop);
        _toolTip.SetToolTip(_btnFramePrev, FramePrev);
        _toolTip.SetToolTip(_btnFrameNext, FrameNext);
        _toolTip.SetToolTip(_btnSecPrev, SecPrev);
        _toolTip.SetToolTip(_btnSecNext, SecNext);
        _toolTip.SetToolTip(_btnLoop, _loopEnabled ? LoopOn : LoopOff);
        _toolTip.SetToolTip(_speedBox, Speed);
        _toolTip.SetToolTip(_btnAdd, Add);
        _toolTip.SetToolTip(_btnRemove, Remove);
        _toolTip.SetToolTip(_cmbColorMode, ColorMode);
    }

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