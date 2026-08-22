using System.ComponentModel;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Display;
using _3FCompare.Core.Settings;
using _3FCompare.App.Utils;

namespace _3FCompare.App;

/// <summary>二级设置窗口（F25）：硬件加速/GPU 选择/步进步长/窗口/解码色彩/布局/FFmpeg。
/// 全部使用深色主题。内容区用 Panel+AutoScroll + 单列 TableLayoutPanel（AutoSize 行），
/// 分区用自绘边缘的 SectionPanel；避免 FlowLayoutPanel/GroupBox 与 AutoSize/Dock 混用时
/// 测量不可靠，保证 150%/200% DPI 下不挤压、不重叠、滚动范围正确。</summary>
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

    private TableLayoutPanel _content = null!;
    private Panel _scrollPanel = null!;
    private readonly List<SectionPanel> _sections = new();

    /// <summary>打开设置时是否定位/高亮 FFmpeg 分区（用于「缺 FFmpeg」引导弹窗的跳转）。</summary>
    private readonly bool _focusFfmpegSection;
    /// <summary>FFmpeg 分区在 _sections 中的索引（BuildUi 中 AddSection 顺序固定为 6）。</summary>
    private const int FfmpegSectionIndex = 6;

    private const int MarginPx = 12;
    private const int SectionSpacing = 24;

    public bool Changed => _changed;
    public AppSettings Result { get; private set; }

    public SettingsDialog(AppSettings settings) : this(settings, false) { }

    /// <param name="focusFfmpegSection">为真则打开时滚动到并高亮 FFmpeg 路径分区，
    /// 供未检测到 FFmpeg 的引导弹窗「打开设置」直接跳转使用。</param>
    public SettingsDialog(AppSettings settings, bool focusFfmpegSection)
    {
        _settings = settings;
        _focusFfmpegSection = focusFfmpegSection;
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

        // 缺 FFmpeg 引导：打开时定位并高亮 FFmpeg 路径分区
        if (_focusFfmpegSection)
        {
            Shown += (_, _) => FocusFfmpegSection();
        }
    }

    /// <summary>滚动到 FFmpeg 路径分区并短暂高亮其边框，便于用户快速找到设置项。</summary>
    private void FocusFfmpegSection()
    {
        if (FfmpegSectionIndex >= 0 && FfmpegSectionIndex < _sections.Count)
        {
            var section = _sections[FfmpegSectionIndex];
            _scrollPanel.ScrollControlIntoView(section);
            section.HighlightBriefly();
            _txtFfmpegDir.Focus();
        }
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
        EnsureGridColumns(grid);
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
        EnsureGridColumns(grid);
        var row = grid.RowCount;
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, 0, row);
        grid.SetColumnSpan(control, 2);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 2, 0, 2);
    }

    /// <summary>懒初始化网格两列：标签列（AutoSize）+ 控件列（Percent 100）。
    /// 原来每次 AddRow 都 Clear 列样式再重建，会强制整行重排，放大布局抖动。</summary>
    private static void EnsureGridColumns(TableLayoutPanel grid)
    {
        if (grid.ColumnStyles.Count == 0)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        }
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

        // 内容容器：单列 TableLayoutPanel（AutoSize 行），高度由分区内容驱动，外层提供滚动。
        // 替换原 FlowLayoutPanel —— FlowLayoutPanel+Dock=Top+AutoSize 嵌套 AutoSize 子项
        // （GroupBox+Dock=Top）时测量不可靠，是 150%/200% DPI 下挤压、重叠、滚动越界的根因。
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 1,
            Padding = new Padding(0),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        // 列宽用 Absolute（初始 665 = 720 client - 16*2 padding），由 UpdateSectionWidths 驱动；
        // Percent 列在 AutoSize TLP 中会参与宽度反推，导致分区宽度塌缩（实测 24px）。
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 665));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content = content;
        _scrollPanel = scrollPanel;
        UpdateSectionWidths();

        // ---------- 语言 ----------
        var langGrid = AddSection(content, "语言 / Language");
        _cmbLanguage = MakeCombo(240);
        _cmbLanguage.Items.AddRange(new object[] { "中文", "English" });
        _cmbLanguage.SelectedIndex = Math.Clamp(_settings.Language, 0, 1);
        AddRow(langGrid, MakeLabel("界面语言 / Language:"), _cmbLanguage);

        // ---------- 硬件加速 ----------
        var hwGrid = AddSection(content, LanguageManager.IsEnglish ? "Hardware Acceleration" : "硬件加速");
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

        // ---------- 步进 ----------
        var stepGrid = AddSection(content, LanguageManager.IsEnglish ? "Stepping" : "步进");
        _numFrameStep = MakeNumeric(1, 999, Math.Clamp(_settings.FrameStep, 1, 999), 0, 80);
        AddRow(stepGrid, MakeLabel(LanguageManager.T("Stepping_StepByFrame")), _numFrameStep);
        _numSecStep = MakeNumeric(1, 1200, (decimal)Math.Clamp(_settings.SecondsStep, 0.1, 600), 0, 80);
        _numSecStep.DecimalPlaces = 1;
        _numSecStep.Increment = 0.5m;
        AddRow(stepGrid, MakeLabel(LanguageManager.T("Stepping_StepBySecond")), _numSecStep);

        // ---------- 窗口 / 全屏 ----------
        var winGrid = AddSection(content, LanguageManager.IsEnglish ? "Window / Fullscreen" : "窗口 / 全屏");
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

        // ---------- 解码 / 色彩 ----------
        var colorGrid = AddSection(content, LanguageManager.IsEnglish ? "Decode / Color" : "解码 / 色彩");
        _cmbColorMode = MakeCombo(240);
        _cmbColorMode.Items.AddRange(new object[] { LanguageManager.T("Color_SDR"), LanguageManager.T("Color_HDRAuto") });
        _cmbColorMode.SelectedIndex = _settings.ColorMode switch
        {
            ColorModeSetting.MapToHdr => 1,
            _ => 0,
        };
        AddRow(colorGrid, MakeLabel(LanguageManager.T("Color_ColorMode")), _cmbColorMode);

        // ---------- 布局 ----------
        var layoutGrid = AddSection(content, LanguageManager.IsEnglish ? "Layout" : "布局");
        _numGridCols = MakeNumeric(1, 3, Math.Clamp(_settings.DefaultGridCols, 1, 3), 0, 70);
        AddRow(layoutGrid, MakeLabel(LanguageManager.T("Layout_DefaultCols")), _numGridCols);
        _numGridRows = MakeNumeric(1, 3, Math.Clamp(_settings.DefaultGridRows, 1, 3), 0, 70);
        AddRow(layoutGrid, MakeLabel(LanguageManager.T("Layout_DefaultRows")), _numGridRows);

        // ---------- FFmpeg 路径 ----------
        var ffGrid = AddSection(content, LanguageManager.IsEnglish ? "FFmpeg Path" : "FFmpeg 路径");

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

        scrollPanel.Controls.Add(content);

        // 滚动面板尺寸变化（含高 DPI 缩放后）→ 分区宽度跟随；保证内容永远占满可用宽度
        scrollPanel.ClientSizeChanged += (_, _) => UpdateSectionWidths();
        scrollPanel.Resize += (_, _) => UpdateSectionWidths();

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

    /// <summary>在内容容器中追加一个分区（自绘边框 + 标题 + 两列内容网格），返回网格。
    /// 分区由 SectionPanel 承载，宽度固定为滚动面板可用宽度（内容区宽度），
    /// 避免 AutoSize 测量在 AutoScroll 面板内无宽度约束导致的宽度坍塌；高度由内容驱动。</summary>
    private TableLayoutPanel AddSection(TableLayoutPanel content, string title)
    {
        var section = new SectionPanel
        {
            BackColor = AppTheme.Colors.PanelBackground,
            BorderColor = AppTheme.Colors.Border,
            Margin = new Padding(0, 0, 0, SectionSpacing),
        };
        section.SetTitle(title);
        var grid = MakeSectionGrid();
        section.Body.Controls.Add(grid);
        var row = content.RowCount++;
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(section, 0, row);
        _sections.Add(section);
        UpdateSectionWidths();
        return grid;
    }

    /// <summary>滚动面板宽度变化时，同步内容列宽（Absolute）与分区最小宽度。
    /// 子分区是 AutoSize 面板，TLP 布局时按其 PreferredSize 摆放；加入 MinimumSize
    /// 约束宽度下限（= 列宽），保证占满可用宽度而不被 TLP 压缩。</summary>
    private void UpdateSectionWidths()
    {
        if (_scrollPanel is null || _content is null) return;
        if (_scrollPanel.ClientSize.Width <= 0) return;
        var w = _scrollPanel.ClientSize.Width - _scrollPanel.Padding.Horizontal;
        if (w <= 0) return;

        _content.ColumnStyles[0].Width = w;
        foreach (var s in _sections)
            s.MinimumSize = new Size(w, 0);
    }

    /// <summary>深色主题分区容器：自绘 1px 边框 + 顶部标题（Label）+ 独立内容面板。
    /// 标题在最上（最后 Add 的控件先参与 Dock 布局，置于顶部），内容区在其下
    /// 按内容 AutoSize 增长——与 AutoScaleMode.Dpi 配合，高 DPI 下始终一致。</summary>
    private sealed class SectionPanel : Panel
    {
        private readonly Label _titleLabel = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 6),
            ForeColor = AppTheme.Colors.Accent,
            Font = AppTheme.Fonts.BodyFont,
            BackColor = Color.Transparent,
        };

        /// <summary>内容区：由 AddSection 把两列网格加入。</summary>
        public Panel Body { get; } = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };

        /// <summary>边框颜色（运行时由 AddSection 设置，无需设计器序列化）。</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BorderColor { get; set; } = AppTheme.Colors.Border;

        public SectionPanel()
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12, 4, 12, 12);
            Controls.Add(Body);
            Controls.Add(_titleLabel);
        }

        public void SetTitle(string title) => _titleLabel.Text = title;

        /// <summary>短暂高亮边框（约 1.6s 后恢复），用于引导用户定位分区。</summary>
        public void HighlightBriefly()
        {
            var accent = AppTheme.Colors.Accent;
            BorderColor = accent;
            Invalidate();
            var box = this;
            var t = new System.Windows.Forms.Timer { Interval = 1600 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                t.Dispose();
                if (box.IsDisposed) return;
                box.BorderColor = AppTheme.Colors.Border;
                box.Invalidate();
            };
            t.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var pen = new Pen(BorderColor, BorderColor == AppTheme.Colors.Accent ? 2f : 1f);
            e.Graphics.DrawRectangle(pen, rect);
        }
    }

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
