# mode_pixel_diff.ps1 - 静图改走 CPU 解码后，**屏幕上那张图还是不是同一张**
#
# 为什么这一条必须在收益之前测：本仓对像素同一性的要求不因收益大而放宽（docs/122 §6 的结构闸）。
# NVDEC 与 libjpeg 的基线 JPEG 输出在舍入/色度上不必逐位相同 —— 若两臂像素不同，
# 那"快 245 ms"就不是净赚，而是要用户裁决的一次画质变化。
#
# 抓图走 PrintWindow(PW_RENDERFULLCONTENT)：屏幕 DC 不绑窗（会被别家窗口压住，docs/122 §4.2 已证），
# gdigrab 抓 D3D 窗口全黑（docs/07 §2）。WinUi.Capture 内部已 SetProcessDPIAware（150% 缩放下不声明会虚报尺寸）。
param(
    [string]$Exe = 'C:\PLAN\3FCompare\3fp\build\imgperf\arm_pp_head\FFF.Player.exe',
    [string]$Fixture = 'C:\PLAN\3FCompare\3fp\build\imgperf\sample_1920.jpg',
    [int]$ModeA = 1, [int]$ModeB = 2,
    [int]$SettleMs = 5000,
    [string]$OutDir = 'C:\PLAN\3FCompare\3fp\build\imgperf\pixdiff'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$target = Join-Path (Split-Path $Exe -Parent) 'FFF.Player.Settings.json'
if (-not (Test-Path -LiteralPath $target)) { throw "找不到 $target" }
$raw = [IO.File]::ReadAllBytes($target)
$hadBom = ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF)
$skip = if ($hadBom) { 3 } else { 0 }
$base = [Text.Encoding]::UTF8.GetString($raw, $skip, $raw.Length - $skip)

function Set-Mode([string]$base, [int]$mode, [string]$target, [bool]$hadBom) {
    $pat = '("解码方式"\s*:\s*)-?\d+'
    $hits = @([regex]::Matches($base, $pat))
    if ($hits.Count -ne 1) { throw "解码方式 命中 $($hits.Count) 次（必须恰好 1）" }
    $out = [regex]::Replace($base, $pat, ('${1}' + $mode))       # 必须 ${1}：'$1'+数字 会被读成 $11
    $chk = ConvertFrom-Json $out
    if ([string]$chk.'解码方式' -ne [string]$mode) { throw "写进去的值回读是 $($chk.'解码方式')，不是 $mode" }
    [IO.File]::WriteAllText($target, $out, (New-Object Text.UTF8Encoding($hadBom)))
    return "解码方式=$mode（回读校验通过）"
}

# 抓图不引 WinUi.cs：Add-Type -Path + -ReferencedAssemblies 在这套 PS7(.NET10) 运行时里
# 满足不了 List<> 的类型转发（CS1069 明说它被转到 System.Private.CoreLib，给了门面或给了实现都仍失败），
# 而仓里可用的写法是 content_probe.ps1 那种**内联 -TypeDefinition + 两个 Drawing 位置**。
# 所以这里只带 Capture 需要的那几个 API，且刻意不用 List<>（用固定容量数组），保持能编译。
if (-not ('PCap' -as [type])) {
    # 逐个补会被"下一层转发"牵着走（GdiPlus 之后又要 System.Private.Windows.Core，无穷举）：
    # 直接把运行时目录里的 System.*.dll 全量给进引用集。Add-Type 会忽略用不上的那些。
    $fwDir = Split-Path ([psobject].Assembly.Location) -Parent
    $dotnetDrawing = @(Get-ChildItem -Path $fwDir -Filter 'System.*.dll' -File |
                       Where-Object { $_.Name -notmatch 'Native' } | ForEach-Object { $_.FullName })
    Write-Host ('  Add-Type 引用集：' + $fwDir + ' 下 System.*.dll 共 ' + $dotnetDrawing.Count + ' 枚')
    $src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Drawing;
using System.Drawing.Imaging;
public class PCap {
    delegate bool EnumCb(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    struct RECT { public int Left, Top, Right, Bottom; }
    static bool aware;
    public static string FindTitled(uint want, out string title) {
        if (!aware) { aware = true; try { SetProcessDPIAware(); } catch {} }
        title = ""; IntPtr hit = IntPtr.Zero; string t = "";
        EnumWindows((h, l) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != want || !IsWindowVisible(h) || GetWindowTextLength(h) <= 0) return true;
            StringBuilder sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity);
            hit = h; t = sb.ToString(); return false;
        }, IntPtr.Zero);
        if (hit == IntPtr.Zero) return null;
        title = t; return hit.ToString();
    }
    public static string Capture(string hwndStr, string path) {
        IntPtr h = new IntPtr(long.Parse(hwndStr));
        RECT r; if (!GetWindowRect(h, out r)) return "GetWindowRect 失败";
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w <= 0 || ht <= 0) return "尺寸非法 " + w + "x" + ht;
        IntPtr dc = GetWindowDC(h); if (dc == IntPtr.Zero) return "GetWindowDC 失败";
        try {
            Bitmap bmp = new Bitmap(w, ht, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) {
                IntPtr mem = g.GetHdc();
                bool ok = PrintWindow(h, mem, 2);          // PW_RENDERFULLCONTENT
                g.ReleaseHdc(mem);
                if (!ok) { bmp.Dispose(); return "PrintWindow 失败"; }
            }
            bmp.Save(path, ImageFormat.Png);
            int total = 0, lit = 0;
            foreach (Color c in new Color[4] { bmp.GetPixel(2, 2), bmp.GetPixel(w / 2, ht / 2),
                                                bmp.GetPixel(w - 3, 2), bmp.GetPixel(2, ht - 3) }) {
                total++; if (c.R + c.G + c.B > 24) lit++;
            }
            bmp.Dispose();
            return "抓到 " + w + "x" + ht + "（角/中样本站亮 " + lit + "/" + total + "）";
        } finally { ReleaseDC(h, dc); }
    }
}
'@
    Add-Type -TypeDefinition $src -ReferencedAssemblies $dotnetDrawing | Out-Null
}

foreach ($pair in @(@($ModeA, 'cpu'), @($ModeB, 'gpu'))) {
    Get-Process -Name FFF.Player -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 1200
    Write-Host ("臂 {0}: {1}" -f $pair[1], (Set-Mode $base $pair[0] $target $hadBom))
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe; $psi.Arguments = '"' + $Fixture + '"'
    $psi.UseShellExecute = $false
    # 这里不读 stderr，所以**不要**重定向：重定向了又不抽干，管道写满（~64 KB）会把子进程堵死，
    # 于是"等不到标题窗口"其实是抓图脚本自己把被测程序卡住了。
    $p = [Diagnostics.Process]::Start($psi)
    $title = $null; $end = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $end) {
        Start-Sleep -Milliseconds 250
        try { $p.Refresh() } catch { }
        if (-not [string]::IsNullOrWhiteSpace($p.MainWindowTitle)) { $title = $p.MainWindowTitle; break }
    }
    if (-not $title) { try { $p.Kill() } catch { }; throw "臂 $($pair[1]) 等不到标题窗口" }
    Start-Sleep -Milliseconds $SettleMs
    $png = Join-Path $OutDir ($pair[1] + '.png')
    $found = ''
    $h = [PCap]::FindTitled([uint32]$p.Id, [ref]$found)
    if (-not $h) { try { $p.Kill() } catch { }; throw "臂 $($pair[1])：$title 找不到绑定的标题窗口 ⇒ 不抓（抓错窗口会得到假的一致）" }
    Write-Host ("  标题='{0}' hwnd={1} 抓图 → {2}" -f $found, $h, $png)
    Write-Host ('  ' + [PCap]::Capture($h, $png))
    if (-not (Test-Path -LiteralPath $png)) { throw "臂 $($pair[1]) 没落盘 $png ⇒ 这一臂没有证据" }
    try { $p.Kill() } catch { }
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    Start-Sleep -Milliseconds 600
}
Write-Host '两臂抓图完成 → 交给 pixel_cmp.py 判同一性'
