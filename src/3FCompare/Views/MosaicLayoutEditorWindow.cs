using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using _3FCompare.Core.Display;
using _3FCompare.Localization;
using _3FCompare.Themes;

namespace _3FCompare.Views;

/// <summary>自由拼接<b>布局编辑器</b>（二级窗口，模态）。
///
/// <para><b>它解决什么</b>：用户要求"横竖自由组合、允许不对称、任意自由拼接（含 T 形交叉）"，
/// 并要一个编辑器来拼。本窗口提供一个矩形画布：点选一块、按竖切/横切把它切开，
/// 每块可指定显示第几路（槽号），确定后整份布局交给宿主。</para>
///
/// <para><b>为什么是普通模态窗口而不是"覆盖窗口上的内嵌控件"</b>：本项目对
/// <b>覆盖窗口上的交互控件</b>（<c>LayoutOverlayWindow</c> / <c>FloatingTransportWindow</c>
/// 那一类 <c>WS_EX_NOACTIVATE</c> 无边框窗口上的按钮/下拉）**从未实测过**能否正常交互 ——
/// <c>docs/30:189</c> 把它列为 P6 待实测项，代码注释里也记着"这正是当初用『轮换』键代替
/// 自选源的原因"。而设置窗口同款的普通模态窗是**已被日常使用验证**的路径。
/// 故编辑器做成独立模态窗，绕开那块未验证风险面。</para>
///
/// <para><b>几何真源</b>：本窗口只持有宿主的 <see cref="MosaicLayout"/> 值对象，
/// 所有块与线段都向它要 —— 既不在这里重算布局（会与宿主漂移），
/// 也不由宿主把像素矩形塞进来（那会把"哪条线在哪"散成两份实现）。
/// 与 <c>LayoutOverlayWindow</c> 的既有约定一致。</para>
///
/// <para><b>操作即原子切分</b>：用户能做的几何操作只有"选一块 → 竖切/横切一刀"（外加合并）。
/// 任何自由拼接都可由一串这样的切割构造出来，而"切一刀"是最容易理解、也最容易撤销的原子操作。</para>
/// </summary>
public sealed class MosaicLayoutEditorWindow : Window
{
    private readonly MosaicLayout _original;
    private MosaicLayout _layout;
    private readonly int _routeCount;
    private readonly MosaicCanvas _canvas;
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };

    private readonly Button _splitV = new();
    private readonly Button _splitH = new();
    private readonly Button _merge = new();
    private readonly ComboBox _routePick = new();

    /// <summary>确定后的布局（取消时为 null）。</summary>
    public MosaicLayout? Result { get; private set; }

    /// <summary>是否按了「确定」。</summary>
    public bool Accepted { get; private set; }

    private static string T(string key) => LanguageManager.T(key);

    public MosaicLayoutEditorWindow(MosaicLayout current, int routeCount)
    {
        _original = current ?? MosaicLayout.Single();
        _layout = _original;
        _routeCount = Math.Max(1, routeCount);

        Title = T("Layout_Title");
        Width = 760;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;
        // 与设置窗口同一约定：模态、不占任务栏
        ShowInTaskbar = false;

        _canvas = new MosaicCanvas(() => _layout)
        {
            SelectedChanged = OnCanvasSelectionChanged,
            SplitRequested = OnCanvasSplitRequested,
        };

        Content = BuildUi();
        UpdateButtons();
    }

    private Control BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        // ── 底部：确定 / 取消 ──
        var ok = new Button { Content = T("Layout_Confirm"), MinWidth = 88 };
        var cancel = new Button { Content = T("Layout_Cancel"), MinWidth = 88 };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // ── 顶部：说明 + 工具条 ──
        var toolbar = new StackPanel { Spacing = 6 };
        toolbar.Children.Add(new TextBlock
        {
            Text = T("Layout_Hint"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _splitV.Content = T("Layout_SplitV");
        _splitH.Content = T("Layout_SplitH");
        _merge.Content = T("Layout_Merge");
        _splitV.Click += (_, _) => DoSplit(SplitAxis.X);
        _splitH.Click += (_, _) => DoSplit(SplitAxis.Y);
        _merge.Click += (_, _) => DoMerge();
        row.Children.Add(_splitV);
        row.Children.Add(_splitH);
        row.Children.Add(_merge);
        toolbar.Children.Add(row);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row2.Children.Add(new TextBlock
        {
            Text = T("Layout_ThisTileShows"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        for (var i = 0; i < _routeCount; i++)
        {
            // 槽号从 0 起，显示用 1 起（与"第 N 路"的用户口径一致）
            _routePick.Items.Add(LanguageManager.IsEnglish ? $"Route {i + 1}" : $"第 {i + 1} 路");
        }
        _routePick.SelectedIndex = 0;
        _routePick.SelectionChanged += (_, _) => DoSetRoute();
        row2.Children.Add(_routePick);

        var reset = new Button { Content = T("Layout_Reset") };
        reset.Click += (_, _) =>
        {
            _layout = MosaicLayout.Single();
            _canvas.SelectedTile = 0;
            Refresh();
            Say(T("Layout_ResetDone"));
        };
        row2.Children.Add(reset);
        toolbar.Children.Add(row2);

        toolbar.Children.Add(_status);
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);

        // ── 中间：画布 ──
        var canvasHost = new Border
        {
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 10, 0, 0),
            Child = _canvas,
        };
        ThemePalette.SetBrush(canvasHost, Border.BorderBrushProperty, "ControlBorderBrush");
        root.Children.Add(canvasHost);

        return root;
    }

    private void Accept()
    {
        // 至少要有一块（构造上恒真，这里是防御：绝不把非法布局交回宿主）
        if (_layout.Tiles.Length == 0)
        {
            Say(T("Layout_Empty"));
            return;
        }
        Result = _layout;
        Accepted = true;
        Close();
    }

    private int Selected => _canvas.SelectedTile;

    private void DoSplit(SplitAxis axis)
    {
        if (Selected < 0 || Selected >= _layout.Tiles.Length)
        {
            Say(T("Layout_SelectFirst"));
            return;
        }
        var before = _layout.Tiles.Length;
        _layout = _layout.Split(Selected, axis, 0.5);
        if (_layout.Tiles.Length == before)
        {
            Say(T("Layout_SplitNoEffect"));
            return;
        }
        Refresh();
        Say(axis == SplitAxis.X ? T("Layout_SplitV_Done") : T("Layout_SplitH_Done"));
    }

    private void DoMerge()
    {
        if (Selected < 0 || Selected >= _layout.Tiles.Length)
        {
            Say(T("Layout_SelectFirst"));
            return;
        }
        var before = _layout.Tiles.Length;
        _layout = _layout.Merge(Selected);
        if (_layout.Tiles.Length == before)
        {
            // 不静默：说清"为什么没合并"
            Say(T("Layout_MergeSkipped"));
            return;
        }
        _canvas.SelectedTile = Math.Min(Selected, _layout.Tiles.Length - 1);
        Refresh();
        Say(T("Layout_MergeDone"));
    }

    private void DoSetRoute()
    {
        if (Selected < 0 || Selected >= _layout.Tiles.Length) return;
        var route = Math.Clamp(_routePick.SelectedIndex, 0, _routeCount - 1);
        var before = _layout.Tiles[Selected].Route;
        _layout = _layout.WithRoute(Selected, route, _routeCount);
        Refresh();
        if (_layout.Tiles[Selected].Route != before)
            Say(LanguageManager.IsEnglish
                ? $"This tile now shows route {route + 1}."
                : $"这一块已设为第 {route + 1} 路。");
    }

    private void OnCanvasSelectionChanged()
    {
        UpdateButtons();
        if (Selected >= 0 && Selected < _layout.Tiles.Length)
            _routePick.SelectedIndex = Math.Clamp(_layout.Tiles[Selected].Route, 0, _routeCount - 1);
        _canvas.InvalidateVisual();
    }

    /// <summary>画布上双击 = 竖切、右键 = 横切，位置由鼠标落点决定（最快的编辑方式）。</summary>
    private void OnCanvasSplitRequested(Point normalized, SplitAxis axis)
    {
        var idx = Selected;
        if (idx < 0 || idx >= _layout.Tiles.Length) return;
        var t = _layout.Tiles[idx];
        // 容器归一化坐标 → 相对该块的比例
        var frac = axis == SplitAxis.X
            ? (t.Rect.Width > 0 ? (normalized.X - t.Rect.X) / t.Rect.Width : 0.5)
            : (t.Rect.Height > 0 ? (normalized.Y - t.Rect.Y) / t.Rect.Height : 0.5);
        var before = _layout.Tiles.Length;
        _layout = _layout.Split(idx, axis, frac);
        if (_layout.Tiles.Length == before) return;
        Refresh();
        Say(T("Layout_SplitAtClick"));
    }

    private void Refresh()
    {
        _canvas.InvalidateVisual();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var has = Selected >= 0 && Selected < _layout.Tiles.Length;
        _splitV.IsEnabled = has;
        _splitH.IsEnabled = has;
        _merge.IsEnabled = has;
        _routePick.IsEnabled = has;
    }

    private void Say(string text) => _status.Text = text;

    // ────────── 自测探针（判据必须打在生产线路上，不能另写一份重算） ──────────

    internal MosaicLayout CurrentLayout => _layout;

    internal void SelectTileForTest(int index)
    {
        _canvas.SelectedTile = index;
        OnCanvasSelectionChanged();
    }

    internal void SplitForTest(SplitAxis axis) => DoSplit(axis);
    internal void MergeForTest() => DoMerge();

    internal void SetRouteForTest(int route)
    {
        _routePick.SelectedIndex = Math.Clamp(route, 0, _routeCount - 1);
        DoSetRoute();
    }

    internal void AcceptForTest() => Accept();

    /// <summary>状态行文本（断言"不静默"时有内容）。</summary>
    internal string StatusTextForTest => _status.Text ?? string.Empty;

    /// <summary>工具按钮是否可用（自测据此断言"没选块时不给操作"，而不是让用户点了没反应）。</summary>
    internal bool SplitEnabledForTest => _splitV.IsEnabled;

    // ══════════════════════ 画布 ══════════════════════

    /// <summary>拼接画布：按固定宽高比绘制当前块表，点选一块，双击/右键切开。
    ///
    /// <para>绘制内容与拉动下**屏幕上真实呈现的东西**同源（都来自 <see cref="MosaicLayout"/>），
    /// 于是"编辑器里看到的"与"应用后看到的"不会各说一套。</para>
    ///
    /// <para><b>为什么继承 <see cref="Control"/> 而不是 <c>Panel</c> / <c>Border</c></b>：
    /// <list type="bullet">
    /// <item><description><c>Panel.Render</c> 与 <c>Border.Render</c> 在 Avalonia 12 里都是
    /// <b>密封</b>的，无法重写（实测编译错 CS0239）—— 而 <see cref="Control"/> 的 <c>Render</c>
    /// 可以（同工程的 <c>CompareGridView</c> 就是这么做的）。</description></item>
    /// <item><description><c>Control</c> 没有 <c>Background</c>，故留白处的点击拿不到 ——
    /// 但那不影响可用性：<see cref="OnPointerPressed"/> 只在**点在内容矩形内**才处理，
    /// 留白处本来就不该有反应（那里没有任何块）。</description></item>
    /// </list></para></summary>
    private sealed class MosaicCanvas : Control
    {
        private readonly Func<MosaicLayout> _layout;

        internal int SelectedTile { get; set; }

        internal Action? SelectedChanged { get; set; }
        internal Action<Point, SplitAxis>? SplitRequested { get; set; }

        public MosaicCanvas(Func<MosaicLayout> layout)
        {
            _layout = layout;
            Focusable = true;
            ClipToBounds = true;
        }

        /// <summary>画布内的内容矩形（固定 16:9，居中留边）。
        /// <para>不做"读主窗口真实比例"：用户可能在全屏/小窗下调布局，
        /// 让编辑器尺寸跟着主窗跳只会让画布看起来在抖。16:9 与实际对比区接近，
        /// 且块表是归一化的 ⇒ 比例不同只影响观感，不影响几何正确性。</para></summary>
        private Rect ContentRect()
        {
            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w <= 1 || h <= 1) return default;
            const double pad = 8;
            const double aspect = 16.0 / 9.0;
            var availW = Math.Max(1, w - pad * 2);
            var availH = Math.Max(1, h - pad * 2);
            var cw = availW;
            var ch = cw / aspect;
            if (ch > availH) { ch = availH; cw = ch * aspect; }
            return new Rect((w - cw) / 2, (h - ch) / 2, cw, ch);
        }

        internal Point? ToNormalized(Point p)
        {
            var c = ContentRect();
            if (c.Width <= 0 || c.Height <= 0) return null;
            if (p.X < c.X || p.Y < c.Y || p.X > c.Right || p.Y > c.Bottom) return null;
            return new Point((p.X - c.X) / c.Width, (p.Y - c.Y) / c.Height);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var c = ContentRect();
            if (c.Width <= 0 || c.Height <= 0) return;

            var layout = _layout();
            var borderPen = new Pen(Brushes.Gray, 1);
            var splitPen = new Pen(Brushes.White, 2);
            var selectedPen = new Pen(Brushes.DeepSkyBlue, 3);

            for (var i = 0; i < layout.Tiles.Length; i++)
            {
                var t = layout.Tiles[i];
                var r = new Rect(
                    c.X + t.Rect.X * c.Width,
                    c.Y + t.Rect.Y * c.Height,
                    t.Rect.Width * c.Width,
                    t.Rect.Height * c.Height);

                context.FillRectangle(TileBrush(t.Route), r);
                context.DrawRectangle(null, i == SelectedTile ? selectedPen : borderPen, r);

                // 槽号标注（显示为 1 起的"第几路"）
                var text = new FormattedText(
                    (t.Route + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    Math.Clamp(Math.Min(r.Width, r.Height) / 3, 10, 28),
                    Brushes.White);
                context.DrawText(text, new Point(
                    r.X + Math.Max(2, (r.Width - text.Width) / 2),
                    r.Y + Math.Max(2, (r.Height - text.Height) / 2)));
            }

            // 线段：与拉动下同一份几何 ⇒ 编辑器所见即应用后所得。
            // T 形交叉在这里表现为"只覆盖一段的线"，用户能直接看出拼接形态。
            // ⚠ LogicalLines 返回 (Position, Axis, From, To) —— 顺序别记错，
            //    写成 (axis, pos, ...) 会得到"把 SplitAxis 当 double 用"的编译错。
            foreach (var line in layout.LogicalLines())
            {
                var (pos, axis, from, to) = line;
                var a = axis == SplitAxis.X
                    ? new Point(c.X + pos * c.Width, c.Y + from * c.Height)
                    : new Point(c.X + from * c.Width, c.Y + pos * c.Height);
                var b = axis == SplitAxis.X
                    ? new Point(c.X + pos * c.Width, c.Y + to * c.Height)
                    : new Point(c.X + to * c.Width, c.Y + pos * c.Height);
                context.DrawLine(splitPen, a, b);
            }
        }

        /// <summary>按槽号给一个稳定的半透明底色（只为区分块，不表达语义）。</summary>
        private static IBrush TileBrush(int route)
        {
            var palette = new[]
            {
                Color.FromArgb(90, 60, 120, 200),
                Color.FromArgb(90, 200, 120, 60),
                Color.FromArgb(90, 80, 180, 100),
                Color.FromArgb(90, 180, 80, 160),
                Color.FromArgb(90, 200, 180, 60),
                Color.FromArgb(90, 90, 200, 200),
                Color.FromArgb(90, 200, 90, 90),
                Color.FromArgb(90, 140, 140, 220),
            };
            return new SolidColorBrush(palette[((route % palette.Length) + palette.Length) % palette.Length]);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var norm = ToNormalized(e.GetPosition(this));
            if (norm is not { } n) return;

            var layout = _layout();
            var hit = -1;
            for (var i = 0; i < layout.Tiles.Length; i++)
            {
                var r = layout.Tiles[i].Rect;
                if (n.X >= r.X && n.X <= r.X + r.Width && n.Y >= r.Y && n.Y <= r.Y + r.Height)
                {
                    hit = i;
                    break;
                }
            }
            if (hit < 0) return;

            SelectedTile = hit;
            SelectedChanged?.Invoke();

            // 双击 = 竖切、右键 = 横切（位置由落点决定）
            if (e.ClickCount >= 2 || e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            {
                var axis = e.GetCurrentPoint(this).Properties.IsRightButtonPressed ? SplitAxis.Y : SplitAxis.X;
                SplitRequested?.Invoke(n, axis);
            }
            e.Handled = true;
        }
    }
}
