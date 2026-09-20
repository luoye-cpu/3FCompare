using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using _3FCompare.App;
using _3FCompare.Core.Display;
using _3FCompare.Platform;

namespace _3FCompare.Controls;

/// <summary>对比模式的"子窗口放大 + 裁剪"参数（**null = 关闭**，此时 <see cref="CompareGridView"/>
/// 的排版与改动前逐字一致）。
///
/// <para><b>语义</b>：所有路共用同一个 <see cref="Zoom"/>/<see cref="CropX"/>/<see cref="CropY"/>
/// ⇒ 各路露出的是<b>各自画面中相同的相对位置</b>（无缝放大）。</para>
///
/// <para><b>为什么按"源画面归一化坐标"裁剪</b>：内核是 letterbox 适配（<c>CalculateVideoDestination</c>），
/// 格与画面宽高比不同时窗口内会有黑边 ⇒ "窗口坐标比例" ≠ "源画面比例"。按源画面比例裁剪，
/// 宽高比不同的两路（ABC 模式的 A 与 B/C）才会露出同一块内容。每路的源尺寸由
/// <see cref="Sources"/> 提供（索引即路号）。</para></summary>
/// <param name="Zoom">放大倍数，&gt;1 才生效。</param>
/// <param name="CropX">要露出的画面块左上角，源画面归一化 x（0~1），越界会被钳到 <c>1-1/Zoom</c>。</param>
/// <param name="CropY">同上，y。</param>
/// <param name="Sources">每路源画面尺寸（索引 = 路号）。越界或 (0,0) 表示未知 ⇒ 该路按"画面铺满窗口"处理。</param>
public sealed record CellMagnify(double Zoom, double CropX, double CropY, IReadOnlyList<PixelSize> Sources)
{
    /// <summary>第 <paramref name="index"/> 路的源画面尺寸；越界返回 (0,0)（未知）。</summary>
    public PixelSize SourceOf(int index)
        => index >= 0 && index < Sources.Count ? Sources[index] : default;
}

/// <summary>多路对比网格容器（WinForms CompareGridView 对应）。
/// 1~9 路 PlayerSurface 等分布局；布局解析复用 Core.Display.GridLayout；
/// 单屏模式只显示选中路；空态绘制本地化提示。</summary>
public sealed class CompareGridView : Control
{
    private readonly List<PlayerSurface> _surfaces = new();
    private readonly TextBlock _hint;
    private bool _singleView;
    private string _preset = "auto";
    private int _selectedIndex = -1;
    private int? _presetCols, _presetRows;
    /// <summary>非均匀对比格表（归一化 0~1，索引即 A/B/C/D 顺序）；见 <see cref="CellOverride"/>。</summary>
    private CellRect[]? _cellOverride;
    /// <summary>子窗口放大参数；null = 关闭（默认）。</summary>
    private CellMagnify? _cellMagnify;

    public event EventHandler? SelectionChanged;
    /// <summary>新表面创建时触发（MainWindow 订阅以绑定鼠标/缩放事件）。</summary>
    public event Action<PlayerSurface>? SurfaceCreated;

    public IReadOnlyList<PlayerSurface> Surfaces => _surfaces;
    public int Count => _surfaces.Count;
    public bool SingleView
    {
        get => _singleView;
        // 本类没有 Render 重写（画面由子 HWND 绘制），InvalidateVisual() 不产生任何绘制，
        // 只是白白标脏一次 —— 真正的布局变化由 Relayout()（InvalidateMeasure）驱动。
        set { _singleView = value; Relayout(); }
    }
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            if (_selectedIndex >= 0 && _selectedIndex < _surfaces.Count)
                _surfaces[_selectedIndex].Selected = false;
            _selectedIndex = value;
            if (_selectedIndex >= 0 && _selectedIndex < _surfaces.Count)
                _surfaces[_selectedIndex].Selected = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            // 无 Render 重写 ⇒ 不需要 InvalidateVisual（选中边框由 PlayerSurface 自己画）
        }
    }

    public CompareGridView()
    {
        _hint = new TextBlock
        {
            Text = LanguageManager.T("Grid_Empty"),

            FontSize = 16,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        ThemePalette.SetBrush(_hint, TextBlock.ForegroundProperty, "TextMutedBrush");
        VisualChildren.Add(_hint);
        LogicalChildren.Add(_hint);
        // P1-2：弱订阅（静态事件不得强持有控件）
        LanguageManager.SubscribeWeak(this, g => g.OnLanguageChanged(null, EventArgs.Empty));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _hint.Text = LanguageManager.T("Grid_Empty"));

    /// <summary>设置路数（增删 PlayerSurface；新增路继承 RealMode）。</summary>
    public void SetCount(int count, bool realMode)
    {
        count = Math.Clamp(count, 0, 9);
        while (_surfaces.Count < count)
        {
            var s = new PlayerSurface(_surfaces.Count, realMode)
            {
                Width = double.NaN, Height = double.NaN,
            };
                        // 选中由 MainWindow 统一处理
            SurfaceCreated?.Invoke(s);
            _surfaces.Add(s);
            VisualChildren.Add(s);
            LogicalChildren.Add(s);
        }
        while (_surfaces.Count > count)
        {
            var s = _surfaces[^1];
            s.DetachSession();
            _surfaces.RemoveAt(_surfaces.Count - 1);
            VisualChildren.Remove(s);
            LogicalChildren.Remove(s);
            if (_selectedIndex >= _surfaces.Count) SelectedIndex = _surfaces.Count - 1;
        }
        if (_selectedIndex < 0 && _surfaces.Count > 0) SelectedIndex = 0;
        _hint.IsVisible = _surfaces.Count == 0;
        InvalidateMeasure();
    }

    public PlayerSurface? GetSurface(int i) => i >= 0 && i < _surfaces.Count ? _surfaces[i] : null;

    /// <summary>当前网格预设名（"2x1"/"2x2"/"3x3"/"auto"）。
    /// 供自测断言"会话还原是否真的应用了预设"——只看 SingleView 抓不到 2x2/3x3 的还原缺失。</summary>
    internal string Preset => _preset;

    /// <summary>设置网格预设（"2x1"/"2x2"/"3x3"/"auto"）。</summary>
    public void SetGridLayout(string preset)
    {
        // 复用 Core 的双向映射，不在 UI 再写一份 switch——
        // 两份 switch 迟早漂移（docs/14 §1.1：PresetOf 漏了 "2x1" 就是这么来的）。
        var (cols, rows) = GridLayout.OverrideOf(preset);
        _presetCols = cols > 0 ? cols : null;
        _presetRows = rows > 0 ? rows : null;
        _preset = GridLayout.PresetOf(GridLayout.CodeFromPreset(preset, false));
        InvalidateMeasure();
    }

    /// <summary>非均匀对比格表（归一化 0~1，长度 = 该模式的格数，索引即 A/B/C/D 顺序）。
    ///
    /// <para><b>null（默认）时走既有的均匀 N×M 网格逻辑</b>——普通网格浏览路径不经过这里，
    /// 因此本次改动对该路径零影响。仅由 <c>MainWindow</c> 在对比模式（AB/ABC/ABCD）下
    /// 用 <see cref="CompareLayout.ComputeCells"/> 的结果设置；退出对比模式时置回 null。</para>
    ///
    /// <para>与 <see cref="SetGridLayout"/> 的关系：两者互斥使用，同时设置时本属性优先
    /// （对比模式是"非均匀格表"，均匀预设无法表达）。</para>
    ///
    /// <para>长度可以<b>小于</b>路数（例如 4 路用 AB 模式只有 2 格）：此时只显示前
    /// <c>Length</c> 路，其余 <see cref="PlayerSurface"/> 隐藏——与
    /// <see cref="CompareLayout.AvailableModes"/> 允许"4 路选 AB 只看其中 2 路"的规则一致。</para></summary>
    public CellRect[]? CellOverride
    {
        get => _cellOverride;
        set
        {
            // 不做深比较：拖动分割线时每帧都会传入新数组，逐元素比较反而更贵；
            // 标脏一次即可，Avalonia 会把重复的 InvalidateMeasure 合并成一次布局。
            _cellOverride = value;
            InvalidateMeasure();
        }
    }

    /// <summary>当前真正生效的格表：单屏模式下不生效（单屏由 <see cref="SingleView"/> 独占），
    /// 空数组等同于"未设置"。集中在此判断，避免 Measure / Arrange 两处条件漂移。</summary>
    private CellRect[]? ActiveCells =>
        !_singleView && _cellOverride is { Length: > 0 } cells ? cells : null;

    /// <summary>
    /// 子窗口放大 + 裁剪参数。<b>null（默认）时排版与改动前逐字一致</b> —— 普通网格与未启用放大的
    /// 对比模式都不经过放大分支。
    ///
    /// <para>非 null 且 <c>Zoom &gt; 1</c> 时，每一路被排到"格的 z 倍大、并偏移到让目标画面块
    /// 对齐到格"的矩形上（换算见 <see cref="CompareCropPlanner.Magnify"/>）。窗口变大 ⇒ 内核按
    /// 更高分辨率真实重渲染（<c>PrepareScaledVideo</c> 的目标尺寸就是 destination 尺寸），
    /// 越界部分由 <c>MainWindow</c> 经 <c>SetWindowRgn</c> 裁掉。</para>
    ///
    /// <para><b>只在对比模式（<see cref="CellOverride"/> 非 null）下生效</b>：均匀网格没有"格"的概念，
    /// 放大窗口会直接盖住相邻路。</para>
    /// </summary>
    public CellMagnify? CellMagnify
    {
        get => _cellMagnify;
        set
        {
            // 不做深比较：拖动裁剪时每帧都会传入新实例，逐元素比较反而更贵；
            // 标脏一次即可，Avalonia 会把重复的 InvalidateMeasure 合并成一次布局。
            _cellMagnify = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ActiveCells is { } cells)
        {
            // 对比模式：每格尺寸不同，不能再用"一个 cellSize 量测所有路"。
            // 超出格数的路按最后一格量测（它们会被 Arrange 隐藏，量测值只用于 DesiredSize）。
            for (var i = 0; i < _surfaces.Count; i++)
            {
                var c = cells[Math.Min(i, cells.Length - 1)];
                _surfaces[i].Measure(new Size(
                    Math.Max(0, c.Width * availableSize.Width),
                    Math.Max(0, c.Height * availableSize.Height)));
            }
            _hint.Measure(availableSize);
            return availableSize;
        }

        // 计算每格尺寸（而不是用整体 availableSize），避免 DesiredSize 膨胀导致布局异常
        var (cols, rows) = GridLayout.ResolveGrid(_surfaces.Count, _singleView,
            _presetCols ?? 0, _presetRows ?? 0);
        if (_singleView) (cols, rows) = (1, 1);
        var cellW = cols > 0 ? availableSize.Width / cols : availableSize.Width;
        var cellH = rows > 0 ? availableSize.Height / rows : availableSize.Height;
        var cellSize = new Size(Math.Max(0, cellW), Math.Max(0, cellH));

        foreach (var s in _surfaces)
            s.Measure(cellSize);
        _hint.Measure(availableSize);

        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _hint.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        if (_surfaces.Count == 0) return finalSize;

        if (ActiveCells is { } cells)
        {
            // 对比模式：把 0~1 的格表乘上容器实际尺寸落位。
            for (var i = 0; i < _surfaces.Count; i++)
            {
                var s = _surfaces[i];
                if (i >= cells.Length)
                {
                    // 格数 < 路数（如 4 路用 AB）：只显示前 cells.Length 路，其余隐藏。
                    // 与均匀路径一样必须显式 IsVisible：退出对比模式后由均匀路径重新置位。
                    s.IsVisible = false;
                    s.Arrange(new Rect(0, 0, 0, 0));
                    continue;
                }

                s.IsVisible = true;
                var c = cells[i];
                // 与均匀路径同样留 1px 缝隙（选中边框不互相覆盖），并同样钳到 0：
                // 负尺寸 Rect 会让子 HWND 定位到非法尺寸（Win32 只认 16 位有符号），见下方注释。
                var r = new Rect(
                    c.X * finalSize.Width + 1,
                    c.Y * finalSize.Height + 1,
                    Math.Max(0, c.Width * finalSize.Width - 2),
                    Math.Max(0, c.Height * finalSize.Height - 2));

                // 子窗口放大 + 偏移（默认关闭，见 CellMagnify）：把窗口排到格的 z 倍大，
                // 并偏移到让目标画面块正好对齐到格。越界部分由 MainWindow 经 SetWindowRgn 裁掉。
                // 此处**只改 Arrange 不改 Measure**：Measure 只影响 DesiredSize，而 NativeControlHost
                // 的 HWND 位置完全由 Arrange 的 Bounds 决定（Avalonia 的 GetAbsoluteBounds 读 Bounds）；
                // 且既有代码本就"量测格、排布格-2px"，量测与排布尺寸不同是这条路径的常态。
                if (_cellMagnify is { Zoom: > 1.0 } magnify)
                {
                    var src = magnify.SourceOf(i);
                    r = CompareCropPlanner.Magnify(
                        r, magnify.Zoom, magnify.CropX, magnify.CropY, src.Width, src.Height).WindowDip;
                }

                s.Arrange(r);
            }
            return finalSize;
        }

        var (cols, rows) = GridLayout.ResolveGrid(_surfaces.Count, _singleView,
            _presetCols ?? 0, _presetRows ?? 0);
        if (_singleView) (cols, rows) = (1, 1);

        var cw = finalSize.Width / cols;
        var ch = finalSize.Height / rows;
        for (var i = 0; i < _surfaces.Count; i++)
        {
            var visible = !_singleView || i == _selectedIndex;
            var s = _surfaces[i];
            s.IsVisible = visible;
            if (!visible) { s.Arrange(new Rect(0, 0, 0, 0)); continue; }
            var visibleIndex = _singleView ? 0 : i;
            var col = visibleIndex % cols;
            var row = visibleIndex / cols;
            // 每格留 1px 缝隙（选中边框不互相覆盖）。
            // 必须像 MeasureOverride 那样钳到 0：窗口窄于 2×列数时 cw<2 ⇒ cw-2 为负，
            // 负宽 Rect 会让子 HWND 定位到非法尺寸（Win32 只认 16 位有符号，
            // 负数会被解释成巨大的正数），布局直接错乱。
            var cellW = Math.Max(0, cw - 2);
            var cellH = Math.Max(0, ch - 2);
            var r = new Rect(col * cw + 1, row * ch + 1, cellW, cellH);
            s.Arrange(r);
        }
        return finalSize;
    }

    private void Relayout() => InvalidateMeasure();
}
