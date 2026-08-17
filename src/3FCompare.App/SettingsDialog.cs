using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;

namespace _3FCompare.App;

/// <summary>二级设置窗口（F25）：硬件加速/GPU 选择/步进步长/窗口/解码色彩/布局。
/// 返回是否点击「确定」且用户修改。</summary>
public sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<AdapterInfo> _adapters;
    private bool _changed;

    // 控件（BuildUi 中初始化；! 抑制 CS8618 因非构造函数内赋值）
    private CheckBox _chkHardware = null!;
    private ComboBox _cmbAdapter = null!;
    private NumericUpDown _numFrameStep = null!;
    private NumericUpDown _numSecStep = null!;
    private CheckBox _chkStartFullscreen = null!;
    private CheckBox _chkHideChrome = null!;
    private ComboBox _cmbColorMode = null!;
    private NumericUpDown _numGridCols = null!;
    private NumericUpDown _numGridRows = null!;
    private TextBox _txtFfmpegDir = null!;
    private Label _lblFfmpegStatus = null!;

    public bool Changed => _changed;

    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        _adapters = GpuEnumeration.Enumerate();
        Result = settings;

        Text = "设置";
        ClientSize = new Size(520, 460);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(36, 36, 42);
        ForeColor = Color.White;

        BuildUi();
    }

    private void BuildUi()
    {
        var tabs = new TabControl
        {
            Location = new Point(12, 12),
            Size = new Size(496, 380),
        };

        // ---- 硬件页 ----
        var hardware = new TabPage("硬件加速");
        _chkHardware = new CheckBox
        {
            Text = "启用硬件解码 (GPU)",
            Location = new Point(16, 16),
            Checked = _settings.HardwareDecode,
            AutoSize = true,
        };
        var lblAdapter = new Label { Text = "解码 GPU：", Location = new Point(16, 52), AutoSize = true };
        _cmbAdapter = new ComboBox
        {
            Location = new Point(96, 48),
            Size = new Size(330, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        foreach (var a in _adapters)
        {
            _cmbAdapter.Items.Add(a.Index < 0 ? a.Description : $"[{a.Index}] {a.Description}");
        }
        _cmbAdapter.SelectedIndex = Math.Max(0, _adapters
            .Select((a, i) => (a, i))
            .FirstOrDefault(x => x.a.Index == _settings.PreferredAdapterIndex)
            .i);
        hardware.Controls.AddRange(new Control[] { _chkHardware, lblAdapter, _cmbAdapter });

        // ---- 步进页 ----
        var stepping = new TabPage("步进");
        var lblFrame = new Label { Text = "按帧步进步长：", Location = new Point(16, 20), AutoSize = true };
        _numFrameStep = new NumericUpDown
        {
            Location = new Point(140, 16),
            Size = new Size(80, 24),
            Minimum = 1,
            Maximum = 999,
            Value = Math.Clamp(_settings.FrameStep, 1, 999),
        };
        var lblSec = new Label { Text = "按秒步进步长：", Location = new Point(16, 56), AutoSize = true };
        _numSecStep = new NumericUpDown
        {
            Location = new Point(140, 52),
            Size = new Size(80, 24),
            Minimum = 0.1m,
            Maximum = 600,
            DecimalPlaces = 1,
            Increment = 0.5m,
            Value = Math.Clamp((decimal)_settings.SecondsStep, 0.1m, 600m),
        };
        stepping.Controls.AddRange(new Control[] { lblFrame, _numFrameStep, lblSec, _numSecStep });

        // ---- 窗口页 ----
        var window = new TabPage("窗口 / 全屏");
        _chkStartFullscreen = new CheckBox
        {
            Text = "启动时进入全屏模式",
            Location = new Point(16, 20),
            Checked = _settings.StartFullscreen,
            AutoSize = true,
        };
        _chkHideChrome = new CheckBox
        {
            Text = "全屏时隐藏时间轴/工具栏",
            Location = new Point(16, 52),
            Checked = _settings.HideChromeInFullscreen,
            AutoSize = true,
        };
        window.Controls.AddRange(new Control[] { _chkStartFullscreen, _chkHideChrome });

        // ---- 解码/色彩页 ----
        var color = new TabPage("解码 / 色彩");
        var lblColor = new Label { Text = "色彩模式：", Location = new Point(16, 20), AutoSize = true };
        _cmbColorMode = new ComboBox
        {
            Location = new Point(100, 16),
            Size = new Size(220, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _cmbColorMode.Items.AddRange(new object[] { "映射到 SDR", "原始 HDR 按 SDR", "峰值映射 HDR" });
        _cmbColorMode.SelectedIndex = Math.Clamp((int)_settings.ColorMode, 0, 2);
        color.Controls.AddRange(new Control[] { lblColor, _cmbColorMode });

        // ---- 布局页 ----
        var layout = new TabPage("布局");
        var lblCols = new Label { Text = "默认网格列数：", Location = new Point(16, 20), AutoSize = true };
        _numGridCols = new NumericUpDown
        {
            Location = new Point(140, 16), Size = new Size(60, 24),
            Minimum = 1, Maximum = 3,
            Value = Math.Clamp(_settings.DefaultGridCols, 1, 3),
        };
        var lblRows = new Label { Text = "默认网格行数：", Location = new Point(16, 56), AutoSize = true };
        _numGridRows = new NumericUpDown
        {
            Location = new Point(140, 52), Size = new Size(60, 24),
            Minimum = 1, Maximum = 3,
            Value = Math.Clamp(_settings.DefaultGridRows, 1, 3),
        };
        layout.Controls.AddRange(new Control[] { lblCols, _numGridCols, lblRows, _numGridRows });

        // ---- FFmpeg 路径页 ----
        var ffmpeg = new TabPage("FFmpeg 路径");
        var lblFfmpeg = new Label
        {
            Text = "FFmpeg DLL 目录（手动设置 > 自动检测）：",
            Location = new Point(16, 16),
            AutoSize = true,
        };
        _txtFfmpegDir = new TextBox
        {
            Text = _settings.FfmpegDirectory ?? string.Empty,
            Location = new Point(16, 40),
            Size = new Size(360, 24),
            BackColor = Color.FromArgb(50, 50, 56),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var btnBrowse = new Button
        {
            Text = "浏览…",
            Location = new Point(384, 38),
            Size = new Size(80, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 66),
            ForeColor = Color.White,
        };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = "选择包含 FFmpeg DLL（avcodec-*.dll, avformat-*.dll 等）的目录";
            if (!string.IsNullOrWhiteSpace(_txtFfmpegDir.Text) && Directory.Exists(_txtFfmpegDir.Text))
                dlg.SelectedPath = _txtFfmpegDir.Text;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _txtFfmpegDir.Text = dlg.SelectedPath;
                UpdateFfmpegStatus();
            }
        };
        _lblFfmpegStatus = new Label
        {
            Location = new Point(16, 72),
            AutoSize = true,
        };
        var btnTest = new Button
        {
            Text = "测试探测",
            Location = new Point(16, 100),
            Size = new Size(100, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(50, 70, 50),
            ForeColor = Color.White,
        };
        btnTest.Click += (_, _) =>
        {
            var dir = _txtFfmpegDir.Text.Trim();
            var err = NativeRuntime.ValidateFfmpegDirectory(dir);
            if (err is not null)
            {
                _lblFfmpegStatus.Text = $"✗ {err}";
                _lblFfmpegStatus.ForeColor = Color.FromArgb(255, 140, 140);
                return;
            }
            var ok = NativeRuntime.IsNativeAvailableWithDirectory(dir);
            _lblFfmpegStatus.Text = ok
                ? "✓ FFF.Native 加载成功，FFmpeg 可用"
                : "✗ FFF.Native 加载失败（检查目录是否包含 FFF.Native.dll 及所有 FFmpeg DLL）";
            _lblFfmpegStatus.ForeColor = ok ? Color.FromArgb(140, 255, 140) : Color.FromArgb(255, 140, 140);
        };
        // 初始状态
        UpdateFfmpegStatus();
        ffmpeg.Controls.AddRange(new Control[] { lblFfmpeg, _txtFfmpegDir, btnBrowse, _lblFfmpegStatus, btnTest });

        tabs.TabPages.AddRange(new TabPage[] { hardware, stepping, window, color, layout, ffmpeg });
        Controls.Add(tabs);

        var btnOk = new Button { Text = "确定", Location = new Point(300, 404), Size = new Size(90, 32), DialogResult = DialogResult.OK };
        var btnCancel = new Button { Text = "取消", Location = new Point(404, 404), Size = new Size(90, 32), DialogResult = DialogResult.Cancel };
        btnOk.FlatStyle = btnCancel.FlatStyle = FlatStyle.Flat;
        btnOk.BackColor = Color.FromArgb(60, 90, 60);
        btnCancel.BackColor = Color.FromArgb(60, 60, 66);
        btnOk.ForeColor = btnCancel.ForeColor = Color.White;
        Controls.AddRange(new Control[] { btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void UpdateFfmpegStatus()
    {
        var dir = _txtFfmpegDir.Text.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            _lblFfmpegStatus.Text = "留空 = 自动检测（应用目录 / PATH）";
            _lblFfmpegStatus.ForeColor = Color.FromArgb(160, 160, 170);
            return;
        }
        var err = NativeRuntime.ValidateFfmpegDirectory(dir);
        _lblFfmpegStatus.Text = err is null ? "✓ 目录有效" : $"✗ {err}";
        _lblFfmpegStatus.ForeColor = err is null ? Color.FromArgb(140, 255, 140) : Color.FromArgb(255, 140, 140);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (DialogResult == DialogResult.OK)
        {
            var ffmpegDir = _txtFfmpegDir.Text.Trim();
            if (string.IsNullOrEmpty(ffmpegDir)) ffmpegDir = null!;
            var changed =
                _chkHardware.Checked != _settings.HardwareDecode ||
                _cmbAdapter.SelectedIndex != _settings.PreferredAdapterIndex ||
                (int)_numFrameStep.Value != _settings.FrameStep ||
                (double)_numSecStep.Value != _settings.SecondsStep ||
                _chkStartFullscreen.Checked != _settings.StartFullscreen ||
                _chkHideChrome.Checked != _settings.HideChromeInFullscreen ||
                _cmbColorMode.SelectedIndex != (int)_settings.ColorMode ||
                (int)_numGridCols.Value != _settings.DefaultGridCols ||
                (int)_numGridRows.Value != _settings.DefaultGridRows ||
                ffmpegDir != (_settings.FfmpegDirectory ?? string.Empty);

            if (changed)
            {
                _changed = true;
                Result = new AppSettings
                {
                    HardwareDecode = _chkHardware.Checked,
                    PreferredAdapterIndex = _adapters.ElementAtOrDefault(_cmbAdapter.SelectedIndex)?.Index ?? -1,
                    FrameStep = (int)_numFrameStep.Value,
                    SecondsStep = (double)_numSecStep.Value,
                    StartFullscreen = _chkStartFullscreen.Checked,
                    HideChromeInFullscreen = _chkHideChrome.Checked,
                    ColorMode = (ColorModeSetting)Math.Clamp(_cmbColorMode.SelectedIndex, 0, 2),
                    DefaultGridCols = (int)_numGridCols.Value,
                    DefaultGridRows = (int)_numGridRows.Value,
                    FfmpegDirectory = string.IsNullOrEmpty(ffmpegDir) ? null : ffmpegDir,
                };
            }
        }
    }
}