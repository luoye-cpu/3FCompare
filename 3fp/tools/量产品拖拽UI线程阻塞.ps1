# 量产品拖拽UI线程阻塞.ps1 —— 真机量"拖拽每一拍把 UI 线程堵多久"
#
# 这一档补的是 docs/交接文档-2026-10-19-图片模式拖拽性能分析.md §5 的读数 2 与 §6 第 4 条：
# 那里"UI 线程被 flip 堵 N ms"只有锁时序推演（Redraw 要 presentMutex_ 是 :6916，Present 在该锁内
# 等 vblank 是 :8005-8080，两条都读源码确证），没有实测。没有这个数，D1（要不要执行修复）就
# 只能靠体感拍。
#
# 量法（不往出货路径里插钩子，纯进程外）：
#   驱动线程  PostMessage 真 WM_LBUTTONDOWN / WM_MOUSEMOVE / WM_LBUTTONUP 进真输出子窗，
#              节拍 8ms（=125Hz，与宿主 PrecisionTimer 请求值同量级）；
#   采样线程  每 4ms 对同一个 HWND 做一次 SendMessageTimeout(WM_NULL, 1000ms) 记往返用时。
#              WM_NULL 只能由目标线程的消息派发处理 ⇒ 往返用时就是"UI 线程多久腾不出手"，
#              这正是体感卡顿的那个操作数；不需要焦点、不需要真光标。
#   抓屏      只在相位边界各一次，用 GDI BitBlt 抓真屏幕区域（DXGI 交换链 PrintWindow 必全黑）。
#
# 臂是阶梯，相邻两条只差一件事；相位顺序就是前置条件（moves 必须在 zoom 之前，否则余量已存在）：
#   idle   零输入                        ⇒ UI 线程本底（应当亚毫秒）
#   moves  按下+移动，但缩放仍在 1.0      ⇒ 只有"消息投递 + 一次 ImageZoom 读"。此档内核余量=False，
#                                         宿主在 播放器图片浏览控制器.vb:271 早退，不提交视图，
#                                         所以它把"消息多"与"内核堵"分开。
#   zoom   连投 8 格滚轮                 ⇒ 走锚定缩放+Redraw 那条同样每拍穿越的路，并把余量造出来
#   drag   按下+来回拖（余量>0）          ⇒ 完整提交路：准备视图 + 平移视图(PanView+Redraw)
#   ctrl   连投 6 格滚轮（末尾正对照）     ⇒ 屏内位移差必须显著；判 DEAD 就整轮不作结论
#                                          （10-19 实测：产品被 Chrome 压住时四臂位移差全 0、
#                                           延迟分布一模一样 —— 没有这条就把量具死读成了"A 不成立"）
# moves 与 drag 的消息负载相同，只差"提不提供视图" ⇒ 只有 drag 的 p99 塌到 vblank 量级才说明机制 A 真；
# 两臂一起高就是消息投递/抓屏的锅，A 不成立。
#
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File 3fp/tools/量产品拖拽UI线程阻塞.ps1 `
#         [-Exe <产品exe>] [-Media <大图>] [-Phases 'idle:6,moves:10,zoom:12,drag:15'] `
#         [-CadenceMs 8] [-SampleMs 4] [-PerfCsv <把 FFF3FP_PERF_* 一起打开，取内核侧 presented/presentwait>]
# 退出码：0 读数有效 / 3 量具死（臂没建立，不产出结论）/ 1 起不来
# 纪律：计时读数本机独跑、期间不改树、不关任何常驻覆盖层（RTSS 在场照旧记账）。

param(
    [string]$Exe = '',
    [string]$Media = '',
    [string]$Phases = 'idle:4,moves:6,zoom:8,drag:8,ctrl:6',
    [int]$CadenceMs = 8,
    [int]$SampleMs = 4,
    [double]$SettleSeconds = 3.0,
    [int]$PreWaitMs = 1500,
    [string]$OutDir = '',
    [switch]$PerfCsv
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $repo '3fp\kernel\FFF.Player\bin\x64\Release\net10.0-windows10.0.26100.0\FFF.Player.exe'
}
if ([string]::IsNullOrWhiteSpace($Media)) {
    $Media = Join-Path $repo '3fp\samples\media\edge\09g_bigblocks_12000x9000.jpg'
}
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Join-Path $repo '_3fc_tmp\drag_quant' }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "缺产品 exe：$Exe" -ForegroundColor Red; exit 1 }
if (-not (Test-Path -LiteralPath $Media)) { Write-Host "缺媒体：$Media" -ForegroundColor Red; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$code = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class DragProbe
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l,
        uint flags, uint timeoutMs, out IntPtr result);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    const uint GW_CHILD = 4;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    public static void BringToTop(IntPtr h)
    {
        // 只置顶+显示，不移不改尺寸：抓屏要的是"这块屏幕区域确实属于被测产品"。
        // 实测被 Chrome 压住时四臂屏内位移差全 0 —— 那是抓到别人的静止画面；
        // HWND_TOP 抬不过一个本身就 topmost 的窗口，所以用 TOPMOST（同一个进程实例，
        // 前后对照两臂都用同一条件，比较口径不变）。
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_SHOWWINDOW);
        SetForegroundWindow(h);
    }

    public static string RectOf(IntPtr h)
    {
        RECT r; if (!GetWindowRect(h, out r)) return "取不到矩形";
        return "(" + r.Left + "," + r.Top + ")..(" + r.Right + "," + r.Bottom + ")";
    }

    static bool Owns(IntPtr h, uint ownPid)
    {
        if (h == IntPtr.Zero) return false;
        uint pid; GetWindowThreadProcessId(h, out pid);
        return pid == ownPid;
    }

    static int CenterX(IntPtr h) { RECT r; GetWindowRect(h, out r); return r.Left + (r.Right - r.Left) / 2; }
    static int CenterY(IntPtr h) { RECT r; GetWindowRect(h, out r); return r.Top + (r.Bottom - r.Top) / 2; }

    public static string OwnerAt(int x, int y, uint ownPid)
    {
        POINT p = new POINT(); p.X = x; p.Y = y;
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return "无窗口";
        uint pid; GetWindowThreadProcessId(h, out pid);
        StringBuilder cls = new StringBuilder(128);
        GetClassNameW(h, cls, 128);
        return (pid == ownPid ? "本进程" : "他进程") + " pid=" + pid + " 类=" + cls;
    }

    /// <summary>最深的、没有子窗的大面积子窗 = 视频输出子窗（播放器画面控件 里 Dock=Fill 的
    /// 那块 Panel）。只按面积挑会挑到它的父容器，父容器上没有 MouseDown/MouseMove 处理器。</summary>
    public static IntPtr BiggestLeafChild(IntPtr parent, uint pid)
    {
        _best = IntPtr.Zero; _bestArea = 0; _ownPid = pid;
        EnumChildWindows(parent, ScanLeaf, IntPtr.Zero);
        return _best;
    }

    static bool ScanLeaf(IntPtr h, IntPtr data)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != _ownPid || !IsWindowVisible(h)) return true;
        if (GetWindow(h, GW_CHILD) != IntPtr.Zero) return true;   // 有子窗 ⇒ 它是容器，不是输出面
        RECT r; if (!GetWindowRect(h, out r)) return true;
        long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
        if (area > _bestArea) { _bestArea = area; _best = h; }
        return true;
    }

    delegate bool EnumProc(IntPtr h, IntPtr data);

    const uint WM_NULL = 0x0000;
    const uint WM_MOUSEMOVE = 0x0200;
    const uint WM_LBUTTONDOWN = 0x0201;
    const uint WM_LBUTTONUP = 0x0202;
    const uint WM_MOUSEWHEEL = 0x020A;
    const uint MK_LBUTTON = 0x0001;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    static readonly IntPtr DPI_AWARE_PER_MONITOR_V2 = new IntPtr(-4);

    static IntPtr _best; static long _bestArea; static uint _ownPid;

    public static string TryPerMonitorAware()
    {
        try
        {
            bool ok = SetProcessDpiAwarenessContext(DPI_AWARE_PER_MONITOR_V2);
            return ok ? "已声明 PerMonitorV2" : "声明失败（进程已有窗口，沿用宿主既有感知度）";
        }
        catch (Exception ex) { return "声明异常 " + ex.GetType().Name; }
    }

    public static IntPtr BiggestChild(IntPtr parent, uint pid)
    {
        _best = IntPtr.Zero; _bestArea = 0; _ownPid = pid;
        EnumChildWindows(parent, Scan, IntPtr.Zero);
        return _best;
    }

    static bool Scan(IntPtr h, IntPtr data)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != _ownPid || !IsWindowVisible(h)) return true;
        RECT r; if (!GetWindowRect(h, out r)) return true;
        long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
        if (area > _bestArea) { _bestArea = area; _best = h; }
        return true;
    }

    public static string Describe(IntPtr h, uint ownPid)
    {
        if (h == IntPtr.Zero) return "零句柄";
        uint pid; GetWindowThreadProcessId(h, out pid);
        StringBuilder cls = new StringBuilder(128);
        GetClassNameW(h, cls, 128);
        RECT r; GetWindowRect(h, out r);
        RECT c; GetClientRect(h, out c);
        uint dpi = GetDpiForWindow(h);
        return "pid=" + pid + ((pid == ownPid) ? "(本进程)" : "(他进程!)") +
               " 类=" + cls + " 窗=" + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
               " 客户=" + (c.Right - c.Left) + "x" + (c.Bottom - c.Top) +
               " dpi=" + dpi + "(" + (dpi / 96.0).ToString("F2", CultureInfo.InvariantCulture) + "x)";
    }

    public static IntPtr HitTest(IntPtr mainHwnd)
    {
        RECT r; if (!GetWindowRect(mainHwnd, out r)) return IntPtr.Zero;
        POINT p = new POINT();
        p.X = r.Left + (r.Right - r.Left) / 2;
        p.Y = r.Top + (r.Bottom - r.Top) / 2;
        return WindowFromPoint(p);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);

    public static string TitleOf(IntPtr h)
    {
        StringBuilder sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        return sb.ToString();
    }

    /// <summary>把抓到的区域写成 24bpp BMP（自己拼文件头，不用 System.Drawing：
    /// pwsh7 的 Add-Type 默认引用集里没有 System.Drawing.Common）。
    /// 这一档存在的理由：屏内位移差为 0 有两种坏法——消息没走到画面 / 抓错了区域，
    /// 光看数字分不开，得留下一张能目视的图。</summary>
    public static string DumpBmp(IntPtr child, string path)
    {
        byte[] raw = Grab(child);
        RECT r; if (!GetWindowRect(child, out r)) return "取不到矩形";
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (raw.Length == 0 || w < 16 || h < 16) return "抓屏为空";
        int stride = ((w * 3 + 3) / 4) * 4;
        int pixelData = 14 + 40;
        long fileSize = pixelData + (long)stride * h;
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0x4D42);              // "BM"
            bw.Write((uint)fileSize); bw.Write((uint)0); bw.Write((uint)pixelData);
            bw.Write((uint)40);                    // BITMAPINFOHEADER
            bw.Write((int)w); bw.Write((int)-h);   // 负高 = 自上而下
            bw.Write((ushort)1); bw.Write((ushort)24); bw.Write((uint)0);
            bw.Write((uint)(stride * h)); bw.Write((int)0); bw.Write((int)0);
            bw.Write((uint)0); bw.Write((uint)0);
            byte[] row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                int srcRow = y * (((w * 4 + 3) / 4) * 4);
                for (int x = 0; x < w; x++)
                {
                    int s = srcRow + x * 4;
                    row[x * 3] = raw[s]; row[x * 3 + 1] = raw[s + 1]; row[x * 3 + 2] = raw[s + 2];
                }
                bw.Write(row);
            }
        }
        return path + " " + w + "x" + h;
    }

    /// <summary>列出所有叶子子窗（HWND + 矩形）。图片模式下产品里同尺寸的全屏面板不止一个，
    /// "面积最大的叶子窗"会挑到压在输出面之上的浮层 —— 消息投给浮层就是什么都不发生。</summary>
    static readonly List<string> LeafLog = new List<string>();
    static bool ScanLeafLog(IntPtr h, IntPtr data)
    {
        uint pid; GetWindowThreadProcessId(h, out pid);
        if (pid != _ownPid || !IsWindowVisible(h)) return true;
        if (GetWindow(h, GW_CHILD) != IntPtr.Zero) return true;
        RECT r; if (!GetWindowRect(h, out r)) return true;
        LeafLog.Add("      hwnd=0x" + ((long)h).ToString("X") + " 窗=" + (r.Right - r.Left) + "x" +
                    (r.Bottom - r.Top) + " @" + r.Left + "," + r.Top);
        return true;
    }

    public static string EnumLeaves(IntPtr parent, uint pid)
    {
        _ownPid = pid;
        LeafLog.Clear();
        EnumChildWindows(parent, ScanLeafLog, IntPtr.Zero);
        return string.Join("\n", LeafLog.ToArray());
    }

    // ── 采样：WM_NULL 往返用时 = UI 线程多久腾不出手 ──
    class Sample { public string Phase; public double TMs; public double Us; public bool Timeout; }
    static readonly object Sync = new object();
    static readonly List<Sample> Samples = new List<Sample>();
    // 同步投递臂（sdrag/smoves）另记一桶：这条量的是"一次输入在 UI 线程里到底花多久"，
    // 就是交接文档 §5 的读数 2；上面那桶量的是"UI 线程多久腾不出手"（旁路 WM_NULL）。
    static readonly List<Sample> InputSamples = new List<Sample>();
    static volatile bool StopSampler;
    static volatile string CurrentPhase = "prep";
    static IntPtr TargetHwnd;

    static void SamplerLoop(int sampleMs)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!StopSampler)
        {
            IntPtr res;
            string phase = CurrentPhase;
            double t = clock.Elapsed.TotalMilliseconds;
            long t0 = Stopwatch.GetTimestamp();
            IntPtr ret = SendMessageTimeoutW(TargetHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 1000, out res);
            double us = (Stopwatch.GetTimestamp() - t0) * 1000000.0 / Stopwatch.Frequency;
            lock (Sync)
            {
                Sample s = new Sample();
                s.Phase = phase; s.TMs = t; s.Us = us; s.Timeout = (ret == IntPtr.Zero);
                Samples.Add(s);
            }
            WaitUntil(clock, t + sampleMs);
        }
    }

    // 混合等待：先粗睡再自旋到点。只 Sleep(1) 在没有 timeBeginPeriod(1) 时粒度是 15.6ms，
    // 会把节拍抹平；只自旋又会给被测机添一条满负载线程。
    static void WaitUntil(Stopwatch clock, double untilMs)
    {
        while (clock.Elapsed.TotalMilliseconds < untilMs - 1.5) Thread.Sleep(1);
        while (clock.Elapsed.TotalMilliseconds < untilMs) Thread.SpinWait(200);
    }

    static IntPtr MakeLParam(int x, int y)
    {
        return new IntPtr((long)(uint)(ushort)x | ((long)(uint)(ushort)y << 16));
    }

    public static void Post(IntPtr h, uint msg, int wParam, int x, int y)
    {
        // 给"投递本身灵不灵"的自检用：PS 侧建一个真 WinForms 面板，按产品同样的
        // DOWN→Capture=True→MOVE→UP 顺序收事件，量的是 WinForms 事件层，不是 Win32 队列层。
        PostMessageW(h, msg, new IntPtr(wParam), MakeLParam(x, y));
    }

    public static uint MsgMove { get { return WM_MOUSEMOVE; } }
    public static uint MsgDown { get { return WM_LBUTTONDOWN; } }
    public static uint MsgUp { get { return WM_LBUTTONUP; } }
    public static uint HeldButton { get { return MK_LBUTTON; } }

    public static void Pump(int ms)
    {
        // 让目标线程把队列里的消息派发掉：跨进程投递没有"等它处理完"的语义，
        // 只能靠消息循环转起来。
        Thread.Sleep(ms);
    }

    static int Wheel(IntPtr child, int delta, int cx, int cy)
    {
        IntPtr res;
        // 滚轮走同步投递：与产品真消息回归（S9/S11）同一条路，投没投进去当场可见。
        SendMessageTimeoutW(child, WM_MOUSEWHEEL,
            new IntPtr((long)(((uint)(short)delta) << 16)), MakeLParam(cx, cy),
            SMTO_ABORTIFHUNG, 1000, out res);
        return 1;
    }

    /// <summary>sync=true 时这条输入消息的往返用时就是"宿主处理一次输入花了多久"（含它自己在
    /// 内核锁里排队的时间）—— 交接文档 §5 的读数 2。async 时只投递不计时，UI 腾不出手由旁路
    /// WM_NULL 采样那桶负责。</summary>
    static void SendOrPost(IntPtr child, uint msg, IntPtr wp, IntPtr lp, bool sync,
                           string phase, Stopwatch clock)
    {
        if (!sync) { PostMessageW(child, msg, wp, lp); return; }
        long t0 = Stopwatch.GetTimestamp();
        IntPtr res;
        IntPtr ret = SendMessageTimeoutW(child, msg, wp, lp, SMTO_ABORTIFHUNG, 1000, out res);
        double us = (Stopwatch.GetTimestamp() - t0) * 1000000.0 / Stopwatch.Frequency;
        lock (Sync)
        {
            Sample s = new Sample();
            s.Phase = phase; s.TMs = clock.Elapsed.TotalMilliseconds; s.Us = us;
            s.Timeout = (ret == IntPtr.Zero);
            InputSamples.Add(s);
        }
    }

    class PhaseStat
    {
        public string Name; public double Seconds; public long Posted; public int Samples;
        public double P50, P95, P99, Max; public int Timeouts, Over1ms, Over8ms, Over16ms;
        public double MotionDelta = -1.0;
        public string MotionSizes = "";
        public string Cadence = "";
    }
    static readonly List<PhaseStat> Stats = new List<PhaseStat>();

    public static string Run(IntPtr child, IntPtr mainHwnd, uint ownPid, string phaseSpec,
                             int cadenceMs, int sampleMs, double settleSeconds, string csvPath)
    {
        TargetHwnd = child;
        StringBuilder report = new StringBuilder();
        // 节拍自检：timeBeginPeriod(1) 的返回码 + 实测 Sleep(1) 粒度。
        // 为什么必须报：10-19 有一轮 idle 臂 3s 只采到 207 个样本（该 750），投递环
        // 12s 只走 692 拍（该 1500）—— 那是计时器粒度回落到 15.6ms 的样子。粒度一掉，
        // "125Hz 拖拽"实际只有 ~58Hz，节拍类读数（呈现/s、p99）全部换了工况，
        // 不报出来就是拿一个没说清条件的数当判词。
        uint tbp = timeBeginPeriod(1);
        Stopwatch gran = Stopwatch.StartNew();
        List<double> gaps = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            double t0 = gran.Elapsed.TotalMilliseconds;
            Thread.Sleep(1);
            gaps.Add(gran.Elapsed.TotalMilliseconds - t0);
        }
        gaps.Sort();
        report.AppendLine("    节拍自检：timeBeginPeriod(1) rc=" + tbp +
            "（0=成功）；Sleep(1) 实测中位 " + gaps[gaps.Count / 2].ToString("F2", CultureInfo.InvariantCulture) +
            "ms p95 " + gaps[(int)(gaps.Count * 0.95) < gaps.Count ? (int)(gaps.Count * 0.95) : gaps.Count - 1].ToString("F2", CultureInfo.InvariantCulture) +
            "ms（>3ms ⇒ 粒度已回落 15.6ms，本轮节拍类读数要打折）");
        BringToTop(mainHwnd);
        // 等产品自己那句「已自动启用图片模式」浮层淡出再开采，否则 idle 臂的"屏内位移差"会被
        // OSD 余晖污染成 0.95（实测），本底就不干净了。开 -PerfCsv 那一臂时要把这个空档压短
        // （内核采样器连续 3 个无呈现窗口就自动收尾），所以做成参数而不是写死 3s。
        Thread.Sleep((int)(settleSeconds * 1000.0));
        // 置顶之后再命中测试一次：这才是"真滚轮会落到哪个窗口"。置顶前命中的是压在产品
        // 上面的别的进程窗口（实测 Chrome_RenderWidgetHostHWND），拿它当目标必然全程零反应。
        IntPtr hit = HitTest(mainHwnd);
        if (hit != IntPtr.Zero && Owns(hit, ownPid))
        {
            report.AppendLine("    投递目标：置顶后中心命中 0x" + ((long)hit).ToString("X") +
                "（原候选 0x" + ((long)child).ToString("X") + "）" +
                (hit == child ? " 与候选同一个" : " 与候选不同 ⇒ 用命中这个"));
            child = hit;
            TargetHwnd = child;
        }
        else
        {
            report.AppendLine("    投递目标：置顶后中心命中的不是本进程（" +
                OwnerAt(CenterX(child), CenterY(child), ownPid) + "）⇒ 沿用最大叶子子窗");
        }
        RECT childRect; GetWindowRect(child, out childRect);
        report.AppendLine("    置顶后归属：输出子窗中心 " +
            OwnerAt(childRect.Left + (childRect.Right - childRect.Left) / 2,
                    childRect.Top + (childRect.Bottom - childRect.Top) / 2, ownPid) +
            " 子窗矩形=" + RectOf(child) + " 主窗矩形=" + RectOf(mainHwnd));
        report.AppendLine("    本进程叶子子窗清单（谁压在输出面之上，看这里）：");
        report.AppendLine(EnumLeaves(mainHwnd, ownPid));
        timeBeginPeriod(1);
        string csvDir = Path.GetDirectoryName(csvPath);
        report.AppendLine("    主窗标题 =「" + TitleOf(mainHwnd) + "」");
        report.AppendLine("    目视留证 before = " + DumpBmp(child, Path.Combine(csvDir, "grab_before.bmp")));
        Stopwatch clock = Stopwatch.StartNew();
        Thread sampler = new Thread(delegate() { SamplerLoop(sampleMs); });
        sampler.IsBackground = true;
        sampler.Start();
        Thread.Sleep(300);                     // 采样线程先进循环，别让启动空档混进 idle 臂

        try
        {
            foreach (string raw in phaseSpec.Split(','))
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                string[] bits = token.Split(':');
                string name = bits[0];
                double secs = bits.Length > 1
                    ? double.Parse(bits[1], CultureInfo.InvariantCulture) : 10.0;
                int stepsArg = bits.Length > 2
                    ? int.Parse(bits[2], CultureInfo.InvariantCulture) : 0;
                PhaseStat stat = new PhaseStat();
                stat.Name = name; stat.Seconds = secs;
                Stats.Add(stat);
                CurrentPhase = name;

                double startTs = clock.Elapsed.TotalMilliseconds;
                byte[] before = Grab(child);
                string beforeSize = GrabSize;
                long posted = 0;

                if (name.StartsWith("moves") || name.StartsWith("drag") ||
                    name.StartsWith("smoves") || name.StartsWith("sdrag"))
                {
                    bool sync = name.StartsWith("s");
                    RECT c; GetClientRect(child, out c);
                    int w = c.Right - c.Left, h = c.Bottom - c.Top;
                    int y = h / 2;
                    int origin = Math.Max(8, w / 2);
                    int x = origin;
                    int amplitude = Math.Max(40, Math.Min(260, w / 4));
                    int stepPx = 4;
                    int dir = 1;
                    long travel = 0;
                    SendOrPost(child, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), MakeLParam(x, y), sync, name, clock);
                    posted++;
                    double until = startTs + secs * 1000.0;
                    bool 量节拍 = name.StartsWith("drag") || name.StartsWith("sdrag");
                    if (量节拍) { ChangeGaps.Clear(); lastChangeMs = -1.0; miniPrev = null; }
                    while (clock.Elapsed.TotalMilliseconds < until)
                    {
                        x += dir * stepPx;
                        if (x > origin + amplitude || x < origin - amplitude) dir = -dir;
                        SendOrPost(child, WM_MOUSEMOVE, new IntPtr(MK_LBUTTON), MakeLParam(x, y), sync, name, clock);
                        posted++;
                        travel += stepPx;
                        if (量节拍) CadenceProbe(child, clock);
                        WaitUntil(clock, clock.Elapsed.TotalMilliseconds + cadenceMs);
                    }
                    SendOrPost(child, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(x, y), sync, name, clock);
                    posted++;
                    stat.Posted = posted;
                    stat.Cadence = 量节拍 ? CadenceSummary((clock.Elapsed.TotalMilliseconds - startTs) / 1000.0) : "";
                    report.AppendLine("    " + name + "：" + (sync ? "同步投递(SendMessage)" : "异步投递(PostMessage)") +
                        " 客户区=" + w + "x" + h + " 起拖 x=" + origin +
                        " y=" + y + " 幅度±" + amplitude + " 步长" + stepPx + "px 累计行程=" + travel + "px");
                }
                else if (name.StartsWith("dbl"))
                {
                    // 真双击序列 DOWN/UP/DBLCLK/UP：产品的 1:1 切换门控写在 e.Clicks>=2 上，
                    // 连投两次 DOWN 不算双击（同 ImageZoomLiveRegression S12 的口径）。
                    RECT c; GetClientRect(child, out c);
                    int x = c.Right / 2, y = c.Bottom / 2;
                    PostMessageW(child, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), MakeLParam(x, y)); posted++;
                    Thread.Sleep(40);
                    PostMessageW(child, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(x, y)); posted++;
                    Thread.Sleep(40);
                    PostMessageW(child, 0x0203, new IntPtr(MK_LBUTTON), MakeLParam(x, y)); posted++;
                    Thread.Sleep(40);
                    PostMessageW(child, WM_LBUTTONUP, IntPtr.Zero, MakeLParam(x, y)); posted++;
                    WaitUntil(clock, startTs + secs * 1000.0);
                    stat.Posted = posted;
                    report.AppendLine("    dbl：真双击 → 适应档 ↔ 像素级 1:1（然后拖一拖看跟不跟手）");
                }
                else if (name.StartsWith("zoom") || name.StartsWith("ctrl"))
                {
                    bool isCtrl = name.StartsWith("ctrl");
                    int steps = isCtrl ? 6 : 8;
                    if (stepsArg > 0 && name.StartsWith("zoom")) steps = stepsArg;
                    RECT scr; GetWindowRect(child, out scr);
                    int cx = scr.Left + (scr.Right - scr.Left) / 2;
                    int cy = scr.Top + (scr.Bottom - scr.Top) / 2;
                    for (int i = 0; i < steps; i++)
                    {
                        posted += Wheel(child, 120, cx, cy);
                        WaitUntil(clock, clock.Elapsed.TotalMilliseconds + 60);
                    }
                    if (isCtrl)
                    {
                        // ctrl 臂只做一件事：证明"这个进程、这个句柄、这套投递"确实能改画面。
                        // 它不过 ⇒ 后面所有零位移都是量具死，不是产品结论（本轮实测就被 Chrome
                        // 压住过一回，四臂位移差全 0、延迟分布一模一样，正是这种假负结果）。
                        WaitUntil(clock, startTs + secs * 1000.0);
                    }
                    else
                    {
                        double until = startTs + secs * 1000.0;
                        while (clock.Elapsed.TotalMilliseconds < until)
                            WaitUntil(clock, clock.Elapsed.TotalMilliseconds + cadenceMs);
                    }
                    stat.Posted = posted;
                    report.AppendLine("    " + name + "：连投 " + steps + " 格 WM_MOUSEWHEEL" +
                        (isCtrl ? "（正对照：屏内位移差必须显著）" : "（放大档 = 1.25^8 = 5.96，把平移余量造出来）"));
                }
                else
                {
                    double until = startTs + secs * 1000.0;
                    while (clock.Elapsed.TotalMilliseconds < until)
                        WaitUntil(clock, clock.Elapsed.TotalMilliseconds + 20);
                    stat.Posted = 0;
                }

                byte[] after = Grab(child);
                stat.MotionDelta = Diff(before, after);
                stat.MotionSizes = beforeSize + "→" + GrabSize;
                CurrentPhase = "tail";
                Thread.Sleep(60);
            }
            report.AppendLine("    目视留证 after = " + DumpBmp(child, Path.Combine(csvDir, "grab_after.bmp")));
        }
        finally
        {
            StopSampler = true;
            try { if (sampler.IsAlive) sampler.Join(2000); } catch { }
            timeEndPeriod(1);
        }

        Dictionary<string, List<Sample>> byPhase = new Dictionary<string, List<Sample>>();
        lock (Sync)
        {
            foreach (Sample s in Samples)
            {
                List<Sample> bucket;
                if (!byPhase.TryGetValue(s.Phase, out bucket))
                {
                    bucket = new List<Sample>();
                    byPhase[s.Phase] = bucket;
                }
                bucket.Add(s);
            }
        }
        // kind 列：ui = 旁路 WM_NULL 往返（"UI 线程多久腾不出手"）；
        // input = 同步臂里那一次输入消息的往返（"处理一次输入花多久"，即交接文档的读数 2）。
        // 上一版 input 那桶只打在控制台、没落盘 ⇒ 读数 2 不可重算，这一版补上。
        StringBuilder csv = new StringBuilder("kind,phase,t_ms,latency_us,timeout\n");
        lock (Sync)
        {
            foreach (Sample s in Samples)
                csv.Append("ui,").Append(s.Phase).Append(',').Append(s.TMs.ToString("F1", CultureInfo.InvariantCulture))
                   .Append(',').Append(s.Us.ToString("F1", CultureInfo.InvariantCulture))
                   .Append(',').Append(s.Timeout ? "1" : "0").Append('\n');
            foreach (Sample s in InputSamples)
                csv.Append("input,").Append(s.Phase).Append(',').Append(s.TMs.ToString("F1", CultureInfo.InvariantCulture))
                   .Append(',').Append(s.Us.ToString("F1", CultureInfo.InvariantCulture))
                   .Append(',').Append(s.Timeout ? "1" : "0").Append('\n');
        }
        File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(false));

        List<string> lines = new List<string>();
        foreach (PhaseStat st in Stats)
        {
            List<Sample> bucket;
            if (!byPhase.TryGetValue(st.Name, out bucket)) bucket = new List<Sample>();
            List<double> vals = new List<double>();
            foreach (Sample s in bucket) if (!s.Timeout) vals.Add(s.Us);
            st.Samples = bucket.Count;
            vals.Sort();
            if (vals.Count > 0)
            {
                st.P50 = Pick(vals, 0.50); st.P95 = Pick(vals, 0.95); st.P99 = Pick(vals, 0.99);
                st.Max = vals[vals.Count - 1];
                foreach (double v in vals)
                {
                    if (v > 1000.0) st.Over1ms++;
                    if (v > 8000.0) st.Over8ms++;
                    if (v > 16000.0) st.Over16ms++;
                }
            }
            foreach (Sample s in bucket) if (s.Timeout) st.Timeouts++;
            // 一臂全是超时 ⇒ 目标线程已经不在了（产品被采样器自动收尾、或被谁关掉）。
            // 这时百分位是从空数组算出来的 0.000ms，看着像"零阻塞"，其实什么都没说 ——
            // 必须显式判 DEAD，不许留成绿行（10-19 实测踩过：三臂 3455/3455 超时仍打出 p50=0.000）。
            int alive = st.Samples - st.Timeouts;
            string deadTag = (st.Samples > 0 && alive * 5 < st.Samples) ? "  ★臂全超时=量具死" : "";
            if (st.MotionDelta < 0.0) deadTag += "  ★抓屏为空(尺寸变了或窗口没了)";
            // 采样率与投递率都得对得上标称，对不上就是这一轮换了工况（见上面"节拍自检"的理由）。
            double wantSamples = st.Seconds * 1000.0 / sampleMs;
            if (wantSamples > 20 && st.Samples < wantSamples * 0.6)
                deadTag += string.Format(CultureInfo.InvariantCulture,
                    "  ★采样率不足 实得{0}/应得{1:F0}", st.Samples, wantSamples);
            double wantPosts = st.Seconds * 1000.0 / cadenceMs;
            if ((st.Name.StartsWith("drag") || st.Name.StartsWith("sdrag") || st.Name.StartsWith("moves"))
                && st.Posted > 10 && st.Posted < wantPosts * 0.6)
                deadTag += string.Format(CultureInfo.InvariantCulture,
                    "  ★投递率不足 实得{0}/应得{1:F0}", st.Posted, wantPosts);
            lines.Add(string.Format(
                "ARM {0,-7} n={1,5} 投递={2,6} p50={3,8:F3}ms p95={4,8:F3}ms p99={5,8:F3}ms max={6,9:F3}ms" +
                " >1ms={7,5} >8ms={8,5} >16ms={9,5} 超时={10} 屏内位移差={11:F2}{12}{13}",
                st.Name, st.Samples, st.Posted,
                st.P50 / 1000.0, st.P95 / 1000.0, st.P99 / 1000.0, st.Max / 1000.0,
                st.Over1ms, st.Over8ms, st.Over16ms, st.Timeouts, st.MotionDelta,
                st.MotionSizes.Length > 0 ? "(" + st.MotionSizes + ")" : "", deadTag));
            if (st.Cadence.Length > 0) lines.Add("      " + st.Cadence);

            List<Sample> inBucket = new List<Sample>();
            lock (Sync) { foreach (Sample s in InputSamples) if (s.Phase == st.Name) inBucket.Add(s); }
            List<double> iv = new List<double>();
            int inTimeouts = 0;
            foreach (Sample s in inBucket) { if (s.Timeout) inTimeouts++; else iv.Add(s.Us); }
            iv.Sort();
            if (iv.Count > 0)
            {
                lines.Add(string.Format(
                    "IN  {0,-7} n={1,5} 单次投递往返 p50={2,8:F3}ms p95={3,8:F3}ms p99={4,8:F3}ms max={5,9:F3}ms" +
                    " >8ms={6} >16ms={7} 超时={8}",
                    st.Name, inBucket.Count,
                    Pick(iv, 0.50) / 1000.0, Pick(iv, 0.95) / 1000.0, Pick(iv, 0.99) / 1000.0,
                    iv[iv.Count - 1] / 1000.0, CountOver(iv, 8000.0), CountOver(iv, 16000.0), inTimeouts));
            }
        }
        int totalSamples;
        lock (Sync) { totalSamples = Samples.Count; }
        string ctrlVerdict = "本轮相位表里没有 ctrl 臂";
        foreach (PhaseStat st in Stats)
        {
            if (!st.Name.StartsWith("ctrl")) continue;
            ctrlVerdict = (st.MotionDelta > 0.5)
                ? ("PASS（屏内位移差=" + st.MotionDelta.ToString("F2", CultureInfo.InvariantCulture) + "）")
                : ("DEAD（屏内位移差=" + st.MotionDelta.ToString("F2", CultureInfo.InvariantCulture) +
                   "）⇒ 消息没走到画面，本轮一律不作结论");
        }
        return report.ToString() + string.Join("\n", lines.ToArray()) +
               "\n    CTRL_VERDICT=" + ctrlVerdict +
               "\n    CSV=" + csvPath + " 采样条数(全部)=" + totalSamples;
    }

    // ── 呈现节拍探针 ────────────────────────────────────────────────────────
    // 为什么要有它：本机拿不到声明刷新率（Win32_VideoController.CurrentRefreshRate 为空），
    // 而"UI 线程被堵 max 6.2ms"这句话要不要紧，完全取决于一个 vblank 是多长。
    // 内核侧那条路也堵死：静图下 Redraw 驱动的 present **不涨** 已呈现视频帧数，
    // 所以 FFF3FP_PERF_OUT 采样器恒见 0 增量 ⇒ 连续 3 个窗口就自动收尾退出产品
    // （10-19 实测：perf_kernel.csv 0 字节、产品 5s 内消失）。⇒ 只能从屏幕侧量。
    // 量法：拖拽每一拍顺手抓输出面中心 96×96，与上一拍比，变了就记一次"合成到达"。
    // 计数落在**本进程**，每拍一次 96×96 BitBlt（≪8ms 预算），扰动量级见台账边界条。
    static byte[] miniPrev = null;
    static readonly List<double> ChangeGaps = new List<double>();
    static double lastChangeMs = -1.0;

    static void CadenceProbe(IntPtr child, Stopwatch clock)
    {
        RECT r; if (!GetWindowRect(child, out r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 128 || h < 128) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return;
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = IntPtr.Zero; IntPtr old = IntPtr.Zero; IntPtr bits;
        try
        {
            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = 96;
            bmi.bmiHeader.biHeight = -96;
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmp = CreateDIBSection(screen, ref bmi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return;
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, 96, 96, screen, r.Left + w / 2 - 48, r.Top + h / 2 - 48, SRCCOPY))
                return;
            byte[] raw = new byte[96 * 4 * 96];
            Marshal.Copy(bits, raw, 0, raw.Length);
            bool changed = miniPrev == null;
            if (!changed)
                for (int i = 0; i < raw.Length; i += 4)
                    if (raw[i] != miniPrev[i] || raw[i + 1] != miniPrev[i + 1] || raw[i + 2] != miniPrev[i + 2])
                    { changed = true; break; }
            miniPrev = raw;
            double now = clock.Elapsed.TotalMilliseconds;
            if (changed)
            {
                if (lastChangeMs > 0) ChangeGaps.Add(now - lastChangeMs);
                lastChangeMs = now;
            }
        }
        catch { }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static string CadenceSummary(double seconds)
    {
        if (ChangeGaps.Count < 3) return "呈现节拍未采到（变化次数 <3）";
        List<double> g = new List<double>(ChangeGaps);
        g.Sort();
        return string.Format(CultureInfo.InvariantCulture,
            "呈现节拍：变化 {0} 次 / {1:F1}s ⇒ ≈{2:F1}/s；相邻间隔中位 {3:F2}ms p95 {4:F2}ms max {5:F2}ms" +
            "（中位间隔就是这台机器上「一次合成到达」的实测周期）",
            ChangeGaps.Count + 1, seconds, (ChangeGaps.Count + 1) / seconds,
            Pick(g, 0.5), Pick(g, 0.95), g[g.Count - 1]);
    }

    static double Pick(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return -1.0;
        int idx = (int)Math.Round(p * (sorted.Count - 1));
        if (idx < 0) idx = 0;
        if (idx >= sorted.Count) idx = sorted.Count - 1;
        return sorted[idx];
    }

    static int CountOver(List<double> sorted, double us)
    {
        int n = 0;
        foreach (double v in sorted) if (v > us) n++;
        return n;
    }

    // ── 抓屏（只在相位边界各一次，粗粒度，用来证明"画面真的动了/没动"）──
    // 不用 System.Drawing：pwsh7 的 Add-Type 默认引用集里没有 System.Drawing.Common，
    // 而一旦显式给 -ReferencedAssemblies 就会取代默认集、把 List<> 弄丢（CS1069 实测）。
    // 所以这里直接走 GDI：屏幕 DC → BitBlt 进 DIB section → 从内存读 BGRA。
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    const uint SRCCOPY = 0x00CC0020;
    const uint DIB_RGB_COLORS = 0;

    static string GrabSize = "?";
    static byte[] Grab(IntPtr child)
    {
        RECT r; if (!GetWindowRect(child, out r)) { GrabSize = "取不到"; return new byte[0]; }
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        GrabSize = w + "x" + h;
        if (w < 32 || h < 32) { GrabSize = "太小" + GrabSize; return new byte[0]; }
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return new byte[0];
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = IntPtr.Zero; IntPtr old = IntPtr.Zero; IntPtr bits;
        try
        {
            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h;              // 负高 = 自上而下的行序
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0;          // BI_RGB
            bmp = CreateDIBSection(screen, ref bmi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return new byte[0];
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY)) return new byte[0];
            int stride = ((w * 4 + 3) / 4) * 4;
            byte[] raw = new byte[stride * h];
            Marshal.Copy(bits, raw, 0, raw.Length);
            return raw;
        }
        catch { return new byte[0]; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static double Diff(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return -1.0;
        double total = 0; int n = 0;
        for (int i = 0; i + 3 < a.Length; i += 4)
        {
            total += Math.Abs((int)a[i] - b[i]) + Math.Abs((int)a[i + 1] - b[i + 1]) +
                     Math.Abs((int)a[i + 2] - b[i + 2]);
            n++;
        }
        return total / Math.Max(1, n * 3.0);
    }
}
'@

# 引用集留空 = 用 Add-Type 的默认集。实测：显式给 -ReferencedAssemblies 会**取代**默认集，
# 内嵌 C# 里的 List<> 立刻 CS1069（类型转发到 System.Private.CoreLib，而 TPA 全表里有两个
# corelib 路径，补进去也没用）；而 System.Drawing 那一侧改成直接走 GDI 就绕开了缺
# System.Drawing.Common 的问题（见 Grab 的注释）。
Add-Type -TypeDefinition $code

Write-Host ('    DPI：' + [DragProbe]::TryPerMonitorAware())
try {
    $rate = (Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop |
        Select-Object -First 1 -ExpandProperty CurrentRefreshRate)
    Write-Host ('    桌面刷新率（Win32_VideoController.CurrentRefreshRate）= ' + $rate + ' Hz')
} catch { Write-Host '    桌面刷新率取不到 ⇒ vblank 时长换算记未判定' }

Write-Host '[1] 起产品（Release x64 那份，含 10-19 弹幕轮重建的内核）' -ForegroundColor Cyan
Write-Host ('    exe = ' + $Exe)
Write-Host ('    媒体 = ' + $Media)
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = '"' + $Media + '"'
$psi.WorkingDirectory = Split-Path -Parent $Exe
$psi.UseShellExecute = $false
if ($PerfCsv) {
    # 内核侧那一臂：采样器只在"本窗有呈现增量"时落行，且连续 3 个无进展窗口就自动收尾，
    # 所以这一臂的相位表必须是"拖拽立刻开始并连续"，否则拿到的是启动空档而不是拖拽。
    $perfPath = Join-Path $OutDir 'perf_kernel.csv'
    $psi.EnvironmentVariables['FFF3FP_PERF_OUT'] = $perfPath
    $psi.EnvironmentVariables['FFF3FP_PERF_SECONDS'] = '20'
    Write-Host ('    FFF3FP_PERF_OUT = ' + $perfPath)
}
$p = [System.Diagnostics.Process]::Start($psi)
if ($null -eq $p) { Write-Host '起不来' -ForegroundColor Red; exit 1 }

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 80; $i++) {
    $p.Refresh()
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    Start-Sleep -Milliseconds 200
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host '拿不到主窗口句柄' -ForegroundColor Red; $p.Kill(); exit 1 }
# 等大静图解码完并自动进入图片模式。开 -PerfCsv 那一臂时这个空档要压短：
# 内核采样器连续 3 个"无呈现增量"窗口就自动收尾退出产品，而静图静止时就是不呈现。
Start-Sleep -Milliseconds $PreWaitMs

$ownPid = [uint32]$p.Id
$centerHit = [DragProbe]::HitTest($hwnd)
$leaf = [DragProbe]::BiggestLeafChild($hwnd, $ownPid)
$biggest = [DragProbe]::BiggestChild($hwnd, $ownPid)
Write-Host ('[2] 主窗   ' + [DragProbe]::Describe($hwnd, $ownPid))
Write-Host ('    中心命中 ' + [DragProbe]::Describe($centerHit, $ownPid))
Write-Host ('    最大叶子子窗 ' + [DragProbe]::Describe($leaf, $ownPid))
Write-Host ('    最大子窗(含容器) ' + [DragProbe]::Describe($biggest, $ownPid))
$child = $centerHit
if (-not ([DragProbe]::Describe($centerHit, $ownPid)).Contains('(本进程)')) {
    Write-Host '    中心命中不属于本进程 ⇒ 退回最大叶子子窗' -ForegroundColor Yellow
    $child = $leaf
}
if ($child -eq [IntPtr]::Zero) {
    Write-Host '    两个候选句柄都拿不到 ⇒ 量具死，本轮不产出结论。' -ForegroundColor Red
    try { $p.Kill() } catch { }
    exit 3
}

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$csvPath = Join-Path $OutDir ("ui_block_$stamp.csv")
Write-Host ('[3] 相位表 = ' + $Phases + '  投递节拍 = ' + $CadenceMs + 'ms  采样节拍 = ' + $SampleMs + 'ms') -ForegroundColor Cyan
$out = [DragProbe]::Run($child, $hwnd, $ownPid, $Phases, $CadenceMs, $SampleMs, $SettleSeconds, $csvPath)
Write-Host $out

# 有效性守卫（fail-closed）：ctrl 臂不过 ⇒ 消息压根没走到画面，其余所有读数一律不作结论。
if ($out -match 'CTRL_VERDICT=DEAD') {
    Write-Host '    ctrl 正对照判 DEAD ⇒ 量具死，本轮不产出结论。' -ForegroundColor Red
    try { $p.Kill() } catch { }
    exit 3
}
if ($out -match '置顶后归属：输出子窗中心 他进程') {
    Write-Host '    置顶后输出面中心仍被别的窗口压住 ⇒ 抓屏读的是别人，本轮不产出结论。' -ForegroundColor Red
    try { $p.Kill() } catch { }
    exit 3
}
# 臂数与采样数都得对得上，对不上就是量具自己的问题。
$postedMatches = [regex]::Matches($out, '投递=\s*(\d+)')
$sum = 0
foreach ($m in $postedMatches) { $sum += [int]$m.Groups[1].Value }
$samplesTxt = ([regex]::Match($out, '采样条数\(全部\)=(\d+)')).Groups[1].Value
$samplesCount = 0
[void][int]::TryParse($samplesTxt, [ref]$samplesCount)
Write-Host ('[4] 自检：臂数=' + $postedMatches.Count + ' 投递合计=' + $sum + ' 采样合计=' + $samplesCount)
# 任一臂被判"全超时/抓屏为空/采样率不足/投递率不足"⇒ 这一轮换了工况或目标线程已不在，
# 百分位是从空数组或被拉低的节拍算出来的，一律不作结论（这类"看着是 0.000ms 零阻塞"的读数比红更坏）。
if ($out -match '量具死|抓屏为空|采样率不足|投递率不足') {
    Write-Host '    有臂被判全超时/抓屏为空/采样率不足/投递率不足 ⇒ 量具或工况不成立，本轮不产出结论。' -ForegroundColor Red
    try { $p.Kill() } catch { }
    exit 3
}
if ($samplesCount -lt 200) {
    Write-Host '    采样条数 <200 ⇒ 采样线程没跑起来，本轮不产出结论。' -ForegroundColor Red
    try { $p.Kill() } catch { }
    exit 3
}
# idle 臂的屏内位移差必须接近 0，否则产品在自己动（或有常驻叠加层），所有"被堵"都归因不到拖拽。
$idleLine = ($out -split "`n" | Where-Object { $_ -match '^ARM idle' } | Select-Object -First 1)
if ($idleLine -and $idleLine -match '屏内位移差=(-?[0-9.]+)') {
    $idm = [double]$Matches[1]
    if ($idm -gt 0.5) {
        Write-Host ('    idle 臂屏内就在动（位移差 ' + $idm + '）⇒ 本底不干净，归因降级为参考。') -ForegroundColor Yellow
    }
}
# 拖拽臂必须真的改变画面：余量没造出来时它是"moves 的同款空转"，不能算完整提交路。
$dragLine = ($out -split "`n" | Where-Object { $_ -match '^ARM drag' } | Select-Object -First 1)
if ($dragLine -and $dragLine -match '屏内位移差=(-?[0-9.]+)') {
    $dm = [double]$Matches[1]
    if ($dm -lt 0.05) {
        Write-Host ('    拖拽臂屏内位移差只有 ' + $dm + ' ⇒ 这一臂没走到提交路径，只能当参考行。') -ForegroundColor Yellow
    }
}

Write-Host '[5] 收尾：关产品' -ForegroundColor Cyan
try { if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null } } catch { }
if ($PerfCsv -and (Test-Path -LiteralPath $perfPath)) {
    Write-Host ('    内核侧 CSV = ' + $perfPath)
    Import-Csv -LiteralPath $perfPath | Select-Object -First 25 |
        Select-Object window, clientW, clientH, presented, dropped, coalesced, devlock_ms, presentwait_ms |
        Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}
exit 0
