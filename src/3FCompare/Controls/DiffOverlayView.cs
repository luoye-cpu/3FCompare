using System;

using _3FCompare.Themes;
using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using _3FCompare.Localization;
using _3FCompare.Core.Backend;

namespace _3FCompare.Controls;

/// <summary>差异热力图（WinForms DiffOverlayView 对应）：
/// 对两路会话做 96×N 网格像素采样（颜色管理前码值），归一化 |ΔRGB| 热力着色。
/// 点击重采样。</summary>
public sealed class DiffOverlayView : Control
{
    private const int CellsX = 96;
    /// <summary>纵向格数下限（原实现 <c>Math.Max(8, …)</c>）。</summary>
    private const int MinCellsY = 8;
    /// <summary>纵向格数上限。原实现没有上界：片源宽为 1（或元数据异常）时
    /// <c>96 × h / w / 2</c> 能算出数十万行，<c>new float[CellsX * _cellsY]</c>
    /// 直接上 GB，而 OOM/溢出异常又被下面的 catch 吞掉，表现为"点了没反应"。</summary>
    private const int MaxCellsY = 256;

    /// <summary>单块回读的目标字节数（RGBA float ⇒ 16 B/像素）。块越大 ⇒ GPU 往返越少、
    /// 单次拷贝越大；块内的"垂直空隙"也会被一并拷回（相邻网格行相距几十像素）。
    /// 定这个值的取舍见 <see cref="PlanRowsPerTile"/>。</summary>
    private const long TargetBlockBytes = 16L * 1024 * 1024;

    /// <summary>回读块数目标上限。GPU staging 的 <c>Map</c> 会强制管线同步，往返一次的
    /// 代价（毫秒级）远大于多拷几 MB ⇒ 分块只为了让单块别大到离谱，不是越多越好。</summary>
    private const int MaxBlocks = 4;

    /// <summary>单块超过这个尺寸就退回"一行一块"：8K 呈现分辨率下把 27 行并成一块是
    /// 137 MB 的一次拷贝 + 一块同样大的托管缓冲，已经比 27 次小回读更糟。</summary>
    private const long MaxBlockBytes = 64L * 1024 * 1024;

    /// <summary>块缓冲常驻上限（float 个数，4 M ≈ 16 MB）：更大就不留在字段里，
    /// 用完即弃 —— 高分辨率下不值得为一次点击常驻几十 MB。</summary>
    private const int MaxRetainBlockFloats = 4 * 1024 * 1024;

    private float[] _heat = Array.Empty<float>();
    /// <summary>两路的块缓冲（跨次重采样复用，尺寸不足时才重新分配）。</summary>
    private float[]? _blockA, _blockB;
    private int _cellsY;
    private int _aIndex, _bIndex = 1;
    private double _diffRatio;

    public int AIndex { get => _aIndex; set { _aIndex = value; InvalidateVisual(); } }
    public int BIndex { get => _bIndex; set { _bIndex = value; InvalidateVisual(); } }

    public void SetPair(int a, int b)
    {
        _aIndex = a; _bIndex = b;
        Resample();
    }

    public DiffOverlayView()
    {
        ClipToBounds = true;
        PointerPressed += (_, _) => Resample();
        // 自绘控件不会因主题变化自动失效，必须显式重绘
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>重新采样并重绘（会话由外部提供）。</summary>
    public void Resample(Func<int, IPlayerSession?>? sessionAt = null)
    {
        _sessionAt = sessionAt ?? _sessionAt;
        if (_sessionAt is null) return;
        var sa = _sessionAt(_aIndex);
        var sb = _sessionAt(_bIndex);
        if (sa is null || sb is null) return;

        try
        {
            var media = sa.ReadMediaInfo();
            // 只挡 null 不够：VideoWidth==0（未打开 / 纯音频 / 元数据缺失）会让下面的
            // (double)CellsX * h / w 变成 Infinity；极端宽高比（w==1）则算出数十万行。
            // 两种情况的异常都被本方法末尾的 catch 吞掉 ⇒ 表面上是"点了没反应"。
            var w = (media?.VideoWidth ?? 0) > 0 ? media!.VideoWidth : 1280;
            var h = (media?.VideoHeight ?? 0) > 0 ? media!.VideoHeight : 720;
            _cellsY = Math.Clamp((int)Math.Round((double)CellsX * h / w / 2), MinCellsY, MaxCellsY); // 2:1 采样密度
            var heat = new float[CellsX * _cellsY];
            var diffCount = 0;

            // 逐格回读会串行执行 96 × _cellsY（16:9 约 2592）次同步 P/Invoke，
            // 每次都要等一次 GPU staging 拷贝（强制管线同步）⇒ 点击后 UI 线程卡死数秒。
            // 现行做法：**一次整块区域回读**（纵向若干网格行并成一块，见
            // <see cref="PlanRowsPerTile"/>），再在托管侧按格取样。
            // 两路的后台缓冲尺寸可能不同（窗口大小不同）⇒ 各自算各自的映射与矩形。
            var rtA = sa.ReadRenderTargetInfo(out var infoA) ? infoA : (RenderTargetInfo?)null;
            var rtB = sb.ReadRenderTargetInfo(out var infoB) ? infoB : (RenderTargetInfo?)null;
            var canBatch = rtA is not null && rtB is not null;

            if (!canBatch)
            {
                // 拿不到渲染目标诊断信息（演示模式）时无法保证回读矩形落在后台缓冲内，
                // 退回逐点读取 —— 该路径没有 GPU 同步，2592 次也很廉价。
                for (var y = 0; y < _cellsY; y++)
                {
                    var py = (int)((y + 0.5) / _cellsY * h);
                    for (var x = 0; x < CellsX; x++)
                    {
                        var idx = y * CellsX + x;
                        var px = (int)((x + 0.5) / CellsX * w);
                        if (!sa.TryReadPixelAtSource(px, py, out var a) || !sb.TryReadPixelAtSource(px, py, out var b))
                        {
                            heat[idx] = -1f;
                            continue;
                        }
                        var d0 = Delta(a.R, a.G, a.B, b.R, b.G, b.B);
                        heat[idx] = d0;
                        if (d0 >= 0.02f) diffCount++;
                    }
                }
            }
            else
            {
                diffCount = ResampleBlocks(sa, sb, rtA!.Value, rtB!.Value, w, h, heat);
            }
            _heat = heat;
            _diffRatio = diffCount * 100.0 / (CellsX * _cellsY);
            InvalidateVisual();
        }
        catch
        {
            _heat = Array.Empty<float>();
            InvalidateVisual();
        }
    }

    /// <summary>两像素的平均通道差（与改动前逐格计算式完全一致）。</summary>
    private static float Delta(float ar, float ag, float ab, float br, float bg, float bb)
        => (Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb)) / 3f;

    /// <summary>片源列坐标 → 后台缓冲列坐标（与 <see cref="PixelReadback"/> 同一换算，
    /// 不走 <c>TryReadPixelAtSource</c> 是因为批量路径只需坐标、不需要逐点 P/Invoke）。
    /// 换算不出时退回片源坐标本身 —— 与 <c>TryReadPixelAtSource</c> 的兜底分支一致。</summary>
    private static int MapColumnX(RenderTargetInfo? rt, int px, int vw, int vh)
    {
        if (rt is null) return px;
        // 行号传 0 不影响结果：SourceToBackBuffer 的 X 分量只由 px 决定
        return PixelReadback.SourceToBackBuffer(px, 0, vw, vh, rt.Value)?.X ?? px;
    }

    /// <summary>批量采样：把网格按纵向若干行分块，每块用**一次** <c>TryReadPixelRegion</c>
    /// 读回"覆盖该块全部采样点的包围矩形"，再在内存里按格取中心像素求 Delta。
    /// <para>为什么按行分块而不是横向分段：一格的采样点就是它映射到后台缓冲的那个像素，
    /// 相邻列的采样点几乎相邻，而相邻行的采样点相距几十像素（片源高 / 网格行数）。
    /// 所以"多行并一块"要多拷回行与行之间的空隙，"多列并一块"几乎不浪费 ⇒ 块的形状
    /// 永远是**横向全覆盖、纵向 rows 行**，分块只发生在纵向。</para>
    /// <para>失败即整块记 -1（与改造前"整行读回失败即整行 -1"同一口径，粒度更细）。</para>
    /// <returns>差异格数（|Δ| ≥ 0.02 的格数）。</returns></summary>
    private int ResampleBlocks(IPlayerSession sa, IPlayerSession sb,
        RenderTargetInfo rtA, RenderTargetInfo rtB, int vw, int vh, float[] heat)
    {
        var cellsY = _cellsY;

        // 列坐标只与 x 有关（与行无关）⇒ 整次重采样只需算一次，
        // 放在循环里会白白算 96 × _cellsY 次换算。
        var colA = new int[CellsX];
        var colB = new int[CellsX];
        for (var x = 0; x < CellsX; x++)
        {
            var px = (int)((x + 0.5) / CellsX * vw);
            colA[x] = MapColumnX(rtA, px, vw, vh);
            colB[x] = MapColumnX(rtB, px, vw, vh);
        }

        // 行的缓冲 Y 同理只由 y 决定（列与行无关），一次算完。
        var rowA = new int[cellsY];
        var rowB = new int[cellsY];
        for (var y = 0; y < cellsY; y++)
        {
            var py = (int)((y + 0.5) / cellsY * vh);
            rowA[y] = MapRowY(rtA, py, vw, vh);
            rowB[y] = MapRowY(rtB, py, vw, vh);
        }

        // 横向包围盒（列的最值）与整次重采样不变，纵向随块变。
        var (xA0, xA1) = Span(colA);
        var (xB0, xB1) = Span(colB);
        var (yAll0, yAll1) = Span(rowA);
        var (yAllB0, yAllB1) = Span(rowB);
        var allHeight = Math.Max(yAll1 - yAll0 + 1, yAllB1 - yAllB0 + 1);
        var rowsPerTile = PlanRowsPerTile(Math.Max(xA1 - xA0 + 1, xB1 - xB0 + 1), allHeight, cellsY);

        var bufferA = _blockA;
        var bufferB = _blockB;
        var diffCount = 0;

        for (var y0 = 0; y0 < cellsY; y0 += rowsPerTile)
        {
            var y1 = Math.Min(cellsY - 1, y0 + rowsPerTile - 1);
            var ay0 = int.MaxValue; var ay1 = int.MinValue;
            var by0 = int.MaxValue; var by1 = int.MinValue;
            for (var y = y0; y <= y1; y++)
            {
                if (rowA[y] < ay0) ay0 = rowA[y];
                if (rowA[y] > ay1) ay1 = rowA[y];
                if (rowB[y] < by0) by0 = rowB[y];
                if (rowB[y] > by1) by1 = rowB[y];
            }
            var aw = xA1 - xA0 + 1;
            var ah = ay1 - ay0 + 1;
            var bw = xB1 - xB0 + 1;
            var bh = by1 - by0 + 1;

            var okA = TryReadTile(sa, xA0, ay0, aw, ah, ref bufferA);
            var okB = TryReadTile(sb, xB0, by0, bw, bh, ref bufferB);

            for (var y = y0; y <= y1; y++)
            {
                for (var x = 0; x < CellsX; x++)
                {
                    var idx = y * CellsX + x;
                    if (!okA || !okB)
                    {
                        heat[idx] = -1f;
                        continue;
                    }
                    Sample(bufferA!, ((rowA[y] - ay0) * aw + (colA[x] - xA0)) * 4,
                        out var ar, out var ag, out var ab);
                    Sample(bufferB!, ((rowB[y] - by0) * bw + (colB[x] - xB0)) * 4,
                        out var br, out var bg, out var bb);
                    var d = Delta(ar, ag, ab, br, bg, bb);
                    heat[idx] = d;
                    if (d >= 0.02f) diffCount++;
                }
            }
        }

        // 复用缓冲：小块的留着下次用，几十 MB 的不常驻（8K/4K 下每次点击重新分配更划算）。
        _blockA = Keep(bufferA);
        _blockB = Keep(bufferB);
        return diffCount;
    }

    /// <summary>每块覆盖多少网格行（块 = 纵向连续 rows 行 × 横向全覆盖）。
    /// <para>两个相互拉扯的目标：① 块数少（每次回读都要等一次 GPU staging 拷贝，即强制
    /// 管线同步，代价是毫秒级）；② 单块小（矩形必须覆盖首末行之间的**全部**像素，
    /// 行距是几十像素 ⇒ 并进来越多行，拷回的"空隙"越多，带宽与内存都跟着涨）。</para>
    /// <para>先按 <see cref="TargetBlockBytes"/> 求预算内能并几行，再抬到满足
    /// <see cref="MaxBlocks"/> 所需的最小行数；若抬完单块仍超过 <see cref="MaxBlockBytes"/>
    /// 就退回一行一块（8K 呈现分辨率下的最坏情形：27 行并一块要拷 137 MB，比 27 次
    /// 小回读更糟）。</para></summary>
    private static int PlanRowsPerTile(int rectWidth, int rectHeight, int cellsY)
    {
        if (cellsY <= 1) return 1;
        var perRowBytes = (long)rectWidth * 16;          // RGBA float
        if (perRowBytes <= 0) return 1;
        // 相邻网格行在后台缓冲里的平均间距（矩形高 ≈ cellsY 个间距）
        var spacing = Math.Max(1, rectHeight / cellsY);
        var byBudget = (int)Math.Max(1, TargetBlockBytes / (perRowBytes * spacing));
        var byCallCount = (cellsY + MaxBlocks - 1) / MaxBlocks;
        var rows = Math.Clamp(Math.Max(byBudget, byCallCount), 1, cellsY);
        var blockBytes = (perRowBytes * ((long)rows * spacing + 1));
        return blockBytes > MaxBlockBytes ? 1 : rows;
    }

    /// <summary>把一批块缓冲留下来复用。超过 <see cref="MaxRetainBlockFloats"/> 的丢掉。</summary>
    private static float[]? Keep(float[]? buffer)
        => buffer is not null && buffer.Length <= MaxRetainBlockFloats ? buffer : null;

    /// <summary>一次块回读。缓冲区按需增长后复用；<paramref name="buffer"/> 即使返回 false
    /// 也可能已被换大，调用方应原样留用。</summary>
    private static bool TryReadTile(IPlayerSession session, int x, int y, int width, int height,
        ref float[]? buffer)
    {
        if (width <= 0 || height <= 0) return false;
        // 必须按 long 算：width × height × 4 是 int 运算，极端尺寸下会溢出成负数。
        var floats = (long)width * height * 4;
        if (floats > int.MaxValue) return false;
        var need = (int)floats;
        if (buffer is null || buffer.Length < need) buffer = new float[need];
        return session.TryReadPixelRegion(x, y, width, height, buffer, out _);
    }

    /// <summary>一组坐标的最小/最大值。</summary>
    private static (int Min, int Max) Span(int[] values)
    {
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return (min, max);
    }

    /// <summary>片源行坐标 → 后台缓冲行坐标（与 <see cref="PixelReadback"/> 同一换算）。
    /// 换算不出时退回片源坐标本身 —— 与 <c>TryReadPixelAtSource</c> 的兜底分支一致。</summary>
    private static int MapRowY(RenderTargetInfo? rt, int py, int vw, int vh)
    {
        if (rt is null) return py;
        // 列号传 0 不影响结果：SourceToBackBuffer 的 Y 分量只由 py 决定
        return PixelReadback.SourceToBackBuffer(0, py, vw, vh, rt.Value)?.Y ?? py;
    }

    /// <summary>从块缓冲按 float 下标取 RGB 分量（下标由调用方按行宽算好）。</summary>
    private static void Sample(float[] block, int index, out float r, out float g, out float b)
    {
        if (index < 0 || index + 2 >= block.Length) { r = g = b = 0; return; }
        r = block[index];
        g = block[index + 1];
        b = block[index + 2];
    }

    private Func<int, IPlayerSession?>? _sessionAt;

    /// <summary>设置会话取用器（MainWindow 注入）。</summary>
    public void SetSessionProvider(Func<int, IPlayerSession?> provider) => _sessionAt = provider;

    // ---- 渲染资源缓存：本控件每帧要画最多 96×256 个热力格 + 60 个图例格，
    // 原先每格 new 一个 SolidColorBrush、每个文本 new 一个 FormattedText/Typeface，
    // 单次渲染就是数千次分配（热力图只在点击后重绘，但窗口缩放/拖动会连续触发）。----

    private static readonly Typeface TypeYaHei = new("Microsoft YaHei UI");
    private static readonly Typeface TypeConsolas = new("Consolas");
    // 文本配色取主题令牌（非 static readonly：切主题后要能重取）。
    // 热力色（HeatColor/HeatBrush）**不**令牌化：它编码的是"差异强度"这一数据，
    // 与主题无关，跟着主题翻转会让强弱含义在浅色下反转。
    // 字段初值取令牌表的深色值：首次 EnsureTheme 会按当前变体覆盖，运行时不生效，
    // 只为消除硬编码副本（单一真源见 ThemeTokens）。
    private IBrush _brushWhite = new SolidColorBrush(Color.Parse(ThemeTokens.Get("TextPrimaryBrush").Dark));
    private IBrush _brushDim = new SolidColorBrush(Color.Parse(ThemeTokens.Get("TextMutedBrush").Dark));
    private IBrush _brushLegend = new SolidColorBrush(Color.Parse(ThemeTokens.Get("TextSecondaryBrush").Dark));
    private IBrush _brushAccent = new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark));
    private ThemeVariant? _themeTag;

    private void EnsureTheme()
    {
        if (_themeTag == ActualThemeVariant) return;
        _themeTag = ActualThemeVariant;
        _brushWhite = ThemePalette.Brush(this, "TextPrimaryBrush");
        _brushDim = ThemePalette.Brush(this, "TextMutedBrush");
        _brushLegend = ThemePalette.Brush(this, "TextSecondaryBrush");
        _brushAccent = ThemePalette.Brush(this, "AccentBrush");
        // 文本缓存里握着旧画笔 ⇒ 清空源串触发重建
        _headerText = null; _headerSrc = string.Empty;
        _failText = null; _failSrc = string.Empty;
        _legendText = null; _legendSrc = string.Empty;
        _pctText = null; _pctSrc = string.Empty;
    }

    /// <summary>热力色画刷缓存：把 t∈[0,1] 量化成 256 级。
    /// <para>为什么量化：热力色是连续函数，精确缓存会让字典随画面内容无上限增长；
    /// 量化到 256 级后缓存恒为 256 项（约 6KB），且相邻级色差 ≤1/255 通道值，肉眼不可辨。</para>
    /// <para>失效条件：无。颜色只取决于档位下标，不随时间/尺寸/语言变化，永不失效。</para></summary>
    private const int HeatLevels = 256;
    private static readonly IBrush?[] HeatBrushes = new IBrush?[HeatLevels];

    private static IBrush HeatBrush(float t)
    {
        var level = (int)(Math.Clamp(t, 0f, 1f) * (HeatLevels - 1));
        // 用量化后的档位反算颜色，保证"同档位 ⇒ 同颜色"（用原始 t 会让同档位出现色差）
        return HeatBrushes[level] ??= new ImmutableSolidColorBrush(HeatColor(level / (float)(HeatLevels - 1)).ToUInt32());
    }

    // 文本按**内容**缓存：内容不变（暂停盯帧时是常态）则复用同一个 FormattedText，
    // 省下每帧一次字形排版。内容变化（切语言/换 A-B 路/进度变化）时按字符串比较自动失效。
    private FormattedText? _headerText; private string _headerSrc = string.Empty;
    private FormattedText? _failText; private string _failSrc = string.Empty;
    private FormattedText? _legendText; private string _legendSrc = string.Empty;
    private FormattedText? _pctText; private string _pctSrc = string.Empty;

    private static FormattedText Reuse(FormattedText? cached, string? cachedSrc, string src,
        Typeface typeface, double size, IBrush brush)
        => cached is not null && string.Equals(cachedSrc, src, StringComparison.Ordinal)
            ? cached
            : new FormattedText(src, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        EnsureTheme();

        // 头部
        var header = LanguageManager.Tf("Diff_HeaderFmt", _aIndex, _bIndex);
        _headerText = Reuse(_headerText, _headerSrc, header, TypeYaHei, 13, _brushWhite);
        _headerSrc = header;
        dc.DrawText(_headerText, new Point(10, 8));

        var top = 36.0;
        var bottom = h - 28;
        if (_heat.Length == 0)
        {
            var fail = LanguageManager.T("Diff_SampleFail");
            _failText = Reuse(_failText, _failSrc, fail, TypeYaHei, 12, _brushDim);
            _failSrc = fail;
            dc.DrawText(_failText, new Point(10, top + 4));
            return;
        }

        // 热力网格：青 → 黄 → 红
        var cw = (w - 20) / CellsX;
        var chh = (bottom - top) / _cellsY;
        for (var y = 0; y < _cellsY; y++)
        {
            for (var x = 0; x < CellsX; x++)
            {
                var v = _heat[y * CellsX + x];
                if (v < 0) continue;          // 采样失败跳过
                if (v < 0.02f) continue;      // 弱差异不画（WinForms 同阈值）
                // 必须用不可变画刷：Render 走延迟回放，可变画刷会被"最后一次赋值"覆盖
                dc.DrawRectangle(HeatBrush(v), null,
                    new Rect(10 + x * cw, top + y * chh, Math.Max(1, cw - 0.5), Math.Max(1, chh - 0.5)));
            }
        }

        // 图例 + 差异百分比
        var legendY = h - 20;
        for (var i = 0; i < 60; i++)
            dc.DrawRectangle(HeatBrush(i / 60f), null, new Rect(10 + i * 2, legendY, 2, 8));
        var legend = $"{LanguageManager.T("Diff_LegendWeak")} → {LanguageManager.T("Diff_LegendStrong")}";
        _legendText = Reuse(_legendText, _legendSrc, legend, TypeYaHei, 10, _brushLegend);
        _legendSrc = legend;
        dc.DrawText(_legendText, new Point(140, legendY - 2));
        var pct = LanguageManager.Tf("Diff_PercentFmt",
            (int)Math.Round(_diffRatio * CellsX * _cellsY / 100.0), CellsX * _cellsY, _diffRatio);
        _pctText = Reuse(_pctText, _pctSrc, pct, TypeConsolas, 11, _brushAccent);
        _pctSrc = pct;
        dc.DrawText(_pctText, new Point(10, legendY - 16));
    }

    private static Color HeatColor(float t)
    {
        // 青(0) → 黄(0.5) → 红(1)
        return t < 0.5f
            ? Color.FromRgb(0, (byte)(180 + 75 * t * 2), (byte)(200 * (1 - t * 2)))
            : Color.FromRgb((byte)(255), (byte)(255 * (1 - (t - 0.5f) * 2)), 0);
    }
}
