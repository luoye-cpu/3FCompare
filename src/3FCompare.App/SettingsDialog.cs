using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.App.Utils;

namespace _3FCompare.App;

/// <summary>二级设置窗口（F25）：硬件加速/GPU 选择/步进步长/窗口/解码色彩/布局/FFmpeg。
/// 全部使用深色主题，标签页内用 TableLayoutPanel 避免重叠。</summary>
public sealed class SettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<AdapterInfo> _adapters;
    private bool _changed;

    // 控件字段
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

    private const int CtlMargin = 16;
    private const int RowGap = 32;
    private const int CtlWidth = 280;

    public bool Changed => _changed;
    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        _adapters = GpuEnumeration.Enumerate();
        Result = settings;

        Text = "设置";
        ClientSize = new Size(580, 500);
        MinimumSize = new Size(580, 500);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = AppTheme.Colors.InputBackground;
        ForeColor = AppTheme.Colors.TextPrimary;

        // 高 DPI 自动缩放：以 96 DPI 为基准，控件树与实际窗体尺寸按 DPI 因子放大
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(Dpi.BaseDpi, Dpi.BaseDpi);

        BuildUi();
    }

    /// <summary>创建带深色主题的标签页。</summary>
    private static TabPage MakeTab(string title)
    {
        return new TabPage(title)
        {
            BackColor = AppTheme.Colors.PanelBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
            UseVisualStyleBackColor = false,
        };
    }

    /// <summary>创建深色主题标签。</summary>
    private static Label MakeLabel(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
    }

    /// <summary>创建深色主题按钮。</summary>
    private static Button MakeButton(string text, int x, int y, int w, int h, Color backColor)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = AppTheme.Colors.Border, BorderSize = 1 },
            BackColor = backColor,
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.BodyFont,
        };
    }

    private void BuildUi()
    {
        // 标签页控件
        var tabs = new TabControl
        {
            Location = new Point(12, 12),
            Size = new Size(544, 400),
            BackColor = AppTheme.Colors.InputBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
        };

        // ============ 硬件页 ============
        var hardware = MakeTab("硬件加速");
        _chkHardware = new CheckBox
        {
            Text = "启用硬件解码 (GPU)",
            Location = new Point(CtlMargin, CtlMargin),
            Checked = _settings.HardwareDecode,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        var lblAdapter = MakeLabel("解码 GPU：", CtlMargin, CtlMargin + 36);
        _cmbAdapter = new ComboBox
        {
            Location = new Point(96, CtlMargin + 32),
            Size = new Size(CtlWidth, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        foreach (var a in _adapters)
            _cmbAdapter.Items.Add(a.Index < 0 ? a.Description : $"[{a.Index}] {a.Description}");
        _cmbAdapter.SelectedIndex = Math.Max(0, _adapters
            .Select((a, i) => (a, i))
            .FirstOrDefault(x => x.a.Index == _settings.PreferredAdapterIndex).i);
        hardware.Controls.AddRange(new Control[] { _chkHardware, lblAdapter, _cmbAdapter });

        // ============ 步进页 ============
        var stepping = MakeTab("步进");
        var lblFrame = MakeLabel("按帧步进步长：", CtlMargin, CtlMargin);
        _numFrameStep = new NumericUpDown
        {
            Location = new Point(140, CtlMargin - 4),
            Size = new Size(80, 24),
            Minimum = 1, Maximum = 999,
            Value = Math.Clamp(_settings.FrameStep, 1, 999),
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        var lblSec = MakeLabel("按秒步进步长：", CtlMargin, CtlMargin + RowGap);
        _numSecStep = new NumericUpDown
        {
            Location = new Point(140, CtlMargin + RowGap - 4),
            Size = new Size(80, 24),
            Minimum = 0.1m, Maximum = 600,
            DecimalPlaces = 1, Increment = 0.5m,
            Value = Math.Clamp((decimal)_settings.SecondsStep, 0.1m, 600m),
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        stepping.Controls.AddRange(new Control[] { lblFrame, _numFrameStep, lblSec, _numSecStep });

        // ============ 窗口页 ============
        var window = MakeTab("窗口 / 全屏");
        _chkStartFullscreen = new CheckBox
        {
            Text = "启动时进入全屏模式",
            Location = new Point(CtlMargin, CtlMargin),
            Checked = _settings.StartFullscreen,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        _chkHideChrome = new CheckBox
        {
            Text = "全屏时隐藏时间轴/工具栏",
            Location = new Point(CtlMargin, CtlMargin + RowGap),
            Checked = _settings.HideChromeInFullscreen,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        window.Controls.AddRange(new Control[] { _chkStartFullscreen, _chkHideChrome });

        // ============ 解码/色彩页 ============
        var color = MakeTab("解码 / 色彩");
        var lblColor = MakeLabel("色彩模式：", CtlMargin, CtlMargin);
        _cmbColorMode = new ComboBox
        {
            Location = new Point(100, CtlMargin - 4),
            Size = new Size(220, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        _cmbColorMode.Items.AddRange(new object[] { "SDR 输出", "HDR 输出 (自动检测)" });
        // 修复色彩模式映射逻辑
        _cmbColorMode.SelectedIndex = _settings.ColorMode switch
        {
            ColorModeSetting.MapToSdr => 0,
            ColorModeSetting.RawHdrAsSdr => 0, // RawHdrAsSdr 也映射到 SDR 输出
            ColorModeSetting.MapToHdr => 1,
            _ => 0,
        };
        color.Controls.AddRange(new Control[] { lblColor, _cmbColorMode });

        // ============ 布局页 ============
        var layout = MakeTab("布局");
        var lblCols = MakeLabel("默认网格列数：", CtlMargin, CtlMargin);
        _numGridCols = new NumericUpDown
        {
            Location = new Point(140, CtlMargin - 4),
            Size = new Size(60, 24),
            Minimum = 1, Maximum = 3,
            Value = Math.Clamp(_settings.DefaultGridCols, 1, 3),
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        var lblRows = MakeLabel("默认网格行数：", CtlMargin, CtlMargin + RowGap);
        _numGridRows = new NumericUpDown
        {
            Location = new Point(140, CtlMargin + RowGap - 4),
            Size = new Size(60, 24),
            Minimum = 1, Maximum = 3,
            Value = Math.Clamp(_settings.DefaultGridRows, 1, 3),
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        layout.Controls.AddRange(new Control[] { lblCols, _numGridCols, lblRows, _numGridRows });

        // ============ FFmpeg 路径页 ============
        var ffmpeg = MakeTab("FFmpeg 路径");
        var lblFfmpeg = new Label
        {
            Text = "FFmpeg DLL 目录：",
            Location = new Point(CtlMargin, CtlMargin),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        var hintFfmpeg = new Label
        {
            Text = "手动设置（优先）> 自动检测（应用目录 / PATH）",
            Location = new Point(CtlMargin, CtlMargin + 20),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextMuted,
            BackColor = Color.Transparent,
            Font = AppTheme.Fonts.CaptionFont,
        };

        // 输入框 + 浏览按钮并排
        _txtFfmpegDir = new TextBox
        {
            Text = _settings.FfmpegDirectory ?? string.Empty,
            Location = new Point(CtlMargin, CtlMargin + 48),
            Size = new Size(360, 24),
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
        };
        var btnBrowse = MakeButton("浏览…", 384, CtlMargin + 46, 80, 28, AppTheme.Colors.ControlBackground);
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = "选择包含 FFmpeg DLL 的目录";
            if (!string.IsNullOrWhiteSpace(_txtFfmpegDir.Text) && Directory.Exists(_txtFfmpegDir.Text))
                dlg.SelectedPath = _txtFfmpegDir.Text;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _txtFfmpegDir.Text = dlg.SelectedPath;
                UpdateFfmpegStatus();
            }
        };

        // 测试按钮 + 状态标签同行
        var btnTest = MakeButton("测试探测", CtlMargin, CtlMargin + 88, 100, 28, AppTheme.Colors.ButtonActive);
        _lblFfmpegStatus = new Label
        {
            Location = new Point(128, CtlMargin + 92),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextMuted,
            BackColor = Color.Transparent,
        };
        btnTest.Click += (_, _) =>
        {
            var dir = _txtFfmpegDir.Text.Trim();
            var err = NativeRuntime.ValidateFfmpegDirectory(dir);
            if (err is not null)
            {
                _lblFfmpegStatus.Text = $"✗ {err}";
                _lblFfmpegStatus.ForeColor = AppTheme.Colors.Error;
                return;
            }
            var ok = NativeRuntime.IsNativeAvailableWithDirectory(dir);
            _lblFfmpegStatus.Text = ok
                ? "✓ FFF.Native 加载成功，FFmpeg 可用"
                : "✗ FFF.Native 加载失败";
            _lblFfmpegStatus.ForeColor = ok ? AppTheme.Colors.Success : AppTheme.Colors.Error;
        };
        UpdateFfmpegStatus();
        ffmpeg.Controls.AddRange(new Control[] { lblFfmpeg, hintFfmpeg, _txtFfmpegDir, btnBrowse, btnTest, _lblFfmpegStatus });

        // 组装标签页
        tabs.TabPages.AddRange(new TabPage[] { hardware, stepping, window, color, layout, ffmpeg });
        Controls.Add(tabs);

        // 底部按钮
        var btnOk = new Button
        {
            Text = "确定", Location = new Point(356, 424), Size = new Size(90, 32),
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = AppTheme.Colors.Border, BorderSize = 1 },
            BackColor = AppTheme.Colors.ButtonActive,
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.BodyFont,
        };
        var btnCancel = new Button
        {
            Text = "取消", Location = new Point(460, 424), Size = new Size(90, 32),
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = AppTheme.Colors.Border, BorderSize = 1 },
            BackColor = AppTheme.Colors.ControlBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.BodyFont,
        };
        Controls.AddRange(new Control[] { btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void UpdateFfmpegStatus()
    {
        var dir = _txtFfmpegDir.Text.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            // 留空 = 自动检测：FFMPEG_DIR → PATH → 应用目录
            var detected = NativeRuntime.AutoDetectFfmpegDirectory();
            if (detected is null)
            {
                _lblFfmpegStatus.Text = "留空 = 自动检测（FFMPEG_DIR / PATH / 应用目录），当前未找到 FFmpeg";
                _lblFfmpegStatus.ForeColor = AppTheme.Colors.TextMuted;
            }
            else
            {
                _lblFfmpegStatus.Text = $"✓ 自动检测到：{detected}";
                _lblFfmpegStatus.ForeColor = AppTheme.Colors.Success;
            }
            return;
        }
        var err = NativeRuntime.ValidateFfmpegDirectory(dir);
        _lblFfmpegStatus.Text = err is null ? "✓ 目录有效" : $"✗ {err}";
        _lblFfmpegStatus.ForeColor = err is null ? AppTheme.Colors.Success : AppTheme.Colors.Error;
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
                _cmbColorMode.SelectedIndex != (_settings.ColorMode == ColorModeSetting.MapToHdr ? 1 : 0) ||
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
                    ColorMode = _cmbColorMode.SelectedIndex == 1 ? ColorModeSetting.MapToHdr : ColorModeSetting.MapToSdr,
                    DefaultGridCols = (int)_numGridCols.Value,
                    DefaultGridRows = (int)_numGridRows.Value,
                    FfmpegDirectory = ffmpegDir,
                    WindowX = _settings.WindowX,
                    WindowY = _settings.WindowY,
                    WindowWidth = _settings.WindowWidth,
                    WindowHeight = _settings.WindowHeight,
                    WindowMaximized = _settings.WindowMaximized,
                };
            }
        }
    }
}