using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.App.Utils;

namespace _3FCompare.App;

/// <summary>二级设置窗口（F25）：硬件加速/GPU 选择/步进步长/窗口/解码色彩/布局/FFmpeg。
/// 全部使用深色主题，标签页内用 TableLayoutPanel 自适应布局，避免高 DPI（150%/200%）下
/// 隐式 AutoScale 对 TabControl/绝对坐标的部分缩放导致的挤压、重叠与越界。</summary>
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
    private ComboBox _cmbLanguage = null!;

    private const int MarginPx = 12;
    private const int SectionSpacing = 24;

    public bool Changed => _changed;
    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        _adapters = GpuEnumeration.Enumerate();
        Result = settings;
        // 同步当前语言到管理器
        LanguageManager.SetLanguage(_settings.Language);

        Text = LanguageManager.T("Settings_DialogTitle");
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(640, 560);
        BackColor = AppTheme.Colors.InputBackground;
        ForeColor = AppTheme.Colors.TextPrimary;

        // 高 DPI：以 96 DPI 为基准，控件树与实际窗体尺寸按 DPI 因子统一缩放。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(Dpi.BaseDpi, Dpi.BaseDpi);
        ClientSize = new Size(720, 760);

        BuildUi();
    }

    /// <summary>创建深色主题标签。</summary>
    private static Label MakeLabel(string text)
        => new()
        {
            Text = text,
            AutoSize = true,
            AutoEllipsis = true,
            Margin = new Padding(0, 6, 8, 0),
            Anchor = AnchorStyles.Left,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };

    /// <summary>创建深色主题按钮。</summary>
    private static Button MakeButton(string text, Color backColor)
        => new()
        {
            Text = text,
            AutoSize = false,
            Width = 90,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = AppTheme.Colors.Border, BorderSize = 1 },
            BackColor = backColor,
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.BodyFont,
        };
    /// <summary>在页容器中新增一行两列条目（标签在左，控件在右）。</summary>
    private static void AddRow(TableLayoutPanel grid, Control label, Control control)
    {
        grid.ColumnStyles.Clear();
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        var row = grid.RowCount;
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(control, 1, row);
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(0, 2, 0, 2);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 2, 0, 2);
    }

    /// <summary>新增一行跨两列的控件（如全宽 CheckBox / 说明文字）。</summary>
    private static void AddFullRow(TableLayoutPanel grid, Control control)
    {
        grid.ColumnStyles.Clear();
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        var row = grid.RowCount;
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 2);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 2, 0, 2);
    }

    /// <summary>创建深色主题 ComboBox（占满整行剩余宽度）。</summary>
    private static ComboBox MakeCombo(int width = 240)
        => new()
        {
            Width = width,
            Height = 24,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
            Margin = new Padding(0, 2, 0, 2),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };

    private void BuildUi()
    {
        // ============ 可滚动内容面板 ============
        // 所有设置项放在一个垂直堆叠的面板里，外层 Panel+AutoScroll 提供滚动条。
        var scrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = AppTheme.Colors.InputBackground,
            Padding = new Padding(16, 16, 16, 16),
        };

        // 内容容器：垂直堆叠，AutoSize 让它根据内容增长，从而触发外层滚动
        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(0),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };

        // ---------- 语言 ----------
        var langSection = MakeSection("语言 / Language");
        var langGrid = MakeSectionGrid();
        _cmbLanguage = MakeCombo(240);
        _cmbLanguage.Items.AddRange(new object[] { "中文", "English" });
        _cmbLanguage.SelectedIndex = Math.Clamp(_settings.Language, 0, 1);
        AddRow(langGrid, MakeLabel("界面语言 / Language:"), _cmbLanguage);
        langSection.Controls.Add(langGrid);
        content.Controls.Add(langSection);
        content.SetFlowBreak(langSection, true);

        // ---------- 硬件加速 ----------
        var hwSection = MakeSection(LanguageManager.IsEnglish ? "Hardware Acceleration" : "硬件加速");
        var hwGrid = MakeSectionGrid();
        _chkHardware = new CheckBox
        {
            Text = LanguageManager.T("Hardware_EnableHardwareDecode"),
            Checked = _settings.HardwareDecode,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        AddFullRow(hwGrid, _chkHardware);
        _cmbAdapter = MakeCombo();
        foreach (var a in _adapters)
            _cmbAdapter.Items.Add(a.Index < 0 ? a.Description : $"[{a.Index}] {a.Description}");
        _cmbAdapter.SelectedIndex = Math.Max(0, _adapters
            .Select((a, i) => (a, i))
            .FirstOrDefault(x => x.a.Index == _settings.PreferredAdapterIndex).i);
        AddRow(hwGrid, MakeLabel(LanguageManager.T("Hardware_DecodeGPU")), _cmbAdapter);
        hwSection.Controls.Add(hwGrid);
        content.Controls.Add(hwSection);
        content.SetFlowBreak(hwSection, true);

        // ---------- 步进 ----------
        var stepSection = MakeSection(LanguageManager.IsEnglish ? "Stepping" : "步进");
        var stepGrid = MakeSectionGrid();
        _numFrameStep = MakeNumeric(1, 999, Math.Clamp(_settings.FrameStep, 1, 999), 0, 80);
        AddRow(stepGrid, MakeLabel(LanguageManager.T("Stepping_StepByFrame")), _numFrameStep);
        _numSecStep = MakeNumeric(1, 1200, (decimal)Math.Clamp(_settings.SecondsStep, 0.1, 600), 0, 80);
        _numSecStep.DecimalPlaces = 1;
        _numSecStep.Increment = 0.5m;
        AddRow(stepGrid, MakeLabel(LanguageManager.T("Stepping_StepBySecond")), _numSecStep);
        stepSection.Controls.Add(stepGrid);
        content.Controls.Add(stepSection);
        content.SetFlowBreak(stepSection, true);

        // ---------- 窗口 / 全屏 ----------
        var winSection = MakeSection(LanguageManager.IsEnglish ? "Window / Fullscreen" : "窗口 / 全屏");
        var winGrid = MakeSectionGrid();
        _chkStartFullscreen = new CheckBox
        {
            Text = LanguageManager.T("Window_StartFullscreen"),
            Checked = _settings.StartFullscreen,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        AddFullRow(winGrid, _chkStartFullscreen);
        _chkHideChrome = new CheckBox
        {
            Text = LanguageManager.T("Window_HideChrome"),
            Checked = _settings.HideChromeInFullscreen,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        AddFullRow(winGrid, _chkHideChrome);
        winSection.Controls.Add(winGrid);
        content.Controls.Add(winSection);
        content.SetFlowBreak(winSection, true);

        // ---------- 解码 / 色彩 ----------
        var colorSection = MakeSection(LanguageManager.IsEnglish ? "Decode / Color" : "解码 / 色彩");
        var colorGrid = MakeSectionGrid();
        _cmbColorMode = MakeCombo(240);
        _cmbColorMode.Items.AddRange(new object[] { LanguageManager.T("Color_SDR"), LanguageManager.T("Color_HDRAuto") });
        _cmbColorMode.SelectedIndex = _settings.ColorMode switch
        {
            ColorModeSetting.MapToHdr => 1,
            _ => 0,
        };
        AddRow(colorGrid, MakeLabel(LanguageManager.T("Color_ColorMode")), _cmbColorMode);
        colorSection.Controls.Add(colorGrid);
        content.Controls.Add(colorSection);
        content.SetFlowBreak(colorSection, true);

        // ---------- 布局 ----------
        var layoutSection = MakeSection(LanguageManager.IsEnglish ? "Layout" : "布局");
        var layoutGrid = MakeSectionGrid();
        _numGridCols = MakeNumeric(1, 3, Math.Clamp(_settings.DefaultGridCols, 1, 3), 0, 70);
        AddRow(layoutGrid, MakeLabel(LanguageManager.T("Layout_DefaultCols")), _numGridCols);
        _numGridRows = MakeNumeric(1, 3, Math.Clamp(_settings.DefaultGridRows, 1, 3), 0, 70);
        AddRow(layoutGrid, MakeLabel(LanguageManager.T("Layout_DefaultRows")), _numGridRows);
        layoutSection.Controls.Add(layoutGrid);
        content.Controls.Add(layoutSection);
        content.SetFlowBreak(layoutSection, true);

        // ---------- FFmpeg 路径 ----------
        var ffSection = MakeSection(LanguageManager.IsEnglish ? "FFmpeg Path" : "FFmpeg 路径");
        var ffGrid = MakeSectionGrid();

        var lblFfmpeg = MakeLabel(LanguageManager.T("FFmpeg_Path"));
        _txtFfmpegDir = new TextBox
        {
            Text = _settings.FfmpegDirectory ?? string.Empty,
            Width = 200,
            Height = 24,
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 4, 2),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        var btnBrowse = MakeButton(LanguageManager.T("FFmpeg_Browse"), AppTheme.Colors.ControlBackground);
        btnBrowse.Width = 80;
        btnBrowse.Margin = new Padding(0, 0, 0, 0);
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog();
            dlg.Description = LanguageManager.T("Msg_FolderTitle");
            if (!string.IsNullOrWhiteSpace(_txtFfmpegDir.Text) && Directory.Exists(_txtFfmpegDir.Text))
                dlg.SelectedPath = _txtFfmpegDir.Text;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _txtFfmpegDir.Text = dlg.SelectedPath;
                UpdateFfmpegStatus();
            }
        };

        // 目录行：文本框 + 浏览按钮并排
        var pathRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 2,
            RowCount = 1,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100f), new ColumnStyle(SizeType.AutoSize) },
            RowStyles = { new RowStyle(SizeType.AutoSize) },
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 0),
        };
        pathRow.Controls.Add(_txtFfmpegDir, 0, 0);
        pathRow.Controls.Add(btnBrowse, 1, 0);
        _txtFfmpegDir.Dock = DockStyle.Fill;
        _txtFfmpegDir.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        btnBrowse.Dock = DockStyle.Fill;
        btnBrowse.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        AddRow(ffGrid, lblFfmpeg, pathRow);

        // 提示
        var hintFfmpeg = MakeLabel(LanguageManager.T("FFmpeg_Hint"));
        hintFfmpeg.ForeColor = AppTheme.Colors.TextMuted;
        hintFfmpeg.Font = AppTheme.Fonts.CaptionFont;
        hintFfmpeg.Margin = new Padding(0, 4, 0, 4);
        AddFullRow(ffGrid, hintFfmpeg);

        // 测试按钮 + 状态同行
        var btnTest = MakeButton(LanguageManager.T("FFmpeg_Test"), AppTheme.Colors.ButtonActive);
        btnTest.Width = 100;
        AddRow(ffGrid, btnTest, MakePlaceholder());

        _lblFfmpegStatus = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            AutoEllipsis = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 4, 0, 4),
            ForeColor = AppTheme.Colors.TextMuted,
            BackColor = Color.Transparent,
        };
        AddFullRow(ffGrid, _lblFfmpegStatus);

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
                ? LanguageManager.T("Msg_NativeLoadSuccess")
                : LanguageManager.T("Msg_NativeLoadFailed");
            _lblFfmpegStatus.ForeColor = ok ? AppTheme.Colors.Success : AppTheme.Colors.Error;
        };
        UpdateFfmpegStatus();
        ffSection.Controls.Add(ffGrid);
        content.Controls.Add(ffSection);
        content.SetFlowBreak(ffSection, true);

        scrollPanel.Controls.Add(content);

        // ============ 主布局：滚动主体 + 底部按钮条 ============
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var buttonRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12, 8, 12, 12),
            BackColor = AppTheme.Colors.PanelBackground,
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var spacer = new Panel { Dock = DockStyle.Fill };
        var btnOk = MakeButton(LanguageManager.T("Settings_Ok"), AppTheme.Colors.ButtonActive);
        btnOk.DialogResult = DialogResult.OK;
        btnOk.Dock = DockStyle.Fill;
        btnOk.Anchor = AnchorStyles.Right;
        var btnCancel = MakeButton(LanguageManager.T("Settings_Cancel"), AppTheme.Colors.ControlBackground);
        btnCancel.DialogResult = DialogResult.Cancel;
        btnCancel.Dock = DockStyle.Fill;
        btnCancel.Anchor = AnchorStyles.Right;

        buttonRow.Controls.Add(spacer, 0, 0);
        buttonRow.Controls.Add(btnOk, 1, 0);
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonRow.Controls.Add(btnCancel, 2, 0);
        buttonRow.ColumnCount = 3;
        spacer.Margin = new Padding(0);
        btnOk.Margin = new Padding(0, 4, 4, 0);
        btnCancel.Margin = new Padding(0, 4, 0, 0);

        root.Controls.Add(scrollPanel, 0, 0);
        root.Controls.Add(buttonRow, 0, 1);

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    /// <summary>创建分区容器（带标题栏的分组面板）。</summary>
    private static GroupBox MakeSection(string title)
        => new()
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(12, 8, 12, 12),
            Margin = new Padding(0, 0, 0, SectionSpacing),
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = AppTheme.Colors.PanelBackground,
            Font = AppTheme.Fonts.BodyFont,
            Width = 660,
        };

    /// <summary>分区内部使用两列网格（标签 + 控件），AutoSize 自适应内容。</summary>
    private static TableLayoutPanel MakeSectionGrid()
        => new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 0),
            Margin = new Padding(0),
        };

    /// <summary>空占位控件，配合测试按钮占满剩余宽度。</summary>
    private static Control MakePlaceholder()
        => new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };

    /// <summary>创建深色主题 NumericUpDown。</summary>
    private static NumericUpDown MakeNumeric(decimal min, decimal max, decimal value, int decimals, int width)
        => new()
        {
            Width = width,
            Height = 24,
            Minimum = min,
            Maximum = max,
            Value = value,
            DecimalPlaces = decimals,
            BackColor = AppTheme.Colors.InputBackgroundAlt,
            ForeColor = AppTheme.Colors.TextPrimary,
            Margin = new Padding(0, 2, 0, 2),
            Anchor = AnchorStyles.Left,
        };

    private void UpdateFfmpegStatus()
    {
        var dir = _txtFfmpegDir.Text.Trim();
        _lblFfmpegStatus.Text = string.Empty;
        if (string.IsNullOrEmpty(dir))
        {
            // 留空 = 自动检测：FFMPEG_DIR → PATH → 应用目录
            var detected = NativeRuntime.AutoDetectFfmpegDirectory();
            if (detected is null)
            {
                _lblFfmpegStatus.Text = LanguageManager.T("Msg_AutoDetect") + "，" + LanguageManager.T("Msg_AutoDetectFailed");
                _lblFfmpegStatus.ForeColor = AppTheme.Colors.TextMuted;
            }
            else
            {
                _lblFfmpegStatus.Text = LanguageManager.T("Msg_AutoDetectSuccess") + detected;
                _lblFfmpegStatus.ForeColor = AppTheme.Colors.Success;
            }
            return;
        }
        var err = NativeRuntime.ValidateFfmpegDirectory(dir);
        _lblFfmpegStatus.Text = err is null ? LanguageManager.T("Msg_ValidateSuccess") : $"✗ {err}";
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
                ffmpegDir != (_settings.FfmpegDirectory ?? string.Empty) ||
                _cmbLanguage.SelectedIndex != _settings.Language;

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
                    Language = _cmbLanguage.SelectedIndex,
                };
            }
        }
    }
}
