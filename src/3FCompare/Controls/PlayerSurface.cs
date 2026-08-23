using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using _3FCompare.Core.Backend;

namespace _3FCompare.Controls;

/// <summary>单路视频表面（NativeControlHost 生产版）。
/// - 子 HWND：真实模式交给 3FP 会话作 D3D11 输出窗口；演示模式 GDI 自绘合成画面。
/// - 鼠标输入：不依赖 Avalonia 事件路由（NativeControlHost 子 HWND 不可靠），
///   直接在子类化 WndProc 中处理 Win32 鼠标消息并暴露为 C# 事件。</summary>
public sealed class PlayerSurface : NativeControlHost
{
    private readonly int _index;
    private readonly bool _realMode;
    private IPlayerSession? _session;
    private bool _selected;
    private bool _failed;
    private string _error = string.Empty;
    private string _fileName = string.Empty;
    private EngineSnapshot? _lastSnapshot;

    private nint _hwnd;
    private nint _origWndProc;
    private readonly WndProcDelegate _subclassProc;

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    // ---- 鼠标事件（由 WndProc 转发，MainWindow 订阅处理缩放/平移/选中）----
    /// <summary>滚轮缩放。delta > 0 = 放大。</summary>
    public event Action<float>? WheelZoom;
    /// <summary>左键按下（表面相对坐标）。</summary>
    public event Action<double, double>? SurfacePressed;
    /// <summary>鼠标移动（表面相对坐标；拖动期间持续触发）。</summary>
    public event Action<double, double>? SurfaceMoved;
    /// <summary>左键释放（表面相对坐标）。</summary>
    public event Action<double, double>? SurfaceReleased;

    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_MOUSEWHEEL = 0x020A;

    // ---- 共享视图变换状态（MainWindow.ApplyViewTransform 更新，各表面读取绘制小地图）----
    public static float SharedZoom { get; set; } = 1f;
    public static float SharedPanX { get; set; }
    public static float SharedPanY { get; set; }

    public int Index => _index;
    public bool RealMode => _realMode;
    public nint Hwnd => _hwnd;

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; InvalidateOverlay(); } }
    }

    public bool IsFailed
    {
        get => _failed;
        set { _failed = value; InvalidateOverlay(); }
    }

    public string ErrorText
    {
        get => _error;
        set { _error = value; InvalidateOverlay(); }
    }

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; InvalidateOverlay(); }
    }

    public PlayerSurface(int index, bool realMode)
    {
        _index = index;
        _realMode = realMode;
        _subclassProc = SubclassedWndProc;
        Focusable = false;
    }

    // ---------- 子窗口生命周期 ----------

    protected override IPlatformHandle? CreateNativeControlCore(IPlatformHandle parent)
    {
        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        _hwnd = CreateWindowExW(0, "STATIC", null,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 640, 360,
            parent.Handle, nint.Zero, nint.Zero, nint.Zero);
        if (_hwnd == nint.Zero)
            throw new InvalidOperationException($"CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");

        _origWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_subclassProc));
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (_hwnd != nint.Zero && _origWndProc != nint.Zero)
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _origWndProc);
        if (_hwnd != nint.Zero)
            DestroyWindow(_hwnd);
        _hwnd = nint.Zero;
    }

    public async System.Threading.Tasks.Task<nint> EnsureHwndAsync(int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (_hwnd == nint.Zero && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(50);
        return _hwnd;
    }

    // ---------- 会话绑定 / 快照 ----------

    public void AttachSession(IPlayerSession session) { _session = session; InvalidateOverlay(); }
    public void DetachSession() { _session = null; InvalidateOverlay(); }

    public void UpdateSnapshot(EngineSnapshot? snapshot)
    {
        // 演示模式脏检查：同帧同位置跳过重绘
        if (!_realMode && snapshot is not null && _lastSnapshot is not null &&
            snapshot.FrameIndex == _lastSnapshot.FrameIndex &&
            snapshot.Position100ns == _lastSnapshot.Position100ns) return;
        _lastSnapshot = snapshot;
        if (!_realMode || _failed) InvalidateOverlay();
    }

    public void InvalidateOverlay()
    {
        if (_hwnd != nint.Zero) InvalidateRect(_hwnd, nint.Zero, false);
    }

    // ---------- WndProc：鼠标消息直接处理 + GDI 绘制 ----------

    private nint SubclassedWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1;
            case WM_PAINT:
                PaintSelf();
                return 0;
            case WM_LBUTTONDOWN:
            {
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                SurfacePressed?.Invoke(x, y);
                SetCapture(hwnd); // 捕获鼠标：移出子窗口仍持续接收 Move/Up
                return 0;
            }
            case WM_LBUTTONUP:
            {
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                SurfaceReleased?.Invoke(x, y);
                ReleaseCapture();
                return 0;
            }
            case WM_MOUSEMOVE:
            {
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                SurfaceMoved?.Invoke(x, y);
                return 0;
            }
            case WM_MOUSEWHEEL:
            {
                var delta = (short)((wParam >> 16) & 0xFFFF);
                var factor = delta > 0 ? 1.15f : 1f / 1.15f;
                WheelZoom?.Invoke(factor);
                return 0;
            }
        }
        return CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>Avalonia 层滚轮处理：WM_MOUSEWHEEL 发给焦点窗口后经视觉树路由到此。</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? 1.15f : 1f / 1.15f;
        WheelZoom?.Invoke(factor);
        e.Handled = true;
    }

    // ---------- GDI 绘制（WM_PAINT） ----------

    private static readonly System.Drawing.SolidBrush BrushPanelBg = new(System.Drawing.Color.FromArgb(30, 30, 36));
    private static readonly System.Drawing.SolidBrush BrushTextWhite = new(System.Drawing.Color.FromArgb(220, 255, 255, 255));
    private static readonly System.Drawing.SolidBrush BrushTextShadow = new(System.Drawing.Color.FromArgb(120, 0, 0, 0));
    private static readonly System.Drawing.SolidBrush BrushTextDim = new(System.Drawing.Color.FromArgb(180, 255, 255, 255));
    private static readonly System.Drawing.SolidBrush BrushErrorRed = new(System.Drawing.Color.FromArgb(255, 100, 100));
    private static readonly System.Drawing.SolidBrush BrushD3DTagBg = new(System.Drawing.Color.FromArgb(160, 0, 120, 0));
    private static readonly System.Drawing.Font FontOverlay = new("Microsoft YaHei UI", 9f);
    private static readonly System.Drawing.Font FontTag = new("Segoe UI", 8f);
    private static readonly Dictionary<int, System.Drawing.Font> _bigFontCache = new();
    private static readonly Dictionary<int, System.Drawing.Font> _timeFontCache = new();

    private void PaintSelf()
    {
        var ps = new PAINTSTRUCT();
        var hdc = BeginPaint(_hwnd, ref ps);
        if (hdc == nint.Zero) return;
        try
        {
            GetClientRect(_hwnd, out var rc);
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) return;
            using var g = System.Drawing.Graphics.FromHdc(hdc);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new System.Drawing.Rectangle(0, 0, w, h);

            if (!_realMode || _session is null)
                PaintSimulatedContent(g, rect);
            PaintBorder(g, rect);
            PaintOverlayInfo(g, rect);
        }
        finally
        {
            EndPaint(_hwnd, ref ps);
        }
    }

    private void PaintSimulatedContent(System.Drawing.Graphics g, System.Drawing.Rectangle rect)
    {
        if (_failed)
        {
            g.FillRectangle(BrushPanelBg, rect);
            return;
        }
        var hue = (_index * 47) % 360;
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect, ColorFromHsv(hue, 0.55f, 0.35f), ColorFromHsv((hue + 60) % 360, 0.65f, 0.18f),
            System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, rect);

        var frame = _lastSnapshot?.FrameIndex ?? 0;
        var pos = _lastSnapshot?.Position100ns ?? 0;
        var frameText = $"FRAME {frame:D6}";
        int bigSize = Math.Max(14, rect.Width / 22);
        if (!_bigFontCache.TryGetValue(bigSize, out var bigFont))
            _bigFontCache[bigSize] = bigFont = new System.Drawing.Font("Consolas", bigSize, System.Drawing.FontStyle.Bold);
        var size = g.MeasureString(frameText, bigFont);
        g.DrawString(frameText, bigFont, BrushTextWhite,
            (rect.Width - size.Width) / 2f, (rect.Height - size.Height) / 2f);

        var timeText = TimeSpan.FromTicks(pos).ToString(@"hh\:mm\:ss\.fff");
        int timeSize = Math.Max(10, rect.Width / 40);
        if (!_timeFontCache.TryGetValue(timeSize, out var timeFont))
            _timeFontCache[timeSize] = timeFont = new System.Drawing.Font("Consolas", timeSize);
        g.DrawString(timeText, timeFont, BrushTextDim,
            (rect.Width - g.MeasureString(timeText, timeFont).Width) / 2f, rect.Height * 0.55f);
    }

    private void PaintBorder(System.Drawing.Graphics g, System.Drawing.Rectangle rect)
    {
        using var pen = new System.Drawing.Pen(
            _selected ? System.Drawing.Color.FromArgb(255, 200, 64) : System.Drawing.Color.FromArgb(60, 60, 66),
            _selected ? 3f : 1f);
        g.DrawRectangle(pen, System.Drawing.Rectangle.Inflate(rect, -1, -1));
    }

    private void PaintOverlayInfo(System.Drawing.Graphics g, System.Drawing.Rectangle rect)
    {
        if (_failed)
        {
            g.DrawString($"✖ {_error}", FontOverlay, BrushErrorRed, new System.Drawing.RectangleF(8, 8, rect.Width - 16, 60));
        }
        else
        {
            var label = $"[{_index + 1}] {_fileName}";
            g.DrawString(label, FontOverlay, BrushTextShadow, new System.Drawing.PointF(10, 10));
            g.DrawString(label, FontOverlay, BrushTextWhite, new System.Drawing.PointF(9, 9));
        }
        if (_realMode)
        {
            g.FillRectangle(BrushD3DTagBg, rect.Right - 44, 6, 38, 18);
            g.DrawString("D3D11", FontTag, System.Drawing.Brushes.White, rect.Right - 42, 8);
        }
    }

    private static System.Drawing.Color ColorFromHsv(float h, float s, float v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        var m = v - c;
        (float r, float g, float b) = h switch
        {
            < 60 => (c, x, 0f), < 120 => (x, c, 0f), < 180 => (0f, c, x),
            < 240 => (0f, x, c), < 300 => (x, 0f, c), _ => (c, 0f, x),
        };
        return System.Drawing.Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    // ---- Win32 ----
    private const int GWLP_WNDPROC = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public nint HDC; public bool fErase; public RECT rcPaint; public bool fRestore; public bool fIncUpdate; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newProc);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProcW(nint prevProc, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}
