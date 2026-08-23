using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace _3FCompare.Platform;

/// <summary>Win32 子窗口承载器：NativeControlHost 创建真实 HWND，
/// 供原生 D3D 输出或 GDI 直接绘制。M0 PoC 验证对象；M2 由 PlayerSurfaceHost
/// （CreateNativeControlCore 托管定位）替代。</summary>
public sealed class HostSurface : NativeControlHost
{
    private IntPtr _hwnd;

    public nint Hwnd => _hwnd;

    public HostSurface()
    {
        // AttachedToLogicalTree 后才有顶层窗口，此时再创建子 HWND
        AttachedToVisualTree += (_, _) =>
        {
            _hwnd = CreateChildWindow();
            // NativeControlHost 的子控件定位由 QueryContinueChild/平台实现处理；
            // PoC 阶段子窗口尺寸固定，M2 做跟随布局。
        };
        DetachedFromVisualTree += (_, _) => DestroyWindow(_hwnd);
    }

    /// <summary>创建子窗口并返回其 HWND（父 = Avalonia 顶层窗口）。</summary>
    private IntPtr CreateChildWindow()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var parent = topLevel?.TryGetPlatformHandle()?.Handle ?? GetActiveWindow();
        if (parent == IntPtr.Zero)
            throw new InvalidOperationException("无法获取 Avalonia 顶层 HWND");

        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        var hwnd = CreateWindowExW(
            0, "STATIC", null, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 800, 450, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
        return hwnd;
    }

    /// <summary>PoC-A：GDI 绘制测试色块（渐变 + 文字），验证子窗口可见性与 DPI 缩放。</summary>
    public void DrawTestPattern()
    {
        if (_hwnd == IntPtr.Zero) return;
        var hdc = GetDC(_hwnd);
        try
        {
            if (!GetClientRect(_hwnd, out var rc)) return;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;

            using var g = System.Drawing.Graphics.FromHdc(hdc);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Color.FromArgb(30, 90, 200),
                System.Drawing.Color.FromArgb(15, 15, 20), 45f);
            g.FillRectangle(brush, 0, 0, w, h);
            g.DrawString($"HWND 承载成功  {w}x{h}",
                new System.Drawing.Font("Segoe UI", 16),
                System.Drawing.Brushes.White, 24, 24);
        }
        finally
        {
            ReleaseDC(_hwnd, hdc);
        }
    }

    // ---- Win32 P/Invoke（自声明，避免依赖 Avalonia internal API）----

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
