using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using _3FCompare.Core.Backend;

namespace _3FCompare.Controls;

/// <summary>单路视频表面（NativeControlHost 生产版，M2）。
/// - 承载 Win32 子 HWND：真实模式交给 3FP 会话作 D3D11 输出窗口；演示模式 GDI 自绘合成画面。
/// - 子窗口带 WS_EX_TRANSPARENT：鼠标输入穿透回 Avalonia 层（解决原生子窗口「airspace」
///   吞输入问题），滚轮/点击/拖拽均以 Avalonia 事件到达本控件。
/// - 覆盖层（选中边框/[n] 文件名/D3D11 标记/错误）经子类化 WndProc 用 GDI 画在 WM_PAINT
///   ——与 WinForms PlayerSurface 相同的「D3D 窗口上画信息层」模式。</summary>
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

    private nint _hwnd;                 // 子窗口（由 CreateNativeControlCore 创建）
    private nint _origWndProc;          // 原窗口过程（STATIC 默认）
    private readonly WndProcDelegate _subclassProc; // 防 GC

    private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;

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

    /// <summary>点击（按下-抬起位移小于阈值）选中本路。</summary>
    public event EventHandler? SurfaceClicked;

    public PlayerSurface(int index, bool realMode)
    {
        _index = index;
        _realMode = realMode;
        _subclassProc = SubclassedWndProc;
        Cursor = new Cursor(StandardCursorType.Hand);
        Focusable = false;
    }

    // ---------- 子窗口生命周期（NativeControlHost 托管定位/尺寸/DPI） ----------

    protected override IPlatformHandle? CreateNativeControlCore(IPlatformHandle parent)
    {
        const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        const uint WS_EX_TRANSPARENT = 0x00000020; // 输入穿透：鼠标事件落回 Avalonia 层

        _hwnd = CreateWindowExW(
            WS_EX_TRANSPARENT, "STATIC", null,
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

    /// <summary>等待子 HWND 就绪（NativeControlHost 附件后由平台层创建）。</summary>
    public async System.Threading.Tasks.Task<nint> EnsureHwndAsync(int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (_hwnd == nint.Zero && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(50);
        return _hwnd;
    }

    // ---------- 会话绑定 / 快照 ----------

    public void AttachSession(IPlayerSession session)
    {
        _session = session;
        InvalidateOverlay();
    }

    public void DetachSession()
    {
        _session = null;
        InvalidateOverlay();
    }

    public void UpdateSnapshot(EngineSnapshot? snapshot)
    {
        _lastSnapshot = snapshot;
        // 演示模式需要按帧重绘；真实模式仅状态性覆盖层变化才需要
        if (!_realMode || _failed)
            InvalidateOverlay();
    }

    /// <summary>请求重画覆盖层（GDI，走 WM_PAINT）。</summary>
    public void InvalidateOverlay()
    {
        if (_hwnd != nint.Zero)
            InvalidateRect(_hwnd, nint.Zero, false);
    }

    // ---------- 输入（子窗口输入穿透，Avalonia 事件直达本控件） ----------

    private Point? _pressPoint;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressPoint = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressPoint is { } press)
        {
            var up = e.GetPosition(this);
            if (Math.Abs(up.X - press.X) < 4 && Math.Abs(up.Y - press.Y) < 4)
                SurfaceClicked?.Invoke(this, EventArgs.Empty);
            _pressPoint = null;
        }
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    // ---------- GDI 绘制（WM_PAINT，WinForms PlayerSurface 移植） ----------

    private nint SubclassedWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return 1; // 不擦背景（避免闪烁/遮 D3D）
            case WM_PAINT:
                PaintSelf();
                return 0;
        }
        return CallWindowProcW(_origWndProc, hwnd, msg, wParam, lParam);
    }

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
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 30, 36));
            g.FillRectangle(bg, rect);
            return;
        }

        var hue = (_index * 47) % 360;
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
            rect,
            ColorFromHsv(hue, 0.55f, 0.35f),
            ColorFromHsv((hue + 60) % 360, 0.65f, 0.18f),
            System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, rect);

        var frame = _lastSnapshot?.FrameIndex ?? 0;
        var pos = _lastSnapshot?.Position100ns ?? 0;
        var frameText = $"FRAME {frame:D6}";
        using var bigFont = new System.Drawing.Font("Consolas", Math.Max(14f, rect.Width / 22f), System.Drawing.FontStyle.Bold);
        using var bigBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(220, 255, 255, 255));
        var size = g.MeasureString(frameText, bigFont);
        g.DrawString(frameText, bigFont, bigBrush,
            (rect.Width - size.Width) / 2f, (rect.Height - size.Height) / 2f);

        var timeText = TimeSpan.FromTicks(pos).ToString(@"hh\:mm\:ss\.fff");
        using var timeFont = new System.Drawing.Font("Consolas", Math.Max(10f, rect.Width / 40f));
        using var timeBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(180, 255, 255, 255));
        g.DrawString(timeText, timeFont, timeBrush,
            (rect.Width - g.MeasureString(timeText, timeFont).Width) / 2f, rect.Height * 0.55f);
    }

    private void PaintBorder(System.Drawing.Graphics g, System.Drawing.Rectangle rect)
    {
        var color = _selected
            ? System.Drawing.Color.FromArgb(255, 200, 64)
            : System.Drawing.Color.FromArgb(60, 60, 66);
        using var pen = new System.Drawing.Pen(color, _selected ? 3f : 1f);
        g.DrawRectangle(pen, System.Drawing.Rectangle.Inflate(rect, -1, -1));
    }

    private void PaintOverlayInfo(System.Drawing.Graphics g, System.Drawing.Rectangle rect)
    {
        using var font = new System.Drawing.Font("Microsoft YaHei UI", 9f);
        if (_failed)
        {
            using var red = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 100, 100));
            g.DrawString($"✖ {_error}", font, red, new System.Drawing.RectangleF(8, 8, rect.Width - 16, 60));
        }
        else
        {
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(220, 255, 255, 255));
            using var dark = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(120, 0, 0, 0));
            var label = $"[{_index + 1}] {_fileName}";
            g.DrawString(label, font, dark, new System.Drawing.PointF(10, 10));
            g.DrawString(label, font, white, new System.Drawing.PointF(9, 9));
        }

        if (_realMode)
        {
            using var tagBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(160, 0, 120, 0));
            g.FillRectangle(tagBrush, rect.Right - 44, 6, 38, 18);
            using var tagFont = new System.Drawing.Font("Segoe UI", 8f);
            g.DrawString("D3D11", tagFont, System.Drawing.Brushes.White, rect.Right - 42, 8);
        }
    }

    private static System.Drawing.Color ColorFromHsv(float h, float s, float v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60f) % 2 - 1));
        var m = v - c;
        (float r, float g, float b) = h switch
        {
            < 60 => (c, x, 0f),
            < 120 => (x, c, 0f),
            < 180 => (0f, c, x),
            < 240 => (0f, x, c),
            < 300 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return System.Drawing.Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    // ---------- Win32 ----------

    private const int GWLP_WNDPROC = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT { public nint HDC; public bool fErase; public RECT rcPaint; public bool fRestore; public bool fIncUpdate; public byte bReserved1, bReserved2, bReserved3, bReserved4, bReserved5, bReserved6, bReserved7, bReserved8; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newProc);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProcW(nint prevProc, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hwnd, ref PAINTSTRUCT ps);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
}
