namespace _3FCompare.Core.Capture;

/// <summary>抓屏线路标识（路径标识）。
///
/// <para>两条线的可信度**不等价**，调用方拿到帧后需要据此判断：</para>
/// <list type="bullet">
/// <item><see cref="Wgc"/>：Windows Graphics Capture。实测能抓到 D3D11 flip-model swapchain 内容，
/// 且**窗口被完全遮挡时结果与无遮挡一致**（docs/26 §八）。</item>
/// <item><see cref="Gdi"/>：BitBlt / PrintWindow 兜底。实测对 flip-model **不可靠**
/// （docs/26 §3.2：7 个采样点只有中心 1 点偶尔命中），且被遮挡时会抓到遮挡物。
/// 结果可能不含视频内容。</item>
/// </list></summary>
public enum CaptureRoute
{
    /// <summary>GDI 兜底（对 flip-model 不可靠，仅当 WGC 不可用时使用）。
    ///
    /// <para><b>刻意取 0</b>：这样 <c>default(CaptureRoute)</c> 与未初始化的字段/结构体会落到
    /// **不可信**的一侧。反过来（Wgc=0）会让"忘了赋值"的地方默认声称自己来自可信线路，
    /// 失败方向是危险的。</para></summary>
    Gdi = 0,

    /// <summary>WGC 主线（可信）。</summary>
    Wgc = 1,
}

/// <summary>选路原因。比 <see cref="CaptureRoute"/> 更细，供日志与上层诊断使用。</summary>
public enum CaptureRouteReason
{
    /// <summary>WGC 可用且窗口可捕获 ⇒ 走 WGC。</summary>
    WgcPreferred = 0,

    /// <summary>系统不支持 WGC（<c>Wgc_IsSupported() == 0</c>）。</summary>
    WgcNotSupported = 1,

    /// <summary>WGC 原生库缺失 / 加载失败。</summary>
    WgcUnavailable = 2,

    /// <summary>WGC 连续失败达到阈值，本会话已降级为 GDI（<see cref="CaptureRouter.Reset"/> 可解除）。</summary>
    WgcDowngraded = 3,

    /// <summary>窗口已最小化（WGC 必然黑帧），直接走 GDI。不改变失败计数。</summary>
    WindowMinimized = 4,
}

/// <summary>一次选路的结果：走哪条线 + 为什么。</summary>
public readonly record struct CaptureRouteDecision(CaptureRoute Route, CaptureRouteReason Reason)
{
    /// <summary>是否选中 WGC 线。</summary>
    public bool IsWgc => Route == CaptureRoute.Wgc;
}

/// <summary>
/// 抓屏双线（WGC 主 + GDI 备）的**选路与回退决策**。
///
/// <para>纯逻辑：不含任何 P/Invoke / Win32 / UI 依赖，因此可以放进 Core 并被单测覆盖
/// （两个测试工程引用不到 UI 层）。真实的抓帧动作由 UI 层的 <c>FrameCapture</c> 门面执行，
/// 它把探测结果与本类的判定串起来。</para>
///
/// <para>策略来源：<c>docs/27-抓屏双线WGC-GDI-实现规划.zh.md</c> §三 的表格：</para>
/// <list type="table">
/// <item><term>IsSupported() == false</term><description>直接走 GDI</description></item>
/// <item><term>DLL 缺失 / 加载失败</term><description>走 GDI（调用方记 WARN，仅一次）</description></item>
/// <item><term>Wgc_Create 失败</term><description>走 GDI（计为一次 WGC 失败）</description></item>
/// <item><term>Wgc_CaptureFrame 失败</term><description>**本次**回退 GDI，下次仍先试 WGC</description></item>
/// <item><term>WGC 连续失败 N 次</term><description>本会话降级为 GDI</description></item>
/// <item><term>窗口最小化</term><description>直接走 GDI</description></item>
/// </list>
///
/// <para>线程安全：状态（连续失败计数 + 降级标记）由单把内部锁保护。抓帧可能来自多条线路的
/// 工作线程，而 <see cref="Reset"/> 会从 UI 线程调用，因此这里不能像早期版本那样"靠调用方串行化"。</para>
/// </summary>
public sealed class CaptureRouter
{
    /// <summary>默认降级阈值：WGC 连续失败达到该次数即在本会话降级为 GDI（docs/27 §三 建议值）。</summary>
    public const int DefaultFailureThreshold = 3;

    private readonly int _failureThreshold;
    /// <summary>保护 <see cref="_consecutiveWgcFailures" /> 与 <see cref="_downgraded" />。</summary>
    private readonly object _gate = new();
    private int _consecutiveWgcFailures;
    private bool _downgraded;

    /// <param name="failureThreshold">连续失败多少次后降级。必须 ≥ 1。</param>
    public CaptureRouter(int failureThreshold = DefaultFailureThreshold)
    {
        if (failureThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(failureThreshold), failureThreshold,
                "降级阈值必须 ≥ 1（为 0 会导致第一次选路就直接降级）");
        _failureThreshold = failureThreshold;
    }

    /// <summary>本会话的降级阈值。</summary>
    public int FailureThreshold => _failureThreshold;

    /// <summary>当前连续失败计数（WGC 成功即清零）。</summary>
    public int ConsecutiveWgcFailures { get { lock (_gate) return _consecutiveWgcFailures; } }

    /// <summary>本会话是否已降级为 GDI（连续失败达阈值后为 true；<see cref="Reset"/> 可解除）。</summary>
    public bool IsWgcDowngraded { get { lock (_gate) return _downgraded; } }

    /// <summary>
    /// 清除降级标记与连续失败计数，让下一次选路重新从 WGC 试起。
    ///
    /// <para><b>为什么需要它</b>：降级本是"本会话"级别的取舍（避免每次都付超时代价），但
    /// <see cref="CaptureRouter"/> 在 UI 层是**进程级静态单例**，若没有任何恢复点，
    /// 3 次瞬时失败（驱动重置、锁屏、抓屏目标被临时占用等）就会让本次进程余下的
    /// 导出帧 / 缩略图**永远**走对 flip-model 不可靠的 GDI（docs/26 §3.2），
    /// 用户会一直看到错内容直到重启应用。</para>
    ///
    /// <para><b>调用时机</b>（由门面 <c>FrameCapture.ResetRouting()</c> 暴露）：
    /// 重新打开媒体、以及显卡/显示器变更事件到达时。这两处都是"抓屏环境可能已经变了"的
    /// 自然恢复点，既给了 WGC 一次重新证明自己的机会，又不会在稳定失败期间反复付超时代价。</para>
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _consecutiveWgcFailures = 0;
            _downgraded = false;
        }
    }

    /// <summary>
    /// 选路。判定顺序按 docs/27 §三 表格从上到下：系统不支持 → 库不可用 → 已降级 → 窗口最小化 → 否则 WGC。
    /// <para>只有<see cref="CaptureRouteReason.WindowMinimized"/>是**瞬态**原因（窗口还原后仍会回到 WGC）；
    /// 其余三个原因在本次选路内不会自愈（<see cref="WgcDowngraded"/> 可被 <see cref="Reset"/> 解除）。</para>
    /// </summary>
    /// <param name="wgcSupported">系统是否支持 WGC（<c>Wgc_IsSupported()</c> 的探测结果）。</param>
    /// <param name="wgcDllAvailable">WGC 原生库是否可用（缺失/加载失败 = false）。</param>
    /// <param name="windowMinimized">目标窗口（顶层祖先）当前是否已最小化。</param>
    public CaptureRouteDecision SelectRoute(bool wgcSupported, bool wgcDllAvailable, bool windowMinimized)
    {
        if (!wgcSupported)
            return new CaptureRouteDecision(CaptureRoute.Gdi, CaptureRouteReason.WgcNotSupported);
        if (!wgcDllAvailable)
            return new CaptureRouteDecision(CaptureRoute.Gdi, CaptureRouteReason.WgcUnavailable);
        if (IsWgcDowngraded)
            return new CaptureRouteDecision(CaptureRoute.Gdi, CaptureRouteReason.WgcDowngraded);
        if (windowMinimized)
            return new CaptureRouteDecision(CaptureRoute.Gdi, CaptureRouteReason.WindowMinimized);

        return new CaptureRouteDecision(CaptureRoute.Wgc, CaptureRouteReason.WgcPreferred);
    }

    /// <summary>WGC 抓帧成功 ⇒ 连续失败计数清零（"连续"语义：中间成功一次就重新计数）。</summary>
    public void ReportWgcSuccess()
    {
        lock (_gate) { _consecutiveWgcFailures = 0; }
    }

    /// <summary>
    /// WGC 抓帧失败（含 <c>Wgc_Create</c> 失败）⇒ 计数 +1；达到阈值即降级（直到 <see cref="Reset"/>）。
    /// <para>刻意**不**接受"失败原因"参数：最小化等可预见情形由 <see cref="SelectRoute"/> 提前拦掉、
    /// 根本不会走到这里，能到这里的都是真实的 WGC 失败，理应计入。</para>
    /// </summary>
    /// <returns>本次失败后是否已降级（true = 已处于降级状态）。</returns>
    public bool ReportWgcFailure()
    {
        lock (_gate)
        {
            // 计数器封顶，避免长时间失败后溢出成负数（那样降级判据会失效）。
            if (_consecutiveWgcFailures < int.MaxValue)
                _consecutiveWgcFailures++;

            if (_consecutiveWgcFailures >= _failureThreshold)
                _downgraded = true;

            return _downgraded;
        }
    }
}
