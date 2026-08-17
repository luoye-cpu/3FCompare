using System.Text;
using _3FCompare.Core.Backend;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>像素探针面板（F19）：鼠标悬停在视频面上时，实时显示该点 RGBA 码值。
/// 通过与 PlayerSurface 的坐标联动工作。</summary>
public sealed class ProbePanel : Panel
{
    private readonly Label _valueLabel;
    private readonly Label _coordLabel;
    private readonly Label _modeLabel;
    private IPlayerSession? _session;
    private int _lastX = -1;
    private int _lastY = -1;

    public ProbePanel()
    {
        Dock = DockStyle.Right;
        Width = 230;
        BackColor = AppTheme.Colors.PanelBackground;

        var title = new Label
        {
            Text = "像素探针",
            Dock = DockStyle.Top,
            Height = 28,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        title.Margin = new Padding(8, 4, 0, 0);

        _coordLabel = new Label
        {
            Text = "坐标: --",
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Consolas", 9f),
            ForeColor = AppTheme.Colors.TextSecondary,
        };
        _modeLabel = new Label
        {
            Text = "色彩模式: --",
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Consolas", 9f),
            ForeColor = AppTheme.Colors.TextSecondary,
        };
        _valueLabel = new Label
        {
            Text = "R: --  G: --  B: --  A: --",
            Dock = DockStyle.Top,
            Height = 60,
            Font = new Font("Consolas", 12f, FontStyle.Bold),
            ForeColor = AppTheme.Colors.Accent,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var hint = new Label
        {
            Text = "鼠标悬停视频面以读取像素\n（显示颜色管理前码值）",
            Dock = DockStyle.Top,
            Height = 40,
            Font = AppTheme.Fonts.CaptionFont,
            ForeColor = AppTheme.Colors.TextMuted,
        };

        Controls.AddRange(new Control[] { hint, _valueLabel, _modeLabel, _coordLabel, title });
        Controls.Add(_valueLabel);
    }

    /// <summary>关联到某路会话（探针读取源）。null 表示取消关联。</summary>
    public void AttachSession(IPlayerSession? session)
    {
        _session = session;
        if (session is null)
        {
            _coordLabel.Text = "坐标: --";
            _valueLabel.Text = "R: --  G: --  B: --  A: --";
            _modeLabel.Text = "色彩模式: --";
        }
    }

    /// <summary>由外部（视频面鼠标移动）驱动更新探针读数。</summary>
    public void UpdatePoint(int x, int y)
    {
        if (_session is null || x < 0 || y < 0) return;
        _lastX = x;
        _lastY = y;
        _coordLabel.Text = $"坐标: ({x}, {y})";

        if (!_session.TryReadPixel(x, y, out var pixel))
        {
            _valueLabel.Text = "读取失败";
            return;
        }

        var r8 = (int)Math.Clamp(pixel.R * 255f, 0, 255);
        var g8 = (int)Math.Clamp(pixel.G * 255f, 0, 255);
        var b8 = (int)Math.Clamp(pixel.B * 255f, 0, 255);
        _valueLabel.Text = $"R:{pixel.R:F3}  G:{pixel.G:F3}\nB:{pixel.B:F3}  A:{pixel.A:F3}  ({pixel.BitDepth}-bit)";
        _modeLabel.Text = $"码值(8位): {r8} {g8} {b8}";
    }

    public void ShowColorMethod(uint actualColorMode)
    {
        _modeLabel.Text = $"色彩模式: {actualColorMode switch { 0 => "映射SDR", 1 => "原始HDR", 2 => "峰值HDR", _ => actualColorMode.ToString() }}";
    }

    /// <summary>复制当前读数到剪贴板（JSON 格式便于记录）。</summary>
    public void CopyToClipboard()
    {
        if (_lastX < 0 || _session is null) return;
        if (_session.TryReadPixel(_lastX, _lastY, out var p))
        {
            var json = $"{{\"x\":{_lastX},\"y\":{_lastY},\"r\":{p.R:F4},\"g\":{p.G:F4},\"b\":{p.B:F4},\"a\":{p.A:F4},\"depth\":{p.BitDepth}}}";
            try { Clipboard.SetText(json); } catch { /* 剪贴板占用忽略 */ }
        }
    }
}