namespace _3FCompare.App.Controls;

/// <summary>传输栏：播放/暂停、双步进（帧/秒）、循环、速度、路数控制、时间显示。</summary>
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
    private readonly Label _timeLabel;
    private readonly Label _infoLabel;
    private readonly ComboBox _speedBox;

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

    public TransportBar()
    {
        Height = 44;
        Dock = DockStyle.Bottom;

        _btnPlay = MakeButton("▶ 播放", new Point(8, 6));
        _btnPlay.Click += (_, _) => PlayPauseClicked?.Invoke(this, EventArgs.Empty);

        _btnStop = MakeButton("■ 停止", new Point(86, 6));
        _btnStop.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);

        _btnFramePrev = MakeButton("◀◀ 帧", new Point(164, 6));
        _btnFramePrev.Click += (_, _) => FrameStepClicked?.Invoke(this, -1);

        _btnFrameNext = MakeButton("帧 ▶▶", new Point(242, 6));
        _btnFrameNext.Click += (_, _) => FrameStepClicked?.Invoke(this, +1);

        _btnSecPrev = MakeButton("◀ 秒", new Point(320, 6));
        _btnSecPrev.Click += (_, _) => SecondsStepClicked?.Invoke(this, -1);

        _btnSecNext = MakeButton("秒 ▶", new Point(390, 6));
        _btnSecNext.Click += (_, _) => SecondsStepClicked?.Invoke(this, +1);

        _btnLoop = MakeButton("🔁 循环:关", new Point(460, 6));
        _btnLoop.Click += (_, _) => LoopToggled?.Invoke(this, EventArgs.Empty);

        _speedBox = new ComboBox
        {
            Location = new Point(548, 7),
            Size = new Size(70, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _speedBox.Items.AddRange(new object[] { "0.5x", "1.0x", "2.0x", "4.0x" });
        _speedBox.SelectedIndex = 1;
        _speedBox.SelectedIndexChanged += (_, _) =>
        {
            if (_speedBox.SelectedItem is string s)
                SpeedChanged?.Invoke(this, double.Parse(s.TrimEnd('x')));
        };

        _btnAdd = MakeButton("+ 加路", new Point(628, 6));
        _btnAdd.Click += (_, _) => AddClicked?.Invoke(this, EventArgs.Empty);

        _btnRemove = MakeButton("− 减路", new Point(698, 6));
        _btnRemove.Click += (_, _) => RemoveClicked?.Invoke(this, EventArgs.Empty);

        _timeLabel = new Label
        {
            Location = new Point(770, 10),
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Consolas", 10f),
            Text = "00:00:00.000 / 00:00:00.000",
        };

        _infoLabel = new Label
        {
            Location = new Point(1020, 10),
            AutoSize = true,
            ForeColor = Color.FromArgb(160, 160, 170),
            Font = new Font("Microsoft YaHei UI", 9f),
            Text = string.Empty,
        };

        Controls.AddRange(new Control[]
        {
            _btnPlay, _btnStop, _btnFramePrev, _btnFrameNext, _btnSecPrev, _btnSecNext,
            _btnLoop, _speedBox, _btnAdd, _btnRemove, _timeLabel, _infoLabel,
        });
    }

    private static Button MakeButton(string text, Point location)
        => new()
        {
            Text = text,
            Location = location,
            Size = new Size(70, 30),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = Color.FromArgb(80, 80, 90), BorderSize = 1 },
            BackColor = Color.FromArgb(40, 40, 46),
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9f),
        };

    public void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        _playing = playing;
        _btnPlay.Text = playing ? "⏸ 暂停" : "▶ 播放";
    }

    public void SetLoop(bool enabled)
    {
        if (_loopEnabled == enabled) return;
        _loopEnabled = enabled;
        _btnLoop.Text = enabled ? "🔁 循环:开" : "🔁 循环:关";
        _btnLoop.BackColor = enabled ? Color.FromArgb(60, 90, 60) : Color.FromArgb(40, 40, 46);
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