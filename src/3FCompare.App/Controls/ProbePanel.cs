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
    private readonly Label _title;
    private readonly Label _hint;

    public ProbePanel()
    {
        Dock = DockStyle.Right;
        Width = 230;
        BackColor = AppTheme.Colors.PanelBackground;

        _title = new Label
        {
            Text = LanguageManager.T("Probe_Title"),
            Dock = DockStyle.Top,
            Height = 28,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _title.Margin = new Padding(8, 4, 0, 0);

        _coordLabel = new Label
        {
            Text = LanguageManager.T("Probe_Coord"),
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Consolas", 9f),
            ForeColor = AppTheme.Colors.TextSecondary,
        };
        _modeLabel = new Label
        {
            Text = LanguageManager.T("Probe_Mode"),
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

        _hint = new Label
        {
            Text = LanguageManager.T("Probe_Hint"),
            Dock = DockStyle.Top,
            Height = 40,
            Font = AppTheme.Fonts.CaptionFont,
            ForeColor = AppTheme.Colors.TextMuted,
        };

        Controls.AddRange(new Control[] { _hint, _valueLabel, _modeLabel, _coordLabel, _title });
    }

    /// <summary>语言切换后刷新静态文本。</summary>
    public void ApplyLanguage()
    {
        _title.Text = LanguageManager.T("Probe_Title");
        _hint.Text = LanguageManager.T("Probe_Hint");
        if (_session is null)
        {
            _coordLabel.Text = LanguageManager.T("Probe_Coord");
            _modeLabel.Text = LanguageManager.T("Probe_Mode");
        }
        else
        {
            UpdatePoint(_lastX, _lastY); // 保留当前读数并刷新语言
        }
    }

    /// <summary>关联到某路会话（探针读取源）。null 表示取消关联。</summary>
    public void AttachSession(IPlayerSession? session)
    {
        _session = session;
        if (session is null)
        {
            _coordLabel.Text = LanguageManager.T("Probe_Coord");
            _valueLabel.Text = "R: --  G: --  B: --  A: --";
            _modeLabel.Text = LanguageManager.T("Probe_Mode");
        }
    }

    /// <summary>由外部（视频面鼠标移动）驱动更新探针读数。</summary>
    public void UpdatePoint(int x, int y)
    {
        if (_session is null || x < 0 || y < 0) return;
        _lastX = x;
        _lastY = y;
        _coordLabel.Text = LanguageManager.T("Probe_Coord").Replace("--", $"({x}, {y})");

        if (!_session.TryReadPixel(x, y, out var pixel))
        {
            _valueLabel.Text = LanguageManager.T("Probe_ReadFail");
            return;
        }

        var r8 = (int)Math.Clamp(pixel.R * 255f, 0, 255);
        var g8 = (int)Math.Clamp(pixel.G * 255f, 0, 255);
        var b8 = (int)Math.Clamp(pixel.B * 255f, 0, 255);
        _valueLabel.Text = $"R:{pixel.R:F3}  G:{pixel.G:F3}\nB:{pixel.B:F3}  A:{pixel.A:F3}  ({pixel.BitDepth}-bit)";
        _modeLabel.Text = $"{LanguageManager.T("Probe_Bits")}: {r8} {g8} {b8}";
    }

    public void ShowColorMethod(uint actualColorMode)
    {
        var modeName = actualColorMode switch
        {
            0 => LanguageManager.T("Probe_MapSdr"),
            1 => LanguageManager.T("Probe_RawHdr"),
            2 => LanguageManager.T("Probe_PeakHdr"),
            _ => actualColorMode.ToString(),
        };
        _modeLabel.Text = $"{LanguageManager.T("Probe_Mode").Replace(": --", "")}: {modeName}";
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