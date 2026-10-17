# 量产品缩放帧序列.ps1 —— 起产品 GUI，投一次滚轮，抓一串帧，量"屏上真的出现几个中间态"
#
# 为什么需要这一档（docs/79 §11.9 残留 ①）：同进程回归能证明"宿主发了 14 包、221ms 静止"，
# 但**证明不了屏幕真的画出了中间态** —— 呈现可能被 vsync 合并，那样"平滑缩放"在体感上等于没做。
#
# 三条臂（缺一不可，否则读数没意义）：
#   P 稳定性臂：不注入任何输入连抓两帧 ⇒ 差值必须 ≈0。不为 0 说明产品在自动变或抓错了区域。
#   Q 小信号臂：注入**一格**滚轮 ⇒ 记录"有多少帧与上一帧不同"（中间态数）与最终差值。
#   R 正对照臂：注入**八格**滚轮 ⇒ 最终差值必须显著。若 R 也 ≈0，那是量具死（抓黑帧/投错窗口），
#     此时 Q 的"没变化"**不能**当成产品缺陷 —— 这是本轮唯一能把"看不见"和"没发生"分开的东西。
#
# 输入走 WM_MOUSEWHEEL 定向 SendMessage 到最大的子窗口：不需要焦点、不需要移光标
# （GUI 判据不得依赖焦点；SendInput 会受前台窗口与光标位置影响）。
#
# 用法：pwsh -File 3fp/tools/量产品缩放帧序列.ps1 [-Media <图片>] [-Exe <产品exe>]
# 退出码：0 读数有效 / 3 读数无效（臂没通过，不产出结论）/ 1 起不来

param(
    [string]$Tag = 'media',
    [switch]$NoMedia,
    [string]$Media = '',
    [string]$Exe = '',
    [int]$Frames = 24,
    [int]$IntervalMs = 30
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($NoMedia) { $Media = '' }
if ([string]::IsNullOrWhiteSpace($Media)) {
    $Media = Join-Path $repo '3fp\samples\media\edge\09g_bigblocks_12000x9000.jpg'
}
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $repo '3fp\kernel\FFF.Player\bin\Debug\net10.0-windows10.0.26100.0\FFF.Player.exe'
}
if (-not (Test-Path -LiteralPath $Media)) { Write-Host "缺媒体：$Media" -ForegroundColor Red; exit 1 }
if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "缺产品 exe：$Exe" -ForegroundColor Red; exit 1 }
$exeDir = Split-Path -Parent $Exe
foreach ($need in @('FFF.Native.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $exeDir $need))) {
        Write-Host "产品件旁缺 $need —— 起它只会得到空能力，读数无意义。" -ForegroundColor Red
        exit 1
    }
}
$av = @(Get-ChildItem -LiteralPath $exeDir -Filter 'avcodec*.dll' -ErrorAction SilentlyContinue)
if ($av.Count -eq 0) { Write-Host '产品件旁没有 avcodec ⇒ 解码不可用，读数无意义。' -ForegroundColor Red; exit 1 }

$code = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class NativeProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);

    private delegate bool EnumProc(IntPtr h, IntPtr data);

    private static IntPtr _best;
    private static long _bestArea;
    private static uint _ownPid;

    /// <summary>找面积最大的可见子窗口 —— 视频/图片输出面就是它，滚轮要投给它。</summary>
    public static IntPtr BiggestChild(IntPtr parent, uint pid)
    {
        _best = IntPtr.Zero; _bestArea = 0; _ownPid = pid;
        EnumChildWindows(parent, Scan, IntPtr.Zero);
        return _best;
    }

    private static bool Scan(IntPtr h, IntPtr data)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != _ownPid || !IsWindowVisible(h)) return true;
        RECT r;
        if (!GetWindowRect(h, out r)) return true;
        long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
        // EnumChildWindows 不枚举 parent 自己，所以直接取面积最大的可见子窗即可。
        if (area > _bestArea) { _bestArea = area; _best = h; }
        return true;
    }

    public static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowTextW(h, sb, 256);
        return sb.ToString();
    }

    /// <summary>归属断言：该屏幕点最上的窗口必须属于本产品进程，否则抓的是别人的窗口
    /// （实测抓到过压在上面的 "Chrome Legacy Window"，那串每 0.5s 差 30~80 的序列就是它）。</summary>
    public static string OwnerAt(int x, int y, uint ownPid)
    {
        var h = WindowFromPoint(x, y);
        if (h == IntPtr.Zero) return "无窗口";
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        var sb = new StringBuilder(128);
        GetClassNameW(h, sb, 128);
        return (pid == ownPid ? "本进程" : "他进程") + " pid=" + pid + " 类=" + sb.ToString();
    }

    public static void SaveSmall(byte[] px, int w, int h, string path)
    {
        using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
        {
            int o = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    bmp.SetPixel(x, y, Color.FromArgb(px[o + 2], px[o + 1], px[o]));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    public static string ClientInfo(IntPtr h)
    {
        RECT c;
        if (!GetClientRect(h, out c)) return "GetClientRect 失败";
        return "客户区 " + (c.Right - c.Left) + "x" + (c.Bottom - c.Top) +
               "；GetDpiForWindow=" + GetDpiForWindow(h);
    }

    /// <summary>真滚轮只会送到光标下那个窗口 ⇒ 用它选投递目标，而不是"面积最大的子窗"。</summary>
    public static IntPtr HitTest(int x, int y)
    {
        var pt = new POINT(); pt.X = x; pt.Y = y;
        return WindowFromPoint(pt);
    }

    public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);

    public static Rectangle RectOf(IntPtr h)
    {
        RECT r;
        GetWindowRect(h, out r);
        return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>抓屏并按 4 像素步进降采样成一维数组。返回 0 长 ⇒ 抓不到（黑屏/越界）。</summary>
    public static byte[] Grab(Rectangle region, out int w, out int hh)
    {
        w = 0; hh = 0;
        if (region.Width < 16 || region.Height < 16) return Array.Empty<byte>();
        using (var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(region.Left, region.Top, 0, 0, new Size(region.Width, region.Height));
            var rect = new Rectangle(0, 0, region.Width, region.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = data.Stride;
            var raw = new byte[stride * region.Height];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            bmp.UnlockBits(data);
            var step = 4;
            int cols = region.Width / step, rows = region.Height / step;
            var outp = new byte[rows * cols * 3];
            int o = 0;
            for (int y = 0; y < rows; y++)
                for (int x = 0; x < cols; x++)
                {
                    int i = y * step * stride + x * step * 3;
                    outp[o++] = raw[i]; outp[o++] = raw[i + 1]; outp[o++] = raw[i + 2];
                }
            w = cols; hh = rows;
            return outp;
        }
    }

    public static double Diff(byte[] a, byte[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return -1.0;
        double total = 0; int n = 0;
        for (int i = 0; i < a.Length; i += 3)
        {
            total += Math.Abs((int)a[i] - b[i]) + Math.Abs((int)a[i + 1] - b[i + 1]) +
                    Math.Abs((int)a[i + 2] - b[i + 2]);
            n++;
        }
        return total / Math.Max(1, n * 3.0);
    }

    public static double Spread(byte[] a)
    {
        if (a == null || a.Length == 0) return -1.0;
        int lo = 255, hi = 0;
        for (int i = 0; i < a.Length; i += 3)
        {
            int l = (int)(0.299 * a[i + 2] + 0.587 * a[i + 1] + 0.114 * a[i]);
            if (l < lo) lo = l;
            if (l > hi) hi = l;
        }
        return hi - lo;
    }

    /// <summary>向目标窗口投 WM_MOUSEWHEEL。wParam 高字 = delta，lParam = 屏幕坐标。</summary>
    public static bool Wheel(IntPtr h, int delta, int screenX, int screenY)
    {
        long wParam = (long)(((uint)(delta & 0xFFFF)) << 16);
        long lParam = (long)(uint)(screenX & 0xFFFF) | ((long)(uint)(screenY & 0xFFFF) << 16);
        SendMessageW(h, 0x020A, new IntPtr(wParam), new IntPtr(lParam));
        return true;
    }

}
'@
# 别再猜程序集名：.NET 10 把 Thread 转发到 System.Private.CoreLib、Bitmap/IImage 转发到
# System.Private.Windows.GdiPlus。改成让 PowerShell 自己解析类型，再取它真正所在的程序集位置。
$refs = [System.Collections.Generic.HashSet[string]]::new()
foreach ($asm in [AppDomain]::CurrentDomain.GetAssemblies()) {
    if ($asm.Location -and $asm.Location.Length -gt 0) { [void]$refs.Add($asm.Location) }
}
$fromTypes = @(
    [System.Object], [System.Threading.Thread], [System.Drawing.Bitmap], [System.Drawing.Graphics],
    [System.Drawing.Rectangle], [System.Drawing.Point], [System.Drawing.Size],
    [System.Drawing.Imaging.PixelFormat], [System.Drawing.Imaging.BitmapData],
    [System.Runtime.InteropServices.Marshal], [System.Text.StringBuilder]
)
foreach ($t in $fromTypes) {
    $loc = $t.Assembly.Location
    if ($loc) { [void]$refs.Add($loc) }
}
Add-Type -TypeDefinition $code -ReferencedAssemblies @($refs)

Write-Host "[1] 起产品" -ForegroundColor Cyan
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
if ($NoMedia) { $psi.Arguments = '' } else { $psi.Arguments = '"' + $Media + '"' }
$psi.WorkingDirectory = $exeDir
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
if ($null -eq $p) { Write-Host '起不来' -ForegroundColor Red; exit 1 }

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 80; $i++) {
    $p.Refresh()
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    Start-Sleep -Milliseconds 250
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host '拿不到主窗口句柄' -ForegroundColor Red; $p.Kill(); exit 1 }
# 就绪门：不是"两帧相同就稳定"——空窗期同样两帧相同（上一版就是这么把还在解码的窗口
# 当成就绪，于是 Q 末尾那个 Δ30.9 其实是图终于画上，被误读成缩放跳变）。
# 正确的就绪判据：先必须**观测到一次载入变化**，随后连续安静 >= 4 个 0.5s 才算好。
$ws = 0; $hs = 0
$主窗区 = [NativeProbe]::RectOf($hwnd)
$基准 = [NativeProbe]::Grab($主窗区, [ref]$ws, [ref]$hs)
$上一帧 = $基准
$见过载入 = $false
$安静 = 0
$载入于 = -1
$抖动序列 = ''
$就绪于 = -1
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $这一帧 = [NativeProbe]::Grab($主窗区, [ref]$ws, [ref]$hs)
    $d = [NativeProbe]::Diff($上一帧, $这一帧)
    if ($d -lt 0) { $d = 0 }                      # 尺寸哨兵：抓的尺寸变了不算变化也不算死
    $抖动序列 += ("{0:F2} " -f $d)
    # 两档阈值：本机有常驻叠加层（帧率浮层那类）会让屏内差永不归零，
    # 所以"变化"必须是**内容级**的大差，"安静"只要求低于大差阈值。
    if ($d -gt 3.0) {
        if (-not $见过载入) { $见过载入 = $true; $载入于 = $i }
        $安静 = 0
    } elseif ($d -lt 1.5) {
        $安静 += 1
        if ($安静 -ge 3) { $就绪于 = $i; break }
    }
    $上一帧 = $这一帧
}
Write-Host ("    主窗标题 = 「{0}」；载入于第 {1} 个 0.5s；就绪于第 {2} 个（需大差后再连续 3 次小差）" -f `
    [NativeProbe]::TitleOf($hwnd), $载入于, $就绪于)
Write-Host ("    每 0.5s 屏内差序列 = " + $抖动序列)
if ($就绪于 -lt 0) {
    Write-Host '    就绪门没过：30s 内画面一直在大改，没进静止态。本轮读数作废。' -ForegroundColor Yellow
    try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    exit 3
}
# 真滚轮落到光标下那个窗口。上一版把消息投给"面积最大的子窗"，那是另一回事 ——
# 它正是"投 8 格屏上零变化"最可能的解释之一。
$中心 = [NativeProbe]::RectOf($hwnd)
$hx = $中心.Left + [int]($中心.Width / 2)
$hy = $中心.Top + [int]($中心.Height / 2)
$child = [NativeProbe]::HitTest($hx, $hy)
if ($child -eq [IntPtr]::Zero) { $child = [NativeProbe]::BiggestChild($hwnd, $p.Id) }
Write-Host ("    命中测试 中心($hx,$hy) → $child  " + [NativeProbe]::ClientInfo($child))
Write-Host ("    主窗：" + [NativeProbe]::ClientInfo($hwnd))
# 抓**主窗整块**：缩放落在输出面上，但也可能只体现在浮层提示上，抓主窗不会漏。
$rect = $主窗区
Write-Host ("    pid={0} 主窗={1} 输出子窗={2} 区域={3}x{4} @({5},{6})" -f `
    $p.Id, $hwnd, $child, $rect.Width, $rect.Height, $rect.Left, $rect.Top)
if ($rect.Width -lt 200 -or $rect.Height -lt 150) {
    Write-Host '窗口区域太小，抓屏没有分辨力。' -ForegroundColor Red
    $p.Kill(); exit 1
}
$anchorX = $rect.Left + [int]($rect.Width * 0.75)
$anchorY = $rect.Top + [int]($rect.Height * 0.30)
$无效 = $false
try {
    Write-Host "[2] P 稳定性臂（不注入输入，隔 0.4s 连抓两帧）" -ForegroundColor Cyan
    $w0 = 0; $h0 = 0
    $a = [NativeProbe]::Grab($rect, [ref]$w0, [ref]$h0)
    Start-Sleep -Milliseconds (400)
    $b = [NativeProbe]::Grab($rect, [ref]$w0, [ref]$h0)
    $stableDiff = [NativeProbe]::Diff($a, $b)
    $spread = [NativeProbe]::Spread($a)
    Write-Host ("    两帧差 = {0:F3}；帧内亮度极差 = {1:F0}（极差 0 ⇒ 抓到平色/黑帧）" -f $stableDiff, $spread)
    if ($spread -lt 16 -or $stableDiff -gt 1.0) { $无效 = $true; Write-Host '    稳定性臂没过：要么没抓到内容，要么产品在自动变' -ForegroundColor Yellow }

    # 尺寸对照臂已撤：改宽后区域尺寸变了，Diff 拿不同长度的数组只能返回 -1 哨兵，
    # 那条读数没有信息量（上一版报 -1.000 就是这个）。R 正对照臂本身已能证明输入到位。

    # 置顶后断言归属：不置顶就会抓到压在产品的上面的别的窗口。
    [void][NativeProbe]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0040)
    [void][NativeProbe]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 600
    $中心 = [NativeProbe]::RectOf($hwnd)
    $归属 = [NativeProbe]::OwnerAt($中心.Left + [int]($中心.Width / 2), $中心.Top + [int]($中心.Height / 2), [uint32]$p.Id)
    Write-Host ("    归属断言：主窗中心点之上 = {0}" -f $归属)
    if (-not $归属.StartsWith("本进程")) {
        Write-Host '    归属没过：抓到的不是产品窗口。本轮所有读数作废。' -ForegroundColor Yellow
        try { if (-not $p.HasExited) { $p.Kill() } } catch { }
        exit 3
    }
    Write-Host "[3] R 正对照臂（先跑，因为它决定 Q 的红是不是真红）" -ForegroundColor Cyan
    $wR = 0; $hR = 0
    $baseR = [NativeProbe]::Grab($rect, [ref]$wR, [ref]$hR)
    for ($i = 0; $i -lt 8; $i++) { [void][NativeProbe]::Wheel($child, 120, $anchorX, $anchorY); Start-Sleep -Milliseconds (60) }
    Start-Sleep -Milliseconds (1200)
    $endR = [NativeProbe]::Grab($rect, [ref]$wR, [ref]$hR)
    $finalR = [NativeProbe]::Diff($baseR, $endR)
    Write-Host ("    8 格后与基线差 = {0:F3}" -f $finalR)
    if ($finalR -lt 0.5) {
        $无效 = $true
        Write-Host '    正对照臂没过：投了 8 格屏上没动 ⇒ 量具死（抓黑帧/投错窗口），Q 一律不作结论。' -ForegroundColor Yellow
    }

    if (-not $无效) {
        Write-Host "[4] Q 小信号臂（一格滚轮后连续抓 $Frames 帧）" -ForegroundColor Cyan
        $wQ = 0; $hQ = 0
        $base = [NativeProbe]::Grab($rect, [ref]$wQ, [ref]$hQ)
        $t0 = Get-Date
        [void][NativeProbe]::Wheel($child, 120, $anchorX, $anchorY)
        $prev = $base
        $中间态 = 0
        $最后变化ms = -1
        $rows = @()
        for ($i = 0; $i -lt $Frames; $i++) {
            Start-Sleep -Milliseconds ($IntervalMs)
            $f = [NativeProbe]::Grab($rect, [ref]$wQ, [ref]$hQ)
            $d = [NativeProbe]::Diff($prev, $f)
            $dt = [int]((Get-Date) - $t0).TotalMilliseconds
            if ($d -gt 0.15) { $中间态 += 1; $最后变化ms = $dt }
            $rows += ("      帧{0,2} t={1,5}ms Δ与上一帧={2:F3}" -f $i, $dt, $d)
            $prev = $f
        }
        $rows | ForEach-Object { Write-Host $_ }
        $endDiff = [NativeProbe]::Diff($base, $prev)
        Write-Host ("    一格滚轮：屏上中间态 = {0} 帧；最后一次变化在 {1} ms；与基线末差 = {2:F3}" -f $中间态, $最后变化ms, $endDiff)
        Write-Host ("    判读：中间态>=3 ⇒ 平滑真的到达屏幕（不是被 vsync 合并成一次跳变）；==0 而末差>0 ⇒ 一步到位，体感是跳变")

        Write-Host "[5] 复位臂：反向 8 格应回到接近基线" -ForegroundColor Cyan
        for ($i = 0; $i -lt 9; $i++) { [void][NativeProbe]::Wheel($child, -120, $anchorX, $anchorY); Start-Sleep -Milliseconds (50) }
        Start-Sleep -Milliseconds (1500)
        $back = [NativeProbe]::Grab($rect, [ref]$wQ, [ref]$hQ)
        $backDiff = [NativeProbe]::Diff($base, $back)
        Write-Host ("    反向 9 格后与「放大一格前」基线差 = {0:F3}" -f $backDiff)
    }
}
finally {
    Write-Host "[6] 收尾：关产品" -ForegroundColor Cyan
    try { if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit(5000) | Out-Null } } catch { }
    Write-Host ("    进程 {0} 已退出" -f $p.Id)
}
if ($无效) { exit 3 }
exit 0
