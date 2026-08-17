using System.Runtime.InteropServices;

using System.Runtime.InteropServices;

namespace _3FCompare.App.Capture;

/// <summary>
/// 真实模式窗口帧捕获（F21/F20 增强）：
/// 抓取**顶层窗口**整帧（D3D flip-model 由合成器合并），再按目标子窗口的屏幕坐标裁剪；
/// 回退 BitBlt 屏幕区。输出 System.Drawing.Bitmap（可存 PNG / 供差异叠加）。
/// 注意：PrintWindow 对 D3D flip-model 是否含内容取决于合成器；调用方可再回退 ReadVideoPixel。
/// </summary>
public static class WgcFrameCapture
{
    /// <summary>抓取目标子窗口当前帧（真实模式 UI 线程调用；阻塞直至成功）。</summary>
    public static System.Drawing.Bitmap? CaptureWindowFrame(nint hwnd, int timeoutMs = 4000)
    {
        _ = timeoutMs;
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

        // Path A：BitBlt 屏幕区（抓屏幕合成结果，含 D3D flip-model；窗口需可见）
        try
        {
            var viaScreen = CaptureViaBitBlt(childRect.Left, childRect.Top, relW, relH);
            if (viaScreen is not null) return viaScreen;
        }
        catch { /* 继续 */ }

        // Path B：PrintWindow 抓顶层（含 D3D 合成内容）→ 裁剪子区域（部分系统有效）
        var topBmp = CaptureViaPrintWindow(top);
        if (topBmp is not null)
        {
            try
            {
                var crop = new System.Drawing.Rectangle(relX, relY, relW, relH);
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

    // ---- PrintWindow ----

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

    private static System.Drawing.Bitmap? CaptureViaBitBlt(int screenX, int screenY, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        var srcDc = GetDC(0);
        if (srcDc == 0) return null;
        try
        {
            var bmp = new System.Drawing.Bitmap(w, h);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            var dstDc = g.GetHdc();
            BitBlt(dstDc, 0, 0, w, h, srcDc, screenX, screenY, 0x00CC0020 /*SRCCOPY*/);
            g.ReleaseHdc(dstDc);
            return bmp;
        }
        finally
        {
            ReleaseDC(0, srcDc);
        }
    }
}