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
/// - 窗口类为自注册的自定义类而非 STATIC：STATIC 会把鼠标命中透传父窗口，
///   导致按下/移动事件收不到（缩放后平移失效的根因）。
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

    // ---- 自定义窗口类（替代 STATIC，确保鼠标消息正确路由）----
    private const string CustomWndClass = "3FCompare_PlayerSurface";
    private static bool _wndClassRegistered;
    private static readonly object _wndClassLock = new();

    /// <summary>注册自定义窗口类。类过程先指向 DefWindowProcW，创建后立刻子类化为
    /// SubclassedWndProc；创建到子类化之间到达的少量消息由 DefWindowProc 安全兜底。</summary>
    private static void EnsureWndClassRegistered()
    {
        if (_wndClassRegistered) return;
        lock (_wndClassLock)
        {
            if (_wndClassRegistered) return;
            var hInst = GetModuleHandleW(null);
            if (hInst == nint.Zero)
                throw new InvalidOperationException("GetModuleHandleW 失败");
            var defProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
            if (defProc == nint.Zero)
                throw new InvalidOperationException("无法取得 DefWindowProcW 地址");
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = defProc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInst,
                hIcon = nint.Zero,
                hCursor = LoadCursorW(nint.Zero, IDC_ARROW),
                hbrBackground = nint.Zero,
                lpszMenuName = null,
                lpszClassName = CustomWndClass,
                hIconSm = nint.Zero,
            };
            if (RegisterClassExW(ref wc) == 0)
            {
                // 并发场景下可能已被同进程抢先注册，视为成功
                if (Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                    throw new InvalidOperationException($"注册窗口类 {CustomWndClass} 失败: Win32 错误 {Marshal.GetLastWin32Error()}");
            }
            _wndClassRegistered = true;
        }
    }

    // ---- 鼠标事件（由 WndProc 转发，MainWindow 订阅处理缩放/平移/选中）----
    /// <summary>左键按下（表面相对坐标）。</summary>
    public event Action<double, double>? SurfacePressed;
    /// <summary>鼠标移动（表面相对坐标；拖动期间持续触发）。</summary>
    public event Action<double, double>? SurfaceMoved;
    /// <summary>左键释放（表面相对坐标）。</summary>
    public event Action<double, double>? SurfaceReleased;

    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_MOUSEWHEEL = 0x020A;

    // ---- Win32 常量 ----
    private const int GWLP_WNDPROC = -4;
    private const uint CS_DBLCLKS = 0x0008;
    private const nint IDC_ARROW = 32512;              // MAKEINTRESOURCE(IDC_ARROW)
    private const nint MA_NOACTIVATE = 3;              // 点击不激活/不夺焦点
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    // ---- 共享视图变换状态（MainWindow.ApplyViewTransform 更新，各表面读取绘制小地图）----
    public static float SharedZoom { get; set; } = 1f;
    public static float SharedPanX { get; set; }
    public static float SharedPanY { get; set; }
    /// <summary>缩放小地图开关（由设置窗口控制）。</summary>
    public static bool SharedMinimapEnabled { get; set; } = true;

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

    // 基类签名返回非空 IPlatformHandle（Avalonia 12 NativeControlHost），此处不会返回 null
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        EnsureWndClassRegistered();
        Console.Error.WriteLine($"[PlayerSurface:{_index}] CreateNativeControlCore");
        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        _hwnd = CreateWindowExW(0, CustomWndClass, null,
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
        Console.Error.WriteLine($"[PlayerSurface:{_index}] DestroyNativeControlCore hwnd={_hwnd}");
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
        // 真实模式且会话正常：所有消息透传给原始过程，完全不干预 D3D11 渲染窗口的消息处理。
        // 此前在 WM_PAINT/WM_ERASEBKGND 上做 GDI 绘制或返回自定义值，
        // 会与引擎的 DXGI SwapChain Present 管线冲突，导致视频卡死。
        bool isRealModeActive = _realMode && _session is not null && !_failed;

        switch (msg)
        {
            case WM_ERASEBKGND:
                return isRealModeActive
                    ? CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam)
                    : 1;
            case WM_PAINT:
                if (isRealModeActive)
                {
                    // 仅验证更新区域，防止 Windows 无限发送 WM_PAINT，但不执行任何 GDI 操作
                    var ps = new PAINTSTRUCT();
                    var hdc = BeginPaint(_hwnd, ref ps);
                    EndPaint(_hwnd, ref ps);
                    return 0;
                }
                PaintSelf();
                return 0;
            case WM_MOUSEACTIVATE:
                // 点击表面不夺走键盘焦点：Space/方向键等快捷键保持由顶层窗口处理
                return MA_NOACTIVATE;
            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK: // CS_DBLCLKS 下第二次快速按下以 DBLCLK 到达，同为拖拽起点
            {
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                // 3FCompare 优化：移除高频 WriteLine（stderr 管道已满时 WndProc 阻塞）
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
        }
        return CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam);
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
// ---- GDI 对象缓存（有界防止内核句柄泄漏）----
    private const int MaxFontCache = 16;
    private const int MaxGradCache = 32;
    private static readonly Dictionary<int, System.Drawing.Font> _bigFontCache = new();
    private static readonly Dictionary<int, System.Drawing.Font> _timeFontCache = new();
    private static readonly Dictionary<(int hue, int w, int h), System.Drawing.Drawing2D.LinearGradientBrush?> _gradCache = new();
    private static readonly object _gradCacheLock = new();

    private void PaintSelf()
    {
        var ps = new PAINTSTRUCT();
        var hdc = BeginPaint(_hwnd, ref ps);
        if (hdc == nint.Zero) return;
        try
        {
            // 真实模式且会话正常：仅验证更新区域，不做 GDI 绘制。
            // GDI 绘制在 D3D11 SwapChain 的 HWND 上会干扰 Present 管线导致视频卡死
            if (_realMode && _session is not null && !_failed)
                return;

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
        int rw = rect.Width, rh = rect.Height;
// 渐变刷按 (hue, w, h) 缓存：同尺寸同色相不重复创建 GDI 渐变对象
	        lock (_gradCacheLock)
	        {
	            var key = (hue, rw, rh);
	            if (!_gradCache.TryGetValue(key, out var cached) || cached is null)
	            {
	                if (_gradCache.Count >= MaxGradCache) _gradCache.Clear();
	                _gradCache[key] = cached = new System.Drawing.Drawing2D.LinearGradientBrush(
	                    rect, ColorFromHsv(hue, 0.55f, 0.35f), ColorFromHsv((hue + 60) % 360, 0.65f, 0.18f),
	                    System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
	            }
	            g.FillRectangle(cached, rect);
	        }

	        var frame = _lastSnapshot?.FrameIndex ?? 0;
	        var pos = _lastSnapshot?.Position100ns ?? 0;
	        var frameText = $"FRAME {frame:D6}";
	        int bigSize = Math.Max(14, rect.Width / 22);
	        if (!_bigFontCache.TryGetValue(bigSize, out var bigFont))
	        {
	            if (_bigFontCache.Count >= MaxFontCache) _bigFontCache.Clear();
	            _bigFontCache[bigSize] = bigFont = new System.Drawing.Font("Consolas", bigSize, System.Drawing.FontStyle.Bold);
	        }
	        var size = g.MeasureString(frameText, bigFont);
	        g.DrawString(frameText, bigFont, BrushTextWhite,
	            (rect.Width - size.Width) / 2f, (rect.Height - size.Height) / 2f);

	        var timeText = TimeSpan.FromTicks(pos).ToString(@"hh\:mm\:ss\.fff");
	        int timeSize = Math.Max(10, rect.Width / 40);
	        if (!_timeFontCache.TryGetValue(timeSize, out var timeFont))
	        {
	            if (_timeFontCache.Count >= MaxFontCache) _timeFontCache.Clear();
	            _timeFontCache[timeSize] = timeFont = new System.Drawing.Font("Consolas", timeSize);
	        }
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

        // 缩放小地图（缩放 > 1 且开启时显示）
        if (SharedMinimapEnabled && SharedZoom > 1.001f)
        {
            var miniSize = Math.Min(rect.Width, rect.Height) / 5;
            var miniRect = new System.Drawing.Rectangle(rect.Right - miniSize - 8, rect.Bottom - miniSize - 8, miniSize, miniSize);
            // 半透明背景
            using var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(160, 20, 20, 24));
            g.FillRectangle(bgBrush, miniRect);
            // 边框
            using var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(100, 100, 110), 1);
            g.DrawRectangle(borderPen, miniRect);
            // 视口指示器（当前缩放/平移对应的可见区域）
            var viewW = miniSize / SharedZoom;
            var viewH = miniSize / SharedZoom;
            // panX/panY 范围 [-1,1]，映射到小地图偏移
            var vpX = (miniSize - viewW) / 2f + (SharedPanX * (miniSize - viewW) / 2f);
            var vpY = (miniSize - viewH) / 2f + (SharedPanY * (miniSize - viewH) / 2f);
            using var vpPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, 255, 200, 64), 1.5f);
            g.DrawRectangle(vpPen, miniRect.X + vpX, miniRect.Y + vpY, viewW, viewH);
            // 缩放比例文字
            var zoomText = $"x{SharedZoom:0.#}";
            using var zoomFont = new System.Drawing.Font("Consolas", 7);
            g.DrawString(zoomText, zoomFont, System.Drawing.Brushes.White, miniRect.X + 2, miniRect.Y + 2);
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
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public nint HDC; public bool fErase; public RECT rcPaint; public bool fRestore; public bool fIncUpdate; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint GetProcAddress(nint module, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadCursorW(nint hInstance, nint lpCursorName);

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
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
}
