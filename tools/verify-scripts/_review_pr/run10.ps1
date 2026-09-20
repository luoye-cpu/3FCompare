# docs/40 验收：消除两条离泵 Present 后的 4 路稳定性（10 次，位置平衡 = 5 轮 × 2 进程）
#
# 单臂（只有新内核），但保持与既有 A/B 相同的"轮内两个进程、次序交替"的跑法，
# 以便与 .review_pr/single_presenter/results_ab.csv 的 B 臂逐次对齐比较。
#
# RTSS / MSI Afterburner 必须保持运行（复现条件，不要退出它们）。
param(
    [int]$Rounds = 5,
    [int]$DurationSec = 30,
    [int]$GapSec = 4,
    [string]$Tag = "b1",
    [int]$RoundBase = 0
)

$ErrorActionPreference = "Continue"

$media  = "C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4"
$binDir = "C:\PLAN\3FCompare\src\3FCompare\bin\Release\net11.0-windows"
$fcExe  = Join-Path $binDir "3FCompare.exe"
$target = Join-Path $binDir "FFF.Native.dll"
$outDir = "C:\PLAN\3FCompare\.review_pr\offpump_present"
$logDir = Join-Path $outDir "logs"
$csv    = Join-Path $outDir "results10-$Tag.csv"
$log    = Join-Path $outDir "run10-$Tag.log"
$appLog = Join-Path $binDir "logs\app-$(Get-Date -Format 'yyyy-MM-dd').log"

if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
if (-not (Test-Path $media))  { throw "素材不存在: $media" }
if (-not (Test-Path $fcExe))  { throw "可执行文件不存在: $fcExe" }

function Write-Line([string]$s) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "$ts  $s"
    Add-Content -Path $log -Value $line -Encoding UTF8
    Write-Host $line
}

function Test-RtssAlive {
    $r = @(Get-Process -Name RTSS, RTSSHooksLoader64, MSIAfterburner -ErrorAction SilentlyContinue)
    return ($r.Count -gt 0)
}

function Get-LogLines([string]$path) {
    if (-not (Test-Path $path)) { return 0 }
    try { return @(Get-Content -Path $path -Encoding UTF8).Count } catch { return 0 }
}

function Get-LogSlice([string]$path, [int]$from) {
    if (-not (Test-Path $path)) { return @() }
    try {
        $all = @(Get-Content -Path $path -Encoding UTF8)
        if ($from -ge $all.Count) { return @() }
        return $all[$from..($all.Count - 1)]
    } catch { return @() }
}

function Invoke-Case([int]$round, [int]$pos) {
    $tag = "b$Tag-r{0}_p{1}_{2}" -f $round, $pos, (Get-Date -Format "HHmmss")
    $outFile = Join-Path $logDir "$tag.out.txt"
    $errFile = Join-Path $logDir "$tag.err.txt"

    $logFrom = Get-LogLines $appLog

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $fcExe
    $psi.WorkingDirectory       = $binDir
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.CreateNoWindow         = $false
    $psi.Arguments              = "--multitest `"$media`" 4 $DurationSec"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $procId = $p.Id
    $tOut = $p.StandardOutput.ReadToEndAsync()
    $tErr = $p.StandardError.ReadToEndAsync()

    $exited = $p.WaitForExit(($DurationSec + 60) * 1000)
    $wall = [math]::Round($sw.Elapsed.TotalSeconds, 1)

    $code = $null
    if ($exited) {
        try { $code = [int]$p.ExitCode } catch { $code = $null }
    } else {
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }

    $outText = ""; $errText = ""
    try { $outText = $tOut.Result } catch { }
    try { $errText = $tErr.Result } catch { }

    # 内核日志行：app 日志里以 [内核] 前缀落地
    $slice = Get-LogSlice $appLog $logFrom
    $excluded = @($slice | Where-Object { $_ -match 'pump-owned Present excluded' })
    $offPump  = @($slice | Where-Object { $_ -match 'off-pump Present' })
    $slow     = @($slice | Where-Object { $_ -match 'presenter pump slow Present' })
    # 每次运行的计数（同一进程内多次触发只记一条日志，用最后一条的累计值）
    $exclTail = if ($excluded.Count -gt 0) { ($excluded[-1] -replace '.*excluded ', '') } else { "" }

    Set-Content -Path $outFile -Value $outText -Encoding UTF8
    Set-Content -Path $errFile -Value $errText -Encoding UTF8

    # 判读：exit<0 = 崩溃；exit=1 = 漂移断言失败（非崩溃）；exit=0 = 通过
    $sawPass    = ($outText -match '全部通过')
    $sawAssert  = ($errText -match '失败 ✗')
    $sawCleanup = ($errText -match '准备退出')
    $verdict = ""
    if (-not $exited) {
        $verdict = "HANG"
    } elseif ($code -ne $null) {
        if ($code -lt 0)      { $verdict = "CRASH" }
        elseif ($code -eq 0)  { $verdict = "OK" }
        elseif ($code -eq 1)  { $verdict = "ASSERT" }
        else                  { $verdict = "OTHER" }
    } else {
        if ($sawCleanup -and $sawPass)        { $verdict = "OK" }
        elseif ($sawCleanup -and $sawAssert)  { $verdict = "ASSERT" }
        else                                  { $verdict = "CRASH" }
    }

    $failStep = ""
    if ($verdict -ne "OK") {
        $m = [regex]::Match($errText, 'multitest\[步骤([^\]]+)\]')
        if ($m.Success) { $failStep = $m.Groups[1].Value }
    }

    $obj = [PSCustomObject]@{
        time       = (Get-Date -Format "HH:mm:ss")
        round      = $round
        pos        = $pos
        verdict    = $verdict
        exitCode   = $code
        codeHex    = if ($code -ne $null -and $code -lt 0) { "0x{0:X8}" -f ([uint32]([int64]$code -band 0xFFFFFFFF)) } else { "" }
        wallSec    = $wall
        failStep   = $failStep
        excludedN  = $excluded.Count
        excludedCt = $exclTail
        offPumpN   = $offPump.Count
        slowN      = $slow.Count
        rtssUp     = (Test-RtssAlive)
        sawPass    = $sawPass
        sawAssert  = $sawAssert
        sawCleanup = $sawCleanup
        pid        = $procId
        outFile    = (Split-Path -Leaf $outFile)
    }
    $obj | Export-Csv -Path $csv -Append -NoTypeInformation -Encoding UTF8
    Write-Line ("r{0} pos{1}  {2,-7} code={3} hex={4} wall={5}s step=[{6}] excluded={7}({8}) offPump={9} slow={10} rtssUp={11}" -f `
        $round, $pos, $verdict, $code, $obj.codeHex, $wall, $failStep, $excluded.Count, $exclTail, $offPump.Count, $slow.Count, $obj.rtssUp)
    return $obj
}

Write-Line "======== docs/40 验收：10 次 4 路（5 轮 × 2 进程）DurationSec=$DurationSec ========"
Write-Line ("RTSS/Afterburner 在跑: " + (Test-RtssAlive))
Write-Line ("内核 DLL = " + $target)
Write-Line ("内核 DLL sha256 = " + (Get-FileHash -Algorithm SHA256 -Path $target).Hash)
Write-Line ("app 日志 = " + $appLog)

$results = @()
for ($r = 1; $r -le $Rounds; $r++) {
    Write-Line ("---- 第 " + ($RoundBase + $r) + " 轮 ----")
    for ($pos = 1; $pos -le 2; $pos++) {
        $one = Invoke-Case ($RoundBase + $r) $pos
        if ($one -is [PSCustomObject]) { $results += $one }
        Start-Sleep -Seconds $GapSec
    }
}

Write-Line "======== 本批结束 ========"
$n      = $results.Count
$crash  = @($results | Where-Object { $_.verdict -eq 'CRASH' }).Count
$ok     = @($results | Where-Object { $_.verdict -eq 'OK' }).Count
$assert = @($results | Where-Object { $_.verdict -eq 'ASSERT' }).Count
$other  = @($results | Where-Object { $_.verdict -eq 'OTHER' }).Count
$hang   = @($results | Where-Object { $_.verdict -eq 'HANG' }).Count
Write-Line ("汇总: 崩溃 {0}/{1} = {2:N1}%  OK={3} ASSERT={4} OTHER={5} HANG={6}" -f `
    $crash, $n, (100.0 * $crash / [math]::Max($n,1)), $ok, $assert, $other, $hang)
Write-Line ("off-pump Present 日志总数 = " + (@($results | Measure-Object -Property offPumpN -Sum).Sum))
Write-Line ("pump-owned Present excluded 日志总数 = " + (@($results | Measure-Object -Property excludedN -Sum).Sum))
