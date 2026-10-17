using System;

using _3FCompare.Themes;
using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media;
using _3FCompare.Localization;
using _3FCompare.Core.Backend;

namespace _3FCompare.Panels;

/// <summary>媒体信息面板（WinForms MediaInfoPanel 对应）：选中路的完整技术报告。</summary>
public sealed class MediaInfoPanel : ScrollViewer
{
    private readonly TextBlock _text = new TextBlock
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
    }.Themed("TextSecondaryBrush");

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
                },
                _text,
            },
        };
        Clear();
        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, p =>
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (p._text.Text == LanguageManager.T("MediaInfo_Empty")) p.Clear(); }));
    }

    private string L(string key) => LanguageManager.T(key);
    private static string YesNo(bool v) => v ? LanguageManager.T("MediaInfo_Yes") : LanguageManager.T("MediaInfo_No");

    /// <summary>把内核的"动态元数据来源"读数翻成可显示文案。
    /// <para>internal static 而非 private：这是纯函数（只读枚举 + 查语言表，不碰 Avalonia 控件、
    /// 不碰内核），<c>3FCompare.Platform.Tests</c> 经 <c>InternalsVisibleTo</c> 直连覆盖三个分支，
    /// 不必为了测它去构造一个窗口。</para>
    /// <para><c>null</c> 的语义是"内核没有这个导出"（旧版 FFF.Native），不是"当前无元数据"——
    /// 后者内核会明确回 <see cref="DynamicMetadataSource.Bitstream"/>。</para></summary>
    internal static string FormatDynamicMetadataSource(DynamicMetadataSource? source) => source switch
    {
        DynamicMetadataSource.Bitstream => LanguageManager.T("MediaInfo_DmBitstream"),
        DynamicMetadataSource.Injected => LanguageManager.T("MediaInfo_DmInjected"),
        _ => LanguageManager.T("MediaInfo_DmUnsupported"),
    };

    public void Clear() => _text.Text = LanguageManager.T("MediaInfo_Empty");

    /// <param name="dynamicMetadataSource">当前 HDR 动态元数据的来源读数（内核 <c>FFF3FP_GetHdrMetadataSource</c>）。
    /// null 表示内核不支持该导出。非 HDR 素材不显示这一行。</param>
    public void ShowMediaInfo(EngineMediaInfo? m, DynamicMetadataSource? dynamicMetadataSource = null)
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
        // 动态元数据只对 HDR 有意义（SDR 素材内核不会评估注入表），来源读数直接问内核：
        // 注入成功后内核自己会把这里翻成 Injected，"注入状态下默认切换过来"不需要 UI 记账。
        if (m.IsHdr)
            sb.Append("  ").Append(L("MediaInfo_DmSource")).Append(": ")
              .AppendLine(FormatDynamicMetadataSource(dynamicMetadataSource));
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
