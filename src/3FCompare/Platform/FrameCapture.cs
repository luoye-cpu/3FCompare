using System;
using System.Drawing;
using System.Runtime.InteropServices;
using _3FCompare.Core.Capture;
using _3FCompare.Core.Diagnostics;
using _3FCompare.Diagnostics;

namespace _3FCompare.App.Capture;

/// <summary>
/// 抓屏**静态门面**：WGC 主线路 + GDI 兜底，负责选路、回退与路径标识（docs/27 §三）。
///
/// <para><b>调用方只需记住一件事</b>：拿到的 <see cref="CapturedFrame.Route"/> 决定这一帧可不可信 ——
/// <see cref="CaptureRoute.Wgc"/> 能抓到 flip-model 内容且被遮挡时结果正确；
/// <see cref="CaptureRoute.Gdi"/> 对 flip-model 不可靠、被遮挡时会抓到遮挡物（docs/26 §3.2 / §八）。</para>
///
/// <para><b>选路与回退策略</b>（严格对应 docs/27 §三 表格，判定实现在
/// <see cref="CaptureRouter"/> 这个纯逻辑类里、可被 Core 单测覆盖）：</para>
/// <list type="table">
/// <item><term>Wgc_IsSupported() == false</term><description>直接走 GDI（WARN 仅一次）</description></item>
/// <item><term>DLL 缺失 / 加载失败</term><description>走 GDI（WARN 仅一次，本会话不再重试探测）</description></item>
/// <item><term>Wgc_Create 失败</term><description>走 GDI（计一次 WGC 失败）</description></item>
/// <item><term>Wgc_CaptureFrame 失败</term><description>**本次**回退 GDI，下次仍先试 WGC</description></item>
/// <item><term>WGC 连续失败 3 次</term><description>降级为 GDI，不再付超时代价（重开媒体 / 显卡显示器变更时 <see cref="ResetRouting"/> 解除）</description></item>
/// <item><term>窗口最小化</term><description>直接走 GDI（WGC 必然黑帧），不计入失败</description></item>
/// </list>
/// </summary>
public static class FrameCapture
{
    private static readonly CaptureRouter Router = new();
    private static readonly GdiFrameCapture Gdi = new();
    private static readonly object WgcLock = new();
    private static WgcFrameCapture? _wgc;

    /// <summary>WGC 探测结果（不可变，只发布一次，volatile 保证安全发布）。</summary>
    internal sealed record ProbeResult(bool DllAvailable, bool Supported);

    private static readonly object ProbeLock = new();
    private static volatile ProbeResult? _probe;

    /// <summary>
    /// 抓取目标窗口当前帧（WGC 优先，失败自动回退 GDI）。
    /// </summary>
    /// <param name="hwnd">视频子窗口句柄（原生库内部会处理"子窗口 → 顶层祖先 + 裁剪"）。</param>
    /// <returns>成功返回帧 + 路径标识；两条线都失败返回 <c>null</c>。调用方负责 Dispose 位图。</returns>
    public static CapturedFrame? CaptureWindowFrame(nint hwnd)
    {
        if (hwnd == 0) return null;

        var probe = GetProbe();
        var minimized = IsWindowMinimized(hwnd);

        // 探测 / 最小化判定是**真实环境**的两个入口（P/Invoke + user32），
        // 其余"选路 → 执行 → 计数 → 回退"全在 RouteAndCapture 里，由单测注入替身覆盖。
        return RouteAndCapture(hwnd, probe.Supported, probe.DllAvailable, minimized, GetWgc, Gdi, Router);
    }

    /// <summary>
    /// 解除"本会话降级为 GDI"的状态，让下一次抓帧重新先试 WGC（S4）。
    ///
    /// <para><b>为什么必须有这个恢复点</b>：<see cref="Router"/> 是**进程级静态单例**，
    /// 降级一旦置位就跨所有窗口、所有会话生效。若没有恢复入口，3 次瞬时失败
    /// （驱动重置 / 锁屏 / 目标窗口被临时占用）就会让本进程余下的导出帧与缩略图
    /// **永远**走对 flip-model 不可靠的 GDI，用户持续看到错内容直到重启应用。</para>
    ///
    /// <para><b>调用时机</b>由上层编排器决定，见 <c>PlaybackCoordinator</c>：
    /// ① 打开媒体（用户可见的"重来一次"动作，且此时目标窗口是全新的）；
    /// ② 引擎报显卡/显示器变更（抓屏环境确实变了，WGC 值得重试）。
    /// 刻意**不**做"每次抓帧都重试"或定时重试——那会让稳定失败期间反复付超时代价，
    /// 正是当初引入降级要避免的。</para>
    ///
    /// <para>不重置 <c>_probe</c>：探测结果是不可变的系统能力（是否支持 WGC / DLL 能否加载），
    /// 进程内不会变化，重探只会白付一次 <see cref="DllNotFoundException"/> 的开销。</para>
    /// </summary>
    internal static void ResetRouting() => Router.Reset();

    /// <summary>
    /// 选路 + 执行 + 回退计数的**唯一实现**：<see cref="CaptureWindowFrame(nint)"/> 与单元测试共用它，
    /// 保证测的不是"测试里另写的一份等价逻辑"。
    ///
    /// <para>把三个环境输入（系统是否支持 / 库是否可用 / 是否最小化）与两条线都作为参数注入，
    /// 是为了让单测能在**不加载真实 WGC/GDI** 的前提下驱动全部选路分支
    /// （真实抓帧慢且依赖窗口，见 docs/27 §五 的分层验证策略）。</para>
    /// </summary>
    /// <param name="wgcFactory">WGC 线工厂。用工厂而非实例，是为了保持"选到 GDI 时不构造 WGC 线"这一惰性。</param>
    /// <param name="router">失败计数与降级状态的持有者（调用方负责串行化）。</param>
    internal static CapturedFrame? RouteAndCapture(
        nint hwnd,
        bool wgcSupported,
        bool wgcDllAvailable,
        bool windowMinimized,
        Func<IFrameCapture> wgcFactory,
        IFrameCapture gdi,
        CaptureRouter router)
    {
        if (hwnd == 0) return null;

        var decision = router.SelectRoute(wgcSupported, wgcDllAvailable, windowMinimized);

        if (decision.Route == CaptureRoute.Wgc)
        {
            var wgc = wgcFactory();
            var wgcBmp = wgc.Capture(hwnd);
            if (wgcBmp is not null)
            {
                router.ReportWgcSuccess();
                return new CapturedFrame(wgcBmp, CaptureRoute.Wgc);
            }

            // 本次失败 ⇒ 立刻回退 GDI；下次调用仍先试 WGC（除非已连续失败到阈值）。
            var downgraded = router.ReportWgcFailure();
            // 组件日志：抓屏线路降级（WGC → GDI）。抓屏失败常与"设备丢失 / 窗口正在销毁"同源，
            // 是判断崩溃前系统状态的重要旁证。
            ComponentLog.Log(Comp.Capture, "WgcFallbackToGdi", -1,
                $"consecutive={router.ConsecutiveWgcFailures}/{router.FailureThreshold} downgraded={downgraded}");
            // 诊断文本只有真实 WGC 线才有；替身/其它实现不产出，故按类型取。
            if (wgc is WgcFrameCapture realWgc)
            {
                var detail = string.IsNullOrEmpty(realWgc.LastErrorMessage) ? string.Empty : $"：{realWgc.LastErrorMessage}";
                AppLog.Debug("Capture",
                    $"WGC 抓帧失败（{WgcNative.Describe(realWgc.LastErrorCode)}{detail}），本次回退 GDI" +
                    $"[连续 {router.ConsecutiveWgcFailures}/{router.FailureThreshold}]");
            }

            if (downgraded)
            {
                AppLog.Warn("Capture",
                    $"WGC 连续失败 {router.FailureThreshold} 次，降级为 GDI 兜底（docs/27 §三）。" +
                    "注意：GDI 对 D3D flip-model swapchain 不可靠，结果可能不含视频内容，且窗口被遮挡时会抓到遮挡物；" +
                    "重新打开媒体或显卡/显示器变更后会重新尝试 WGC");
            }
        }

        var gdiBmp = gdi.Capture(hwnd);
        return gdiBmp is null ? null : new CapturedFrame(gdiBmp, CaptureRoute.Gdi);
    }

    /// <summary>
    /// 探测 WGC 可用性，**整个进程只做一次**（因此 WARN 也只会出现一次，不会刷日志）。
    /// <para>原生库缺失时 <c>Wgc_IsSupported()</c> 会抛 <see cref="DllNotFoundException"/> 一类的异常，
    /// 这里必须捕获 —— 阶段 3 之前 DLL 尚未随包分发，缺库是**预期状态**，不是故障。</para>
    /// </summary>
    private static ProbeResult GetProbe()
    {
        var cached = _probe;
        if (cached is not null) return cached;

        lock (ProbeLock)
        {
            cached = _probe;
            if (cached is not null) return cached;

            var result = ProbeFrom(WgcNative.Wgc_IsSupported, out var failure);

            if (failure is not null)
            {
                AppLog.Warn("Capture",
                    $"WGC 原生库 3FC.WgcCapture.dll 不可用（{failure.GetType().Name}: {failure.Message}），" +
                    "抓屏降级为 GDI 兜底；本会话不再重试探测（阶段 3 才会随包分发该 DLL，docs/27 §四）");
            }
            else if (result.Supported)
            {
                AppLog.Info("Capture", "抓屏双线就绪：WGC 为主线路，GDI 为兜底（docs/27 §三）");
            }
            else
            {
                AppLog.Warn("Capture",
                    "系统不支持 WGC（Wgc_IsSupported=0），抓屏全程走 GDI 兜底；" +
                    "GDI 对 flip-model 不可靠，结果可能不含视频内容（docs/26 §3.2）");
            }

            _probe = result;
            return result;
        }
    }

    /// <summary>
    /// 探测结果的**纯映射**：把一次原生探测调用折成（库是否可用, 系统是否支持）。
    ///
    /// <para><b>为什么异常必须在这里被吃掉</b>：原生库缺失时 <c>Wgc_IsSupported()</c> 会抛
    /// <see cref="DllNotFoundException"/> 一类的异常，而缺库是**预期状态**（便携部署、内嵌资源解压失败），
    /// 不是故障；让它冒泡会直接掀掉抓帧线程。</para>
    ///
    /// <para>独立成方法（而非内联在 <see cref="GetProbe"/> 里）只为可测试性：单测用替身即可覆盖
    /// "支持 / 不支持 / 抛异常"三条分支，不必加载真实的 <c>3FC.WgcCapture.dll</c>。</para>
    /// </summary>
    /// <param name="nativeProbe">原生探测调用，返回非 0 表示系统支持 WGC。</param>
    /// <param name="failure">捕获到的异常；未抛异常时为 <c>null</c>。</param>
    internal static ProbeResult ProbeFrom(Func<int> nativeProbe, out Exception? failure)
    {
        try
        {
            var supported = nativeProbe() != 0;
            failure = null;
            return new ProbeResult(DllAvailable: true, Supported: supported);
        }
        catch (Exception ex)
        {
            failure = ex;
            return new ProbeResult(DllAvailable: false, Supported: false);
        }
    }

    private static WgcFrameCapture GetWgc()
    {
        var wgc = _wgc;
        if (wgc is not null) return wgc;

        lock (WgcLock)
        {
            return _wgc ??= new WgcFrameCapture();
        }
    }

    /// <summary>
    /// 目标窗口（的**顶层祖先**）是否已最小化。
    /// <para>视频画面是 WS_CHILD 子窗口，最小化的是它的顶层窗口；直接对子窗口调 <c>IsIconic</c>
    /// 永远返回 false，会把"最小化"这种必然黑帧的情形漏给 WGC 去白等约 2 秒。</para>
    /// </summary>
    private static bool IsWindowMinimized(nint hwnd)
    {
        try
        {
            var root = GetAncestor(hwnd, 2 /*GA_ROOT*/);
            return IsIconic(root == 0 ? hwnd : root);
        }
        catch
        {
            // 查询失败不阻塞抓帧：按"未最小化"处理，让 WGC 自己判定（失败也只是回退一次）
            return false;
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);
}
