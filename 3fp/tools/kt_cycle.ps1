# kt_cycle.ps1 - 逐轮轮转格式，并把内核 [KT] 全部戳位落到每轮一个原始文件
#
# 为什么不用 fmt_cycle.ps1：fmt_cycle 只认一个 [pp] 戳、命中即 break，
# 而六戳分解要的是**同一条 stderr 流上的全部 [KT] 行**（break 就把后面的段丢了）。
#
# 判据口径：
#   · 钟 = QueryPerformanceCounter。父进程 Stopwatch.GetTimestamp() 与 Win32 QPC 同钟同频，
#     所以所有 qpc 减 spawnQpc 再除 Frequency 就是"相对进程创建毫秒"，跨语言可比。
#   · 逐轮轮转格式（同窗内 PNG/JPEG/JXL 交替），否则被负载漂移污染（fmt_cycle 头注有实测）。
#   · 解析器对标签名无感：只按 `\[KT\] <tag> qpc=<int>` 抓，代理改了标签也不用改这里。
#   · 收到终止标签后再宽限 -TailMs 把尾巴行抽干（呈交之后仍有 seek/flush 等戳位，不能漏）。
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Images,   # "label=path;label=path"
    [int]$Rounds = 6,
    [int]$TimeoutSec = 25,
    [int]$TailMs = 2500,
    [string]$TerminalTags = 'k_present_ret,k_bind_render_done,k_open_return',
    [string]$RawDir = '',
    [string]$CsvOut = ''
)
$ErrorActionPreference = 'Stop'
$exeDir = Split-Path $Exe -Parent
if ([string]::IsNullOrWhiteSpace($RawDir)) { $RawDir = Join-Path $exeDir 'kt_raw' }
if ([string]::IsNullOrWhiteSpace($CsvOut)) { $CsvOut = Join-Path $exeDir ('kt_cycle_' + (Get-Date -Format 'HHmmss') + '.csv') }
New-Item -ItemType Directory -Path $RawDir -Force | Out-Null
if (Test-Path -LiteralPath $CsvOut) { Remove-Item -LiteralPath $CsvOut -Force }

$term = @($TerminalTags -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$list = @($Images -split ';' | ForEach-Object {
    $kv = $_ -split '=', 2
    [pscustomobject]@{ Label = $kv[0]; Path = ($kv[1] -replace '\^', ':') }
})
foreach ($i in $list) { if (-not (Test-Path -LiteralPath $i.Path)) { throw "夹具不存在：$($i.Path)" } }

if (-not ('KtAb.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace KtAb {
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

function Get-Median($vals) {
    $s = @($vals | Sort-Object)
    if ($s.Count -eq 0) { return $null }
    $m = [int][Math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return $s[$m] }
    return [math]::Round(($s[$m - 1] + $s[$m]) / 2.0, 2)
}

$freq = [Diagnostics.Stopwatch]::Frequency
$rows = New-Object System.Collections.Generic.List[object]
$allTags = New-Object System.Collections.Generic.HashSet[string]

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
        $reader = $p.StandardError

        $stamps = New-Object System.Collections.Generic.List[object]   # 有序：标签 + 相对 spawn 毫秒
        $seen = New-Object System.Collections.Generic.HashSet[string]
        # 非 [KT] 行也留档（内核自己的 `[hwcap] …candidates=…trialRefused=…` 判词就在里面）；
        # 聚合器只吃 "#"-以外且形如 "<ms> <tag>" 的行，所以这些不会污染统计。上限 600 行防爆。
        $others = New-Object System.Collections.Generic.List[string]
        $t1 = -1.0; $note = ''
        $rdTask = $reader.ReadLineAsync()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
        $termHitAt = $null

        while ([DateTime]::UtcNow -lt $deadline) {
            if ($p.HasExited) { $note = "early-exit exit=$($p.ExitCode)"; break }
            if ($t1 -lt 0) {
                $tt = ''
                if ([KtAb.Native]::FindTitled([uint32]$p.Id, [ref]$tt)) { $t1 = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }
            }
            $wait = 120
            if ($null -ne $termHitAt) {
                $left = $TailMs - ((Get-Date) - $termHitAt).TotalMilliseconds
                if ($left -le 0) { break }
                $wait = [int][Math]::Min(300, $left)
            }
            if (-not $rdTask.Wait($wait)) {
                if ($null -ne $termHitAt) { break }   # 尾巴已静默
                continue
            }
            $line = $rdTask.Result; $rdTask = $null
            if ($null -eq $line) { $note = 'stderr-EOF'; break }
            if ($line -match '\[KT\]\s+(\S+)\s+qpc=(\d+)') {
                $tag = $Matches[1]
                $relMs = ([int64]$Matches[2] - $spawnQpc) * 1000.0 / $freq
                $stamps.Add([pscustomobject]@{ Tag = $tag; Ms = [math]::Round($relMs, 2) })
                [void]$seen.Add($tag)
                [void]$allTags.Add($tag)
                if ($null -eq $termHitAt -and ($term -contains $tag)) { $termHitAt = Get-Date }
            } elseif ($line -match '\[LT\]\s+(\S+)\s+([\d.]+)') {
                # 宿主 [LT] 打的是**绝对 QPC 毫秒**（Stopwatch.GetTimestamp()*1000/Frequency），
                # 内核 [KT] 打的是**绝对 QPC 原始刻度**。两者同钟 ⇒ 都能换算成"相对进程创建毫秒"，
                # 于是宿主段与内核段可以落在同一条时间轴上排序（§2-B 要的正是"图层群结束 vs 内容上屏"的先后）。
                $tag = $Matches[1]
                $relMs = [double]$Matches[2] - $spawnQpc * 1000.0 / $freq
                $stamps.Add([pscustomobject]@{ Tag = $tag; Ms = [math]::Round($relMs, 2) })
                [void]$seen.Add($tag)
                [void]$allTags.Add($tag)
            } elseif ($others.Count -lt 600) {
                $others.Add($line)
            }
            $rdTask = $reader.ReadLineAsync()
        }
        if ($seen.Count -eq 0 -and $note -eq '') { $note = 'NO-[KT]' }
        foreach ($need in $term) { if ($seen.Contains($need)) { $note = ''; break } }
        if ($note -eq '' ) { $note = 'ok' }

        try { if (-not $p.HasExited) { $p.Kill() } } catch { }
        try { $p.WaitForExit(5000) | Out-Null } catch { }

        $rawFile = Join-Path $RawDir ('r{0:d2}_{1}.txt' -f $r, $fx.Label)
        $header = @(
            "# round=$r fmt=$($fx.Label) pid=$($p.Id) spawnQpc=$spawnQpc freq=$freq",
            "# t1_ms=$t1 note=$note load=$((Get-TotalCpuPct))"
        )
        # 数字必须用**不换文化**的写法：'{0,10:N2}' 在中文环境会打成 "3,985.55"（带千分位），
        # 聚合器的 ^[\d.]+ 就一行也解析不出来，而"文件明明在"⇒ 极难一眼看出（本轮真踩过）。
        $inv = [System.Globalization.CultureInfo]::InvariantCulture
        $body = $stamps | ForEach-Object { ($_.Ms.ToString('F2', $inv) + '  ' + $_.Tag) }
        $tail = if ($others.Count -gt 0) { @('# ---- 非 [KT] stderr（原样留档，聚合器不吃这些行）----') +
                    ($others | ForEach-Object { '#| ' + $_ }) } else { @() }
        [System.IO.File]::WriteAllLines($rawFile, @($header + $body + $tail), [System.Text.UTF8Encoding]::new($false))

        $termMs = -1.0
        if ($stamps.Count -gt 0) { $termMs = $stamps[$stamps.Count - 1].Ms }
        $rows.Add([pscustomobject]@{
            Round = $r; Fmt = $fx.Label; LoadPct = (Get-TotalCpuPct)
            T1ms = $t1; Stamps = $stamps.Count; LastMs = [math]::Round($termMs, 1); Note = $note
        })
        Write-Host ("r{0} {1}: 戳 {2} 枚 T1={3} 末戳={4:N1} {5}" -f $r, $fx.Label, $stamps.Count, $t1, $termMs, $note)
    }
}
$rows | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8
Write-Host ("明细：{0}`n原始：{1}" -f $CsvOut, $RawDir)
Write-Host ("标签全集（{0} 枚）：{1}" -f $allTags.Count, (($allTags | Sort-Object) -join ' '))
foreach ($g in ($rows | Group-Object Fmt)) {
    $zero = @($g.Group | Where-Object { $_.Stamps -eq 0 }).Count
    Write-Host ("  {0}: 轮 {1} 空 {2} 末戳中位 {3}" -f $g.Name, $g.Group.Count, $zero, (Get-Median ($g.Group | Where-Object { $_.LastMs -ge 0 } | ForEach-Object { $_.LastMs })))
}
