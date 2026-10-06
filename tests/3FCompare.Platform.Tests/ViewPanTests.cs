namespace _3FCompare.Platform.Tests;

/// <summary>
/// 单路放大视图的平移与锚点数学（<see cref="ViewPan"/>）。
///
/// <para><b>验收方式：拿内核的式子当独立参照，不拿被测公式自己对自己。</b>
/// <see cref="KernelDestLeft"/> / <see cref="ScreenXOfSourcePoint"/> 是按
/// <c>3fp/kernel/.../VideoRenderer.cpp:4840-4863</c> 逐行重写的第二份实现（常数、钳位区间、符号都从那里抄），
/// 断言的是两条物理性质：
/// ① <b>跟手</b>——光标在屏幕上走 N 物理像素，画面上同一源点也走 N 物理像素，且<b>与倍率无关</b>；
/// ② <b>锚点</b>——滚轮改倍率时，光标下那个源点在屏幕上的位置不变。
/// 旧实现漏掉的正是①里"与倍率无关"这一维：它的增益与 <c>(z−1)</c> 成反比缺失，
/// 于是 z=1.15 慢 8 倍、z=4 快 3 倍，只在 z=2 侥幸正确 —— 那种实现能过"方向对不对"，
/// 过不了下面这条跨倍率的恒等式。</para>
///
/// <para><b>量程现在是可以验的</b>：内核把原点钳在 <c>x₀</c> 与 <c>x₀ + 拟合 − 变换后</c> 这**一对端点**
/// 之间（<c>VideoRenderer.cpp:4846-4853</c> 取 min/max）。<c>z&gt;1</c> 时另一端在左（负原点，露出
/// 源画面的边半段），<c>z&lt;1</c> 时另一端在右（盒子缩进拟合盒内、四周留黑边）。两个 regime 共用
/// 同一条 <c>origin = 中点 − pan·|变换后 − 拟合|/2</c>，所以 pan 的指向跨 z=1 连续，本类按
/// <c>pan ∈ [-1,1]</c> 全程标定，且横轴同时取 <c>z&gt;1</c> 与 <c>z&lt;1</c> 两侧。</para>
/// </summary>
public class ViewPanTests
{
    private const double Tol = 1e-6;

    /// <summary>像素位移的容差：pan 是 <c>float</c>（内核 API 就要 float），7 位有效数字
    /// 落到千级像素上是 1e-4 级；旧律的误差是**倍数级**（z=1.15 差 8 倍、z=8 差 7 倍），
    /// 所以放到 0.01px 既不会误判浮点噪声，也绝不会放过倍数量级的错。</summary>
    private const double PixTol = 0.01;

    // ══════════ 独立参照：内核 destination 的两行式子 ══════════

    /// <summary>内核的变换后盒子左沿（相对拟合盒左沿，物理像素）：<c>destination.x − x₀</c>。
    /// <para>抄自 <c>VideoRenderer.cpp:4840-4863</c>：盒子宽 <c>max(1, fitW·z)</c>；原点两端是
    /// <c>0</c> 与 <c>fitW − 盒子宽</c>（谁小谁做下界 —— z&gt;1 时后者在左、z&lt;1 时在右）；
    /// <c>origin = 区间中点 − pan·|盒子宽 − fitW|/2</c>，再钳回区间。量程取<b>绝对值</b>，
    /// 照旧写 <c>max((z−1)/2·fitW, 0)</c> 的话 z&lt;1 时平移整段死掉（内核旧式就是这么错的）。</para></summary>
    private static double KernelDestLeft(double fitWPhys, double zoom, double pan)
    {
        var zoomed = Math.Max(1.0, fitWPhys * zoom);
        var farEdge = fitWPhys - zoomed;                       // 另一端（相对 x₀=0）
        var lo = Math.Min(0.0, farEdge);
        var hi = Math.Max(0.0, farEdge);
        var offset = pan * Math.Abs(zoomed - fitWPhys) / 2.0;
        return Math.Clamp((lo + hi) / 2.0 - offset, lo, hi);
    }

    /// <summary>源归一化坐标 <c>u</c> 那一点在屏幕上的横坐标（相对拟合盒左沿，物理像素）。
    /// 放大盒宽 <c>fitW·z</c> 承载整个源宽，故 <c>u</c> 落在 <c>u·fitW·z</c> 处。</summary>
    private static double ScreenXOfSourcePoint(double u, double fitWPhys, double zoom, double pan) =>
        KernelDestLeft(fitWPhys, zoom, pan) + u * fitWPhys * zoom;

    /// <summary>一个源点在屏幕上随 pan 移动的像素数（画面位移，右为正）。</summary>
    private static double PictureShift(double fitWPhys, double zoom, double pan0, double pan1, double u = 0.37) =>
        ScreenXOfSourcePoint(u, fitWPhys, zoom, pan1) - ScreenXOfSourcePoint(u, fitWPhys, zoom, pan0);

    // ══════════ ①跟手：位移与倍率无关 ══════════

    /// <summary>核心不变量：向右拖 100 物理像素 ⇒ 画面必须正好右移 100 物理像素，
    /// <b>在任何倍率下都一样</b>。这是用户报的"缩小了拖得慢、放大了拖得快"那一条。
    ///
    /// <para>倍率只取"量程装得下这段拖动"的那些：一个单位 pan 的量程是
    /// <c>fitW·(z−1)/2</c>，z=1.15 时只有 60px &lt; 100px —— 那是几何极限不是增益错，
    /// 单独立一条测（见 <see cref="Accumulate_TravelSmallerThanDrag_ClampsToExtent"/>）。</para></summary>
    [Theory]
    [InlineData(0.25)]   // 缩小到适配盒以内（旧内核此时整段跳过变换 ⇒ 这条会恒等式失效）
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    [InlineData(32.0)]
    public void Accumulate_AnyZoomLevel_PictureTracksCursorExactly(double zoom)
    {
        const double fitW = 800.0, fitH = 450.0, dx = 100.0, dy = 60.0;

        var (panX, panY) = ViewPan.Accumulate(0f, 0f, dx, dy, fitW, fitH, zoom);

        // 画面实际走了多少：由内核式子独立算出，不看被测公式
        var movedX = PictureShift(fitW, zoom, 0, panX);
        var movedY = PictureShift(fitH, zoom, 0, panY);
        Assert.Equal(dx, movedX, PixTol);
        Assert.Equal(dy, movedY, PixTol);
    }

    /// <summary>符号（旧结论仍要钉住）：向右/下拖 ⇒ pan 变<b>小</b>（内核把 pan 当视口位置）。
    /// 反向拖必须对称。</summary>
    [Fact]
    public void Accumulate_RightAndDownDrag_DecreasesPanSoPictureFollowsCursor()
    {
        var (x, y) = ViewPan.Accumulate(0f, 0f, 100, 100, 800, 450, 3.0);
        Assert.True(x < 0f);
        Assert.True(y < 0f);

        var (rx, ry) = ViewPan.Accumulate(0f, 0f, -100, -100, 800, 450, 3.0);
        Assert.Equal(-x, rx, Tol);
        Assert.Equal(-y, ry, Tol);
    }

    /// <summary>横纵分轴：黑边使 <c>fitW ≠ fitH</c>，两轴各按自己的量程标定。
    /// 旧律两轴同除"窗口短边"，这里画面纵位移会与光标纵位移差一个 <c>fitW/fitH</c> 倍。</summary>
    [Fact]
    public void Accumulate_Letterboxed_DragDiagonal_TracksBothAxes()
    {
        const double fitW = 1280.0, fitH = 400.0; // 扁格：上下黑边占大半

        var (panX, panY) = ViewPan.Accumulate(0f, 0f, 200, 200, fitW, fitH, 2.5);

        Assert.Equal(200.0, PictureShift(fitW, 2.5, 0, panX), PixTol);
        Assert.Equal(200.0, PictureShift(fitH, 2.5, 0, panY), PixTol);
        // 两轴增益确实不同（否则这条断言与上一条重复，测不到"分轴"）
        Assert.True(Math.Abs(panX - panY) > 1e-3, $"两轴 pan 相同说明分轴没生效：{panX} vs {panY}");
    }

    /// <summary>DPI 相消：物理位移与物理 dest 盒同域，故"DIP 相同的拖动得到相同的 pan"。
    /// 150% 屏上拖 150 物理像素 == 100% 屏上拖 100 物理像素（适配盒也按同比例放大）。</summary>
    [Fact]
    public void Accumulate_PhysicalDomain_SameDipDragGivesSamePanAtAnyScaling()
    {
        // 100% ：fit=800 物理像素，拖 100；150%：同一窗口的 fit=1200 物理像素，拖 150
        var at100 = ViewPan.Accumulate(0f, 0f, 100, 0, 800, 800, 2.0);
        var at150 = ViewPan.Accumulate(0f, 0f, 150, 0, 1200, 1200, 2.0);

        Assert.Equal(at100.PanX, at150.PanX, 1e-6);
    }

    /// <summary>连续性：小步累计 == 一步到位。节流丢帧（16ms 直接丢弃那一支）只允许丢次数，
    /// 不允许丢位移量。</summary>
    [Fact]
    public void Accumulate_StepwiseEqualsSingleStep()
    {
        var single = ViewPan.Accumulate(0f, 0f, 240, 0, 900, 500, 3.0);
        var (a, _) = ViewPan.Accumulate(0f, 0f, 100, 0, 900, 500, 3.0);
        var (b, _) = ViewPan.Accumulate(a, 0f, 60, 0, 900, 500, 3.0);
        var (c, _) = ViewPan.Accumulate(b, 0f, 80, 0, 900, 500, 3.0);

        Assert.Equal(single.PanX, c, Tol);
    }

    // ══════════ ②边界与退化 ══════════

    /// <summary>拖满整个量程必须正好到 <c>±1</c>（量程定义与内核同源，多一点少一点都是标定错）。
    /// 量程 = <c>fitW·(z−1)/2</c>。</summary>
    [Fact]
    public void Accumulate_FullTravelDrag_LandsExactlyOnUnitBoundary()
    {
        const double fitW = 800.0, zoom = 3.0;
        var travel = fitW * (zoom - 1.0) / 2.0; // = 800

        var (x, _) = ViewPan.Accumulate(0f, 0f, travel, 0, fitW, 450, zoom);
        Assert.Equal(-1.0, x, Tol);

        var (r, _) = ViewPan.Accumulate(0f, 0f, -travel, 0, fitW, 450, zoom);
        Assert.Equal(1.0, r, Tol);
    }

    /// <summary>超量拖动停在 <c>[-1,1]</c>；内核 <c>SetViewTransform</c> 对非有限值直接
    /// 返回 InvalidArgument ⇒ NaN/±∞ 绝不能从这一层漏出去。</summary>
    [Fact]
    public void Accumulate_ClampsToUnitRange_NeverOverflows()
    {
        var (x, y) = ViewPan.Accumulate(0.5f, -0.5f, 1e9, -1e9, 800, 450, 3.0);
        Assert.Equal(ViewPan.Min, x, Tol);
        Assert.Equal(ViewPan.Max, y, Tol);

        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var (bx, by) = ViewPan.Accumulate(0.25f, -0.75f, bad, bad, 800, 450, 3.0);
            Assert.Equal(0.25f, bx, Tol);   // 非法输入 = 本次不动，不是把 NaN 传下去
            Assert.Equal(-0.75f, by, Tol);
        }
    }

    /// <summary>倍率贴着 1 时一个单位 pan 的量程 <c>fitW·(z−1)/2</c> 比拖动量还短 —— 画面已经
    /// 无处可挪，<b>物理上</b>不可能跟手。此时必须钳到端点（并只走满剩余量程），而不是溢出或 NaN。
    /// 这条把"几何极限"与"增益算错"分开：前者只能少拖，后者是缺陷。</summary>
    [Theory]
    [InlineData(1.0002)]
    [InlineData(1.15)]
    [InlineData(0.9)]     // 缩小侧同样有"量程比拖动短"的时候：盒子只能在黑边里走 40px
    public void Accumulate_TravelSmallerThanDrag_ClampsToExtent(double zoom)
    {
        const double fitW = 800.0;
        var travel = fitW * Math.Abs(zoom - 1.0) / 2.0;
        Assert.True(travel < 100.0, $"夹具前提不成立：量程 {travel} 并不小于 100px 拖动");

        var (panX, _) = ViewPan.Accumulate(0f, 0f, 100, 0, fitW, 450, zoom);

        Assert.Equal(ViewPan.Min, panX, Tol);                     // 顶到端点
        Assert.Equal(travel, PictureShift(fitW, zoom, 0, panX), PixTol); // 从居中出发只能再走一个量程
    }

    /// <summary>缩小侧（<c>z &lt; 1</c>）的几何契约：盒子必须<b>待在拟合盒之内</b>——原点
    /// <c>∈ [0, fit − 盒宽]</c>、永不为负（负原点是放大侧的事），pan=+1 贴左、−1 贴右、0 居中。
    ///
    /// <para>为什么单独一条：旧内核此时整段跳过变换（盒子恒等拟合盒，量程为 0），而把放大侧的
    /// 区间 <c>[x₀ − (盒宽 − 拟合), x₀]</c> 原样搬过来，在 z&lt;1 时两端<b>反向</b>，
    /// <c>std::clamp(lo &gt; hi)</c> 是未定义行为 —— 这条钉的就是"区间由 min/max 生成"这一步。
    /// 顺带反推回适配盒：内核上报的 <c>DestWidth</c> 除以 z 必须回到 <c>fitW</c>（缩小侧也算变换后）。</para></summary>
    [Theory]
    [InlineData(0.8)]
    [InlineData(0.5)]
    [InlineData(0.05)]
    public void ZoomedOut_BoxStaysInsideFittedBox_AndPanSpansBothEnds(double zoom)
    {
        const double fitW = 1000.0;
        var boxW = Math.Max(1.0, fitW * zoom);
        var maxLeft = fitW - boxW;

        Assert.Equal(0.0, KernelDestLeft(fitW, zoom, 1.0), PixTol);       // pan=+1 ⇒ 盒子贴左，黑边在右
        Assert.Equal(maxLeft, KernelDestLeft(fitW, zoom, -1.0), PixTol);  // pan=−1 ⇒ 盒子贴右
        Assert.Equal(maxLeft / 2.0, KernelDestLeft(fitW, zoom, 0.0), PixTol); // 居中

        for (var pan = -1.0; pan <= 1.0001; pan += 0.25)
            Assert.InRange(KernelDestLeft(fitW, zoom, pan), 0.0, maxLeft);

        // 被测侧必须认同一件事：缩小也是"施加了变换"，所以 Dest 要能反推回同一个 fitW。
        var (backW, _) = ViewPan.FitBoxFromDest(boxW, boxW, zoom);
        Assert.Equal(fitW, backW, PixTol);
    }

    /// <summary>退化输入：倍率落在内核恒等带里（<c>|z−1| ≤ 1e-4</c>，两侧都算 ⇒ 内核整段跳过变换，
    /// pan 无意义）、适配盒未知 ⇒ 原样返回，不得除零出 Infinity。
    /// ⚠ 这一条以前把 <c>z=0.5</c> 也算退化（那时内核只在 z&gt;1.0001 进变换段）；现在 z&lt;1 是
    /// 真缩小、pan 有量程，所以改用带内的 <c>0.99995</c> 顶位，另立一条测"缩小侧不退化"。</summary>
    [Fact]
    public void Accumulate_DegenerateInputs_AreNoOp()
    {
        Assert.Equal((0.3f, -0.4f), ViewPan.Accumulate(0.3f, -0.4f, 100, 100, 800, 450, 1.0));
        Assert.Equal((0.3f, -0.4f), ViewPan.Accumulate(0.3f, -0.4f, 100, 100, 800, 450, 0.99995));
        Assert.Equal((0.3f, -0.4f), ViewPan.Accumulate(0.3f, -0.4f, 100, 100, 800, 450, 1.00005));
        Assert.Equal((0.3f, -0.4f), ViewPan.Accumulate(0.3f, -0.4f, 100, 100, 0, 0, 3.0));
        Assert.Equal((0.3f, -0.4f), ViewPan.Accumulate(0.3f, -0.4f, 100, 100, -1, -1, 3.0));
        Assert.Equal((0.3f, -0.4f), ViewPan.Accumulate(0.3f, -0.4f, 100, 100, 800, 450, double.NaN));
        Assert.Equal((0.3f, -0.4f), ViewPan.Accumulate(0.3f, -0.4f, 100, 100, 800, 450, 0.0));
    }

    /// <summary>分轴独立：只知道一路的横向量程时，纵向必须照常换算 —— 两轴各自判"量程 > 0"，
    /// 不能因为一轴缺数就整条不动（也不能反过来把缺数当 0 除）。</summary>
    [Fact]
    public void Accumulate_OneAxisBoxUnknown_OtherAxisStillTracks()
    {
        var (x, y) = ViewPan.Accumulate(0f, 0f, 100, 100, 0, 450, 3.0);

        Assert.Equal(0f, x, Tol);                                  // 横轴无量程 ⇒ 不动
        Assert.NotEqual(0f, y);
        Assert.Equal(100.0, PictureShift(450, 3.0, 0, y), PixTol); // 纵轴按自己的量程跟手
    }

    // ══════════ ③光标锚点 ══════════

    /// <summary>核心不变量：以光标下那一点为锚改倍率后，那个源点在屏幕上的位置<b>不变</b>。
    /// 这是用户报的"像素和光标固定不上"那一条（单路滚轮此前完全没做锚点）。
    ///
    /// <para><b>夹具是物理可现的</b>：不凭空编 <c>u</c>，而是给定"<c>(z₀, pan₀)</c> 下光标落在
    /// 可见区间的第 <c>cursorT</c> 处"，用<b>参照实现</b>反推出它对应的源归一化坐标 ——
    /// 随机拼三元组会造出"光标下的点其实早已滑出视野"这种现实里不存在、又恰好让恒等式空转的输入。</para></summary>
    [Theory]
    [InlineData(1.0, 0.0, 0.5, 2.0)]   // 从适配态正中放大（注：这例的正确答案 == 不锚点，
                                       // 单独用它测不出锚点有没有生效，只当"中心仍是中心"的哨兵）
    [InlineData(1.0, 0.0, 0.2, 4.0)]   // 从适配态偏左放大
    [InlineData(2.0, -0.5, 0.5, 4.0)]  // 已平移 + 继续放大
    [InlineData(2.0, 0.3, 0.8, 5.0)]
    [InlineData(6.0, -0.4, 0.15, 3.0)] // 缩小也要锚点
    [InlineData(3.0, 1.0, 0.9, 6.0)]    // 顶到上界后继续放大（仍可锚点）
    [InlineData(4.0, 0.9, 0.05, 2.0)]   // 锚点物理不可达 ⇒ 必须停在正确的那一端
    [InlineData(2.0, 0.0, 0.5, 0.6)]    // 跨 z=1 缩进适配盒内：居中那点仍是居中
    [InlineData(1.5, -0.3, 0.35, 0.7)]  // 缩小侧带 pan 出发：可达性仍由参照判
    public void AnchorPan_PointUnderCursor_StaysOnScreen(double z0, float pan0, double cursorT, double z1)
    {
        const double fitW = 1000.0;

        var destLeft = KernelDestLeft(fitW, z0, pan0);
        var cursorX = cursorT * fitW;                                   // 可见视口就是拟合盒 [0, fitW]
        var u = (cursorX - destLeft) / (z0 * fitW);                     // 参照实现反推源坐标
        Assert.InRange(u, 0.0, 1.0);                                    // 夹具自证：光标真的落在画面上

        var pan1 = ViewPan.AnchorPan(pan0, u, z0, z1);

        // 可达性由**参照**判定（pan 越大那点越往左，故两个端点就是可达区间）：
        // 可达 ⇒ 精确钉住；不可达 ⇒ 必须停在"朝光标方向"的那一端。
        // 写成"顶到边界就只断言在 [-1,1] 内"是没有牙的：不锚点的实现同样停在边界附近，会假绿。
        var xAtLeft = ScreenXOfSourcePoint(u, fitW, z1, 1.0);   // pan=+1 ⇒ 那点最靠左
        var xAtRight = ScreenXOfSourcePoint(u, fitW, z1, -1.0); // pan=−1 ⇒ 那点最靠右
        if (cursorX >= xAtLeft - 1e-9 && cursorX <= xAtRight + 1e-9)
            Assert.Equal(cursorX, ScreenXOfSourcePoint(u, fitW, z1, pan1), 1e-3);
        else if (cursorX > xAtRight)
            Assert.Equal(-1f, pan1, Tol);
        else
            Assert.Equal(1f, pan1, Tol);
    }

    /// <summary>锚点式的两个已知退化值：居中放大 pan 保持 0；从适配态放大时 <c>pan₁ = 2u − 1</c>
    /// （右端 ⇒ +1，左端 ⇒ −1）。这两个手算值是对公式最便宜的独立背书。</summary>
    [Fact]
    public void AnchorPan_HandComputableCases_MatchClosedForm()
    {
        Assert.Equal(0f, ViewPan.AnchorPan(0f, 0.5, 2.0, 4.0), Tol);
        Assert.Equal(1f, ViewPan.AnchorPan(0f, 1.0, 1.0, 4.0), Tol);
        Assert.Equal(-1f, ViewPan.AnchorPan(0f, 0.0, 1.0, 4.0), Tol);
        Assert.Equal(0f, ViewPan.AnchorPan(0f, 0.5, 1.0, 4.0), Tol);
    }

    /// <summary><c>z₁ → 1</c>：量程塌缩，锚点物理上无法满足 ⇒ 必须归 0（居中适配），
    /// 不能返回被 <c>(z₁−1)</c>  blow up 出来的巨值或 NaN。</summary>
    [Fact]
    public void AnchorPan_ZoomBackToFit_ReturnsZeroNotBlownUpValue()
    {
        Assert.Equal(0f, ViewPan.AnchorPan(0.6f, 0.9, 4.0, 1.0), Tol);
        Assert.Equal(0f, ViewPan.AnchorPan(-0.6f, 0.1, 4.0, 1.00005), Tol);
    }

    /// <summary>门槛只留一处判据：app 的"这一下能不能拖"、<c>FitBoxFromDest</c> 的"要不要除 z"、
    /// 内核的"会不会施加变换"必须读同一条 <see cref="ViewPan.KernelAppliesTransform"/>。
    /// 带宽必须**两侧对称**（放大侧与缩小侧同阈值），带内一律 false —— 判错的两种后果都是屏幕上
    /// "拖了没反应"：误 true 是 app 发了 pan 而内核整段跳过，误 false 是内核在缩小态认真平移而 app 不给拖。</summary>
    [Theory]
    [InlineData(0.6, true)]      // 缩小侧：现在算"施加了变换"
    [InlineData(0.05, true)]
    [InlineData(1.15, true)]
    [InlineData(1.0, false)]     // 恰为 1：无量程
    [InlineData(0.99995, false)] // 带内（缩小侧）
    [InlineData(1.00005, false)] // 带内（放大侧）
    [InlineData(0.0, false)]     // 零/负/NaN 不是"缩小"，内核连盒子都不会那样画
    [InlineData(-1.0, false)]
    [InlineData(double.NaN, false)]
    public void KernelAppliesTransform_IsSymmetricAroundOne(double zoom, bool expected) =>
        Assert.Equal(expected, ViewPan.KernelAppliesTransform(zoom));

    /// <summary>取不到锚点（光标落在黑边 / 演示模式）时调用方传 0.5 = 以画面中心为锚；
    /// 非有限的 u 必须内部兜到 0.5，而不是把 NaN 乘进式子里。</summary>
    [Fact]
    public void AnchorPan_InvalidCursorFraction_FallsBackToCentre()
    {
        var center = ViewPan.AnchorPan(0.2f, 0.5, 2.0, 4.0);
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, -1.0, 2.0 })
            Assert.Equal(center, ViewPan.AnchorPan(0.2f, bad, 2.0, 4.0), Tol);
    }

    /// <summary>倍率没变（<c>z₁ == z₀</c> 的退化调用）必须原样返回，不能凭空跳一下。
    /// 注意"缩小"（<c>z₁ &lt; z₀</c>）<b>不是</b>退化，它同样要锚点 —— 见上面 Theory 的 6→3 那例。
    /// 而 <c>z₁ == z₀ == 1</c> 归"量程塌缩 ⇒ 归零"那条规则管（已在下面测到），不在此列。</summary>
    [Fact]
    public void AnchorPan_NoZoomChange_KeepsPan()
    {
        Assert.Equal(0.35f, ViewPan.AnchorPan(0.35f, 0.2, 3.0, 3.0), Tol);
        Assert.Equal(0.35f, ViewPan.AnchorPan(0.35f, 0.2, 8.0, 8.0), Tol);
    }

    // ══════════ ④适配盒反推 ══════════

    /// <summary><c>rt.DestWidth</c> 是内核<b>变换后</b>上报的绘制盒 ⇒ 除以 z 回到量程基准。
    /// z ≤ 1 时内核没改过盒子，必须原样返回（除了 1 会凭空放大 fit）。</summary>
    [Fact]
    public void FitBoxFromDest_DividesOutTheAppliedZoom()
    {
        Assert.Equal((800.0, 450.0), ViewPan.FitBoxFromDest(1600, 900, 2.0));
        Assert.Equal((800.0, 450.0), ViewPan.FitBoxFromDest(800, 450, 1.0));
        Assert.Equal((800.0, 450.0), ViewPan.FitBoxFromDest(800, 450, 0.0)); // 未知倍率按 1 处理
        Assert.Equal((1000.0, 500.0), ViewPan.FitBoxFromDest(1000, 500, 1.00005)); // 内核此时未变换
        Assert.Equal((0.0, 0.0), ViewPan.FitBoxFromDest(0, 0, 2.0));
        Assert.Equal((800.0, 0.0), ViewPan.FitBoxFromDest(1600, -5, 2.0));
    }

    /// <summary>与内核同源的一致性：把 fit 交给参照实现放大 <c>×z</c>，必须回到传入的 Dest。
    /// 这条是防止 <see cref="ViewPan.FitBoxFromDest"/> 与内核的 <c>zoomedWidth</c> 定义漂移。</summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(1.15)]
    [InlineData(32.0)]
    public void FitBoxFromDest_InvertsKernelBoxGrowth(double zoom)
    {
        const double fitW = 640.0;
        var (back, _) = ViewPan.FitBoxFromDest(fitW * zoom, 360 * zoom, zoom);
        Assert.Equal(fitW, back, 1e-9);
    }

    // ══════════ ⑤锚点优先级（两条缩放路径共用的那一次选择）══════════

    /// <summary>光标下有读数时，光标那点必须压过可见区中心 —— 否则"以光标为锚"名存实亡。</summary>
    [Fact]
    public void ResolveAnchorFraction_CursorPresent_WinsOverVisibleCentre()
    {
        Assert.Equal((0.2, 0.8), ViewPan.ResolveAnchorFraction((0.2, 0.8), (0.5, 0.5)));
    }

    /// <summary>光标落在黑边时（扁格/瘦格里的常态）退到<b>可见区中心</b>，不是源中心 0.5。
    /// 已平移时用 0.5 兜底会把画面猛地拽回源中心 —— 本轮修的正是这一条，所以给它单独的用例：
    /// 谁把优先级改回 <c>cursor ?? 0.5</c>，这里立刻红。</summary>
    [Fact]
    public void ResolveAnchorFraction_CursorOverLetterbox_UsesVisibleCentreNotSourceCentre()
    {
        var got = ViewPan.ResolveAnchorFraction(null, (0.63, 0.41));

        Assert.Equal((0.63, 0.41), got);
        Assert.True(got.U != 0.5, "退化锚又变回源中心了：已平移时一滚就把画面拽回去");
    }

    /// <summary>两者都拿不到（会话未就绪 / 演示模式）才用源中心。</summary>
    [Fact]
    public void ResolveAnchorFraction_NothingKnown_FallsBackToSourceCentre()
    {
        Assert.Equal((0.5, 0.5), ViewPan.ResolveAnchorFraction(null, null));
    }
}
