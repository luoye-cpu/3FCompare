# fmt_cycle.ps1 - 同窗轮转测多个图像格式的"内容上屏"时刻
#
# 为什么要它：分块跑（先 PNG 八轮、再 JPEG 八轮…）会被负载漂移污染（实测各块负载 34%~68.5%）。
# 这里逐轮轮转所有格式，格式之间的差就在同一窗里可比。
# 测点 = 内核 [pp]（静图首帧呈交完成），与父进程 Stopwatch 同为 QueryPerformanceCounter 钟。
# 注意：-Exe 必须是带 [pp] 打点的臂（arm_pp_*）；无打点 ⇒ 每轮都记未判。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Images,   # "label=path;label=path"
    [int]$Rounds = 8,
    [int]$TimeoutSec = 25,
    [string]$CsvOut = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($CsvOut)) {
    $CsvOut = Join-Path (Split-Path $Exe -Parent) ('fmt_cycle_' + (Get-Date -Format 'HHmmss') + '.csv')
}
if (Test-Path -LiteralPath $CsvOut) { Remove-Item -LiteralPath $CsvOut -Force }
$list = @($Images -split ';' | ForEach-Object {
    $kv = $_ -split '=', 2
    [pscustomobject]@{ Label = $kv[0]; Path = ($kv[1] -replace '\^', ':') }
})
foreach ($i in $list) { if (-not (Test-Path -LiteralPath $i.Path)) { throw "夹具不存在：$($i.Path)" } }

if (-not ('PpAb.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace PpAb {
    public static class Native {
        delegate bool EnumCb(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        public static bool FindTitled(uint want, out string title) {
            title = ""; IntPtr hit = IntPtr.Zero; string t = "";
            EnumWindows((h, l) => {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != want || !IsWindowVisible(h) || GetWindowTextLength(h) <= 0) return true;
                var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity);
                hit = h; t = sb.ToString(); return false;
            }, IntPtr.Zero);
            if (hit == IntPtr.Zero) return false;
            title = t; return true;
        }
    }
}
'@ | Out-Null
}

function Get-TotalCpuPct {
    try { return [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime }
    catch { return -1.0 }
}

$rows = New-Object System.Collections.Generic.List[object]
for ($r = 1; $r -le $Rounds; $r++) {
    foreach ($fx in $list) {
        Get-Process -Name FFF.Player -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
        Start-Sleep -Milliseconds 1200
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $Exe; $psi.Arguments = '"' + $fx.Path + '"'
        $psi.UseShellExecute = $false; $psi.RedirectStandardError = $true
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $spawnQpc = [Diagnostics.Stopwatch]::GetTimestamp()
        $p = [System.Diagnostics.Process]::Start($psi)
        $spawnMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        $reader = $p.StandardError
        $t1 = -1.0; $pp = -1.0; $note = ''
        $rdTask = $reader.ReadLineAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($p.HasExited) { $note = "early-exit exit=$($p.ExitCode)"; break }
            if ($t1 -lt 0) {
                $tt = ''
                if ([PpAb.Native]::FindTitled([uint32]$p.Id, [ref]$tt)) { $t1 = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
            }
            if (-not $rdTask.Wait(120)) { continue }
            $line = $rdTask.Result; $rdTask = $null
            if ($null -eq $line) { $note = 'stderr-EOF'; break }
            if ($line -match '\[pp\] still_first_present qpc=(\d+)') {
                $pp = ([int64]$Matches[1] - $spawnQpc) * 1000.0 / [Diagnostics.Stopwatch]::Frequency
                break
            }
            $rdTask = $reader.ReadLineAsync()
        }
        if ($pp -lt 0 -and $note -eq '') { $note = 'NO-[pp]' }
        if ($t1 -lt 0 -and $note -eq '') { $note = 'no-titled-window' }
        try { if (-not $p.HasExited) { $p.Kill() } } catch { }
        try { $p.WaitForExit(5000) | Out-Null } catch { }
        $rows.Add([pscustomobject]@{
            Round = $r; Fmt = $fx.Label; LoadPct = (Get-TotalCpuPct); SpawnMs = $spawnMs
            T1ms = $t1; PPms = [math]::Round($pp, 1); Note = $note
        })
    }
    Write-Host ("round {0}/{1} 完成" -f $r, $Rounds)
}
$rows | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8
Write-Host ("明细：{0}" -f $CsvOut)
$bad = @($rows | Where-Object { $_.PPms -lt 0 })
Write-Host ("总样本 {0} | 未判 {1}（{2}）" -f $rows.Count, $bad.Count, (($bad | Group-Object Fmt | ForEach-Object { $_.Name + '×' + $_.Count }) -join ', '))
