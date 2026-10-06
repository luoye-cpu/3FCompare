using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// WinUi —— 实机 UI 探针：按标题找窗口、枚举子窗口（类名/文本/矩形/可见性）、
// 以及把一次真实鼠标点击投递到指定子窗口中心。
//
// 为什么自己写：LakeUI 是自绘控件，但每个 WinForms 控件仍有自己的 HWND，
//   GetWindowTextW 能取到 Control.Text ⇒ 可以按文本定位「常见图片」这类档位；
//   点击用 PostMessage(WM_LBUTTONDOWN/UP) 走控件自己的鼠标管线，
//   与焦点/z 序无关（截图判据同理不能依赖焦点）。
public class WinUi {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumCb cb, IntPtr l);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")]
    static extern IntPtr PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr h);

    delegate bool EnumCb(IntPtr h, IntPtr l);

    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct POINT { public int X, Y; }

    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    const int MK_LBUTTON = 0x0001;

    static List<IntPtr> found = new List<IntPtr>();
    static EnumCb keeper;

    static IntPtr Top(string title) {
        IntPtr h = FindWindowW(null, title);
        if (h == IntPtr.Zero) throw new Exception("WINDOW NOT FOUND: " + title);
        return h;
    }

    static bool Cb(IntPtr h, IntPtr l) { found.Add(h); return true; }

    static List<IntPtr> Children(IntPtr top) {
        found = new List<IntPtr>();
        keeper = new EnumCb(Cb);
        EnumChildWindows(top, keeper, IntPtr.Zero);
        return found;
    }

    static string Describe(IntPtr top, IntPtr h, int idx) {
        var cls = new StringBuilder(256); GetClassNameW(h, cls, 256);
        var txt = new StringBuilder(512); GetWindowTextW(h, txt, 512);
        RECT r; GetWindowRect(h, out r);
        return string.Format("#{0} hwnd={1} class={2} text=\"{3}\" vis={4} {5}x{6}@{7},{8}",
            idx, h.ToInt64(), cls, txt, IsWindowVisible(h) ? "Y" : "N",
            r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top);
    }

    // 列出全部子窗口；-OnlyText 时只列文本非空或类名含 LakeUI 的
    public static List<string> Dump(string title) {
        var top = Top(title);
        var list = new List<string> { "TOP " + top.ToInt64() + " " + Describe(top, top, -1) };
        int i = 0;
        foreach (var h in Children(top)) list.Add(Describe(top, h, i++));
        return list;
    }

    // 按文本找子窗口并点击其中心；返回命中与点击信息
    public static List<string> ClickByText(string title, string text) {
        var top = Top(title);
        var outp = new List<string>();
        int i = 0;
        foreach (var h in Children(top)) {
            var txt = new StringBuilder(512); GetWindowTextW(h, txt, 512);
            if (string.Equals(txt.ToString(), text, StringComparison.Ordinal)) {
                outp.Add("命中 " + Describe(top, h, i));
                outp.Add(ClickChildAt(h));
                return outp;
            }
            i++;
        }
        outp.Add("未找到文本为 \"" + text + "\" 的子窗口（共 " + i + " 个子窗口）");
        return outp;
    }

    public static List<string> ClickByIndex(string title, int index) {
        var top = Top(title);
        var cs = Children(top);
        if (index < 0 || index >= cs.Count) return new List<string> { "索引越界，子窗口数=" + cs.Count };
        return new List<string> { "目标 " + Describe(top, cs[index], index), ClickChildAt(cs[index]) };
    }

    // 直接点某个类名+序号（LakeUI 自绘的选项卡项没有独立 HWND 时用屏幕坐标）
    public static List<string> ClickScreen(string title, int x, int y) {
        var top = Top(title);
        var list = Children(top);
        for (int i = 0; i < list.Count; i++) {
            RECT r; GetWindowRect(list[i], out r);
            if (r.Left <= x && x < r.Right && r.Top <= y && y < r.Bottom) {
                var rel = new POINT { X = x - r.Left, Y = y - r.Top };
                return new List<string> {
                    "按屏幕坐标命中 #" + i + " " + Describe(top, list[i], i),
                    PostClick(list[i], rel.X, rel.Y)
                };
            }
        }
        return new List<string> { "屏幕坐标 " + x + "," + y + " 不在任何子窗口内" };
    }

    static string ClickChildAt(IntPtr h) {
        RECT c; GetClientRect(h, out c);
        return PostClick(h, c.Right / 2, c.Bottom / 2);
    }

    // 只投一次 WM_MOUSEMOVE（不按下）：LakeUI 的底栏是"悬停才显形"的，
    // 不先 hover 就点，控件可能收下消息但被淡出状态忽略。
    public static string Hover(string title, int x, int y) {
        var top = Top(title);
        PostMessageW(top, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((y << 16) | (x & 0xFFFF)));
        return "已 hover " + title + " 客户区(" + x + "," + y + ")";
    }

    // ── 真实点击：置前 + 移动系统光标 + mouse_event。
    //    PostMessage 的假点击会被 LakeUI 的"悬停才显形"管线忽略，且 PrintWindow 拍不到
    //    D2D 合成的覆盖层 ⇒ 要拿"用户看得见"的证据，只能走真实输入路径。
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;

    // ── 按进程枚举顶层窗口标题：双击后"谁接手了"就看这个。
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb2 cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    delegate bool EnumCb2(IntPtr h, IntPtr l);

    static List<string> titles = new List<string>();
    static uint wantedPid;
    static EnumCb2 keeper2;

    static bool CbTitle(IntPtr h, IntPtr l) {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != wantedPid) return true;
        if (!IsWindowVisible(h)) return true;
        var t = new StringBuilder(512); GetWindowTextW(h, t, 512);
        if (t.Length > 0) titles.Add(t.ToString());
        return true;
    }

    public static List<string> TitlesOfProcess(int pid) {
        titles = new List<string>(); wantedPid = (uint)pid;
        keeper2 = new EnumCb2(CbTitle);
        EnumWindows(keeper2, IntPtr.Zero);
        return titles;
    }

    public static string WindowTitle(long hwndValue) {
        var t = new StringBuilder(512);
        GetWindowTextW(new IntPtr(hwndValue), t, 512);
        return t.ToString();
    }

    public static string Focus(string title) {
        var top = Top(title);
        return "SetForegroundWindow=" + SetForegroundWindow(top) + " hwnd=" + top.ToInt64();
    }

    public static string RealClick(long hwndValue, int clientX, int clientY) {
        IntPtr h = hwndValue != 0 ? new IntPtr(hwndValue) : IntPtr.Zero;
        if (h != IntPtr.Zero) SetForegroundWindow(h);
        var p = new POINT { X = clientX, Y = clientY };
        if (h != IntPtr.Zero) { if (!ClientToScreen(h, ref p)) return "ClientToScreen 失败"; }
        SetCursorPos(p.X, p.Y);
        System.Threading.Thread.Sleep(120);          // 让悬停显形管线跑起来
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        return string.Format("真实点击 屏幕({0},{1}) hwnd={2}", p.X, p.Y, h.ToInt64());
    }

    static string PostClick(IntPtr h, int x, int y) {
        IntPtr lp = (IntPtr)((y << 16) | (x & 0xFFFF));
        PostMessageW(h, WM_MOUSEMOVE, IntPtr.Zero, lp);
        PostMessageW(h, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lp);
        System.Threading.Thread.Sleep(60);
        PostMessageW(h, WM_LBUTTONUP, IntPtr.Zero, lp);
        uint dpi = GetDpiForWindow(h);
        return string.Format("点击 hwnd={0} 客户区({1},{2}) dpi={3}", h.ToInt64(), x, y, dpi);
    }

    // ── 按 HWND 抓图：PrintWindow(PW_RENDERFULLCONTENT) 由 DWM 侧渲染到内存 DC，
    //    与 z 序、遮挡、焦点都无关（docs/07 §2 已证 gdigrab 抓 D3D 窗口全黑，故必须走这条）。
    [DllImport("user32.dll")]
    static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);

    public static string Capture(long hwndValue, string path) {
        return Capture(hwndValue, path, 0, 0);
    }

    // PowerShell 默认是 DPI-不感知进程：GetWindowRect 会返回虚报的 96dpi 尺寸（144dpi 下
    // 1198x748 被报成 799x499），PrintWindow 画进这个较小的 DC ⇒ 底部内容被切掉。
    // 所以允许调用方（DPI 感知的应用自己报的尺寸）显式给宽高。
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
    static bool dpiAwareTried;

    public static string Capture(long hwndValue, string path, int forcedW, int forcedH) {
        if (!dpiAwareTried) {   // 不声明就拿到 96dpi 虚报的矩形，PrintWindow 只能画到左上角一块
            dpiAwareTried = true;
            try { SetProcessDPIAware(); } catch { }
        }
        IntPtr h = new IntPtr(hwndValue);
        RECT r; if (!GetWindowRect(h, out r)) return "GetWindowRect 失败";
        int w = forcedW > 0 ? forcedW : r.Right - r.Left;
        int ht = forcedH > 0 ? forcedH : r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return "尺寸非法 " + w + "x" + ht;
        IntPtr hdc = GetWindowDC(h);
        if (hdc == IntPtr.Zero) return "GetWindowDC 失败";
        try {
            var bmp = new System.Drawing.Bitmap(w, ht, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var g = System.Drawing.Graphics.FromImage(bmp);
            IntPtr gdc = g.GetHdc();
            bool ok = PrintWindow(h, gdc, 2 /*PW_RENDERFULLCONTENT*/);
            g.ReleaseHdc(gdc); g.Dispose();
            if (!ok) { bmp.Dispose(); return "PrintWindow 失败"; }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            bmp.Dispose();
            return "saved " + path + " " + w + "x" + ht + " vis=" + (IsWindowVisible(h) ? "Y" : "N");
        } finally { ReleaseDC(h, hdc); }
    }

    // 按标题抓（标题会被应用改掉，故仅作便捷入口）
    public static long FindWindowByTitle(string title) { return Top(title).ToInt64(); }

    public static string CaptureByTitle(string title, string path) {
        return Capture(Top(title).ToInt64(), path);
    }
}
