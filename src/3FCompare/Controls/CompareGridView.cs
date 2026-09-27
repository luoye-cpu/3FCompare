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
/// <param name="Align">对齐模式。<see cref="CompareAlign.Pixel"/> 时 <see cref="CropOf"/> 会按各路
/// 源尺寸反算出<b>逐路不同</b>的裁剪区间（见 <see cref="CompareCropPlanner.AlignedCrop"/>）。</param>
public sealed record CellMagnify(double Zoom, double CropX, double CropY, IReadOnlyList<PixelSize> Sources,
    CompareAlign Align = CompareAlign.Relative)
{
    /// <summary>第 <paramref name="index"/> 路的源画面尺寸；越界返回 (0,0)（未知）。</summary>
    public PixelSize SourceOf(int index)
        => index >= 0 && index < Sources.Count ? Sources[index] : default;

    /// <summary>第 <paramref name="index"/> 路的<b>有效放大倍数</b>。
    ///
    /// <para><b>相对对齐</b>：恒等于 <see cref="Zoom"/>（既有行为逐字不变）。</para>
    ///
    /// <para><b>像素级对齐</b>：按该路源宽与基准源宽之比放大（<see cref="CompareCropPlanner.AlignedZoom"/>）
    /// —— 只挪裁剪位置改变不了"格恒好露出 1/z"这个构造，必须让各路的有效 z 不同，
    /// 才能让各路露出<b>相同源像素尺寸</b>的区域。</para>
    ///
    /// <para><b>退化</b>：该路源未知、或全部源都未知（演示模式 / 纯音频 ⇒ 没有基准）
    /// 时返回共用 <see cref="Zoom"/>。</para></summary>
    public double ZoomOf(int index)
    {
        if (Align != CompareAlign.Pixel) return Zoom;

        var src = SourceOf(index);
        if (src.Width <= 0 || src.Height <= 0) return Zoom;

        var min = CompareCropPlanner.MinSourceSize(Sources);
        if (min.Width <= 0) return Zoom;

        return CompareCropPlanner.AlignedZoom(Zoom, src.Width, min.Width);
    }

    /// <summary>第 <paramref name="index"/> 路应当露出的区间左上角（源画面归一化坐标）。
    ///
    /// <para><b>相对对齐</b>：所有路共用 <see cref="CropX"/> / <see cref="CropY"/> ⇒
    /// 露出各自画面中相同的相对位置（既有行为，逐字不变）。</para>
    ///
    /// <para><b>像素级对齐</b>：各路露出区间的<b>像素起点相同、像素尺寸相同</b>
    /// （<see cref="CompareCropPlanner.AlignedCrop"/>）。此时 <see cref="CropX"/> /
    /// <see cref="CropY"/> 的语义从"归一化位置"变为"视口在可平移范围内的相对位置"
    /// （0=贴左/上，1=贴右/下）—— 必须经本方法取值，直接读字段会得到错误的区间。</para>
    ///
    /// <para><b>退化</b>：该路源未知，或所有路源都未知（⇒ 没有基准尺寸）时逐路退回共用值 ——
    /// 此时像素级对齐无从计算，退回相对对齐是唯一安全的选择（不会露出错误的块）。</para></summary>
    public (double CropX, double CropY) CropOf(int index)
    {
        if (Align != CompareAlign.Pixel) return (CropX, CropY);

        var src = SourceOf(index);
        if (src.Width <= 0 || src.Height <= 0) return (CropX, CropY);

        var min = CompareCropPlanner.MinSourceSize(Sources);
        if (min.Width <= 0 || min.Height <= 0) return (CropX, CropY);

        return (CompareCropPlanner.AlignedCrop(CropX, Zoom, src.Width, min.Width),
                CompareCropPlanner.AlignedCrop(CropY, Zoom, src.Height, min.Height));
    }
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
    /// <summary>格 → 路的映射（<c>route[格号] = 路号</c>）；null = 恒等（第 i 格显示第 i 路）。
    /// 见 <see cref="CellRoute"/>。</summary>
    private int[]? _cellRoute;
    /// <summary>子窗口放大参数；null = 关闭（默认）。</summary>
    private CellMagnify? _cellMagnify;
    /// <summary>最近一次 Arrange 真正落位（占格）的路号集合，由 <see cref="PublishVisibleRoutes"/> 整体替换。
    /// 初值取"全部可见"以外的空集无所谓：第一次 Arrange 一定会发布真值。</summary>
    private HashSet<int> _visibleRoutes = new();
    /// <summary>上次发布时的格数（路数）。与 <see cref="_visibleRoutes"/> 一起构成发布门：
    /// 只比集合会漏掉"格数不变但多了新路"的情形（新路默认启用）。</summary>
    private int _publishedLaneCount = -1;

    public event EventHandler? SelectionChanged;
    /// <summary>新表面创建时触发（MainWindow 订阅以绑定鼠标/缩放事件）。</summary>
    public event Action<PlayerSurface>? SurfaceCreated;
    /// <summary>真正占格的路号集合变了（= 当前视图模式下"看得见"的那几路变了）。
    /// <para>宿主据此决定哪些会话启用、哪些停用，见 <c>SyncController.SetActiveRoutes</c>。
    /// 触发点在 Arrange 内，故宿主必须<b>延后一拍</b>再动会话，别在布局里 Pause/Seek。</para></summary>
    public event Action? VisibleRoutesChanged;

    /// <summary>当前真正占格的路号（只读；未占格 = 本模式下不显示，应当停用）。
    /// 由 <see cref="PublishVisibleRoutes"/> 维护，外部只读。</summary>
    public IReadOnlySet<int> VisibleRouteIndices => _visibleRoutes;

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
            // 单屏模式下"谁占格"就由选中路决定 ⇒ 必须重排，否则占格集合不发布、
            // 新选中的那一路还停在停用态（网格模式下选中只改边框，不重排）。
            if (_singleView) Relayout();
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

    /// <summary>格 → 路的映射。<c>null</c>（默认）或空数组时为<b>恒等</b>：第 i 格显示第 i 路。
    ///
    /// <para><b>语义</b>：<c>route[cellIndex] = routeIndex</c>。长度必须等于
    /// <see cref="CellOverride"/> 的格数，且必须是 <c>0..路数-1</c> 的一个<b>排列</b>
    /// （重复路号会让某一路显示两格、另一路不显示 —— 调用方负责保证，本类按"未出现的路隐藏"处理）。</para>
    ///
    /// <para><b>为什么放在这里而不是 MainWindow 里做"格置换"</b>：格置换（交换 cells[0]/cells[1]
    /// 的矩形）只能表达"交换"，表达不了"第 0 格显示第 3 路"。而命中测试、裁剪下发、放大源尺寸
    /// 都要知道"这一格到底是哪一路" —— 若只有格置换，它们只能反推，两处公式迟早漂移。
    /// 把映射集中在本类，四个消费方（Measure / Arrange / 命中 / 裁剪）共用同一份数据。</para>
    ///
    /// <para><b>只在对比模式下生效</b>（与 <see cref="CellOverride"/> 同条件）：均匀网格没有"格"的
    /// 概念，也没有对应的路号语义。</para></summary>
    public int[]? CellRoute
    {
        get => _cellRoute;
        set
        {
            _cellRoute = value is { Length: > 0 } ? value : null;
            InvalidateMeasure();
        }
    }

    /// <summary>当前真正生效的格表：单屏模式下不生效（单屏由 <see cref="SingleView"/> 独占），
    /// 空数组等同于"未设置"。集中在此判断，避免 Measure / Arrange 两处条件漂移。</summary>
    private CellRect[]? ActiveCells =>
        !_singleView && _cellOverride is { Length: > 0 } cells ? cells : null;

    /// <summary>当前生效的格→路映射；未处于对比模式或映射非法时按<b>恒等</b>处理。
    /// 与 <see cref="ActiveCells"/> 同条件，两处判断不会漂移。</summary>
    private int[]? ActiveRoute =>
        ActiveCells is null ? null : _cellRoute is { Length: > 0 } r ? r : null;

    /// <summary>第 <paramref name="routeIndex"/> 路应显示在哪一格；不在映射里返回 -1（该路隐藏）。
    ///
    /// <para><b>这是"路号 → 格号"的反向查询</b>：排版按路循环（每个 <see cref="PlayerSurface"/>
    /// 都要被安排一次），而映射是按格存的，故需要反查。</para>
    ///
    /// <para>映射为 null（恒等）时返回 <paramref name="routeIndex"/> 本身 ——
    /// 于是调用方只需判 <c>&lt; 0</c>，不必区分"没映射"与"映射里没有这一路"，
    /// 恒等路径与改动前<b>逐字一致</b>。</para></summary>
    internal int CellIndexOfRoute(int routeIndex)
    {
        var route = ActiveRoute;
        if (route is null) return routeIndex;
        for (var cell = 0; cell < route.Length; cell++)
            if (route[cell] == routeIndex) return cell;
        return -1;
    }

    /// <summary>第 <paramref name="cellIndex"/> 格显示的是第几路；越界或映射非法时返回 -1。
    /// 命中测试与裁剪下发按"格"工作，需要这个方向。</summary>
    internal int RouteIndexOfCell(int cellIndex)
    {
        var route = ActiveRoute;
        if (route is null) return cellIndex;
        return cellIndex >= 0 && cellIndex < route.Length ? route[cellIndex] : -1;
    }

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

    /// <summary>对比模式下把每一格先涂黑，作为子 HWND 之下的底。</summary>
    /// <para>放大后的内容框可以小于格（宽高比失配时 fit/zoom 只占格的一部分），格内因此露出一圈
    /// "不属于任何画面"的区域。它落在子 HWND 之下，谁在后面就是谁：不画就是主题底色，浅色主题下
    /// 是一块刺眼的白，读起来像"这块没渲染出来"；画成黑则与内核自己那圈 letterbox 同色，
    /// 用户读到的是"画面自带黑边"——与 z=1 时的观感语法连续。子 HWND 永远盖在本控件的绘制之上
    /// （airspace），所以有画面的部分不受影响。</para>
    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        if (ActiveCells is not { } cells) return;
        var w = Bounds.Width;
        var h = Bounds.Height;
        foreach (var c in cells)
            dc.FillRectangle(Brushes.Black, new Rect(
                c.X * w, c.Y * h, Math.Max(0, c.Width * w), Math.Max(0, c.Height * h)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ActiveCells is { } cells)
        {
            // 对比模式：每格尺寸不同，不能再用"一个 cellSize 量测所有路"。
            // 超出格数的路（或不在格→路映射里的路）按最后一格量测
            //（它们会被 Arrange 隐藏，量测值只用于 DesiredSize）。
            for (var i = 0; i < _surfaces.Count; i++)
            {
                var cell = CellIndexOfRoute(i);
                // 上界也要兜：CellIndexOfRoute 在映射为恒等时直接返回路号，故"路数 > 格数"
                // （4 路用 AB / AB 竖分）时它会给出 >= cells.Length 的值。只判 < 0 会让 Measure
                // 先于 Arrange 崩掉（ArrangeOverride 一直是双侧守卫，见下方 cellIndex >= cells.Length）。
                var c = cells[cell >= 0 && cell < cells.Length ? cell : cells.Length - 1];
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
        if (_surfaces.Count == 0)
        {
            // 0 路也要发布空集：在发布前 return 会让 _visibleRoutes 停留在上一次会话的脏值，
            // 而"关会话 → 重开"正是靠这条路把新 slot 拉回正确启用态的时机。
            PublishVisibleRoutes(new HashSet<int>());
            return finalSize;
        }

        // 本拍真正占格的路号在两条分支里各自收集，末尾统一发布（见 PublishVisibleRoutes）。
        // 用新集合 + 交换而不是原地改：发布事件时读到的必须是"已经落地的那一份"。
        var vis = new HashSet<int>();

        if (ActiveCells is { } cells)
        {
            // 对比模式：把 0~1 的格表乘上容器实际尺寸落位。
            // 路 i 落到哪一格由 CellIndexOfRoute 决定（格→路映射非空时不是"第 i 路占第 i 格"）。
            for (var i = 0; i < _surfaces.Count; i++)
            {
                var s = _surfaces[i];
                var cellIndex = CellIndexOfRoute(i);
                if (cellIndex < 0 || cellIndex >= cells.Length)
                {
                    // 格数 < 路数（如 4 路用 AB）、或该路不在格→路映射里：隐藏。
                    // 与均匀路径一样必须显式 IsVisible：退出对比模式后由均匀路径重新置位。
                    s.IsVisible = false;
                    s.Arrange(new Rect(0, 0, 0, 0));
                    continue;
                }

                s.IsVisible = true;
                vis.Add(i);
                var c = cells[cellIndex];
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
                    // 像素级对齐下每路的裁剪区间不同 ⇒ 必须逐路取 CropOf，
                    // 直接用 magnify.CropX/Y 会让所有路露出相同的相对位置（对齐失效）。
                    var crop = magnify.CropOf(i);
                    r = CompareCropPlanner.Magnify(
                        r, magnify.ZoomOf(i), crop.CropX, crop.CropY, src.Width, src.Height).WindowDip;
                }

                s.Arrange(r);
            }
            PublishVisibleRoutes(vis);
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
            vis.Add(i);
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
        PublishVisibleRoutes(vis);
        return finalSize;
    }

    /// <summary>发布"本拍真正占格的路号"：集合没变就不发事件，也不换引用。
    ///
    /// <para><b>为什么可见性在这里定、而不是在"切模式 / 换映射 / 加减路 / 单屏"各处各算一遍</b>：
    /// 那四条路径最终都要落到本方法决定哪几路真的占格、哪几路被摆成 0×0，
    /// 在外面各算一遍等于把同一条规则写五份，漏一份的表现就是"某条路径下多余路还在解码"。
    /// 本方法只负责<b>告知</b>变了这件事，动作由宿主（<c>MainWindow</c>）延后一拍去做 ——
    /// 在 Arrange 里直接 Pause/Seek 会话会把内核调用塞进布局过程。</para></summary>
    private void PublishVisibleRoutes(HashSet<int> next)
    {
        // 只比索引集不够：2 格对比模式下**新拖入的第 3 路**不改变 {0,1}，事件就不发，
        // 于是那条新 slot 以 SyncSlot.Active 的默认值 true 落地、被一起解码+呈现
        // ——正是"路启用态"这次要消除的浪费。路数本身也是这条判据的一部分。
        if (_visibleRoutes.SetEquals(next) && _publishedLaneCount == _surfaces.Count) return;
        _visibleRoutes = next;
        _publishedLaneCount = _surfaces.Count;
        VisibleRoutesChanged?.Invoke();
    }

    private void Relayout() => InvalidateMeasure();
}
