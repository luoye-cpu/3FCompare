# pp_ab.ps1 - 同窗交替 A/B，测"静图首帧真的呈交上屏"的时刻（T2p）
#
# 为什么要它：屏幕 DC 的旧 T2 判据已被证明不测内容（docs/122 §0 判词 2、§5），
# 而 PrintWindow 路每拍 167-431 ms，做不了细粒度时间学。内核在静图首帧 Render
# 成功返回处打一条 QPC 读数（[pp]），与父进程 Stopwatch 同一个计数器（.NET Stopwatch
# 就是 QueryPerformanceCounter），于是内容上屏时刻可以在不影响被测物的前提下取到。
#
# 用法：
#   pwsh -File pp_ab.ps1 -ExeA <arm_base\FFF.Player.exe> -ExeB <arm_head\FFF.Player.exe> \
#        -TagA base -TagB head -Image <png> -Rounds 20 -CsvOut out.csv
#
# 判据：[pp] 行里的 qpc 减去"起跑前抓的 qpc"，除频率。拿不到 [pp] ⇒ 该轮记 INVALID 并点名，
# 不折成 0、不静默丢（缺列记"未判"）。
param(
    [Parameter(Mandatory = $true)][string]$ExeA,
    [Parameter(Mandatory = $true)][string]$ExeB,
    [string]$TagA = 'A',
    [string]$TagB = 'B',
    [string]$Image = 'C:\PLAN\3FCompare\3fp\build\imgperf\sample_1920.png',
    [int]$Rounds = 20,
    [int]$TimeoutSec = 25,
    [string]$CsvOut = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ExeA)) { throw "找不到 $ExeA" }
if (-not (Test-Path -LiteralPath $ExeB)) { throw "找不到 $ExeB" }
if (-not (Test-Path -LiteralPath $Image)) { throw "样张不存在：$Image" }
if ([string]::IsNullOrWhiteSpace($CsvOut)) {
    $CsvOut = Join-Path (Split-Path $ExeA -Parent) ('pp_ab_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.csv')
}
if (Test-Path -LiteralPath $CsvOut) { Remove-Item -LiteralPath $CsvOut -Force }

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

function Get-Median([double[]]$v) {
    if (-not $v -or $v.Count -eq 0) { return -1 }
    $s = @($v | Sort-Object); $n = $s.Count
    if ($n % 2 -eq 1) { return $s[[int](($n - 1) / 2)] }
    return ($s[$n / 2 - 1] + $s[$n / 2]) / 2.0
}

function Get-TotalCpuPct {
    try { return [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime }
    catch { return -1.0 }
}

function Clear-PlayerInstances {
    Get-Process -Name FFF.Player -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
    Start-Sleep -Milliseconds 1200
}

function Measure-One([string]$exe, [string]$tag, [int]$round) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $psi.Arguments = '"' + $Image + '"'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    Clear-PlayerInstances
    $freq = [Diagnostics.Stopwatch]::Frequency
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $spawnQpc = [Diagnostics.Stopwatch]::GetTimestamp()
    $p = [System.Diagnostics.Process]::Start($psi)
    $spawnMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
    $reader = $p.StandardError
    $t1 = -1.0; $pp = -1.0; $note = ''; $ppRaw = ''
    # 一次只挂一个未完成的读，否则同一个 reader 上并发两笔读会交错丢行。
    $rdTask = $reader.ReadLineAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($p.HasExited) { $note = "early-exit exit=$($p.ExitCode)"; break }
        if ($t1 -lt 0) {
            $tt = ''
            if ([PpAb.Native]::FindTitled([uint32]$p.Id, [ref]$tt)) { $t1 = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
        }
        $wt = $rdTask.Wait(120)
        if (-not $wt) { continue }
        $line = $rdTask.Result
        $rdTask = $null
        if ($null -eq $line) { $note = 'stderr-EOF'; break }
        if ($line -match '\[pp\] still_first_present qpc=(\d+)') {
            $pp = ([int64]$Matches[1] - $spawnQpc) * 1000.0 / $freq
            $ppRaw = $line.Trim()
            break
        }
    }
    if ($pp -lt 0 -and $note -eq '') { $note = 'NO-[pp]-LINE(timeout)' }
    if ($t1 -lt 0 -and $note -eq '') { $note = 'no-titled-window' }
    try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    Start-Sleep -Milliseconds 800
    return [pscustomobject]@{
        Round = $round; Arm = $tag; LoadPct = (Get-TotalCpuPct); SpawnMs = $spawnMs
        T1ms = $t1; PPms = [math]::Round($pp, 1); Note = $note; PPLine = $ppRaw
    }
}

$rows = New-Object System.Collections.Generic.List[object]
for ($r = 1; $r -le $Rounds; $r++) {
    foreach ($arm in @(@{ e = $ExeA; t = $TagA }, @{ e = $ExeB; t = $TagB })) {
        $rows.Add((Measure-One $arm.e $arm.t $r))
    }
    Write-Host ("round {0}/{1} 完成" -f $r, $Rounds)
}
$rows | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8
$bad = @($rows | Where-Object { $_.PPms -lt 0 })
Write-Host ("总轮 {0} | 有效 {1} | 无效 {2}（{3}）" -f $rows.Count, ($rows.Count - $bad.Count), $bad.Count, (($bad | ForEach-Object { $_.Arm + '#' + $_.Round + ':' + $_.Note }) -join ', '))
foreach ($t in @($TagA, $TagB)) {
    $s = @($rows | Where-Object { $_.Arm -eq $t -and $_.PPms -ge 0 })
    if ($s.Count -eq 0) { Write-Host ("  {0} 无有效样本" -f $t); continue }
    $pp = [double[]]@($s | ForEach-Object { $_.PPms })
    $t1 = [double[]]@($s | ForEach-Object { $_.T1ms } | Where-Object { $_ -gt 0 })
    Write-Host ("  {0,-6} n={1,3}  T1 中位={2,8:N1}  T2p 中位={3,8:N1}  内容-窗口={4,7:N1}" -f `
        $t, $s.Count, (Get-Median $t1), (Get-Median $pp), ((Get-Median $pp) - (Get-Median $t1)))
}
Write-Host ("明细：{0}" -f $CsvOut)
