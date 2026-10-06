param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$WaitMs = 6000
)
# 判据：宿主启动时若找不到 FFmpeg，Form1_Load 会在「核心文件缺失」处 Return（早退路径），
#      随后弹出错误说明。本探针同时取：① 启动耗时 ② 该进程所有可见窗口的标题。
#      早退路径 ⇒ 出现含「缺失/错误/无法」的标题，且耗时明显更低。
$ErrorActionPreference = 'Stop'
if (-not ('StartupProbe.Native2' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace StartupProbe {
    public static class Native2 {
        delegate bool EnumCb(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr h);
        public static List<string> Titles(uint wantedPid) {
            var r = new List<string>();
            EnumWindows((h, l) => {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != wantedPid) return true;
                if (!IsWindowVisible(h)) return true;
                int len = GetWindowTextLengthW(h);
                var sb = new StringBuilder(len + 2);
                GetWindowTextW(h, sb, sb.Capacity);
                if (sb.Length > 0) r.Add(sb.ToString());
                return true;
            }, IntPtr.Zero);
            return r;
        }
        public static bool HasVisibleTitledWindow(uint wantedPid) {
            return Titles(wantedPid).Count > 0;
        }
    }
}
'@
}
$dir = Split-Path -Parent $Exe
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = [System.Diagnostics.Process]::Start($Exe)
$pid2 = [uint32]$p.Id
$ms = -1.0
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while ([DateTime]::UtcNow -lt $deadline) {
    if ($p.HasExited) { break }
    if ([StartupProbe.Native2]::HasVisibleTitledWindow($pid2)) { $ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1); break }
    Start-Sleep -Milliseconds 4
}
Start-Sleep -Milliseconds $WaitMs
$titles = @()
try { $titles = [StartupProbe.Native2]::Titles($pid2) } catch { }
try { if (-not $p.HasExited) { $p.Kill() } } catch { }
try { $p.WaitForExit(5000) | Out-Null } catch { }
Start-Sleep -Milliseconds 1000

$bad = @($titles | Where-Object { $_ -match '缺失|错误|无法|失败|missing|error' })
Write-Host ("exe      : {0}" -f $Exe)
Write-Host ("启动耗时 : {0} ms" -f $ms)
Write-Host ("窗口标题 : {0}" -f ($(if ($titles) { $titles -join ' | ' } else { '(无)' })))
if ($bad.Count -gt 0) {
    Write-Host ("判定     : 早退路径（核心文件缺失/错误） ❌  命中: {0}" -f ($bad -join ' | '))
} else {
    Write-Host "判定     : 未见错误窗口 ✅"
}
