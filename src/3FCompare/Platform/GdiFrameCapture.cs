using System;
using System.Runtime.InteropServices;
using _3FCompare.Core.Capture;
using _3FCompare.Core.Diagnostics;

namespace _3FCompare.App.Capture;

/// <summary>
/// 抓屏**兜底线**：GDI（<c>BitBlt</c> 屏幕区 / <c>PrintWindow</c> 顶层窗口裁剪）。
///
/// <para><b>⚠ 可信度警告（实测结论，勿再写"含 D3D flip-model"这类断言）</b>：
/// 本路径对 D3D11 flip-model swapchain **不可靠** —— 实测 7 个采样点只有中心 1 点偶尔命中
/// （<c>docs/26 §3.2</c>）。flip-model 的内容不进 GDI 重定向表面，所以结果**可能完全不含视频内容**。
/// 另外窗口被遮挡时会抓到遮挡物（<c>docs/26 §3.2</c>）。</para>
///
/// <para>因此本线只作为 <b>WGC 不可用时的兜底</b>，且**不要**把它的输出当作画质证据
/// （<c>docs/27-抓屏双线WGC-GDI-实现规划.zh.md</c> §三）。选路由 <see cref="FrameCapture"/> 负责。</para>
///
/// <para>实现说明（两条子路径，按顺序尝试）：</para>
/// <list type="number">
/// <item><b>Path A</b>：<c>BitBlt</c> 抓屏幕区 —— 抓的是 DWM 合成后的桌面，需要窗口可见。</item>
/// <item><b>Path B</b>：<c>PrintWindow</c> 抓**顶层窗口**整帧，再按子窗口相对坐标裁剪成子窗口客户区大小。</item>
/// </list>
/// </summary>
internal sealed class GdiFrameCapture : IFrameCapture
{
    /// <summary>路径标识固定为 GDI。</summary>
    public CaptureRoute Route => CaptureRoute.Gdi;

    /// <summary>抓取目标子窗口当前帧；两条子路径都失败时返回 null。</summary>
    public System.Drawing.Bitmap? Capture(nint hwnd)
    {
        if (hwnd == 0) return null;

        // 定位子窗口的顶层窗口与其屏幕矩形
        nint top = hwnd;
        try
        {
            var parent = GetAncestor(hwnd, 2 /*GA_ROOT*/);
            if (parent != 0) top = parent;
        }
        catch { /* 保留自身 */ }

        if (!GetWindowRect(top, out var topRect)) return null;
        if (!GetWindowRect(hwnd, out var childRect)) return null;

        var relX = childRect.Left - topRect.Left;
        var relY = childRect.Top - topRect.Top;
        var relW = childRect.Right - childRect.Left;
        var relH = childRect.Bottom - childRect.Top;
        if (relW <= 0 || relH <= 0 || relW > 8192 || relH > 8192) return null;

        // Path A：BitBlt 屏幕区。⚠ 对 flip-model 不可靠，且被遮挡时抓到遮挡物（docs/26 §3.2）。
        try
        {
            var viaScreen = CaptureViaBitBlt(childRect.Left, childRect.Top, relW, relH);
            if (viaScreen is not null) return viaScreen;
        }
        catch { /* 继续 */ }

        // Path B：PrintWindow 抓顶层 → 裁剪子区域（部分系统有效）
        var topBmp = CaptureViaPrintWindow(top);
        if (topBmp is not null)
        {
            try
            {
                // 左上角也必须钳制：子窗口矩形有可能落在顶层窗口之外（负相对坐标），
                // 旧实现只钳右下 ⇒ Clone 收到负 X/Y 直接抛异常，被下面的 catch 吞掉后返回 null，
                // 现象是"截图莫名失败且日志里什么都没有"。
                var cropX = Math.Max(0, relX);
                var cropY = Math.Max(0, relY);
                if (cropX != relX || cropY != relY)
                    AppLog.Warn("Capture", $"子窗口矩形越出顶层窗口左上（relX={relX}, relY={relY}），已按 0 钳制");

                var crop = new System.Drawing.Rectangle(cropX, cropY, relW, relH);
                if (crop.Right > topBmp.Width) crop.Width = topBmp.Width - crop.X;
                if (crop.Bottom > topBmp.Height) crop.Height = topBmp.Height - crop.Y;
                if (crop.Width <= 0 || crop.Height <= 0) { topBmp.Dispose(); return null; }
                var result = topBmp.Clone(crop, topBmp.PixelFormat);
                topBmp.Dispose();
                return result;
            }
            catch
            {
                topBmp.Dispose();
                return null;
            }
        }

        return null;
    }

    /// <summary>本实现不持有非托管资源（每次抓帧内部成对申请/释放 HDC）。</summary>
    public void Dispose() { }

    // ---- PrintWindow ----

    // 本文件是 Platform 目录下 Win32 P/Invoke 的既有风格（DllImport + ExactSpelling），
    // 与 WindowRegionClipper.cs 一致；自定义原生库（3FC.WgcCapture.dll）才用 LibraryImport
    // ——见 WgcFrameCapture.cs 的说明。
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static System.Drawing.Bitmap? CaptureViaPrintWindow(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) return null;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

        var bmp = new System.Drawing.Bitmap(w, h);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        var ok = false;
        try
        {
            const uint PwRenderFullContent = 0x00000002;
            ok = PrintWindow(hwnd, hdc, PwRenderFullContent);
            if (!ok) ok = PrintWindow(hwnd, hdc, 0);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        if (!ok)
        {
            bmp.Dispose();
            return null;
        }
        return bmp;
    }

    // ---- BitBlt 屏幕区 ----

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(nint hdcDest, int x, int y, int w, int h, nint hdcSrc, int sx, int sy, uint rop);

    // 虚拟屏幕（所有显示器拼接而成的坐标系）度量索引：
    // 副屏位于主屏左侧/上方时，屏幕坐标为负 —— 不校验的话 BitBlt 拿到越界源坐标
    // 仍然返回 TRUE，只是抓回一张黑图/错位区域，上层无从分辨（导出一张错误 PNG 且无告警）。
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // SRCCOPY | CAPTUREBLT：CAPTUREBLT 让 BitBlt 走合成后的桌面（含分层/半透明窗口），
    // 否则带 WS_EX_LAYERED 的窗口内容会被跳过，抓到的是它下面的画面。
    private const uint RopSrcCopyCapture = 0x00CC0020 | 0x40000000;

    private static System.Drawing.Bitmap? CaptureViaBitBlt(int screenX, int screenY, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        var vx = GetSystemMetrics(SmXVirtualScreen);
        var vy = GetSystemMetrics(SmYVirtualScreen);
        var vw = GetSystemMetrics(SmCxVirtualScreen);
        var vh = GetSystemMetrics(SmCyVirtualScreen);
        // 拿不到虚拟屏幕几何（极罕见）就不走这条不可信路径，交给 Path B 兜底
        if (vw <= 0 || vh <= 0) return null;

        // 把源矩形钳到虚拟屏幕内；完全落在虚拟屏幕之外（例如窗口被最小化到屏幕外）直接失败
        var x0 = Math.Max(screenX, vx);
        var y0 = Math.Max(screenY, vy);
        var x1 = Math.Min(screenX + w, vx + vw);
        var y1 = Math.Min(screenY + h, vy + vh);
        if (x1 <= x0 || y1 <= y0) return null;
        var cw = x1 - x0;
        var ch = y1 - y0;

        var srcDc = GetDC(0);
        if (srcDc == 0) return null;
        try
        {
            var bmp = new System.Drawing.Bitmap(w, h);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            var dstDc = g.GetHdc();
            // S5：GetHdc 与 ReleaseHdc 必须成对出现在 try/finally 里（同文件 CaptureViaPrintWindow
            // 已是这个写法）。若 BitBlt 抛出而 ReleaseHdc 被跳过，Graphics 仍持有设备上下文，
            // 随后的 Dispose 会抛或留下泄漏的 HDC —— 抓屏是会被反复调用的路径，不能靠"P/Invoke
            // 一般不会抛"来赌。
            bool ok;
            try
            {
                // 目标偏移用 (x0 - screenX / y0 - screenY)：被钳掉的那部分保持未绘制，
                // 这样输出尺寸仍是 w×h，差异叠加等按像素网格比较的调用方不会错位。
                ok = BitBlt(dstDc, x0 - screenX, y0 - screenY, cw, ch, srcDc, x0, y0, RopSrcCopyCapture);
            }
            finally
            {
                g.ReleaseHdc(dstDc);
            }
            // 过去忽略返回值：BitBlt 失败时会静默返回一张全黑图，
            // 上层还以为抓帧成功（Path B 的 PrintWindow 兜底因此永远走不到）。
            if (!ok)
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }
        finally
        {
            ReleaseDC(0, srcDc);
        }
    }
}
