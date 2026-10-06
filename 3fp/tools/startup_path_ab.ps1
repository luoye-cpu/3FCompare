param(
    [string]$Exe = "C:\PLAN\3FCompare\3fp\kernel\FFF.Player\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.exe",
    [int]$Rounds = 9
)
# 裁决实验：核心文件探测是否真的扫了 PATH？
# 逻辑：`可以加载核心文件` 用 For Each 遍历迭代器且首次命中即 Return（迭代器惰性）。
#   若 BaseDirectory 里有 FFmpeg ⇒ PATH 目录根本不会被枚举 ⇒ 缩短 PATH 不改变耗时。
#   若 PATH 确实被扫描 ⇒ 64 项 PATH 明显慢于 2 项 PATH。
# 判据：同批次交替对比「完整 PATH」vs「极简 PATH」的中位数差。
$ErrorActionPreference = 'Stop'
if (-not ('StartupProbe.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
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

$dir = Split-Path -Parent $Exe
$fullPath = $env:PATH
$fullCount = @($fullPath -split ';' | Where-Object { $_.Trim() }).Count
$minPath = "$dir;C:\Windows\System32;C:\Windows"

function Measure-One([string]$pathValue) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $dir
    $psi.EnvironmentVariables['PATH'] = $pathValue
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $procId = [uint32]$p.Id
    $ms = -1.0
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($p.HasExited) { break }
        if ([StartupProbe.Native]::HasVisibleTitledWindow($procId)) {
            $ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1); break
        }
        Start-Sleep -Milliseconds 4
    }
    try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    Start-Sleep -Milliseconds 1200
    return $ms
}

Write-Host ("完整 PATH 条目数: {0} / 极简 PATH 条目数: 3" -f $fullCount)
Measure-One $fullPath | Out-Null; Measure-One $minPath | Out-Null

$runsFull = @(); $runsMin = @()
for ($i = 1; $i -le $Rounds; $i++) {
    $v1 = Measure-One $fullPath; $runsFull += $v1
    $v2 = Measure-One $minPath;  $runsMin  += $v2
    Write-Host ("round {0}: 完整PATH={1}  极简PATH={2}  delta={3}" -f $i, $v1, $v2, [math]::Round($v2 - $v1, 1))
}
function Get-Stat($arr) {
    $s = @($arr | Sort-Object)
    $med = if ($s.Count % 2 -eq 1) { $s[[int][math]::Floor($s.Count / 2)] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }
    return [pscustomobject]@{ Min = $s[0]; Median = [math]::Round($med, 1); Max = $s[-1]; Mean = [math]::Round((($arr | Measure-Object -Average).Average), 1) }
}
$stF = Get-Stat $runsFull; $stM = Get-Stat $runsMin
Write-Host "`n=== 完整 PATH (64 项) ==="; $stF | Format-List
Write-Host "=== 极简 PATH (3 项) ==="; $stM | Format-List
Write-Host ("中位数差（极简 - 完整）: {0} ms" -f [math]::Round($stM.Median - $stF.Median, 1))
Write-Host "判读：差值在 ±15 ms 内 ⇒ PATH 未被枚举（BaseDirectory 命中即短路）；显著为负 ⇒ PATH 确实被扫描。"
