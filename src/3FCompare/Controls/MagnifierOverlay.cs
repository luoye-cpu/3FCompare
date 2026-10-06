using Avalonia;
using _3FCompare.Themes;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using _3FCompare.Core.Backend;
using _3FCompare.Core.Imaging;

namespace _3FCompare.Controls;

/// <summary>放大镜覆盖层（WinForms MagnifierOverlay 对应）：
/// 192×144（12×12 采样网格 × 16px 块）十字线/对齐网格/4x 坐标注记/光标格描边 +
/// 光标邻域像素放大内容；随光标定位，鼠标不命中（IsHitTestVisible=false）。</summary>
public sealed class MagnifierOverlay : Control
{
    private Point _position = new(-500, -500);
    private bool _visible;
    private IPlayerSession? _session;
    private float[]? _pixelGrid = new float[ZoomGrid * ZoomGrid * 4]; // CPU 邻域像素 (backbuffer space)
    // _pixelGrid 的色彩域信息必须与它**同时**更新：回读数据与域标志错配会让 HDR 帧
    // 被按 SDR 解释（或反之）—— 与"v*255 截断"是同一类成因，只是错在域而不是位深。
    private uint _gridBitDepth;
    private bool _gridHdr;

    private const int Zoom = 4;
    internal const int ZoomGrid = 12; // 12×12 采样网格 → 放大到 16px 块 = 192px

    /// <summary>浮窗宽（= ZoomGrid × 16px 块）。
    /// 过去这里是硬写的 160，而构造函数里 Width 实际是 192 —— 两者不一致，
    /// 且注释也跟着写成"160×120"，按注释改尺寸就会算出错误的采样布局。
    /// 现在尺寸只有这一个来源，Width/Height 直接取它。</summary>
    public const double WidthPx = 192;

    /// <summary>浮窗高（= ZoomGrid × 16px 块），与 <see cref="WidthPx"/> 同为唯一来源。</summary>
    public const double HeightPx = 144;

    public const float ZoomFactor = 4f;

    public MagnifierOverlay()
    {
        IsHitTestVisible = false;
        Width = WidthPx; Height = HeightPx;
        // ⚠ 必须把对齐钉成"左上"（#22）。
        // 默认 HorizontalAlignment/VerticalAlignment 是 Stretch，而本控件又显式设了 Width/Height ——
        // Avalonia 这时会把它**居中**排布：实测 1332×750 的 CenterPanel 下，本控件 Bounds 原点
        // 是 (570, 303)。于是"本控件局部坐标"与"父容器坐标"相差一个常量偏移，
        // 而 Render 的 DrawingContext 原点在本控件左上角 ⇒ 把容器坐标直接当局部坐标画会整体偏移半个面板。
        // 钉成左上后两者原点恒重合（Bounds 原点是 (0,0)，尺寸恒为 192×144），换算有稳定参照；
        // 顺带消除另一个隐患：IsVisible 刚置 true 时布局还没跑到，Bounds 仍是 (0,0,0,0)，
        // 靠 Bounds 反推原点会拿到"过期值"（隐藏态下原点同样是 (0,0)，故钉死后两种情况都正确）。
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        IsVisible = false;
        // 自绘控件不会因主题变化自动失效，必须显式重绘（否则放大镜停在旧配色）
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>自测钩子：采样缓冲是否已就绪（C3 回归断言用。</summary>
    internal bool HasPixelGrid => _pixelGrid is not null;

    /// <summary>呈现状态变化（IsVisible 或 OverlayPosition 变了）。
    /// 放大镜本体是 Control，呈现由独立顶层窗口（<see cref="MagnifierOverlayWindow"/>，
    /// 宿主 MainWindow 持有）承担；本控件只发通知，窗口的显示/隐藏/定位由宿主订阅本事件完成。
    /// 事件在属性变更后同步触发，宿主据此把窗口摆到屏幕坐标。</summary>
    public event EventHandler? PresentationChanged;

    /// <summary>窗口托管模式（P1-4）：本控件作为 <see cref="MagnifierOverlayWindow"/> 的内容独占整窗，
    /// 浮窗的摆放由宿主把整个窗口定位到屏幕完成，本控件恒铺满窗口（局部原点 = 窗口原点）。
    /// <see cref="UpdateAt"/> 在此模式下跳过"本控件局部坐标"换算（Parent 已不是 CenterPanel），
    /// 改在 <see cref="HostContainer"/> 坐标系里做偏移/翻转/钳制，结果记入 <see cref="DesiredPosition"/>。</summary>
    internal bool WindowHosted { get; set; }

    /// <summary>窗口托管模式下做坐标换算/钳制的宿主容器（MainWindow 构造时设为 CenterPanel）。
    /// 本控件被搬进独立窗口后 <c>Parent</c> 不再是 CenterPanel，必须由宿主显式指定。</summary>
    internal Visual? HostContainer { get; set; }

    /// <summary>窗口托管模式下浮窗左上角在 <see cref="HostContainer"/> 坐标系（DIP）里的期望位置
    /// （<see cref="PlaceOverlay"/> 的输出）。宿主订阅 <see cref="PresentationChanged"/> 后读它定位窗口。</summary>
    internal Point DesiredPosition { get; private set; }

    /// <summary>自测钩子：浮窗左上角在本控件**局部坐标系**里的位置（Render 直接用它的那个值）。
    /// 只读暴露给 <c>--selftest</c> 的坐标换算断言；生产代码不读它。</summary>
    internal Point OverlayPosition => _position;

    /// <summary>自测钩子：把采样缓冲置回"读回失败"状态。
    /// 用于复现"换路前读回失败 → 缓冲为 null → 换路后永久空框"这一路径。</summary>
    internal void SimulatePixelReadFailureForSelfTest() => _pixelGrid = null;

    /// <summary>绑定当前选中会话（探针移动时用于读像素）。</summary>
    public void AttachSession(IPlayerSession? session)
    {
        _session = session;
        // 恢复采样缓冲：换路 / 重开时必须重建。若沿用换路前的 null，
        // RefreshPixels 会永久走"引擎未就绪"分支，放大镜只剩空框且再也不恢复。
        _pixelGrid ??= new float[ZoomGrid * ZoomGrid * 4];
        // 换路后后台缓冲尺寸/内容都变了，必须让节流缓存失效，
        // 否则新路第一次移动恰好落在旧的 (gx,gy) 上会被"位置未变"跳过，显示上一路的像素。
        _lastGx = -1;
        _lastGy = -1;
    }

    /// <summary>更新放大镜位置与采样。
    ///
    /// <para><b>坐标契约（#22 修复点）</b>：<paramref name="localInSurface"/> 必须是**相对
    /// <paramref name="source"/>（该路 PlayerSurface）** 的坐标（<c>e.GetPosition(surface)</c>），
    /// 本方法负责把它换算到浮窗自己的坐标系——**不能**由调用方直接传 CenterPanel 的全局坐标
    /// （那会让采样把"第 3 格的坐标"当成"第 1 格后台缓冲的坐标"，docs/15 §2.2），
    /// 也**不能**把 surface 局部坐标当成浮窗坐标（见下方换算）。
    /// <paramref name="renderScaling"/> 用于 DIP → 物理像素。</para>
    ///
    /// <para><b>为什么必须换算（#22：实测比工单描述更严重）</b>：<see cref="_position"/> 是
    /// <b>本控件局部</b>坐标（Render 的 DrawingContext 原点在本控件左上角）。旧实现直接把
    /// surface 局部坐标写进去，于是浮窗被**两重**偏移叠加地摆错：</para>
    /// <list type="number">
    /// <item><description>少算了 surface 在容器里的原点 —— 4 路对比模式下第 4 路约 (933,263) DIP，
    /// 第 1 格只有 (1,1)（工单提到的就是这一重）；</description></item>
    /// <item><description>少算了本控件自身在容器里的原点 —— 显式 Width/Height + 默认 Stretch 对齐
    /// 会被 Avalonia <b>居中</b>排布（实测 1332×750 的 CenterPanel 下是 (570,303)）。
    /// 这一重<b>对所有格都成立</b>，所以"只有第 1 格正确"也不成立：放大镜整体还额外偏了半个面板。</description></item>
    /// </list>
    /// <para>现在：构造函数把对齐钉成左上（消除第 2 重，见那里），本方法用 <c>TranslatePoint</c>
    /// 做第 1 重换算。</para>
    ///
    /// <para><b>缩放 / 滚动偏移已由 TranslatePoint 吸收，不需要再除 zoom</b>：视频的视图变换
    /// （滚轮缩放 / 拖拽平移）与"无缝放大"（<c>CompareGridView.CellMagnify</c>）都只改
    /// <b>Arrange 出来的矩形</b>（引擎侧 viewport 或窗口的 Bounds），不改坐标系的语义。
    /// <c>TranslatePoint</c> 读的正是这条视觉树变换链，放大后的窗口偏移/尺寸已经算在里面，
    /// 故这里既不加滚动偏移、也不除以 zoom（再除一次反而会把浮窗拉回错误位置）。</para>
    /// </summary>
    /// <param name="source">光标所在的那一路表面（坐标换算的源坐标系）。</param>
    public void UpdateAt(Visual source, Point localInSurface, double renderScaling)
    {
        // 窗口托管模式：内容恒铺满窗口（局部原点 = 窗口原点），无需局部坐标换算；
        // 偏移/翻转/钳制改在 HostContainer 坐标系里做，结果经 DesiredPosition 交给宿主摆窗口。
        if (WindowHosted)
        {
            var host = HostContainer;
            var cursorInHost = host is not null ? source.TranslatePoint(localInSurface, host) : null;
            if (cursorInHost is null)
            {
                HideOverlay();
                return;
            }
            var hostSize = (host as Control)?.Bounds.Size ?? default;
            DesiredPosition = PlaceOverlay(cursorInHost.Value, hostSize);
            _position = new Point(0, 0);
            _visible = true;
            IsVisible = true;
            RefreshPixels(localInSurface, renderScaling);
            InvalidateVisual();
            PresentationChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        // ① 源坐标系（surface 局部 DIP）→ 中间坐标系（父容器 CenterPanel）。
        //    选父容器而不是本控件做中间坐标系，是为了让"钳制"按容器的实际尺寸算：
        //    本控件自身带显式 Width/Height，Bounds 不一定等于容器，拿它钳制会算错边界。
        var container = Parent as Visual;
        var cursor = (container is not null ? source.TranslatePoint(localInSurface, container) : null)
                     ?? localInSurface;
        var containerSize = container is Control c ? c.Bounds.Size
                                                  : new Size(double.PositiveInfinity, double.PositiveInfinity);
        var inContainer = PlaceOverlay(cursor, containerSize);

        // ② 中间坐标系（CenterPanel）→ 目标坐标系（本控件局部）。容器原点若不在本控件原点
        //    （显式尺寸 + 对齐方式导致），这一步是必需的；原点重合时它是恒等变换。
        _position = (container is not null ? container.TranslatePoint(inContainer, this) : null)
                    ?? inContainer;
        _visible = true;
        IsVisible = true;
        RefreshPixels(localInSurface, renderScaling);
        InvalidateVisual();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>浮窗相对光标的默认偏移（右下）。</summary>
    private const double OverlayGap = 16;
    /// <summary>翻转到光标左上时的额外内缩（沿用既有取值，不改视觉）。</summary>
    private const double OverlayFlipGap = 8;

    /// <summary>浮窗摆放（纯计算，坐标全部在<b>同一个容器坐标系</b>里）。
    ///
    /// <para>抽成 <c>internal static</c> 纯函数的理由：这是 #22 唯一与视觉树无关的部分
    /// （偏移 / 翻转 / 钳制），可以在单测里逐条验算；而"坐标换算"本身依赖视觉树，
    /// 只能由 <c>--selftest</c> 的几何断言覆盖（见 <c>AssertMagnifierFollowsCursor</c>）。
    /// 两者分工明确：单测钉算术，实机断言钉换算链路。</para>
    ///
    /// <para>行为与改动前逐字一致（右下优先 → 越界翻到左上 → 再钳到容器内），
    /// 只是不再依赖实例字段 <see cref="_position"/> 的中间值。</para></summary>
    internal static Point PlaceOverlay(Point cursorInContainer, Size container)
    {
        var x = cursorInContainer.X + OverlayGap;
        var y = cursorInContainer.Y + OverlayGap;
        // 越界则翻到光标左上；翻完仍可能为负（容器比浮窗还窄，或光标贴着左上角），
        // 负坐标会让浮窗整块滑出可视区 —— 用户看到的仍是"放大镜不显示"。
        if (x + WidthPx > container.Width) x = cursorInContainer.X - WidthPx - OverlayFlipGap;
        if (y + HeightPx > container.Height) y = cursorInContainer.Y - HeightPx - OverlayFlipGap;
        // 上界同样要按 (容器 - 浮窗) 钳制，避免小容器下浮窗被推到右下角之外。
        return new Point(
            Math.Clamp(x, 0, Math.Max(0, container.Width - WidthPx)),
            Math.Clamp(y, 0, Math.Max(0, container.Height - HeightPx)));
    }

    /// <summary>两次 GPU 回读之间的最小间隔（约 30Hz）。
    /// 与 ProbePanel.MinReadIntervalMs 同口径：同一功能的两份实现必须收敛，
    /// 否则一处调了节流、另一处没调，用户只会觉得"卡"却说不清是哪一处。</summary>
    private const long MinReadIntervalMs = 33;
    private long _lastReadTicks;
    private int _lastGx = -1, _lastGy = -1;

    // 最近一次**成功回读**那块的采样窗口左上角（-1 = 还没有可信窗口）。
    // 必须与 _pixelGrid 同生同灭（与 _gridBitDepth/_gridHdr 同一约束）：光标格是把光标
    // 物理坐标映射进"缓冲里的那一格"，窗口记错就标错格。
    // 置 null 的三处生产分支要一并归哨兵——AttachSession 的 ??= 会把缓冲重新造出来，
    // 那时若窗口还是上一路的，标记格就会对着全黑缓冲乱指。
    private int _gridLeft = -1, _gridTop = -1;

    // 光标物理坐标与 _gridLeft/_gridTop **故意不同源**：坐标是实时的，窗口是"最近一次成功
    // 回读"的那块。对不上（窗口被移动而回读还挡在节流后面）时 TryCursorCell 返回 null，
    // 宁可不画也不把标记按在中心格上。写入时机见 RefreshPixels 开头。
    private int _cursorPixelX = -1, _cursorPixelY = -1;

    private void RefreshPixels(Point localInSurface, double renderScaling)
    {
        // DIP → 物理像素：DPI 非 100% 时漏掉这步会整体偏移一个缩放倍数
        // （原实现注释自认"本类不知道 scaling，用 1 兜底"，即永远漏乘）。
        //
        // ⚠ 这两个值是本方法**第一段语句**，不能挪到下面任何 return 之后：节流与
        // "网格未移动"两处提前返回期间光标仍在动（在同一个 12×12 窗口里移动时根本不回读），
        // 标记格却必须跟着光标换格 —— 只在回读路径上记，窗口内的移动就整段丢显。
        // 它只依赖 renderScaling、不依赖回读结果，因此可以（也应当）放在最前面。
        var scale = renderScaling > 0 ? renderScaling : 1.0;
        _cursorPixelX = (int)(localInSurface.X * scale);
        _cursorPixelY = (int)(localInSurface.Y * scale);

        // 注意：判据里不能含 "_pixelGrid is null"。它一旦被置 null（读回失败 / 引擎未就绪），
        // 就会永远命中同一判据提前返回，再也进不到下面重新赋值的分支 —— 永久只剩空框。
        if (_session is null || !_session.ReadRenderTargetInfo(out var rt) ||
            rt.SwapWidth == 0 || rt.SwapHeight == 0)
        {
            _pixelGrid = null;
            _gridLeft = _gridTop = -1;
            return;
        }
        try
        {
            // 采样网格宽 ZoomGrid 像素，以中心为参考。
            // 后台缓冲尺寸可能小于 ZoomGrid（极小窗口），此时上界会变负 → 归零。
            var half = ZoomGrid / 2;
            var maxX = Math.Max(0, (int)rt.SwapWidth - ZoomGrid);
            var maxY = Math.Max(0, (int)rt.SwapHeight - ZoomGrid);
            var gx = Math.Clamp(_cursorPixelX - half, 0, maxX);
            var gy = Math.Clamp(_cursorPixelY - half, 0, maxY);

            // 节流：TryReadPixelRegion 是**同步** P/Invoke，内部要等 GPU staging 拷贝完成
            // （强制管线同步）。指针移动事件可达每帧数十次，逐个回读会拖慢渲染；
            // 人眼分辨不出 30Hz 以上的放大镜刷新率（与 ProbePanel 同一判据）。
            var now = Environment.TickCount64;
            if (now - _lastReadTicks < MinReadIntervalMs) return;
            // 采样网格**未移动**时不必再回读：缓动/抖动只改亚像素坐标，
            // 量化到整格的 (gx,gy) 没变 ⇒ 画面内容一模一样。
            // 注意不能在这里更新 _lastReadTicks：否则"原地抖动"会不断放行整格移动的回读。
            if (gx == _lastGx && gy == _lastGy && _pixelGrid is not null) return;
            _lastReadTicks = now;
            _lastGx = gx;
            _lastGy = gy;

            // 复用缓冲：原先每次指针移动都 new float[576]（指针热路径上的无谓分配）。
            // 仅在失败时保留"置 null"的语义——自测的 HasPixelGrid 依赖它，
            // 且失败是少数路径，不值得为省一次分配去动既有语义。
            var buffer = _pixelGrid ?? new float[ZoomGrid * ZoomGrid * 4];
            if (!_session.TryReadPixelRegion(gx, gy, ZoomGrid, ZoomGrid, buffer, out _))
            {
                _pixelGrid = null;
                _gridLeft = _gridTop = -1;
                return;
            }
            _pixelGrid = buffer;
            _gridLeft = gx;
            _gridTop = gy;
            _gridBitDepth = rt.OutputBitDepth;
            _gridHdr = rt.Hdr;
        }
        catch
        {
            _pixelGrid = null; // 引擎未就绪时放大镜只画框架
            _gridLeft = _gridTop = -1;
        }
    }

    /// <summary>把光标的物理像素坐标映射到采样窗口内的格号（纯计算，不碰 GPU 也不碰视觉树）。
    ///
    /// <para>抽成 <c>internal static</c> 纯函数的理由同 <see cref="PlaceOverlay"/>：
    /// "哪一格在光标下"是这条链上唯一与渲染有关、又能脱离窗口验算的算术，
    /// Render 需要窗口与绘屏上下文，离线量不到，只能把映射单独钉住。</para>
    ///
    /// <para><b>为什么必须有这个映射</b>：RefreshPixels 里的 (gx,gy) 是 <c>Math.Clamp</c> 过的，
    /// 后台缓冲被钳在 [0, swap-网格]，贴着边缘时窗口整体滑离"以光标为中心"的位置 ——
    /// 光标像素仍在窗口里，但不再落在中心格 (6,6)。不标出真实格号，边缘处的放大镜
    /// 就只是"一坨不知道哪点对着指针"的色块，正是本轮的用户投诉。</para>
    ///
    /// <para><b>为什么越界返回 null 而不是硬标中心格</b>：还有两种情况光标像素真的不在
    /// 这块窗口里——"窗口已移动但回读被节流/网格未移动挡住"（缓冲显示的还是旧窗口），
    /// 以及光标落在后台缓冲之外（surface 比 swap chain 大 / 信箱边距）。
    /// 这时标中心格就是撒谎，而"放大镜跟着指针"正是这块浮窗唯一的存在理由；
    /// 不画最多是暂时少一个提示。</para></summary>
    /// <param name="cursorX">光标物理像素 X（DIP × renderScaling，见 RefreshPixels）。</param>
    /// <param name="cursorY">光标物理像素 Y。</param>
    /// <param name="gridLeft">当前缓冲对应的采样窗口左上角 X；负值是"还没有可信窗口"的哨兵。</param>
    /// <param name="gridTop">采样窗口左上角 Y。</param>
    /// <param name="gridSize">采样窗口边长（ZoomGrid）。</param>
    internal static (int Col, int Row)? TryCursorCell(
        int cursorX, int cursorY, int gridLeft, int gridTop, int gridSize)
    {
        // 负原点在本控件里只有一种来源：从未成功回读（_gridLeft 初值 / 失败分支置 -1）。
        // 真实的 gx 被钳在 [0, swap-网格] 内，永远不为负，所以这里按"不可信"处理。
        if (gridSize <= 0 || gridLeft < 0 || gridTop < 0) return null;
        var col = cursorX - gridLeft;
        var row = cursorY - gridTop;
        if (col < 0 || col >= gridSize || row < 0 || row >= gridSize) return null;
        return (col, row);
    }

    public void HideOverlay()
    {
        _visible = false;
        IsVisible = false;
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    // 颜色取自主题令牌（背板/网格/握把类令牌深浅同值：它们压在视频画面上，
    // 对比度需求与主题无关，跟着主题翻转反而会把画面糊掉）。
    // 不能做成 static readonly：切换主题后要能重取（见 EnsureTheme）。
    // 字段初值取令牌表的深色值：首次 EnsureTheme 会按当前变体覆盖，运行时不生效，
    // 只为消除硬编码副本（单一真源见 ThemeTokens）。
    private IBrush _backdrop = new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayScrimBrush").Dark));
    private Pen _accentPen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark)), 2);
    // 光标格标记用的细描边：不复用 _accentPen 的 2px —— 一格只有 16px，描边居中一半压在格内，
    // 粗边会吃掉相邻格的内容，标记本身反倒成了遮挡。
    private Pen _cursorCellPen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("AccentBrush").Dark)), 1);
    private Pen _gridLinePen = new(new SolidColorBrush(Color.Parse(ThemeTokens.Get("OverlayGridBrush").Dark)), 1);
    private IBrush _caption = new SolidColorBrush(Color.Parse(ThemeTokens.Get("TextSecondaryBrush").Dark));
    private ThemeVariant? _themeTag;

    private void EnsureTheme()
    {
        if (_themeTag == ActualThemeVariant) return;
        _themeTag = ActualThemeVariant;
        _backdrop = ThemePalette.Brush(this, "OverlayScrimBrush");
        _accentPen = new Pen(ThemePalette.Brush(this, "AccentBrush"), 2);
        _cursorCellPen = new Pen(ThemePalette.Brush(this, "AccentBrush"), 1);
        _gridLinePen = new Pen(ThemePalette.Brush(this, "OverlayGridBrush"), 1);
        _caption = ThemePalette.Brush(this, "TextSecondaryBrush");
        _captionText = null; // 握着旧画笔，必须重建
    }

    // 格子颜色随像素变化，无法静态化。**但绝不能**复用同一个可变 SolidColorBrush 改 Color：
    // Avalonia 12 的 Render 走**延迟回放**（渲染指令先记录、稍后统一播放），
    // DrawRectangle 只记下画刷**引用**，真正取色发生在回放时刻 —— 那时画刷只剩
    // 最后一次赋值的颜色，144 个格子会画成同一块纯色。
    // 因此必须用**不可变**画刷（ImmutableSolidColorBrush：颜色在构造时固定）。
    // 缓存策略：按精确的 32 位 ARGB 键缓存，命中即复用（画面静止时命中率接近 100%）；
    // 有界（MaxCellBrushCache），超上限整体清空 —— 放大镜颜色集合随内容变化，
    // 不做 LRU 是因为整体清空的代价（重建 ≤1024 个小对象）远低于维护 LRU 的复杂度。
    private const int MaxCellBrushCache = 1024;
    private readonly Dictionary<uint, IBrush> _cellBrushCache = new();

    /// <summary>取某颜色的不可变画刷（缓存复用）。</summary>
    private IBrush CellBrush(Color color)
    {
        var key = color.ToUInt32();
        if (_cellBrushCache.TryGetValue(key, out var cached)) return cached;
        if (_cellBrushCache.Count >= MaxCellBrushCache) _cellBrushCache.Clear();
        var brush = new ImmutableSolidColorBrush(key);
        _cellBrushCache[key] = brush;
        return brush;
    }

    // 字幕只在缩放倍率变化时才需要重建
    private FormattedText? _captionText;
    private double _captionZoom = -1;

    public override void Render(DrawingContext dc)
    {
        base.Render(dc);
        if (!_visible) return;
        EnsureTheme();
        var rect = new Rect(_position.X, _position.Y, Width, Height);

        dc.DrawRectangle(_backdrop, null, rect);
        dc.DrawRectangle(null, _accentPen, rect);

        // 格宽两处共用（像素块 + 光标格标记）：提到 if 外面，避免两处各算一份，
        // 口径一旦不一致，标记框就和实际画出来的格错开半个像素。
        var cellW = rect.Width / ZoomGrid;
        var cellH = rect.Height / ZoomGrid;

        // 像素内容：把采样网格最近邻放大成 M×M 块
        if (_pixelGrid is not null)
        {
            for (var gy = 0; gy < ZoomGrid; gy++)
            {
                for (var gx = 0; gx < ZoomGrid; gx++)
                {
                    var i = (gy * ZoomGrid + gx) * 4;
                    // 8-bit 显示码值走跨域统一口径（docs/45 P0-4）：SDR 回读是 gamma 编码、
                    // HDR 回读是线性 scRGB，直接 v*255 会让两者不可比。
                    ColorNormalizer.ToDisplay8Bit(
                        _pixelGrid[i], _pixelGrid[i + 1], _pixelGrid[i + 2], _pixelGrid[i + 3],
                        _gridBitDepth, _gridHdr,
                        out var r8, out var g8, out var b8, out var a8);
                    var color = Color.FromArgb((byte)a8, (byte)r8, (byte)g8, (byte)b8);
                    dc.DrawRectangle(CellBrush(color), null,
                        new Rect(rect.X + gx * cellW, rect.Y + gy * cellH, cellW, cellH));
                }
            }
        }

        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        var line = _gridLinePen;
        // 十字线
        dc.DrawLine(line, new Point(cx, rect.Y), new Point(cx, rect.Bottom));
        dc.DrawLine(line, new Point(rect.X, cy), new Point(rect.Right, cy));
        // 对齐网格（1/4 分割）
        for (var i = 1; i < 4; i++)
        {
            dc.DrawLine(line, new Point(rect.X + rect.Width * i / 4, rect.Y), new Point(rect.X + rect.Width * i / 4, rect.Bottom));
            dc.DrawLine(line, new Point(rect.X, rect.Y + rect.Height * i / 4), new Point(rect.Right, rect.Y + rect.Height * i / 4));
        }

        // 光标所在格的描边：画在网格线之后，否则十字线/1/4 线会把它压住一半。
        // 贴边时窗口被钳过、光标不在中心格 —— 这里按真实格号描边（正是"放大镜跟指针脱节"
        // 的成因）；回读落后于光标或光标落在缓冲之外时 TryCursorCell 返回 null，
        // 宁可不画也不把标记硬按在中心格上（判据与理由见该方法）。
        if (_pixelGrid is not null &&
            TryCursorCell(_cursorPixelX, _cursorPixelY, _gridLeft, _gridTop, ZoomGrid) is { } cursorCell)
        {
            dc.DrawRectangle(null, _cursorCellPen,
                new Rect(rect.X + cursorCell.Col * cellW, rect.Y + cursorCell.Row * cellH, cellW, cellH));
        }

        if (_captionText is null || Math.Abs(_captionZoom - Zoom) > 0.01)
        {
            _captionText = new FormattedText($"{Zoom:0}x", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 10, _caption);
            _captionZoom = Zoom;
        }
        dc.DrawText(_captionText, new Point(rect.X + 4, rect.Bottom - _captionText.Height - 2));
    }

    /// <summary>取采样网格**中心点**的像素值（自测用）。
    ///
    /// 用途：与探针做交叉验证。放大镜和探针是"取光标下像素"的**两份实现**，
    /// 本轮 P0 缺陷正是其中一份（放大镜）坐标算错，而探针一直是对的。
    /// 只验证坐标公式不够（公式对了也可能传错参数），
    /// 用真实像素值比对才能证明放大镜显示的就是光标下的内容。
    /// </summary>
    internal bool TryGetCenterSample(out float r, out float g, out float b)
    {
        r = g = b = 0;
        if (_pixelGrid is null) return false;
        var half = ZoomGrid / 2;
        var i = (half * ZoomGrid + half) * 4;
        if (i + 2 >= _pixelGrid.Length) return false;
        r = _pixelGrid[i];
        g = _pixelGrid[i + 1];
        b = _pixelGrid[i + 2];
        return true;
    }

    /// <summary>自测取证用：最近一次成功回读的窗口左上角 + 当次光标物理坐标。
    /// 中心格恒为 <c>(ZoomGrid/2, ZoomGrid/2)</c> ⇒ 放大镜实际读到的缓冲像素是
    /// <c>(GridLeft + 6, GridTop + 6)</c>。"放大镜和探针读的不是同一格"这种失配，
    /// 只有把这个坐标摊出来才分得清是**格号差**还是**同一格给出两个值**（后者才是坐标算错）。</summary>
    internal (int GridLeft, int GridTop, int CursorX, int CursorY) SampleWindowForSelfTest =>
        (_gridLeft, _gridTop, _cursorPixelX, _cursorPixelY);
}
