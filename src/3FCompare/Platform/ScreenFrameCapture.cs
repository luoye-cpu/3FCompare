namespace _3FCompare.App.Capture;

/// <summary>
/// 【兼容入口】抓取视频子窗口当前帧。
///
/// <para><b>⚠ 本类曾经是"GDI 路径"本身，现已退化为 <see cref="FrameCapture"/> 的薄壳。</b>
/// 保留它只是为了不破坏既有调用方（<c>MainWindow.Capture.cs</c> / <c>MainWindow.Playback.cs</c> /
/// <c>MainWindow.SelfTest.cs</c>）；新代码请直接调用 <see cref="FrameCapture"/>。</para>
///
/// <para><b>为什么改用门面</b>：本类原来的实现是纯 GDI（<c>BitBlt</c> 屏幕区 + <c>PrintWindow</c> 顶层裁剪），
/// 而 <b>GDI 路径对 D3D11 flip-model swapchain 不可靠</b> —— 实测 7 个采样点只有中心 1 点偶尔命中
/// （<c>docs/26 §3.2</c>）：flip-model 的内容不进 GDI 重定向表面，所以结果<b>可能完全不含视频内容</b>；
/// 且窗口被遮挡时会抓到遮挡物。<c>docs/26 §八</c> 进一步实测 WGC 能同时解决这两个缺陷
/// （能抓到 flip-model 内容；被不透明窗口完全遮挡时结果与无遮挡一致）。</para>
///
/// <para>⇒ 因此抓屏改为 <b>WGC 主 + GDI 备</b>的双线结构（<c>docs/27</c> §三），
/// 选路、单次失败回退与连续失败降级都由 <see cref="FrameCapture"/> 负责。
/// 本壳方法**丢弃了路径标识**，调用方无从知道这一帧来自哪条线；
/// 需要判断可信度（例如导出对比素材时标注来源）请改用
/// <see cref="FrameCapture.CaptureWindowFrame"/> 并读取 <c>CapturedFrame.Route</c>。</para>
///
/// <para>GDI 逻辑本身**未删除**，已原样迁移到 <see cref="GdiFrameCapture"/> 作为兜底线。</para>
/// </summary>
public static class ScreenFrameCapture
{
    /// <summary>抓取目标子窗口当前帧（真实模式 UI 线程调用；阻塞直至成功或失败）。</summary>
    /// <param name="hwnd">视频子窗口句柄。</param>
    /// <returns>成功返回位图（调用方负责 Dispose）；两条线都失败返回 <c>null</c>。</returns>
    public static System.Drawing.Bitmap? CaptureWindowFrame(nint hwnd)
        => FrameCapture.CaptureWindowFrame(hwnd)?.Bitmap;
}
