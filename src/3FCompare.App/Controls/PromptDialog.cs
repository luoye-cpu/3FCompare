using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>轻量提示对话框：支持自定义按钮文字与「打开设置」类二级动作。
/// WinForms 原生 MessageBox 无法定制按钮文字，缺 FFmpeg 引导需要「关闭 / 打开设置」
/// 两个半自定义按钮，故自建一个深色主题对话框。</summary>
public sealed class PromptDialog : Form
{
    /// <summary>确认（主要，默认）按钮文字。</summary>
    public string PrimaryText = "关闭";
    /// <summary>次要（可空：null 时隐藏）按钮文字。</summary>
    public string? SecondaryText;

    /// <summary>用户点击了「打开设置」类次要按钮（ShowDialog 返回 DialogResult.No 时据此判断）。</summary>
    public bool SecondaryClicked { get; private set; }

    public PromptDialog(string title, string message)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = AppTheme.Colors.InputBackground;
        ForeColor = AppTheme.Colors.TextPrimary;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(Dpi.BaseDpi, Dpi.BaseDpi);
        ClientSize = new Size(560, 240);

        var iconLabel = new Label
        {
            AutoSize = false,
            Size = new Size(48, 48),
            Location = new Point(24, 24),
            Text = "⚠",
            Font = new Font("Segoe UI", 24f),
            ForeColor = AppTheme.Colors.Accent,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
        };

        var msgLabel = new Label
        {
            AutoSize = false,
            Location = new Point(88, 22),
            Size = new Size(ClientSize.Width - 88 - 24, 136),
            Text = message,
            Font = AppTheme.Fonts.BodyFont,
            ForeColor = AppTheme.Colors.TextPrimary,
            BackColor = Color.Transparent,
        };

        // 底部按钮行：RightToLeft 使按钮按从右到左排列，自然靠右。
        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(12, 10, 12, 8),
            BackColor = AppTheme.Colors.PanelBackground,
        };

        _primaryBtn = MakeButton();
        _primaryBtn.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };

        _secondaryBtn = MakeButton();
        _secondaryBtn.Click += (_, _) =>
        {
            SecondaryClicked = true;
            DialogResult = DialogResult.No;
            Close();
        };

        // 先加主按钮（最右），再加次按钮（其左）
        btnRow.Controls.Add(_primaryBtn);
        btnRow.Controls.Add(_secondaryBtn);

        Controls.AddRange(new Control[] { btnRow, msgLabel, iconLabel });
        AcceptButton = _primaryBtn;
        CancelButton = _primaryBtn;
    }

    protected override void OnLoad(EventArgs e)
    {
        _primaryBtn.Text = PrimaryText;
        if (string.IsNullOrEmpty(SecondaryText))
        {
            _secondaryBtn.Visible = false;
        }
        else
        {
            _secondaryBtn.Text = SecondaryText;
        }
        base.OnLoad(e);
    }

    private readonly Button _primaryBtn;
    private readonly Button _secondaryBtn;

    /// <summary>创建对话框按钮（深色主题扁平）。</summary>
    private static Button MakeButton()
        => new()
        {
            Text = "",
            AutoSize = false,
            Size = new Size(104, 34),
            Margin = new Padding(4, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderColor = AppTheme.Colors.Border, BorderSize = 1 },
            BackColor = AppTheme.Colors.ControlBackground,
            ForeColor = AppTheme.Colors.TextPrimary,
            Font = AppTheme.Fonts.BodyFont,
        };
}