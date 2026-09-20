# 无缝放大实测：按 PID 采样 GPU 专用显存，与 exe 打印的阶段标记（epoch ms）对齐。
# 显存只能从进程外测：内核 D3D 设备在 FFF.Native.dll 里，进程内拿不到 DXGI 适配器。
#
# 用法： powershell -File .review_pr/gpu_probe.ps1 -Video <mp4> -Routes 4 -Zoom 2 -Seconds 8 [-BudgetMpx 200] [-Tag z2]
param(
    [Parameter(Mandatory = $true)][string]$Video,
    [int]$Routes = 4,
    [double]$Zoom = 2.0,
    [int]$Seconds = 8,
    [double]$BudgetMpx = 0,
    [int]$TimeoutSec = 240,
    [string]$Tag = "run"
)

$ErrorActionPreference = 'Continue'
$exe = 'C:\PLAN\3FCompare\src\3FCompare\bin\Debug\net11.0-windows\3FCompare.exe'
$outFile = Join-Path $PSScriptRoot "out_$Tag.txt"
$errFile = Join-Path $PSScriptRoot "err_$Tag.txt"

$argList = @('--magnifybench', $Video, "$Routes", "$Zoom", "$Seconds")
if ($BudgetMpx -gt 0) { $argList += "$BudgetMpx" }

Write-Host "=== $Tag : routes=$Routes zoom=$Zoom sec=$Seconds budgetMpx=$BudgetMpx ==="
$p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile

$samples = New-Object System.Collections.Generic.List[object]
$counters = [ordered]@{}
$deadline = (Get-Date).AddSeconds($TimeoutSec)
$killed = $false
$procId = $p.Id

function Test-Alive { $null -ne (Get-Process -Id $procId -ErrorAction SilentlyContinue) }
function Test-Done {
    # 完成判据取自 exe 输出（而不是 Process.HasExited：实测 Start-Process -PassThru 的
    # HasExited 会提前返回 True，导致探针在 exe 仍在跑时就收工）
    if (-not (Test-Path $outFile)) { return $false }
    $t = Get-Content $outFile -Raw -ErrorAction SilentlyContinue
    return ($t -match 'magnifybench: (完成|闸门拒绝|放大后)' -or $t -match '\[ExitSelfTest\] 准备退出')
}

# 等进程建立 D3D 上下文（首次出现该 PID 的 GPU 计数器实例）
$disc = $null
while ((Get-Date) -lt $deadline -and -not (Test-Done) -and (Test-Alive)) {
    Start-Sleep -Milliseconds 500
    try {
        $disc = (Get-Counter '\GPU Process Memory(*)\Local Usage' -MaxSamples 1 -ErrorAction SilentlyContinue).CounterSamples |
            Where-Object { $_.InstanceName -like "pid_${procId}_*" }
    } catch { $disc = $null }
    if ($disc) { break }
}
if ($disc) {
    foreach ($s in $disc) {
        try { $counters[$s.InstanceName] = New-Object Diagnostics.PerformanceCounter('GPU Process Memory', 'Local Usage', $s.InstanceName, $true) }
        catch { }
    }
}
Write-Host "counter instances: $($counters.Keys -join ' | ')"

# 轮询（持久 PerformanceCounter 复用句柄，~15ms/次；每次全量 Get-Counter 要 1.5s）
# 退出条件三选一：完成标记 / 进程消失（崩溃）/ 超时。Get-Process 是可靠的存活判据
# （Process.HasExited 在 Start-Process -PassThru 下会提前返回 True，不可用）。
while ((Get-Date) -lt $deadline -and -not (Test-Done) -and (Test-Alive)) {
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $row = [ordered]@{ ts = $ts }
    foreach ($k in @($counters.Keys)) {
        try { $row[$k] = [math]::Round($counters[$k].NextValue() / 1MB, 1) } catch { $row[$k] = -1 }
    }
    $samples.Add([pscustomobject]$row)
    Start-Sleep -Milliseconds 250
}

# 收尾：等 exe 自己退出（它已打印完成标记，正常几秒内结束）
$swExit = [Diagnostics.Stopwatch]::StartNew()
while ($swExit.Elapsed.TotalSeconds -lt 30 -and (Test-Alive)) { Start-Sleep -Milliseconds 500 }
if (Test-Alive) { $killed = $true; try { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue } catch { } }
$exitCode = if (Test-Done) { 'done' } elseif (-not (Test-Alive)) { 'CRASHED' } else { 'timeout' }
Write-Host "exit=$exitCode killedByTimeout=$killed samples=$($samples.Count)"

# ── 解析 exe 输出 ──
$lines = @(Get-Content $outFile -ErrorAction SilentlyContinue)
$phases = @()
$geoms = @()
foreach ($l in $lines) {
    if ($l -notmatch '^#BENCH') { continue }
    $kv = [ordered]@{}
    foreach ($m in [regex]::Matches($l, '([A-Za-z]\w*)=([^\s]+)')) { $kv[$m.Groups[1].Value] = $m.Groups[2].Value }
    if ($l -match '^#BENCHPHASE') { $phases += [pscustomobject]$kv }
    elseif ($l -match '^#BENCHGEOM') { $geoms += [pscustomobject]$kv }
}

function Stat-InWindow([long]$t0, [long]$t1, [string]$field) {
    $vals = @($samples | Where-Object { $_.ts -ge $t0 -and $_.ts -le $t1 } |
        ForEach-Object { $_.$field } | Where-Object { $_ -ne $null -and $_ -ge 0 })
    if ($vals.Count -eq 0) { return $null }
    $sorted = $vals | Sort-Object
    [pscustomobject]@{
        n     = $vals.Count
        min   = [math]::Round(($sorted | Measure-Object -Minimum).Minimum, 1)
        max   = [math]::Round(($sorted | Measure-Object -Maximum).Maximum, 1)
        med   = [math]::Round($sorted[[int][math]::Floor($sorted.Count / 2)], 1)
        first = [math]::Round($sorted[0], 1)
        last  = [math]::Round($sorted[-1], 1)
    }
}

Write-Host "`n--- 几何（exe 自报） ---"
foreach ($g in $geoms) {
    Write-Host ("zoom={0,-4} gate={1,-7} totalMpx={2,-7} estMpx={3,-7} windows=[{4}] compareArea={5} budgetMpx={6}" -f `
            $g.zoom, $g.gate, $g.totalMpx, $g.estMpx, $g.windows, $g.compareArea, $g.budgetMpx)
}

Write-Host "`n--- 各阶段显存（GPU 专用/Local，MB）---"
foreach ($ph in $phases) {
    $t0 = [long]$ph.t0; $t1 = [long]$ph.t1
    foreach ($k in @($counters.Keys)) {
        $st = Stat-InWindow $t0 $t1 $k
        if ($st -eq $null) { Write-Host ("{0,-11} z={1,-4} {2} : 无样本" -f $ph.name, $ph.zoom, $k); continue }
        Write-Host ("{0,-11} z={1,-4} {2} : n={3,-3} min={4,-8} med={5,-8} max={6,-8} MB" -f `
                $ph.name, $ph.zoom, ($k -replace '_luid_.*', ''), $st.n, $st.min, $st.med, $st.max)
    }
    Write-Host ("{0,-11} z={1,-4} wall={2,-6} fps/路={3,-6} clock={4}s hitches=[{5}] presented=[{6}]" -f `
            $ph.name, $ph.zoom, $ph.wall, $ph.fps, $ph.clock, $ph.hitches, $ph.presented)
}

Write-Host "`n--- exe 输出（magnifybench / BENCH 行）---"
$lines | Where-Object { $_ -match 'magnifybench|#BENCH' } | ForEach-Object { Write-Host $_ }
$err = @(Get-Content $errFile -ErrorAction SilentlyContinue)
if ($err.Count -gt 0) {
    Write-Host "--- stderr（末 12 行）---"
    $err | Select-Object -Last 12 | ForEach-Object { Write-Host $_ }
}
