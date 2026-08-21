using _3FCompare.Core.Backend;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>媒体信息面板（F3）：显示选中路的完整媒体信息（分辨率/帧率/编码/HDR/码率/色彩等）。
/// 展示来自 3FP GetMediaInfo 的增强字段。</summary>
public sealed class MediaInfoPanel : Panel
{
    private readonly Label _title;
    private readonly RichTextBox _content;
    private readonly Label _emptyHint;

    public MediaInfoPanel()
    {
        Dock = DockStyle.Fill;
        BackColor = AppTheme.Colors.PanelBackground;

        _title = new Label
        {
            Text = LanguageManager.T("MediaInfo_Title"),
            Dock = DockStyle.Top,
            Height = 26,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
            Padding = new Padding(4, 2, 0, 0),
        };

        _emptyHint = new Label
        {
            Text = LanguageManager.T("MediaInfo_Empty"),
            Dock = DockStyle.Top,
            Height = 30,
            Font = AppTheme.Fonts.BodyFont,
            ForeColor = AppTheme.Colors.TextMuted,
            Padding = new Padding(4, 4, 0, 0),
        };

        _content = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = AppTheme.Colors.InputBackground,
            ForeColor = AppTheme.Colors.TextSecondary,
            BorderStyle = BorderStyle.None,
            Font = AppTheme.Fonts.MonospaceFont,
            DetectUrls = false,
        };

        Controls.Add(_content);
        Controls.Add(_emptyHint);
        Controls.Add(_title);
    }

    public void ShowMediaInfo(EngineMediaInfo? media)
    {
        if (media is null)
        {
            _content.Clear();
            _emptyHint.Visible = true;
            _content.Visible = false;
            return;
        }

        _emptyHint.Visible = false;
        _content.Visible = true;

        var yes = LanguageManager.T("MediaInfo_Yes");
        var no = LanguageManager.T("MediaInfo_No");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{LanguageManager.T("MediaInfo_File")}: {Path.GetFileName(media.Path)}");
        sb.AppendLine();
        sb.AppendLine(LanguageManager.T("MediaInfo_Video"));
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Resolution")}:   {media.VideoWidth} × {media.VideoHeight}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Codec")}:     {media.Codec}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Framerate")}:     {media.FrameRate:0.###} fps");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_PixelFormat")}: {media.PixelFormat ?? "--"} ({media.ChromaSubsampling ?? "--"}, {media.BitDepth}-bit)");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Color")}:     {media.ColorSpace ?? "--"} / {media.ColorPrimaries ?? "--"} / {media.ColorTransfer ?? "--"}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Hdr")}:      {(media.IsHdr ? yes : no)}{(string.IsNullOrEmpty(media.HdrFormat) || media.HdrFormat == "SDR" ? "" : $" ({media.HdrFormat})")}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Frames")}:     {media.FrameCount:N0}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Lossless")}:     {(media.IsLossless ? yes : no)}   {LanguageManager.T("MediaInfo_Interlaced")}: {(media.Interlaced ? yes : no)}");
        sb.AppendLine();
        sb.AppendLine(LanguageManager.T("MediaInfo_Audio"));
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_AudioCodec")}:     {media.AudioCodec ?? "--"}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Channels")}:     {media.AudioChannels}{(media.AudioSampleRate > 0 ? $" @ {media.AudioSampleRate / 1000.0:0.#} kHz" : "")}");
        sb.AppendLine();
        sb.AppendLine(LanguageManager.T("MediaInfo_Container"));
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Format")}:     {media.Format ?? "--"}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Duration")}:     {TimeSpan.FromTicks(media.Duration100ns):hh\\:mm\\:ss\\.fff}");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_Bitrate")}:     {media.BitRate / 1000.0:0.#} kbps");
        sb.AppendLine($"  {LanguageManager.T("MediaInfo_FileSize")}: {media.FileSize / (1024.0 * 1024.0):0.##} MB");

        _content.Text = sb.ToString();
    }

    /// <summary>语言切换后刷新静态文本。</summary>
    public void ApplyLanguage()
    {
        _title.Text = LanguageManager.T("MediaInfo_Title");
        _emptyHint.Text = LanguageManager.T("MediaInfo_Empty");
    }

    public void Clear()
    {
        _content.Clear();
        _emptyHint.Visible = true;
        _content.Visible = false;
    }
}