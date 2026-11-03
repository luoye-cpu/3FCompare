using System;

using _3FCompare.Themes;
using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.Localization;
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
        // 换会话 ⇒ 上一次读回的邻域作废：它来自另一条会话的后台缓冲，
        // 拿它顶新会话的读数就是"读了一个不存在的像素"。
        _nbValid = false;
        RefreshCoord();
        _value.Text = string.Empty;
    }

    /// <summary>两次 GPU 回读之间的最小间隔（约 30Hz）。</summary>
    private const long MinReadIntervalMs = 33;
    private long _lastReadTicks;

    /// <summary>探针邻域边长（后台缓冲像素）。
    /// <para>为什么一下读 9×9 而不是 1×1：两条路的代价<b>一样</b> —— 内核
    /// <c>ReadVideoPixel</c> / <c>ReadVideoPixelRegion</c> 都是"建一块 staging 纹理 →
    /// <c>CopySubresourceRegion</c> → <c>Map</c>"，<c>Map</c> 才是强制的管线同步，
    /// 9×9 的拷贝量（1.3 KB）在它面前可以忽略。多读回来的邻域于是变成了缓存：画面静止时
    /// 光标仍在这一块里移动就直接取缓冲，不再发那次 GPU 同步（见 <see cref="TrySample"/>）。</para></summary>
    private const int Neighborhood = 9;

    /// <summary>最近一次回读的邻域（RGBA float，行主序）以及它在后台缓冲里的位置。
    /// <see cref="_nbValid"/> 为 false 表示无效（读回失败 / 换了会话）。</summary>
    private float[]? _neighborhood;
    private int _nbLeft, _nbTop;
    private uint _nbDepth;
    private bool _nbValid;
    /// <summary>缓存键：读回时的渲染目标信息与呈现帧号。缩放/平移/改窗口会改绘制矩形，
    /// seek/步进会改帧号 —— 任一变化都必须视为未命中，否则会把旧画面上的像素当成本帧读数。</summary>
    private RenderTargetInfo _nbTarget;
    private long _nbFrameIndex;

    /// <summary>最近一次邻域的 RGB 均值（诊断/自测用，不上 UI：显示口径仍是中心像素）。
    /// 留着这个读数是为了让"邻域"这件事有可观测面 —— 它是判断"探针读到的到底是
    /// 一个孤立坏点还是一片内容"的唯一依据。</summary>
    internal (float R, float G, float B) LastNeighborhoodMean { get; private set; }

    /// <summary>读取 (x,y) 像素（颜色管理前码值）。失败显示读取失败。</summary>
    public void UpdatePoint(int x, int y)
    {
        if (_session is null) return;

        // 回读是**同步** P/Invoke：GPU staging 回读会强制管线同步。
        // 指针移动事件可达每帧数十次，逐个回读会白白拖慢渲染，而人眼分辨不出
        // 30Hz 以上的数值刷新 —— 所以只对回读节流（docs/15 六章）。
        var now = Environment.TickCount64;
        if (now - _lastReadTicks < MinReadIntervalMs) return;
        _lastReadTicks = now;

        try
        {
            // 展示给用户的仍是**片源像素坐标**（"源第几行第几列"才有意义），
            // 但内核回读的坐标域是后台缓冲 ⇒ 读取前必须换算
            // （PixelReadback.SourceToBackBuffer）。漏掉这步在窗口小于片源时
            // 会读到偏右下的错误位置。
            if (TrySample(x, y, out var s, out var hdr))
            {
                _last = s;
                _lastX = x; _lastY = y;
                _hasSample = true;
                RefreshCoord();
                // 8-bit 读数必须走跨域统一口径：SDR 回读是 gamma 编码、HDR 回读是线性 scRGB，
                // 直接 v*255 会让两者不可比（docs/45 P0-4；口径定义见 ColorNormalizer）。
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

    /// <summary>取探针读数：画面静止时优先命中上一次 9×9 邻域缓存，否则发一次整块回读。
    /// <para><paramref name="hdr"/> 一并带出，省掉调用方为算 8-bit 再做一次
    /// <c>ReadRenderTargetInfo</c>（那是同一个必须发出去的 P/Invoke）。</para></summary>
    private bool TrySample(int srcX, int srcY, out PixelSample sample, out bool hdr)
    {
        sample = default;
        hdr = false;
        var session = _session;
        if (session is null) return false;

        var media = session.ReadMediaInfo();
        // 拿不到诊断信息（演示模式 / 旧内核）时既无从换算坐标，也无从保证邻域落在
        // 后台缓冲内 ⇒ 退回单像素读取，与 PixelReadback.TryReadPixelAtSource
        // 的兜底分支一致（那里也是"读不到就按原坐标单点读"）。
        if (media is null || media.VideoWidth <= 0 || media.VideoHeight <= 0 ||
            !session.ReadRenderTargetInfo(out var rt) ||
            rt.SwapWidth == 0 || rt.SwapHeight == 0)
        {
            return session.TryReadPixelAtSource(srcX, srcY, out sample);
        }
        hdr = rt.Hdr;

        var mapped = PixelReadback.SourceToBackBuffer(srcX, srcY, media.VideoWidth, media.VideoHeight, rt);
        if (mapped is null) return false;
        var bx = mapped.Value.X;
        var by = mapped.Value.Y;

        // 缓存只在**画面静止**时复用：暂停时后台缓冲不再变化，从邻域里取的像素与重读逐位相同。
        // 播放中必须每次重读 —— 复用会把读数冻结在上一次回读那一刻，那是可见的行为改变。
        // 帧号与渲染目标信息一起进缓存键：暂停后 seek/步进会换帧，缩放平移/改窗口会换
        // 绘制矩形，两者都让旧邻域失效。
        // 快照取不到（会话正在销毁 / 内核不支持）按"未静止"处理：宁可多读一次也不复用。
        var still = false;
        var frameIndex = -1L;
        try
        {
            var snapshot = session.ReadSnapshot();
            still = snapshot.State is PlayerState.Paused or PlayerState.Ready;
            frameIndex = snapshot.FrameIndex;
        }
        catch { /* 快照不可读 ⇒ 不复用缓存 */ }

        if (still && _nbValid && _neighborhood is not null &&
            _nbFrameIndex == frameIndex && _nbTarget == rt &&
            bx >= _nbLeft && bx < _nbLeft + Neighborhood &&
            by >= _nbTop && by < _nbTop + Neighborhood)
        {
            var i = ((by - _nbTop) * Neighborhood + (bx - _nbLeft)) * 4;
            sample = new PixelSample(_neighborhood[i], _neighborhood[i + 1],
                _neighborhood[i + 2], _neighborhood[i + 3], _nbDepth);
            return true;
        }

        // 邻域以光标为中心，贴边时整体内移（与放大镜同一钳制方式，见 MagnifierOverlay）。
        var maxX = Math.Max(0, (int)rt.SwapWidth - Neighborhood);
        var maxY = Math.Max(0, (int)rt.SwapHeight - Neighborhood);
        var left = Math.Clamp(bx - Neighborhood / 2, 0, maxX);
        var top = Math.Clamp(by - Neighborhood / 2, 0, maxY);

        var need = Neighborhood * Neighborhood * 4;
        if (_neighborhood is null || _neighborhood.Length < need) _neighborhood = new float[need];

        if (!session.TryReadPixelRegion(left, top, Neighborhood, Neighborhood, _neighborhood, out var depth))
        {
            // 失败即作废缓存：下一次照常重读，不会把一块旧数据当成本次的邻域用。
            _nbValid = false;
            return false;
        }
        _nbLeft = left;
        _nbTop = top;
        _nbDepth = depth;
        _nbTarget = rt;
        _nbFrameIndex = frameIndex;
        _nbValid = true;

        var idx = ((by - top) * Neighborhood + (bx - left)) * 4;
        sample = new PixelSample(_neighborhood[idx], _neighborhood[idx + 1],
            _neighborhood[idx + 2], _neighborhood[idx + 3], depth);
        RecordNeighborhoodMean();
        return true;
    }

    /// <summary>算一遍邻域的 RGB 均值（只在上面的缓存未命中、刚回读完时算一次）。</summary>
    private void RecordNeighborhoodMean()
    {
        var buffer = _neighborhood;
        if (buffer is null) return;
        var count = Neighborhood * Neighborhood;
        float r = 0, g = 0, b = 0;
        for (var i = 0; i < count; i++)
        {
            r += buffer[i * 4];
            g += buffer[i * 4 + 1];
            b += buffer[i * 4 + 2];
        }
        LastNeighborhoodMean = (r / count, g / count, b / count);
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
