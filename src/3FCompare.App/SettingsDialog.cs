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

    private const int MarginPx = 12;

    public bool Changed => _changed;
    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        _adapters = GpuEnumeration.Enumerate();
        Result = settings;

        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(580, 460);
        BackColor = AppTheme.Colors.InputBackground;
        ForeColor = AppTheme.Colors.TextPrimary;

        // 高 DPI：以 96 DPI 为基准，控件树与实际窗体尺寸按 DPI 因子统一缩放。
        // 与历史实现不同，这里不手工锁死 FixedDialog/ClientSize 的绝对像素，
        // 而是配合 TableLayoutPanel（Dock/AutoSize 自适应）保证缩放后控件成比例、不挤压。
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(Dpi.BaseDpi, Dpi.BaseDpi);
        ClientSize = new Size(580, 500);

        BuildUi();
    }

    /// <summary>创建带深色主题的标签页。</summary>
    private static TabPage MakeTab(string title)
        => new(title)
        {
            BackColor = AppTheme.Colors.PanelBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
            UseVisualStyleBackColor = false,
            Padding = new Padding(MarginPx),
        };

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

    /// <summary>创建两列表格（label + control）布局的页容器。
    /// 内容行 AutoSize 保持紧凑，末尾追加一行弹性空行吸收多余高度，
    /// 避免 Dock=Fill 时把各行均匀拉伸造成稀疏/挤压。</summary>
    private static TableLayoutPanel MakePageGrid()
        => new()
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
        };

    /// <summary>确保表格有：内容行（AutoSize）+ 末尾弹性空行（Percent 100）。</summary>
    private static void EnsureLayoutRows(TableLayoutPanel grid)
    {
        grid.ColumnStyles.Clear();
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // 标签列
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); // 控件列
        // 内容行 AutoSize，最后一行弹性占满
        grid.RowStyles.Clear();
        var contentRows = grid.RowCount - 1;
        for (var i = 0; i < contentRows; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 弹性空行
        // 弹性空行占位控件
        if (grid.RowCount - 1 >= 0 && grid.GetControlFromPosition(1, grid.RowCount - 1) is null)
        {
            var spacer = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0) };
            grid.Controls.Add(spacer, 0, grid.RowCount - 1);
            grid.SetColumnSpan(spacer, 2);
        }
    }

    /// <summary>在页容器中新增一行两列条目（标签在左，控件在右，占满可用宽度）。</summary>
    private static void AddRow(TableLayoutPanel grid, Control label, Control control)
    {
        var row = grid.RowCount - 1; // 在弹性空行之前插入
        grid.RowCount++;
        GridInsertRow(grid, row, label, control, columnSpan: 1);
    }

    /// <summary>新增一行跨两列的控件（如全宽 CheckBox / 说明文字）。</summary>
    private static void AddFullRow(TableLayoutPanel grid, Control control)
    {
        var row = grid.RowCount - 1; // 在弹性空行之前插入
        grid.RowCount++;
        GridInsertRow(grid, row, control, null, columnSpan: 2);
    }

    private static void GridInsertRow(TableLayoutPanel grid, int row, Control a, Control? b, int columnSpan)
    {
        // 把原 row.. 处的控件下移一行，腾出插入位
        for (var r = grid.RowCount - 2; r >= row; r--)
        {
            for (var c = 0; c < 2; c++)
            {
                var cc = grid.GetControlFromPosition(c, r);
                if (cc is not null)
                {
                    grid.SetRow(cc, r + 1);
                    grid.SetColumn(cc, c);
                }
            }
        }
        if (b is null)
        {
            grid.Controls.Add(a, 0, row);
            grid.SetColumnSpan(a, columnSpan);
            a.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        else
        {
            grid.Controls.Add(a, 0, row);
            grid.Controls.Add(b, 1, row);
            b.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            b.Margin = new Padding(0, 2, 0, 2);
        }
        a.Margin = new Padding(0, 2, 0, 2);
        EnsureLayoutRows(grid);
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
        // ============ 标签页 ============
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Colors.InputBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
        };

        // ---- 硬件页 ----
        var hardware = MakeTab("硬件加速");
        var hwGrid = MakePageGrid();
        _chkHardware = new CheckBox
        {
            Text = "启用硬件解码 (GPU)",
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
        AddRow(hwGrid, MakeLabel("解码 GPU："), _cmbAdapter);
        hardware.Controls.Add(hwGrid);

        // ---- 步进页 ----
        var stepping = MakeTab("步进");
        var stepGrid = MakePageGrid();
        _numFrameStep = MakeNumeric(1, 999, Math.Clamp(_settings.FrameStep, 1, 999), 0, 80);
        AddRow(stepGrid, MakeLabel("按帧步进步长："), _numFrameStep);
        _numSecStep = MakeNumeric(1, 1200, (decimal)Math.Clamp(_settings.SecondsStep, 0.1, 600), 0, 80);
        _numSecStep.DecimalPlaces = 1;
        _numSecStep.Increment = 0.5m;
        AddRow(stepGrid, MakeLabel("按秒步进步长："), _numSecStep);
        stepping.Controls.Add(stepGrid);

        // ---- 窗口页 ----
        var window = MakeTab("窗口 / 全屏");
        var winGrid = MakePageGrid();
        _chkStartFullscreen = new CheckBox
        {
            Text = "启动时进入全屏模式",
            Checked = _settings.StartFullscreen,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        AddFullRow(winGrid, _chkStartFullscreen);
        _chkHideChrome = new CheckBox
        {
            Text = "全屏时隐藏时间轴/工具栏",
            Checked = _settings.HideChromeInFullscreen,
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };
        AddFullRow(winGrid, _chkHideChrome);
        window.Controls.Add(winGrid);

        // ---- 解码/色彩页 ----
        var color = MakeTab("解码 / 色彩");
        var colorGrid = MakePageGrid();
        _cmbColorMode = MakeCombo(240);
        _cmbColorMode.Items.AddRange(new object[] { "SDR 输出", "HDR 输出 (自动检测)" });
        _cmbColorMode.SelectedIndex = _settings.ColorMode switch
        {
            ColorModeSetting.MapToHdr => 1,
            _ => 0,
        };
        AddRow(colorGrid, MakeLabel("色彩模式："), _cmbColorMode);
        color.Controls.Add(colorGrid);

        // ---- 布局页 ----
        var layout = MakeTab("布局");
        var layoutGrid = MakePageGrid();
        _numGridCols = MakeNumeric(1, 3, Math.Clamp(_settings.DefaultGridCols, 1, 3), 0, 70);
        AddRow(layoutGrid, MakeLabel("默认网格列数："), _numGridCols);
        _numGridRows = MakeNumeric(1, 3, Math.Clamp(_settings.DefaultGridRows, 1, 3), 0, 70);
        AddRow(layoutGrid, MakeLabel("默认网格行数："), _numGridRows);
        layout.Controls.Add(layoutGrid);

        // ---- FFmpeg 页 ----
        var ffmpeg = MakeTab("FFmpeg 路径");
        var ffGrid = MakePageGrid();

        var lblFfmpeg = MakeLabel("FFmpeg DLL 目录：");
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
        var btnBrowse = MakeButton("浏览…", AppTheme.Colors.ControlBackground);
        btnBrowse.Width = 80;
        btnBrowse.Margin = new Padding(0, 0, 0, 0);
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
        var hintFfmpeg = MakeLabel("手动设置（优先）> 自动检测（应用目录 / PATH）");
        hintFfmpeg.ForeColor = AppTheme.Colors.TextMuted;
        hintFfmpeg.Font = AppTheme.Fonts.CaptionFont;
        hintFfmpeg.Margin = new Padding(0, 4, 0, 4);
        AddFullRow(ffGrid, hintFfmpeg);

        // 测试按钮 + 状态同行
        var btnTest = MakeButton("测试探测", AppTheme.Colors.ButtonActive);
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
                ? "✓ FFF.Native 加载成功，FFmpeg 可用"
                : "✗ FFF.Native 加载失败";
            _lblFfmpegStatus.ForeColor = ok ? AppTheme.Colors.Success : AppTheme.Colors.Error;
        };
        UpdateFfmpegStatus();
        ffmpeg.Controls.Add(ffGrid);

        // ============ 组装 ============
        tabs.TabPages.AddRange(new TabPage[] { hardware, stepping, window, color, layout, ffmpeg });

        // 主布局：主体（TabControl）+ 底部按钮条
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
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
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttonRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var spacer = new Panel { Dock = DockStyle.Fill };
        var btnOk = MakeButton("确定", AppTheme.Colors.ButtonActive);
        btnOk.DialogResult = DialogResult.OK;
        btnOk.Dock = DockStyle.Fill;
        btnOk.Anchor = AnchorStyles.Right;
        var btnCancel = MakeButton("取消", AppTheme.Colors.ControlBackground);
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

        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(buttonRow, 0, 1);

        Controls.Add(root);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

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
