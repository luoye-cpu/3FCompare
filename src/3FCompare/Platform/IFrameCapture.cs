using System.Drawing;
using _3FCompare.Core.Capture;

namespace _3FCompare.App.Capture;

/// <summary>
/// 抓到的单帧 + **路径标识**（这一帧来自哪条线）。
///
/// <para>为什么必须带标识：两条线的可信度不等价 —— WGC 能抓到 flip-model 内容且被遮挡时结果正确；
/// GDI 对 flip-model 不可靠、被遮挡时会抓到遮挡物（docs/26 §3.2 / §八）。
/// 上层（导出帧、缩略图预览）需要据此判断这一帧能不能当"画面证据"用。</para>
/// </summary>
/// <param name="Bitmap">抓到的位图，调用方负责 Dispose。</param>
/// <param name="Route">产出该帧的线路。</param>
public readonly record struct CapturedFrame(Bitmap Bitmap, CaptureRoute Route);

/// <summary>
/// 单条抓屏线的抽象（docs/27 §三）。
///
/// <para>实现：<see cref="WgcFrameCapture"/>（主）/ <see cref="GdiFrameCapture"/>（备）。
/// 选路与回退由静态门面 <see cref="FrameCapture"/> 负责，调用方一般不直接持有实现。</para>
/// </summary>
public interface IFrameCapture : IDisposable
{
    /// <summary>本实现产出的路径标识。</summary>
    CaptureRoute Route { get; }

    /// <summary>
    /// 抓取目标窗口当前帧（阻塞直至成功或失败）。
    /// </summary>
    /// <param name="hwnd">视频子窗口句柄。</param>
    /// <returns>成功返回位图（调用方负责 Dispose）；失败返回 <c>null</c>，由门面决定是否回退。</returns>
    Bitmap? Capture(nint hwnd);
}
