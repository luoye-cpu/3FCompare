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
            Text = "媒体信息",
            Dock = DockStyle.Top,
            Height = 26,
            Font = AppTheme.Fonts.TitleFont,
            ForeColor = AppTheme.Colors.TextPrimary,
            Padding = new Padding(4, 2, 0, 0),
        };

        _emptyHint = new Label
        {
            Text = "选中一个已打开的媒体以查看信息",
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

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"文件: {Path.GetFileName(media.Path)}");
        sb.AppendLine();
        sb.AppendLine("— 视频 —");
        sb.AppendLine($"  分辨率:   {media.VideoWidth} × {media.VideoHeight}");
        sb.AppendLine($"  编码:     {media.Codec}");
        sb.AppendLine($"  帧率:     {media.FrameRate:0.###} fps");
        sb.AppendLine($"  像素格式: {media.PixelFormat ?? "--"} ({media.ChromaSubsampling ?? "--"}, {media.BitDepth}-bit)");
        sb.AppendLine($"  色彩:     {media.ColorSpace ?? "--"} / {media.ColorPrimaries ?? "--"} / {media.ColorTransfer ?? "--"}");
        sb.AppendLine($"  HDR:      {(media.IsHdr ? "是" : "否")}{(string.IsNullOrEmpty(media.HdrFormat) || media.HdrFormat == "SDR" ? "" : $" ({media.HdrFormat})")}");
        sb.AppendLine($"  帧数:     {media.FrameCount:N0}");
        sb.AppendLine($"  无损:     {(media.IsLossless ? "是" : "否")}   交错: {(media.Interlaced ? "是" : "否")}");
        sb.AppendLine();
        sb.AppendLine("— 音频 —");
        sb.AppendLine($"  编码:     {media.AudioCodec ?? "--"}");
        sb.AppendLine($"  声道:     {media.AudioChannels}{(media.AudioSampleRate > 0 ? $" @ {media.AudioSampleRate / 1000.0:0.#} kHz" : "")}");
        sb.AppendLine();
        sb.AppendLine("— 容器 —");
        sb.AppendLine($"  格式:     {media.Format ?? "--"}");
        sb.AppendLine($"  时长:     {TimeSpan.FromTicks(media.Duration100ns):hh\\:mm\\:ss\\.fff}");
        sb.AppendLine($"  码率:     {media.BitRate / 1000.0:0.#} kbps");
        sb.AppendLine($"  文件大小: {media.FileSize / (1024.0 * 1024.0):0.##} MB");

        _content.Text = sb.ToString();
    }

    public void Clear()
    {
        _content.Clear();
        _emptyHint.Visible = true;
        _content.Visible = false;
    }
}