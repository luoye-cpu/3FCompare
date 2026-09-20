using _3FCompare.App.Capture;
using _3FCompare.Core.Capture;

namespace _3FCompare.Platform.Tests;

/// <summary>
/// 路径标识（<see cref="CapturedFrame.Route"/>）的守门测试。
///
/// <para>为什么这是安全属性而不是"文档属性"：上层（导出帧、缩略图预览）**只看这个标识**来决定
/// 一帧能不能当画质证据 —— WGC 能抓 D3D flip-model 且被遮挡时结果正确，GDI 对 flip-model 不可靠、
/// 被遮挡时会抓到遮挡物（docs/26 §3.2 / §八）。标识与实际线路错配，等于把不可信结果当可信结果用，
/// 而画面本身看不出异常（docs/28 §一 B1 的教训）。</para>
///
/// <para>这里用**位图实例身份**做交叉验证：只断言"Route == Wgc"是不够的（实现完全可能标 WGC
/// 却返回 GDI 的位图）；必须同时钉住"返回的就是那条线交出的那张图"。</para>
/// </summary>
public class CapturedFrameRouteTests
{
    private const nint Hwnd = 0x1234;

    /// <summary>
    /// 两条线都会产出位图时，标识与位图必须一一对应、不能串线。
    /// 断言成对出现，任一侧被互换（标 WGC 给 GDI 图、或反之）都会失败。
    /// </summary>
    [Fact]
    public void 标识与实际产帧线路一一对应不串线()
    {
        var gdi = new FakeLane(CaptureRoute.Gdi);
        var router = new CaptureRouter();

        // ① WGC 成功：标识 Wgc，且返回的正是 WGC 线交出的那张图
        var wgcOk = new FakeLane(CaptureRoute.Wgc);
        var viaWgc = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgcOk, gdi, router);
        Assert.NotNull(viaWgc);
        try
        {
            Assert.Equal(CaptureRoute.Wgc, viaWgc.Value.Route);
            Assert.Same(wgcOk.HandedOut[0], viaWgc.Value.Bitmap);
        }
        finally { viaWgc.Value.Bitmap.Dispose(); }

        // ② WGC 失败回退：标识 Gdi，且返回的正是 GDI 线交出的那张图
        var wgcFail = new FakeLane(CaptureRoute.Wgc, succeeds: false);
        var viaGdi = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgcFail, gdi, router);
        Assert.NotNull(viaGdi);
        try
        {
            Assert.Equal(CaptureRoute.Gdi, viaGdi.Value.Route);
            Assert.Same(gdi.HandedOut[0], viaGdi.Value.Bitmap);
        }
        finally { viaGdi.Value.Bitmap.Dispose(); }

        // 两条线交出的位图是两个不同实例 ⇒ 上面的身份断言确实有鉴别力（不是恒等成立）
        Assert.NotSame(wgcOk.HandedOut[0], gdi.HandedOut[0]);
    }

    /// <summary>
    /// 所有权契约：门面只是把位图交出去，**不得**代为释放
    /// （<c>CapturedFrame</c> 的文档写明"调用方负责 Dispose 位图"）。
    /// 若门面在返回前误 Dispose，下面的读取会抛异常。
    /// </summary>
    [Fact]
    public void 门面交出的位图仍可读_所有权在调用方()
    {
        var wgc = new FakeLane(CaptureRoute.Wgc);
        var gdi = new FakeLane(CaptureRoute.Gdi);

        var frame = FrameCapture.RouteAndCapture(Hwnd, true, true, false, () => wgc, gdi, new CaptureRouter());

        Assert.NotNull(frame);
        var bmp = frame.Value.Bitmap;
        try
        {
            Assert.Equal(2, bmp.Width);
            Assert.Equal(2, bmp.Height);
            bmp.GetPixel(0, 0);     // 已释放的 Bitmap 会在这里抛
        }
        finally { bmp.Dispose(); }
    }

    /// <summary>
    /// 每条线自己声明的 <see cref="IFrameCapture.Route"/> 必须与其定位一致。
    /// 两条线的可信度不等价，实现与标识错配会让"这条线是谁"这件事从源头就错。
    /// </summary>
    [Fact]
    public void 每条抓屏线声明的标识与其定位一致()
    {
        using var wgc = new WgcFrameCapture();
        using var gdi = new GdiFrameCapture();

        Assert.Equal(CaptureRoute.Wgc, wgc.Route);
        Assert.Equal(CaptureRoute.Gdi, gdi.Route);
        Assert.NotEqual(wgc.Route, gdi.Route);
    }
}
