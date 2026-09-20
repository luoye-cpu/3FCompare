using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using _3FCompare.App;
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
    private float[] _heat = Array.Empty<float>();
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
            // 改为 **每网格行一次** TryReadPixelRegion 批量读回整行（≤ MaxCellsY 次），
            // 再在托管侧按列取样：回读次数从 ~2592 降到 ~27，托管缓冲也只有一行宽（有界）。
            // 两路的后台缓冲尺寸可能不同（窗口大小不同）⇒ 各自算各自的映射与行宽。
            var rtA = sa.ReadRenderTargetInfo(out var infoA) ? infoA : (RenderTargetInfo?)null;
            var rtB = sb.ReadRenderTargetInfo(out var infoB) ? infoB : (RenderTargetInfo?)null;
            var canBatch = rtA is not null && rtB is not null;

            // 行缓冲按需求增长后复用：一次重采样内所有行共用，避免每行各分配一次。
            float[]? rowA = null, rowB = null;
            // 列坐标只与 x 有关（与行无关）⇒ 整次重采样只需算一次，
            // 放在循环里会白白算 96 × _cellsY 次换算。
            var colA = new int[CellsX];
            var colB = new int[CellsX];
            for (var x = 0; x < CellsX; x++)
            {
                var px = (int)((x + 0.5) / CellsX * w);
                colA[x] = MapColumnX(rtA, px, w, h);
                colB[x] = MapColumnX(rtB, px, w, h);
            }

            for (var y = 0; y < _cellsY; y++)
            {
                var py = (int)((y + 0.5) / _cellsY * h);

                if (!canBatch)
                {
                    // 拿不到渲染目标诊断信息（演示模式）时无法保证行宽落在后台缓冲内，
                    // 退回改动前的逐点读取 —— 该路径没有 GPU 同步，2592 次也很廉价。
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
                    continue;
                }

                // 每网格行一次批量回读（横带高度 1），返回该带在后台缓冲里的起始 X
                var okA = TryReadRow(sa, rtA!.Value, ref rowA, colA, py, w, h, out var originA);
                var okB = TryReadRow(sb, rtB!.Value, ref rowB, colB, py, w, h, out var originB);

                for (var x = 0; x < CellsX; x++)
                {
                    var idx = y * CellsX + x;
                    if (!okA || !okB)
                    {
                        heat[idx] = -1f;
                        continue;
                    }
                    Sample(rowA!, colA[x] - originA, out var ar, out var ag, out var ab);
                    Sample(rowB!, colB[x] - originB, out var br, out var bg, out var bb);
                    var d = Delta(ar, ag, ab, br, bg, bb);
                    heat[idx] = d;
                    if (d >= 0.02f) diffCount++;
                }
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

    /// <summary>批量读回"第 py 行"覆盖到的那一条后台缓冲横带（高度 1）。
    /// <para>进入本方法前调用方已确保 <paramref name="rt"/> 有效：列坐标由
    /// <see cref="PixelReadback.SourceToBackBuffer"/> 钳制在交换链内，横带不会越界。</para>
    /// <returns>false 表示整行读回失败（调用方按采样失败记 -1）。</returns></summary>
    private static bool TryReadRow(IPlayerSession session, RenderTargetInfo rt,
        ref float[]? buffer, int[] columns, int py, int vw, int vh, out int originX)
    {
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var c in columns)
        {
            if (c < min) min = c;
            if (c > max) max = c;
        }
        originX = min;
        // 带内所有列的 Y 相同（同一个 py），取任一列换算出的 Y 即可
        var by = PixelReadback.SourceToBackBuffer(columns[0], py, vw, vh, rt)?.Y ?? py;
        var width = max - min + 1;
        if (width <= 0) return false;

        var need = width * 4;
        if (buffer is null || buffer.Length < need) buffer = new float[need];
        return session.TryReadPixelRegion(min, by, width, 1, buffer, out _);
    }

    /// <summary>从行缓冲取某列（已相对行首）的 RGB 分量。</summary>
    private static void Sample(float[] row, int offset, out float r, out float g, out float b)
    {
        var i = offset * 4;
        if (i + 2 >= row.Length) { r = g = b = 0; return; }
        r = row[i];
        g = row[i + 1];
        b = row[i + 2];
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
