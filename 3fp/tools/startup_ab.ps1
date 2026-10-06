param(
    [string]$ExeA = "C:\PLAN\3FCompare\3fp\kernel\FFF.Player\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.exe",
    [string]$ExeB = "C:\Users\20210\AppData\Local\Temp\fff_r2r_pub\FFF.Player.exe",
    [int]$Rounds = 9
)
# A/B 交替对照（黄金规则 7）：同一批次内 A,B,A,B... 消除系统负载漂移
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
function Measure-One([string]$exe) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($exe)
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
# ⚠ PowerShell 变量名大小写不敏感 ⇒ 绝不能用 $A/$a 这样只差大小写的名字（会被当成同一变量，统计被污染）
# 预热
Measure-One $ExeA | Out-Null; Measure-One $ExeB | Out-Null
$runsA = @(); $runsB = @()
for ($i = 1; $i -le $Rounds; $i++) {
    $valA = Measure-One $ExeA; $runsA += $valA
    $valB = Measure-One $ExeB; $runsB += $valB
    Write-Host ("round {0}: A(IL)={1}  B(R2R)={2}  delta={3}" -f $i, $valA, $valB, [math]::Round($valB-$valA,1))
}
function Get-Stat($arr) {
    $s = @($arr | Sort-Object)
    $med = if ($s.Count % 2 -eq 1) { $s[[int][math]::Floor($s.Count/2)] } else { ($s[$s.Count/2-1]+$s[$s.Count/2])/2 }
    return [pscustomobject]@{ Min=$s[0]; Median=[math]::Round($med,1); Max=$s[-1]; Mean=[math]::Round((($arr|Measure-Object -Average).Average),1) }
}
Write-Host "`n=== A (纯 IL) ==="; Get-Stat $runsA | Format-List
Write-Host "=== B (R2R) ==="; Get-Stat $runsB | Format-List
$statA = Get-Stat $runsA; $statB = Get-Stat $runsB
Write-Host ("A 中位数 {0} ms / B 中位数 {1} ms => 改善 {2} ms ({3:F1}%)" -f $statA.Median, $statB.Median, [math]::Round($statA.Median-$statB.Median,1), (($statA.Median-$statB.Median)/$statA.Median*100))
$wins = 0
for ($i=0; $i -lt $Rounds; $i++) { if ($runsB[$i] -lt $runsA[$i]) { $wins++ } }
Write-Host ("B 优于 A 的轮次: {0}/{1}" -f $wins, $Rounds)
