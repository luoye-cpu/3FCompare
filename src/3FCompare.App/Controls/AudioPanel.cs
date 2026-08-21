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
    private readonly Label _title;
    private readonly Label _lblTrack;
    private readonly Label _lblVol;
    private readonly Label _hint;
    private IPlayerSession? _session;
    private bool _busy;

    public AudioPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = AppTheme.Colors.PanelBackground;

        _title = new Label
        {
            Text = LanguageManager.T("Audio_Title"),
            Dock = DockStyle.Top,
            Height = 26,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
        };

        // 流式布局（从上到下）：音轨 / 音量 / 静音 / 提示，高 DPI 下自动缩放不挤压
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12, 12, 4, 0),
            BackColor = AppTheme.Colors.PanelBackground,
        };

        _lblTrack = new Label { Text = LanguageManager.T("Audio_Track"), AutoSize = true, ForeColor = AppTheme.Colors.TextPrimary, Margin = new Padding(0, 8, 0, 0) };
        _trackBox = new ComboBox
        {
            Size = new Size(170, 24),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _trackBox.Margin = new Padding(0, 0, 0, 8);
        _trackBox.SelectedIndexChanged += (_, _) => ApplyTrack();

        _lblVol = new Label { Text = LanguageManager.T("Audio_Volume"), AutoSize = true, ForeColor = AppTheme.Colors.TextPrimary, Margin = new Padding(0, 8, 0, 0) };
        _volumeBar = new TrackBar
        {
            Size = new Size(180, 30),
            Minimum = 0,
            Maximum = 100,
            Value = 80,
            TickStyle = TickStyle.None,
        };
        _volumeBar.Margin = new Padding(0, 0, 0, 8);
        _volumeBar.ValueChanged += (_, _) => ApplyVolume();

        _chkMute = new CheckBox
        {
            Text = LanguageManager.T("Audio_Mute"),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextPrimary,
            Margin = new Padding(0, 4, 0, 8),
        };
        _chkMute.CheckedChanged += (_, _) => ApplyVolume();

        _hint = new Label
        {
            Text = LanguageManager.T("Audio_Hint"),
            AutoSize = true,
            ForeColor = AppTheme.Colors.TextMuted,
            Font = AppTheme.Fonts.CaptionFont,
            Margin = new Padding(0, 8, 0, 0),
        };

        flow.Controls.AddRange(new Control[] { _lblTrack, _trackBox, _lblVol, _volumeBar, _chkMute, _hint });
        Controls.AddRange(new Control[] { flow, _title });
    }

    /// <summary>语言切换后刷新静态文本。</summary>
    public void ApplyLanguage()
    {
        _title.Text = LanguageManager.T("Audio_Title");
        _lblTrack.Text = LanguageManager.T("Audio_Track");
        _lblVol.Text = LanguageManager.T("Audio_Volume");
        _chkMute.Text = LanguageManager.T("Audio_Mute");
        _hint.Text = LanguageManager.T("Audio_Hint");
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
            _trackBox.Items.Add(LanguageManager.Tf("Audio_TrackFmt", 0, media.AudioCodec, media.AudioChannels));
            _trackBox.SelectedIndex = 0;
        }
        else
        {
            _trackBox.Items.Add(LanguageManager.T("Audio_NoTrack"));
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