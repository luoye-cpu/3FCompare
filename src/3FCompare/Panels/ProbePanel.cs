using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.App;
using _3FCompare.Controls;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Imaging;

namespace _3FCompare.Panels;

/// <summary>像素探针面板（WinForms ProbePanel 对应）：读取选中表面悬停像素的
/// 颜色管理前码值（float RGBA + 8bit），支持复制 JSON。</summary>
public sealed class ProbePanel : StackPanel
{
    private readonly TextBlock _coord, _value;
    private IPlayerSession? _session;
    private int _lastX, _lastY;
    private PixelSample _last;
    private bool _hasSample;

    public ProbePanel()
    {
        Margin = new global::Avalonia.Thickness(10);
        Spacing = 8;

        _coord = Mk(12);
        _value = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14, FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        }.Themed("AccentTextBrush");
        var hint = Mk(11).Themed("TextMutedBrush");
        hint.Text = LanguageManager.T("Probe_Hint");

        // 图标改矢量（docs/31 阶段 4）。"JSON" 文本刻意保留：本地化表里没有"复制"类键，
        // 而本批约定不动 LanguageManager，可见标签是这里最省事的可发现性来源（零新键）。
        var copy = new Button { Height = 26, HorizontalAlignment = HorizontalAlignment.Left };
        copy.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                AppIcons.Create("Copy", 13),
                new TextBlock { Text = "JSON", VerticalAlignment = VerticalAlignment.Center }
                    .Themed("TextPrimaryBrush"),
            },
        };
        ToolTip.SetTip(copy, "JSON");
        copy.Click += (_, _) => CopyToClipboard();

        var header = new TextBlock
        {
            Text = LanguageManager.T("Probe_Title"), FontSize = 13, FontWeight = FontWeight.Bold,
        };
        Children.Add(header);
        Children.Add(_coord);
        Children.Add(_value);
        Children.Add(hint);
        Children.Add(copy);

        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, p => global::Avalonia.Threading.Dispatcher.UIThread.Post(p.RefreshCoord));
        RefreshCoord();
    }

    private void RefreshCoord() =>
        _coord.Text = _hasSample ? $"X:{_lastX} Y:{_lastY} {_last.BitDepth}bit" : LanguageManager.T("Probe_Coord");

    private static TextBlock Mk(double size) => new TextBlock
    {
        FontSize = size, TextWrapping = TextWrapping.Wrap,
    }.Themed("TextSecondaryBrush");

    public void AttachSession(IPlayerSession? session)
    {
        _session = session;
        _hasSample = false;
        RefreshCoord();
        _value.Text = string.Empty;
    }

    /// <summary>两次 GPU 回读之间的最小间隔（约 30Hz）。</summary>
    private const long MinReadIntervalMs = 33;
    private long _lastReadTicks;

    /// <summary>读取 (x,y) 像素（颜色管理前码值）。失败显示读取失败。</summary>
    public void UpdatePoint(int x, int y)
    {
        if (_session is null) return;

        // TryReadPixel 是**同步** P/Invoke：GPU staging 回读会强制管线同步。
        // 指针移动事件可达每帧数十次，逐个回读会白白拖慢渲染，而人眼分辨不出
        // 30Hz 以上的数值刷新 —— 所以只对回读节流（docs/15 六章）。
        var now = Environment.TickCount64;
        if (now - _lastReadTicks < MinReadIntervalMs) return;
        _lastReadTicks = now;

        try
        {
            // 展示给用户的仍是**片源像素坐标**（"源第几行第几列"才有意义），
            // 但内核 FFF3FP_ReadVideoPixel 的坐标域是后台缓冲 ⇒ 读取前必须换算
            // （PixelReadback.TryReadPixelAtSource）。漏掉这步在窗口小于片源时
            // 会读到偏右下的错误位置。
            if (_session.TryReadPixelAtSource(x, y, out var s))
            {
                _last = s;
                _lastX = x; _lastY = y;
                _hasSample = true;
                RefreshCoord();
                // 8-bit 读数必须走跨域统一口径：SDR 回读是 gamma 编码、HDR 回读是线性 scRGB，
                // 直接 v*255 会让两者不可比（docs/45 P0-4；口径定义见 ColorNormalizer）。
                var hdr = _session.ReadRenderTargetInfo(out var rt) && rt.Hdr;
                ColorNormalizer.ToDisplay8Bit(s.R, s.G, s.B, s.A, s.BitDepth, hdr,
                    out var r8, out var g8, out var b8, out _);
                _value.Text =
                    $"R {s.R:0.000000}  G {s.G:0.000000}\nB {s.B:0.000000}  A {s.A:0.000000}\n" +
                    $"{LanguageManager.T("Probe_Bits")}: {r8},{g8},{b8}";
                return;
            }
        }
        catch { /* 会话未就绪 */ }
        _value.Text = LanguageManager.T("Probe_ReadFail");
    }

    public void CopyToClipboard()
    {
        if (!_hasSample) return;
        // Avalonia 12：IClipboard.SetTextAsync 移除，改用 DataTransfer+SetDataAsync
        var json = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\n  \"x\": {_lastX},\n  \"y\": {_lastY},\n  \"r\": {_last.R:0.######},\n  \"g\": {_last.G:0.######}," +
            $"\n  \"b\": {_last.B:0.######},\n  \"a\": {_last.A:0.######},\n  \"bitDepth\": {_last.BitDepth}\n}}");
        var item = new global::Avalonia.Input.DataTransferItem();
        item.SetText(json);
        var transfer = new global::Avalonia.Input.DataTransfer();
        transfer.Add(item);
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetDataAsync(transfer);
    }
}
