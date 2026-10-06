param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$Runs = 9,
    [int]$WarmupRuns = 2,
    [int]$TimeoutSec = 60,
    [int]$PollMs = 4,
    [string]$CsvOut = ''
)
# startup_time.ps1 —— 图片模式宿主「进程启动 → 主窗口可见」外部计时（自包含，不依赖 WinUi.cs）
#
# 判据：
#   计时点 = Process.Start 之前记 T0 → 该进程首个「可见的顶层窗口」出现记 T1。
#   涵盖：.NET 运行时加载 + WinForms 初始化 + Form1_Load 全流程 + 首帧呈现请求。
#   这是「用户感受到的冷启动」的合理代理量。
#
#   不含：双击文件关联、命令传参、其它进程抢 CPU（但仍受系统负载影响 ⇒ 只比相对差）。
#
# ⚠ 单实例互斥体：宿主 IsSingleInstance=True ⇒ 必须确保跑前无残留实例。
# ⚠ Kill 用 Terminate（不触发正常退出路径），每次跑完等 1.2s 让句柄/互斥体回收。
$ErrorActionPreference = 'Stop'

if (-not ('StartupProbe.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace StartupProbe {
    public static class Native {
        delegate bool EnumCb(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr h);

        public static bool HasVisibleTitledWindow(uint wantedPid) {
            bool found = false;
            EnumWindows((h, l) => {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != wantedPid) return true;
                if (!IsWindowVisible(h)) return true;
                int len = GetWindowTextLengthW(h);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 2);
                GetWindowTextW(h, sb, sb.Capacity);
                if (sb.Length > 0) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
'@
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "找不到 exe: $Exe" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path

function Measure-One {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($Exe)
    $procId = [uint32]$p.Id
    $visMs = -1.0
    $note = ''
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($p.HasExited) { $note = "提前退出 exit=$($p.ExitCode)"; break }
        if ([StartupProbe.Native]::HasVisibleTitledWindow($procId)) {
            $visMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1); break
        }
        Start-Sleep -Milliseconds $PollMs
    }
    if ($visMs -lt 0 -and $note -eq '') { $note = "超时未出现可见窗口" }
    try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    Start-Sleep -Milliseconds 1200
    return [pscustomobject]@{ Pid = $procId; Ms = $visMs; Note = $note }
}

$results = @()
if ($WarmupRuns -gt 0) {
    Write-Host "== 预热 $WarmupRuns 次（不计入）=="
    for ($i = 1; $i -le $WarmupRuns; $i++) {
        $r = Measure-One
        Write-Host ("  warmup {0}: {1} ms {2}" -f $i, $r.Ms, $r.Note)
    }
}
Write-Host "== 正式 $Runs 次 =="
for ($i = 1; $i -le $Runs; $i++) {
    $r = Measure-One
    $results += $r
    Write-Host ("  run {0}: {1} ms {2}" -f $i, $r.Ms, $r.Note)
}

$ok = @($results | Where-Object { $_.Ms -ge 0 } | Select-Object -ExpandProperty Ms)
if ($ok.Count -eq 0) { Write-Host "`n全部失败，无有效样本。"; exit 1 }
$sorted = @($ok | Sort-Object)
$median = if ($sorted.Count % 2 -eq 1) { $sorted[[int][math]::Floor($sorted.Count / 2)] }
          else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
Write-Host "`n===== 汇总 ====="
[pscustomobject]@{
    Exe      = $Exe
    N        = $ok.Count
    MinMs    = $sorted[0]
    MedianMs = [math]::Round($median, 1)
    MaxMs    = $sorted[-1]
    MeanMs   = [math]::Round(($ok | Measure-Object -Average).Average, 1)
    AllMs    = ($sorted -join ',')
} | Format-List
if ($CsvOut -ne '') {
    $results | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8
    Write-Host "明细已写: $CsvOut"
}
