# quiet_ab.ps1 - 冷启动两臂交替复测（安静窗口专用）
#
# 为什么需要它：docs/107 §五.1 的复测方案要求「两臂交替 ≥20 轮」，且必须同窗对照
# （跨窗口不可归因）。startup_image_time.ps1 是单臂串行工具，本脚本把它编排成
# 逐轮交替（A、B、A、B…），每轮记录当时的 CPU 负载，最后给中位数与 Mann-Whitney U。
#
# 用法：
#   pwsh -File quiet_ab.ps1 -ExeA <exe> -ExeB <exe> [-TagA JIT] [-TagB R2R] -Rounds 20 -CsvOut out.csv
#
# 判据沿用 startup_image_time.ps1：SpawnMs>50 判废；T2<=0 视为无效轮。
# 负载护栏：每轮开跑前采样一次总 CPU；avg 由调用者事后按 CsvOut 判读（不做自动中止，
# 避免把「窗口变吵」变成静默丢数据）。

param(
    [Parameter(Mandatory = $true)][string]$ExeA,
    [Parameter(Mandatory = $true)][string]$ExeB,
    [string]$TagA = 'A',
    [string]$TagB = 'B',
    [string]$Image = 'C:\PLAN\3FCompare\3fp\build\imgperf\sample_1920.png',
    [int]$Rounds = 20,
    [string]$CsvOut = '',
    # 臂级环境变量，供「同一 exe、不同开关」的 A/B 使用（如 FFF_EARLY_OPEN=1）。
    # 格式："KEY=VAL" 或 "KEY1=VAL1;KEY2=VAL2"。不设则两臂继承当前环境。
    [string]$EnvA = '',
    [string]$EnvB = ''
)

$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'startup_image_time.ps1'
if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "找不到 $tool（startup_image_time.ps1 应在同目录）" }
foreach ($e in @($ExeA, $ExeB)) { if (-not (Test-Path -LiteralPath $e -PathType Leaf)) { throw "exe 不存在：$e" } }
if (-not (Test-Path -LiteralPath $Image -PathType Leaf)) { throw "样张不存在：$Image" }
if ([string]::IsNullOrWhiteSpace($CsvOut)) {
    $CsvOut = Join-Path (Split-Path $ExeA -Parent) ('quiet_ab_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.csv')
}
if (Test-Path -LiteralPath $CsvOut) { Remove-Item -LiteralPath $CsvOut -Force }

function Get-TotalCpuPct {
    try { return [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime }
    catch { return -1.0 }
}

function Get-Median([double[]]$v) {
    if (-not $v -or $v.Count -eq 0) { return -1 }
    $s = @($v | Sort-Object)
    $n = $s.Count
    if ($n % 2 -eq 1) { return $s[[int](($n - 1) / 2)] }
    return ($s[$n / 2 - 1] + $s[$n / 2]) / 2.0
}

# Mann-Whitney U（双侧，正态近似 + 结的秩平均）；n 很小或全结时 p 仅供参考
function Get-MannWhitneyU([double[]]$a, [double[]]$b) {
    if ($a.Count -lt 3 -or $b.Count -lt 3) { return [pscustomobject]@{ U = -1; Z = -1; P = -1; Note = '样本不足' } }
    $all = @()
    foreach ($x in $a) { $all += [pscustomobject]@{ V = $x; G = 'A' } }
    foreach ($x in $b) { $all += [pscustomobject]@{ V = $x; G = 'B' } }
    $sorted = @($all | Sort-Object V)
    $ranks = New-Object 'double[]' $sorted.Count
    $i = 0
    while ($i -lt $sorted.Count) {
        $j = $i
        while ($j + 1 -lt $sorted.Count -and $sorted[$j + 1].V -eq $sorted[$i].V) { $j++ }
        $avgRank = (($i + 1) + ($j + 1)) / 2.0
        for ($k = $i; $k -le $j; $k++) { $ranks[$k] = $avgRank }
        $i = $j + 1
    }
    $r1 = 0.0
    for ($k = 0; $k -lt $sorted.Count; $k++) { if ($sorted[$k].G -eq 'A') { $r1 += $ranks[$k] } }
    $n1 = [double]$a.Count; $n2 = [double]$b.Count
    $u1 = $r1 - ($n1 * ($n1 + 1) / 2.0)
    $u2 = $n1 * $n2 - $u1
    $u = [Math]::Min($u1, $u2)
    $mu = $n1 * $n2 / 2.0
    $sigma = [Math]::Sqrt($n1 * $n2 * ($n1 + $n2 + 1) / 12.0)
    if ($sigma -le 0) { return [pscustomobject]@{ U = $u; Z = 0; P = 1; Note = '零方差' } }
    $z = ($u - $mu) / $sigma
    # 正态近似 p 值：用 Erf 的手写实现（不依赖 [Math]::Erf——部分会话/宿主环境下不可用）
    $p = 2.0 * (1.0 - 0.5 * (1.0 + (Get-Erf ([Math]::Abs($z) / [Math]::Sqrt(2.0)))))
    return [pscustomobject]@{ U = [math]::Round($u, 1); Z = [math]::Round($z, 2); P = [math]::Round($p, 3); Note = '' }
}

# 单实例互斥：探针会拒绝在已有 FFF.Player 时启动。轮间残留（kill 未生效、
# 或上一轮进程还活着）会让整轮静默无输出，这里每轮前强制清场。
function Clear-PlayerInstances {
    Get-Process -Name FFF.Player -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }
    Start-Sleep -Milliseconds 1200
}

# Erf 的 Abramowitz-Stegun 7.1.26 近似（误差 < 1.5e-7，足够 p 值判定）
function Get-Erf([double]$x) {
    $sign = if ($x -lt 0) { -1.0 } else { 1.0 }
    $x = [Math]::Abs($x)
    $t = 1.0 / (1.0 + 0.3275911 * $x)
    $y = 1.0 - (((((1.061405429 * $t - 1.453152027) * $t) + 1.421413741) * $t - 0.284496736) * $t + 0.254829592) * $t * [Math]::Exp(-$x * $x)
    return $sign * $y
}

$rows = New-Object System.Collections.Generic.List[object]
$armDefs = @(
    [pscustomobject]@{ Exe = $ExeA; Tag = $TagA; Env = $EnvA },
    [pscustomobject]@{ Exe = $ExeB; Tag = $TagB; Env = $EnvB }
)

$envKeys = @()
for ($round = 1; $round -le $Rounds; $round++) {
    foreach ($arm in $armDefs) {
        $load = Get-TotalCpuPct
        # 先擦掉上一臂设的键，再注入本臂的；子进程直接继承，探针无需改动。
        foreach ($k in $envKeys) { Remove-Item -Path ("Env:" + $k) -ErrorAction SilentlyContinue }
        $envKeys = @()
        foreach ($pair in @($arm.Env -split ';' | Where-Object { $_ -match '=' })) {
            $kv = $pair -split '=', 2
            Set-Item -Path ("Env:" + $kv[0]) -Value $kv[1]
            $envKeys += $kv[0]
        }
        Clear-PlayerInstances
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ('ab1_' + [guid]::NewGuid().ToString('N') + '.csv')
        try {
            # 不再 | Out-Null：探针异常要落到 Note 里，否则整轮会静默变“无输出”
            $probeOut = & pwsh -NoProfile -File $tool -Exe $arm.Exe -Image $Image -Runs 1 -WarmupRuns 0 -CsvOut $tmp -Tag $arm.Tag 2>&1
            $probeErr = ($probeOut | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] -or $_ -match 'Exception|already running|Cannot|无法' } | Select-Object -First 1)
            if (Test-Path -LiteralPath $tmp) {
                $r = @(Import-Csv -LiteralPath $tmp) | Select-Object -First 1
                if ($r) {
                    $rows.Add([pscustomobject]@{
                        Round  = $round
                        Arm    = $arm.Tag
                        LoadPct = $load
                        ArmEnv = $arm.Env
                        SpawnMs = [double]$r.SpawnMs
                        T1     = [double]$r.TitledMs
                        T2     = [double]$r.OnScreenMs
                        StdDev = $r.ContentStdDev
                        Note   = $r.Note
                    })
                } else {
                    $rows.Add([pscustomobject]@{ Round = $round; Arm = $arm.Tag; LoadPct = $load; SpawnMs = -1; T1 = -1; T2 = -1; StdDev = ''; Note = '无输出行' })
                }
            } else {
                $why = if ($probeErr) { "探针异常: $probeErr" } else { '探针未产出 CSV' }
                $rows.Add([pscustomobject]@{ Round = $round; Arm = $arm.Tag; LoadPct = $load; SpawnMs = -1; T1 = -1; T2 = -1; StdDev = ''; Note = $why })
            }
        } catch {
            $rows.Add([pscustomobject]@{ Round = $round; Arm = $arm.Tag; LoadPct = $load; SpawnMs = -1; T1 = -1; T2 = -1; StdDev = ''; Note = "探针异常: $($_.Exception.Message)" })
        }
        finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host ("round {0}/{1} 完成" -f $round, $Rounds)
}

$rows | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8

# ---- 汇总（分档：见 docs/117 —— 本机存在 ~1/3 轮次的 240ms 进程启动拦截，
#      会完整传导到 T2。fast 档作绝对基线，slow 档只作相对比较，>700ms 才真判废）----
function Show-Stratum([string]$name, [object[]]$set) {
    if (-not $set -or $set.Count -eq 0) { Write-Host ("  [{0}] 无样本" -f $name); return }
    Write-Host ("  [{0}] n={1}" -f $name, $set.Count)
    foreach ($tag in @($TagA, $TagB)) {
        $s = @($set | Where-Object { $_.Arm -eq $tag })
        if ($s.Count -eq 0) { Write-Host ("    {0,-8} n=  0" -f $tag); continue }
        $t2 = [double[]]@($s | ForEach-Object { [double]$_.T2 })
        $t1 = [double[]]@($s | ForEach-Object { [double]$_.T1 })
        Write-Host ("    {0,-8} n={1,3}  T1 中位={2,8:N1}  T2 中位={3,8:N1}" -f $tag, $s.Count, (Get-Median $t1), (Get-Median $t2))
    }
    $a = [double[]]@($set | Where-Object { $_.Arm -eq $TagA } | ForEach-Object { [double]$_.T2 })
    $b = [double[]]@($set | Where-Object { $_.Arm -eq $TagB } | ForEach-Object { [double]$_.T2 })
    if ($a.Count -ge 3 -and $b.Count -ge 3) {
        $mw = Get-MannWhitneyU $a $b
        $dl = (Get-Median $b) - (Get-Median $a)
        Write-Host ("    Δ({0}-{1}) = {2,7:N1} ms   U={3} p={4}" -f $TagB, $TagA, $dl, $mw.U, $mw.P)
    }
}

$fast  = @($rows | Where-Object { [double]$_.SpawnMs -ge 0 -and [double]$_.SpawnMs -lt 60 -and [double]$_.T2 -gt 0 })
$slow  = @($rows | Where-Object { [double]$_.SpawnMs -ge 60 -and [double]$_.SpawnMs -le 700 -and [double]$_.T2 -gt 0 })
$waste = @($rows | Where-Object { [double]$_.SpawnMs -gt 700 -or [double]$_.T2 -le 0 })
$loadAvg = if ($rows.Count) { [math]::Round((($rows | Measure-Object LoadPct -Average).Average), 1) } else { -1 }
Write-Host ''
Write-Host ("总轮 {0} | fast={1} slow={2} 废={3} | 平均负载 {4}%" -f $rows.Count, $fast.Count, $slow.Count, $waste.Count, $loadAvg)
Show-Stratum 'fast <60ms（绝对基线）' $fast
Show-Stratum 'slow 60-700ms（仅相对比较）' $slow
Write-Host ("明细：{0}" -f $CsvOut)
