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
    [string]$EnvB = '',
    # 逐轮 trace 目录（docs/151）：传入时启动 startup_image_time 的 -TraceDir，
    # 并且把自证列（StartupDefaultSetMax / CpuAtT2 / Env*）一并写进 CSV。
    [string]$TraceDir = '',
    # 交替臂顺序（A→B / B→A 逐轮翻转）：抵消轮内漂移，docs/151 用。
    [switch]$AltOrder,
    # 预热轮（两臂各跑一次，不计数）：稳定文件缓存后再进正式轮。
    [int]$WarmupRounds = 0
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
for ($round = (1 - $WarmupRounds); $round -le $Rounds; $round++) {
    $isWarmup = $round -lt 1
    if ($isWarmup) { Write-Host ("== warmup {0}/{1} (discarded) ==" -f ($round + $WarmupRounds), $WarmupRounds) }
    # AltOrder：偶数轮把 B 放到 A 前面，抵消"同一轮里后跑的那臂总是继承同一机器状态"的偏差。
    $armOrder = $armDefs
    if ($AltOrder -and ($round % 2 -eq 0)) { $armOrder = @($armDefs[1], $armDefs[0]) }
    foreach ($arm in $armOrder) {
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
            $probeArgs = @('-NoProfile', '-File', $tool, '-Exe', $arm.Exe, '-Image', $Image,
                '-Runs', '1', '-WarmupRuns', '0', '-CsvOut', $tmp, '-Tag', $arm.Tag)
            if ($TraceDir -ne '') {
                $armTrace = Join-Path $TraceDir ("{0}_r{1}" -f $arm.Tag, $round)
                $probeArgs += @('-TraceDir', $armTrace)
            }
            $probeOut = & pwsh @probeArgs 2>&1
            $probeErr = ($probeOut | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] -or $_ -match 'Exception|already running|Cannot|无法' } | Select-Object -First 1)
            if (Test-Path -LiteralPath $tmp) {
                $r = @(Import-Csv -LiteralPath $tmp) | Select-Object -First 1
                if ($r) {
                    $cpuAtT2 = [double]$r.CpuAtT2Ms
                    $cpuRatio = if ($cpuAtT2 -ge 0 -and [double]$r.OnScreenMs -gt 0) { [math]::Round($cpuAtT2 / [double]$r.OnScreenMs * 100, 1) } else { -1.0 }
                    $rows.Add([pscustomobject]@{
                        Round  = $round
                        Arm    = $arm.Tag
                        LoadPct = $load
                        ArmEnv = $arm.Env
                        SpawnMs = [double]$r.SpawnMs
                        T1     = [double]$r.TitledMs
                        T2     = [double]$r.OnScreenMs
                        StdDev = $r.ContentStdDev
                        CpuAtT2Ms = $cpuAtT2
                        CpuOverWallPct = $cpuRatio
                        StartupSetMax = [double]$r.StartupDefaultSetMax
                        EnvStartupPlacement = $r.EnvStartupPlacement
                        Note   = $r.Note
                    })
                } else {
                    if (-not $isWarmup) { $rows.Add([pscustomobject]@{ Round = $round; Arm = $arm.Tag; LoadPct = $load; ArmEnv = $arm.Env; SpawnMs = -1; T1 = -1; T2 = -1; StdDev = ''; CpuAtT2Ms = -1; CpuOverWallPct = -1; StartupSetMax = -1; EnvStartupPlacement = ''; Note = '无输出行' }) }
                }
            } else {
                $why = if ($probeErr) { "探针异常: $probeErr" } else { '探针未产出 CSV' }
                if (-not $isWarmup) { $rows.Add([pscustomobject]@{ Round = $round; Arm = $arm.Tag; LoadPct = $load; ArmEnv = $arm.Env; SpawnMs = -1; T1 = -1; T2 = -1; StdDev = ''; CpuAtT2Ms = -1; CpuOverWallPct = -1; StartupSetMax = -1; EnvStartupPlacement = ''; Note = $why }) }
            }
        } catch {
            if (-not $isWarmup) { $rows.Add([pscustomobject]@{ Round = $round; Arm = $arm.Tag; LoadPct = $load; ArmEnv = $arm.Env; SpawnMs = -1; T1 = -1; T2 = -1; StdDev = ''; CpuAtT2Ms = -1; CpuOverWallPct = -1; StartupSetMax = -1; EnvStartupPlacement = ''; Note = "探针异常: $($_.Exception.Message)" }) }
        }
        finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $isWarmup) { Write-Host ("round {0}/{1} 完成" -f $round, $Rounds) }
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
$loadAvg = if ($rows.Count) { [math]::Round((($rows | Where-Object { $_.LoadPct -ge 0 } | Measure-Object LoadPct -Average).Average), 1) } else { -1 }
Write-Host ''
Write-Host ("总轮 {0} | fast={1} slow={2} 废={3} | 平均负载 {4}%" -f $rows.Count, $fast.Count, $slow.Count, $waste.Count, $loadAvg)
Show-Stratum 'fast <60ms（绝对基线）' $fast
Show-Stratum 'slow 60-700ms（仅相对比较）' $slow

# ---- 配对差（同轮 B−A）与符号检验：AltOrder 已抵消轮内顺序偏差 ----------------
$pairedT2 = @(); $pairedCpu = @()
foreach ($roundKey in @($rows | ForEach-Object { [int]$_.Round } | Sort-Object -Unique)) {
    $a = @($rows | Where-Object { [int]$_.Round -eq $roundKey -and $_.Arm -eq $TagA }) | Select-Object -First 1
    $b = @($rows | Where-Object { [int]$_.Round -eq $roundKey -and $_.Arm -eq $TagB }) | Select-Object -First 1
    if ($a -and $b) {
        if ([double]$a.T2 -gt 0 -and [double]$b.T2 -gt 0) { $pairedT2 += ([double]$b.T2 - [double]$a.T2) }
        if ([double]$a.CpuOverWallPct -ge 0 -and [double]$b.CpuOverWallPct -ge 0) { $pairedCpu += ([double]$b.CpuOverWallPct - [double]$a.CpuOverWallPct) }
    }
}
function Show-Paired([string]$name, $deltas, [string]$unit) {
    if ($deltas.Count -lt 6) { Write-Host ("  [{0}] 配对样本不足 n={1}" -f $name, $deltas.Count); return }
    $med = Get-Median ([double[]]$deltas)
    $pos = @($deltas | Where-Object { $_ -gt 0 }).Count
    $neg = @($deltas | Where-Object { $_ -lt 0 }).Count
    $n = $pos + $neg
    $z = if ($n -gt 0) { ($pos - $n / 2.0) / [Math]::Sqrt($n / 4.0) } else { 0 }
    $p = 2.0 * (1.0 - 0.5 * (1.0 + (Get-Erf ([Math]::Abs($z) / [Math]::Sqrt(2.0)))))
    Write-Host ("  [{0}] 配对 Δ(B-A) 中位={1,7:N1}{2}  正/负={3}/{4}  sign-p={5}" -f $name, $med, $unit, $pos, $neg, [math]::Round($p, 4))
}
Write-Host ''
Write-Host '===== 配对差（同轮 B−A；AltOrder 抵消轮内顺序偏差）====='
Show-Paired 'T2' $pairedT2 ' ms'
Show-Paired 'CPU/墙钟比' $pairedCpu ' %'

# ---- 启动期 CPU 放置自证（docs/151）----------------------------------------------
# StartupSetMax = 启动窗口内该进程「进程默认 CPU 集合」的大小，由 GetProcessDefaultCpuSets
# 在 OS 侧直接读。FFF_STARTUP_PLACEMENT=1 的臂必须出现非零；否则说明 env 没进 GUI 或策略
# 被拒——这时 T1/T2 的「无差异」不能当成策略无效的证据。
$placementArms = @($armDefs | Where-Object { $_.Env -match 'FFF_STARTUP_PLACEMENT\s*=\s*1' } | ForEach-Object { $_.Tag })
Write-Host ''
Write-Host '===== 启动期 CPU 放置自证（StartupSetMax = 启动窗口内进程默认 CPU 集合大小）====='
$validRows = @($fast + $slow)
foreach ($tag in @($TagA, $TagB)) {
    $s = @($validRows | Where-Object { $_.Arm -eq $tag })
    if ($s.Count -eq 0) { Write-Host ("  {0,-8} 无有效样本" -f $tag); continue }
    $setCounts = [double[]]@($s | ForEach-Object { [double]$_.StartupSetMax })
    $nonZero = @($setCounts | Where-Object { $_ -gt 0 }).Count
    $isPlacementArm = @($placementArms) -contains $tag
    $verdict = if ($isPlacementArm) {
        if ($nonZero -gt 0) { 'OK（策略确实生效）' }
        else { '自证失败：该臂没有一个样本看到进程默认集合 ⇒ env 未注入或策略被拒' }
    } else {
        if ($nonZero -eq 0) { 'OK（未要求放置，确实没施加）' } else { "注意：未要求放置却有 $nonZero 轮看到集合" }
    }
    Write-Host ("  {0,-8} StartupSetMax 中位={1,6:N0} 非零轮={2}/{3}  {4}" -f $tag, (Get-Median $setCounts), $nonZero, $s.Count, $verdict)
}
Write-Host ('  臂环境：A=[{0}] B=[{1}]' -f $EnvA, $EnvB)

# ---- 启动期 CPU 时间 / 墙钟比（docs/146 阶段0 判据：<30% 则该方向判死）-----------
Write-Host ''
Write-Host '===== 启动期 CPU 时间 / 墙钟比（T2 之前的子进程 CPU 秒 ÷ T2 秒）====='
foreach ($tag in @($TagA, $TagB)) {
    $s = @($validRows | Where-Object { $_.Arm -eq $tag -and [double]$_.CpuOverWallPct -ge 0 })
    if ($s.Count -eq 0) { Write-Host ("  {0,-8} 无读数" -f $tag); continue }
    $ratios = [double[]]@($s | ForEach-Object { [double]$_.CpuOverWallPct })
    Write-Host ("  {0,-8} n={1,3}  CPU/墙钟 中位={2,6:N1}%" -f $tag, $s.Count, (Get-Median $ratios))
}
Write-Host ("明细：{0}" -f $CsvOut)
