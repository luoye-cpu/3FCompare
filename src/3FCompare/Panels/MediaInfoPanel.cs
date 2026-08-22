using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Core.Backend;

namespace _3FCompare.Panels;

/// <summary>媒体信息面板（WinForms MediaInfoPanel 对应）：选中路的完整技术报告。</summary>
public sealed class MediaInfoPanel : ScrollViewer
{
    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 11.5,
        Foreground = new SolidColorBrush(Color.Parse("#FFC8C8D2")),
        TextWrapping = TextWrapping.Wrap,
    };

    public MediaInfoPanel()
    {
        Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(10),
            Children =
            {
                new TextBlock
                {
                    Text = LanguageManager.T("MediaInfo_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#FFFFFFFF")),
                },
                _text,
            },
        };
        Clear();
        LanguageManager.LanguageChanged += (_, _) =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_text.Text == LanguageManager.T("MediaInfo_Empty")) Clear(); });
    }

    private string L(string key) => LanguageManager.T(key);
    private static string YesNo(bool v) => v ? LanguageManager.T("MediaInfo_Yes") : LanguageManager.T("MediaInfo_No");

    public void Clear() => _text.Text = LanguageManager.T("MediaInfo_Empty");

    public void ShowMediaInfo(EngineMediaInfo? m)
    {
        if (m is null) { Clear(); return; }

        var nl = Environment.NewLine;
        var sb = new System.Text.StringBuilder();
        sb.Append(L("MediaInfo_File")).Append(": ").AppendLine(Path.GetFileName(m.Path));
        sb.AppendLine(L("MediaInfo_Video"));
        sb.Append("  ").Append(L("MediaInfo_Resolution")).Append(": ").Append($"{m.VideoWidth}x{m.VideoHeight}").AppendLine();
        sb.Append("  ").Append(L("MediaInfo_Codec")).Append(": ").AppendLine(m.Codec);
        sb.Append("  ").Append(L("MediaInfo_Framerate")).Append(": ").AppendLine($"{m.FrameRate:0.###} fps");
        if (!string.IsNullOrEmpty(m.PixelFormat))
            sb.Append("  ").Append(L("MediaInfo_PixelFormat")).Append(": ")
              .AppendLine(m.PixelFormat + (string.IsNullOrEmpty(m.ChromaSubsampling) ? "" : $" ({m.ChromaSubsampling})") + $" {m.BitDepth}bit");
        if (!string.IsNullOrEmpty(m.ColorPrimaries) || !string.IsNullOrEmpty(m.ColorTransfer))
            sb.Append("  ").Append(L("MediaInfo_Color")).Append(": ")
              .AppendLine($"{m.ColorPrimaries}/{m.ColorTransfer}/{m.ColorSpace}");
        sb.Append("  ").Append(L("MediaInfo_Hdr")).Append(": ")
          .AppendLine(m.IsHdr ? (m.HdrFormat ?? "HDR") : LanguageManager.T("MediaInfo_No"));
        if (m.FrameCount > 0)
            sb.Append("  ").Append(L("MediaInfo_Frames")).Append(": ").AppendLine(m.FrameCount.ToString());
        sb.Append("  ").Append(L("MediaInfo_Lossless")).Append(": ").AppendLine(YesNo(m.IsLossless));
        if (m.Interlaced)
            sb.Append("  ").Append(L("MediaInfo_Interlaced")).AppendLine();
        if (!string.IsNullOrEmpty(m.AudioCodec))
        {
            sb.AppendLine(L("MediaInfo_Audio"));
            sb.Append("  ").Append(L("MediaInfo_AudioCodec")).Append(": ").AppendLine(m.AudioCodec);
            sb.Append("  ").Append(L("MediaInfo_Channels")).Append(": ")
              .AppendLine(m.AudioChannels > 0 ? $"{m.AudioChannels}ch @ {m.AudioSampleRate / 1000.0:0.#}kHz" : "-");
        }
        sb.AppendLine(L("MediaInfo_Container"));
        sb.Append("  ").Append(L("MediaInfo_Format")).Append(": ").AppendLine(m.Format ?? "-");
        sb.Append("  ").Append(L("MediaInfo_Duration")).Append(": ")
          .AppendLine(TimeSpan.FromTicks(m.Duration100ns).ToString(@"hh\:mm\:ss\.fff"));
        if (m.BitRate > 0)
            sb.Append("  ").Append(L("MediaInfo_Bitrate")).Append(": ").AppendLine($"{m.BitRate / 1000.0:0.#} kbps");
        if (m.FileSize > 0)
            sb.Append("  ").Append(L("MediaInfo_FileSize")).Append(": ").AppendLine($"{m.FileSize / 1024.0 / 1024.0:0.#} MB");
        _text.Text = sb.ToString();
    }
}
