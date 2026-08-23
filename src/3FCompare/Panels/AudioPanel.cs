using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Core.Backend;

namespace _3FCompare.Panels;

/// <summary>音频面板（WinForms AudioPanel 对应）：音轨选择 + 音量 + 静音（真实会话生效）。</summary>
public sealed class AudioPanel : StackPanel
{
    private readonly ComboBox _track = new() { Width = 200 };
    private readonly Slider _volume = new()
    {
        Minimum = 0, Maximum = 100, Value = 80, Width = 190,
    };
    private readonly CheckBox _mute = new() { Content = "Mute" };
    private IPlayerSession? _session;
    private bool _suppress;

    public AudioPanel()
    {
        Margin = new global::Avalonia.Thickness(10);
        Spacing = 8;

        _track.SelectionChanged += (_, _) =>
        {
            if (!_suppress) _session?.SelectAudioStream(Math.Max(0, _track.SelectedIndex));
        };
        _volume.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty && !_suppress)
                ApplyVolume();
        };
        _mute.IsCheckedChanged += (_, _) =>
        {
            if (!_suppress) ApplyVolume();
        };

        var trackLabel = new TextBlock { Text = LanguageManager.T("Audio_Track"), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#FFC8C8D2")) };
        var volLabel = new TextBlock { Text = LanguageManager.T("Audio_Volume"), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#FFC8C8D2")) };
        _mute.Content = LanguageManager.T("Audio_Mute");
        var hint = new TextBlock
        {
            Text = LanguageManager.T("Audio_Hint"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#8C8C96")),
        };

        Children.Add(new TextBlock
        {
            Text = LanguageManager.T("Audio_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")),
        });
        Children.Add(trackLabel);
        Children.Add(_track);
        Children.Add(volLabel);
        Children.Add(_volume);
        Children.Add(_mute);
        Children.Add(hint);
    }

    private void ApplyVolume()
    {
        try
        {
            _session?.SetVolume((float)(_volume.Value / 100.0), _mute.IsChecked == true);
        }
        catch { /* 演示模式无操作 */ }
    }

    /// <summary>绑定会话（真实模式：从媒体信息构建音轨下拉；演示：占位）。</summary>
    public void AttachSession(IPlayerSession? session, EngineMediaInfo? info)
    {
        _session = session;
        _suppress = true;
        _track.Items.Clear();
        if (info is not null && !string.IsNullOrEmpty(info.AudioCodec))
            _track.Items.Add(string.Format(LanguageManager.IsEnglish ? "Track 1: {0} ({1}ch)" : "轨 1: {0} ({1}ch)",
                info.AudioCodec, info.AudioChannels));
        else
            _track.Items.Add(LanguageManager.T("Audio_NoTrack"));
        _track.SelectedIndex = 0;
        _suppress = false;
        ApplyVolume();
    }
}
