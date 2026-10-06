using System;

namespace _3FCompare.Platform;

/// <summary>单路（非对比模式）放大视图的平移数学 —— 把屏幕位移与缩放增量换算成内核
/// <c>FFF3FP_SetViewTransform</c> 的归一化 pan。
///
/// <para><b>内核侧的唯一事实来源</b>（<c>3fp/kernel/FFF.Native/3FP/Render/VideoRenderer.cpp:4840-4863</c>）：
/// <code>
/// zoomedW = fitW · z
/// T(z)    = |zoomedW − fitW| / 2 = fitW · |z − 1| /2   // 原点可走区间的一半
/// originX = fitLeft + (fitW − zoomedW)/2 − pan · T(z)   // 再钳进 [min, max] 两端
/// </code>
/// 其中 <c>fitW</c> 是<b>变换前</b>的 destination 短/长边（等比适配盒，物理像素）。
/// <c>z &gt; 1</c> 时盒子比适配盒大、原点为负（露出源画面的边半段）；<c>z &lt; 1</c> 时盒子缩进
/// 适配盒内、四周留黑边，原点区间反向但端点仍是同一对 —— 所以两个 regime 共用上式。
/// 把这条式子取逆就得到本类的两条律：<b>跟手</b>要 <c>dpan = dx / 量程</c>，
/// <b>锚点</b>要 <c>pan₁</c> 使光标下那一点在缩放前后落在同一屏幕位置。</para>
///
/// <para><b>为什么原来的律必然错</b>：旧实现用 <c>k = 2/(窗口短边DIP)</c>，分母里<b>没有
/// <c>(z−1)</c></b>，也没有 dest 盒 —— 而内核的 pan→像素换算系数恰好带 <c>(z−1)</c>。两者合成
/// ⇒ 画面位移 = 光标位移 × (z−1)：z=1.15 只有 0.15 倍（"缩小了拖不动"），z=4 是 3 倍
/// （"放大了飞出去"），只有 z=2 侥幸 1:1。分母取<b>窗口</b>短边还额外错两处：多路时每路
/// 的适配盒远小于窗口，以及横/纵本应各按 <c>fitW</c>/<c>fitH</c>（黑边使两者不等）。</para>
///
/// <para><b>为什么不自己按窗口尺寸算适配盒</b>：内核用的是<b>显示</b>尺寸
/// （<c>VideoRenderer.cpp:4804-4806</c> 会按 <c>discAspect_</c> 把源宽折算成
/// <c>sourceHeight·aspect</c>），变形宽银幕片源的 fit 框与"按编码尺寸等比适配"不同。
/// 实机 <c>rt.DestWidth</c> 已经含了这一切 ⇒ 用 <see cref="FitBoxFromDest"/> 反推，
/// 不要另立一套算式（本仓曾有一份"按 fit 框自算"的重载，因与实机口径相反已整体删除）。</para></summary>
public static class ViewPan
{
    /// <summary>pan 的合法区间（内核 <c>SetViewTransform</c> 也按 <c>[-1,1]</c> 钳）。</summary>
    public const float Min = -1f;
    public const float Max = 1f;

    /// <summary>内核把"倍率视为 1"的带宽（<c>VideoRenderer.cpp:4840</c> 的
    /// <c>|z − 1| &gt; 1e-4</c>）。带内内核整段跳过变换 ⇒ 盒子就是适配盒、pan 完全没有量程，
    /// 此时任何"跟手/锚点"都无从谈起，只能退回居中适配。</summary>
    public const double KernelIdentityBand = 1e-4;

    /// <summary>内核这一帧到底会不会施加视口变换（<b>两侧都算</b>：放大与缩小）。
    /// 与内核的门槛逐字对应，改内核那一行必须同时改这里 —— 不一致的后果是"托管以为动了、
    /// 内核其实没动"，屏幕上表现为拖了不回弹的那一下。</summary>
    public static bool KernelAppliesTransform(double zoom) =>
        zoom > 0.0 && Math.Abs(zoom - 1.0) > KernelIdentityBand;

    /// <summary>一次拖动增量 → 新的 pan（跟手律：画面在屏幕上走过的像素 == 光标走过的像素）。</summary>
    /// <param name="dxPhys">本次拖动的屏幕水平位移，<b>物理像素</b>，右为正（<c>GetCursorPos</c> 直接可差）。</param>
    /// <param name="dyPhys">同上，向下为正。</param>
    /// <param name="fitWidthPhys">被拖那一路的适配盒宽（物理像素），由 <see cref="FitBoxFromDest"/> 得到。</param>
    /// <param name="fitHeightPhys">同上，高。两者可以不等（黑边），故必须分轴。</param>
    /// <param name="zoom">当前倍率 <c>z</c>。量程正比于 <c>(z−1)</c>，所以增益必须随 z 变。</param>
    /// <remarks><b>DPI 不在此处出现</b>：分子（<c>GetCursorPos</c> 差值）与分母（后台缓冲里的
    /// dest 盒）同为物理像素，量纲自动相消。旧律之所以要显式除 <c>RenderScaling</c>，是因为
    /// 它的分母取自 DIP 的 <c>Window.Bounds</c> —— 分子分母不同域才需要补一次换算。</remarks>
    public static (float PanX, float PanY) Accumulate(float currentPanX, float currentPanY,
        double dxPhys, double dyPhys, double fitWidthPhys, double fitHeightPhys, double zoom)
    {
        if (!KernelAppliesTransform(zoom)) return (currentPanX, currentPanY);

        var panX = currentPanX;
        var panY = currentPanY;

        // 量程 = 内核把一个单位 pan 换算成的像素数（见类注释），取逆即得增益。
        // 它是区间全长的一半、恒非负：z>1 与 z<1 都用同一条 Δorigin = −量程·Δpan，
        // 所以"往右拖 ⇒ 画面往右"这一跟手律在两个 regime 里同号。
        var spanX = TravelSpan(fitWidthPhys, zoom);
        var spanY = TravelSpan(fitHeightPhys, zoom);
        if (spanX > 0) panX = ClampPan(currentPanX - (float)(dxPhys / spanX), currentPanX);
        if (spanY > 0) panY = ClampPan(currentPanY - (float)(dyPhys / spanY), currentPanY);

        return (panX, panY);
    }

    /// <summary>适配盒（物理像素）：把内核上报的<b>变换后</b> Dest 反推回 <c>z=1</c> 的等比适配框。
    /// <para>内核在 <c>VideoRenderer.cpp:4836-4837</c> 就地改写 <c>destination.width/height</c>
    /// 为 <c>fit·z</c>，<c>:4867-4870</c> 才把它存进 <c>lastDest*</c> 供 <c>GetRenderTargetInfo</c>
    /// 回传 ⇒ 实机 <c>rt.DestWidth</c> 是放大后的盒子，除以 z 才回到量程的基准。</para>
    /// <para>尺寸不可用（≤0 / 非有限）返回 0，调用方据此不动 —— 与
    /// <see cref="Accumulate"/> 的"量程为 0 即本次不动"约定一致。</para></summary>
    public static (double Width, double Height) FitBoxFromDest(
        double destWidthPhys, double destHeightPhys, double zoom)
    {
        var z = KernelAppliesTransform(zoom) ? zoom : 1.0;
        return (PositiveOrZero(destWidthPhys) / z, PositiveOrZero(destHeightPhys) / z);
    }

    /// <summary>以光标为锚点改倍率 → 新的 pan 分量。
    ///
    /// <para><b>推导</b>（与内核 <c>:4840-4863</c> 同一条式子，两个 regime 共用）：记
    /// <c>T(z) = fitW·|z−1|/2</c> 为一个单位 pan 的像素量程（区间全长的一半），变换后盒子左沿
    /// <c>x = x₀ + fitW(1−z)/2 − pan·T(z)</c>，源归一化坐标 <c>u</c> 那一点落在
    /// <c>P = x + u·z·fitW</c>。要求缩放前后那一点都不动（<c>P</c>、<c>u</c> 不变）：
    /// <c>pan₁·T(z₁) = fitW(z₀−z₁)(1/2 − u) + pan₀·T(z₀)</c>，约掉 <c>fitW/2</c> 即得下式。</para>
    /// <code>pan₁ = [(z₀−z₁)(1−2u) + pan₀·|z₀−1|] / |z₁−1|</code>
    /// <para>在 <c>z₀, z₁ &gt; 1</c> 区间内它与旧式
    /// <c>[(z₀−1)(1+pan₀) + 2u(z₁−z₀)]/(z₁−1) − 1</c> 逐项相等（展开核对过），
    /// 所以放大那一路的行为没有被这次改动碰过。</para>
    ///
    /// <para><b>自检</b>：<c>u=0.5, pan₀=0</c> ⇒ <c>pan₁=0</c>（画面中心仍是中心）；
    /// <c>z₀=1</c> ⇒ <c>pan₁=2u−1</c>，与"从适配态起放大时把光标处推到对应视口位置"直接吻合
    /// （<c>u=1</c> 落在右端 ⇒ <c>pan₁=+1</c>）。
    /// 注意这与对比模式 <c>CompareCropPlanner.AnchorCrop</c> 的式子
    /// <c>u₁ = u − (u−u₀)·z₀/z₁</c> 是<b>同一条件的两种坐标域表达</b>：那边操作"露出区间的左上角"，
    /// 这边操作"归一化视口位置"，不能互换套用。</para>
    ///
    /// <para><b>退化</b>：目标倍率落在内核恒等带里（<c>|z₁−1| ≤ 1e-4</c>，内核整段跳过变换）⇒
    /// 返回 0（退回居中适配，与 <c>ResetViewTransform</c> 一致）。<b>缩小</b>（<c>z₁ &lt; 1</c>）时
    /// 整幅画面一起缩，除 <c>u=0.5</c> 外没有任何点能钉住 —— 这时它是"尽量钉"再钳回区间，
    /// 不是算错。已顶到 <c>±1</c> 边界的 <c>pan₀</c> 同理（画面已到能露出的极限）。</para></summary>
    /// <param name="pan0">当前 pan 分量。</param>
    /// <param name="cursorSourceFraction">要钉住的那一点的<b>源归一化坐标</b>（横分量除源宽、
    /// 纵分量除源高，0~1）。传哪个点就由哪个点作锚：光标下那点、或可见区中心当前对着那点
    /// （光标落在黑边时的退化，由调用方决定 —— 拿 0.5 兜底在已平移时等于把画面拽回源中心）。</param>
    /// <param name="z0">当前倍率。</param>
    /// <param name="z1">目标倍率。</param>
    public static float AnchorPan(float pan0, double cursorSourceFraction, double z0, double z1)
    {
        if (!KernelAppliesTransform(z1)) return 0f;

        // 倍率没变 ⇒ 不该凭空跳一下（z₁ < z₀ 是"缩小"，同样要锚点，不能当退化挡掉）
        if (Math.Abs(z1 - z0) < 1e-9) return ClampPan(pan0, pan0);

        // 越界与非有限的 u 一律兜到 0.5：这只是挡 NaN/垃圾值（内核收到非有限 pan 直接拒绝），
        // 不是推荐的退化策略 —— 想要"围绕可见画面中心放缩"的调用方应自己算好那点再传进来。
        var u = cursorSourceFraction is >= 0.0 and <= 1.0 ? cursorSourceFraction : 0.5;
        // 两个 |z−1| 就是内核的 T(z) 里那个 |z−1|；z₀ 不再钳到 ≥1 —— 缩小态也是合法的出发点。
        var pan1 = ((z0 - z1) * (1.0 - 2.0 * u) + pan0 * Math.Abs(z0 - 1.0)) / Math.Abs(z1 - 1.0);
        return ClampPan((float)pan1, pan0);
    }

    /// <summary>缩放要钉住哪一点：<b>光标下那点</b>优先；光标落在黑边（拿不到）时用
    /// <b>可见区中心当前对着的那一点</b>；两者都没有才退到源中心 <c>(0.5,0.5)</c>。
    ///
    /// <para><b>为什么抽成一条</b>：这是两条缩放路径（单路滚轮、对比模式相对对齐支）共用的
    /// 一次选择。原来各写一份 <c>?? 0.5</c> —— 改一份漏一份就是"同一个手势两种手感"，
    /// 而漏掉的那份会把画面猛地拽回源中心（已平移时"可见区中心"≠"源中心"）。</para>
    /// <para>优先级本身就是判据，所以三条都要有用例钉住（见 <c>ViewPanTests</c>）。</para></summary>
    public static (double U, double V) ResolveAnchorFraction(
        (double U, double V)? cursorOnSource, (double U, double V)? visibleCenter) =>
        cursorOnSource ?? visibleCenter ?? (0.5, 0.5);

    /// <summary>一个单位 pan 对应的屏幕像素量程 <c>fitW·|z−1|/2</c>（区间全长的一半，非负）；
    /// 非法输入返回 0。</summary>
    private static double TravelSpan(double fitPhys, double zoom) =>
        fitPhys > 0 ? fitPhys * Math.Abs(zoom - 1.0) / 2.0 : 0.0;

    /// <summary>钳回 <c>[-1,1]</c>，非有限值（NaN/±∞ 进不了内核，它直接返回 InvalidArgument）
    /// 退回调用方给的原值。</summary>
    private static float ClampPan(float v, float fallback) =>
        float.IsFinite(v) ? Math.Clamp(v, Min, Max) : fallback;

    private static double PositiveOrZero(double v) => v > 0 && double.IsFinite(v) ? v : 0.0;
}
