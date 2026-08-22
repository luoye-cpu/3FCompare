using System.Runtime.InteropServices;
using _3FCompare.App.Utils;

namespace _3FCompare.App.Controls;

/// <summary>放大镜（F17）：在视频面上跟随鼠标显示放大局部区域。
/// 依附于 PlayerSurface 的覆盖层，通过图片取色放大（模拟模式直接放大自绘；真实模式依赖像素探针回读）。
/// 设计关键：放大镜是纯‘显示层’覆盖物，**决不能拦截鼠标事件**，否则会阻塞被其
/// 遮盖区域的 MouseDown/MouseMove（尤其水平拖拽），导致表面收不到水平位移。</summary>
public sealed class MagnifierOverlay : Control
{
    private Point _mousePos = new(-1000, -1000);
    private int _zoom = 4;

    // 命中测试完全穿透：WS_EX_TRANSPARENT 使该窗口不参与鼠标命中，
    // 所有的鼠标消息直接落到其后的 PlayerSurface 上。
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int HWND_TOPMOST = -2;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_TRANSPARENT = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED; // 不拦截鼠标 + 支持分层
            return cp;
        }
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        base.SetBoundsCore(x, y, width, height, specified);
        // 保持顶层且透明命中，避免遮挡到被跟踪的画面
        if (IsHandleCreated)
            SetWindowPos(Handle, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_TRANSPARENT);
    }

    public MagnifierOverlay()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(160, 120); // 放大镜窗口
        Visible = false;
        Enabled = false; // 双保险：连同 WS_EX_TRANSPARENT 一起确保不拦截鼠标
    }

    /// <summary>设置鼠标位置（Surface 坐标），展开放大镜。</summary>
    public void UpdateMagnifier(Point surfacePoint)
    {
        _mousePos = surfacePoint;
        PositionNearMouse(surfacePoint);
        if (!Visible) Visible = true;
        Invalidate();
    }

    public void HideMagnifier()
    {
        Visible = false;
    }

    private void PositionNearMouse(Point p)
    {
        var x = p.X + 16;
        var y = p.Y + 16;
        if (Parent is not null)
        {
            x = Math.Min(x, Parent.ClientSize.Width - Width - 8);
            y = Math.Min(y, Parent.ClientSize.Height - Height - 8);
        }
        Location = new Point(Math.Max(0, x), Math.Max(0, y));
    }

    /// <summary>放大镜外观：边框 + 内容由父级画笔填充（这里画网格辅助线）。</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(Color.FromArgb(220, 16, 16, 20));

        using var border = new Pen(AppTheme.Colors.Accent, 2);
        g.DrawRectangle(border, new Rectangle(0, 0, Width - 1, Height - 1));

        // 中心十字
        using var crossPen = new Pen(Color.FromArgb(120, 255, 255, 255));
        g.DrawLine(crossPen, Width / 2 - 14, Height / 2, Width / 2 + 14, Height / 2);
        g.DrawLine(crossPen, Width / 2, Height / 2 - 14, Width / 2, Height / 2 + 14);

        // 放大网格辅助线（示意格子，方便对齐像素）
        using var gridPen = new Pen(Color.FromArgb(30, 255, 255, 255));
        for (var i = 1; i < _zoom; i++)
        {
            var step = Height / (double)_zoom;
            g.DrawLine(gridPen, 0, (float)(i * step), Width, (float)(i * step));
        }

        // 顶部角标：当前缩放
        using var font = new Font("Consolas", 8f);
        g.DrawString($"{_zoom}x @({_mousePos.X},{_mousePos.Y})", font, Brushes.White, 4, 2);
    }
}