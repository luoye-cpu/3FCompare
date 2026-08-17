using _3FCompare.Core.Backend;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>音频控制面板（F25 音频可选）：音轨选择 + 音量 + 静音。
/// 真实模式透传 3FP SelectAudioStream / SetVolume；演示模式为 no-op。</summary>
public sealed class AudioPanel : Panel
{
    private readonly ComboBox _trackBox;
    private readonly TrackBar _volumeBar;
    private readonly CheckBox _chkMute;
    private IPlayerSession? _session;
    private bool _busy;

    public AudioPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = AppTheme.Colors.PanelBackground;
        Padding = AppTheme.Spacing.Large;

        var title = new Label
        {
            Text = "音频",
            Dock = DockStyle.Top,
            Height = 26,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
        };

        var lblTrack = new Label { Text = "音轨:", Location = new Point(12, 40), AutoSize = true, ForeColor = AppTheme.Colors.TextPrimary };
        _trackBox = new ComboBox
        {
            Location = new Point(60, 36),
            Size = new Size(150, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _trackBox.SelectedIndexChanged += (_, _) => ApplyTrack();

        var lblVol = new Label { Text = "音量:", Location = new Point(12, 76), AutoSize = true, ForeColor = AppTheme.Colors.TextPrimary };
        _volumeBar = new TrackBar
        {
            Location = new Point(60, 68),
            Size = new Size(150, 30),
            Minimum = 0,
            Maximum = 100,
            Value = 80,
            TickStyle = TickStyle.None,
        };
        _volumeBar.ValueChanged += (_, _) => ApplyVolume();

        _chkMute = new CheckBox
        {
            Text = "静音",
            Location = new Point(60, 104),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
        };
        _chkMute.CheckedChanged += (_, _) => ApplyVolume();

        var hint = new Label
        {
            Text = "音轨选择对真实 3FP 会话生效；演示模式为占位。",
            Location = new Point(12, 140),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextMuted,
            Font = AppTheme.Fonts.CaptionFont,
        };

        Controls.AddRange(new Control[] { hint, _chkMute, lblVol, _volumeBar, lblTrack, _trackBox, title });
    }

    /// <summary>关联会话并从媒体信息填充音轨列表。</summary>
    public void AttachSession(IPlayerSession? session, EngineMediaInfo? media)
    {
        _session = session;
        _busy = true;
        _trackBox.Items.Clear();

        if (media is not null && !string.IsNullOrEmpty(media.AudioCodec))
        {
            // 单音轨信息直接展示；多音轨在多轨容器时从 streams 枚举（此处简化）
            _trackBox.Items.Add($"轨 0: {media.AudioCodec} ({media.AudioChannels}ch)");
            _trackBox.SelectedIndex = 0;
        }
        else
        {
            _trackBox.Items.Add("无音轨");
            _trackBox.SelectedIndex = 0;
        }
        _busy = false;
    }

    private void ApplyTrack()
    {
        if (_busy || _session is null || _trackBox.SelectedIndex < 0) return;
        try { _session.SelectAudioStream(_trackBox.SelectedIndex); }
        catch { /* 状态不允许时忽略 */ }
    }

    private void ApplyVolume()
    {
        if (_session is null) return;
        try { _session.SetVolume(_volumeBar.Value / 100f, _chkMute.Checked); }
        catch { /* 忽略 */ }
    }
}