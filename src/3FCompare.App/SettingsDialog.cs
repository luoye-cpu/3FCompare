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

    // 控件
    private CheckBox _chkHardware;
    private ComboBox _cmbAdapter;
    private NumericUpDown _numFrameStep;
    private NumericUpDown _numSecStep;
    private CheckBox _chkStartFullscreen;
    private CheckBox _chkHideChrome;
    private ComboBox _cmbColorMode;
    private NumericUpDown _numGridCols;
    private NumericUpDown _numGridRows;

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

        tabs.TabPages.AddRange(new TabPage[] { hardware, stepping, window, color, layout });
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (DialogResult == DialogResult.OK)
        {
            var changed =
                _chkHardware.Checked != _settings.HardwareDecode ||
                _cmbAdapter.SelectedIndex != _settings.PreferredAdapterIndex ||
                (int)_numFrameStep.Value != _settings.FrameStep ||
                (double)_numSecStep.Value != _settings.SecondsStep ||
                _chkStartFullscreen.Checked != _settings.StartFullscreen ||
                _chkHideChrome.Checked != _settings.HideChromeInFullscreen ||
                _cmbColorMode.SelectedIndex != (int)_settings.ColorMode ||
                (int)_numGridCols.Value != _settings.DefaultGridCols ||
                (int)_numGridRows.Value != _settings.DefaultGridRows;

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
                };
            }
        }
    }
}