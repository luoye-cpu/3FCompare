# RTSS / Afterburner 对「上游 FFF.Player(3FP)」与「3FCompare(3FC)」的影响对照实验
# 设计：位置平衡（拉丁方轮换），同批次交错，单变量 = RTSS 是否在跑。
param(
    [Parameter(Mandatory = $true)][string]$Label,      # A=RTSS 运行中 / B=RTSS 已退出
    [int]$Rounds = 4,                                   # 每个配置跑几轮
    [int]$DurationSec = 30,                             # 单次播放时长
    [int]$GapSec = 3,                                   # 相邻两次之间的间隔
    [string[]]$Only = @('FP1', 'FC1', 'FC4')            # 本批只跑哪些配置
)

$ErrorActionPreference = "Continue"

$media  = "C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4"
$fpExe  = "C:\PLAN\3FCompare\third_party\fff_project\FFF.Player\bin\Release\net10.0-windows10.0.26100.0\FFF.Player.exe"
$fcExe  = "C:\PLAN\3FCompare\src\3FCompare\bin\Release\net11.0-windows\3FCompare.exe"
$outDir = "C:\PLAN\3FCompare\.review_pr\rtss_3fp"
$csv    = Join-Path $outDir "results_$Label.csv"
$log    = Join-Path $outDir "run_$Label.log"

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
if (-not (Test-Path $media)) { throw "素材不存在: $media" }

# 三个被测配置。autoExit=$true 表示程序播完会自己退出(3FC multitest)；
# $false 表示需要外部到点强杀(上游播放器是交互程序，不会自己退出)。
$specs = @{
    FP1 = @{ exe = $fpExe; argList = @($media);                              autoExit = $false; desc = "3FP 上游播放器 单路" }
    FC1 = @{ exe = $fcExe; argList = @('--multitest', $media, '1', '30');    autoExit = $true;  desc = "3FC 1 路" }
    FC4 = @{ exe = $fcExe; argList = @('--multitest', $media, '4', '30');    autoExit = $true;  desc = "3FC 4 路(阳性对照)" }
}

# 拉丁方：保证每个配置出现在各位置的次数相等，消除次序效应（docs/33 §六 教训）
$orders = @()
if ($Only.Count -ge 3) {
    $a = $Only[0]; $b = $Only[1]; $c = $Only[2]
    $orders = @(@($a, $b, $c), @($b, $c, $a), @($c, $a, $b))
} elseif ($Only.Count -eq 2) {
    $a = $Only[0]; $b = $Only[1]
    $orders = @(@($a, $b), @($b, $a))
} else {
    $orders = @(@($Only[0]))
}

function Write-Line([string]$s) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $line = "$ts  $s"
    Add-Content -Path $log -Value $line -Encoding UTF8
    Write-Host $line   # 必须走 Host，不可用 Write-Output，否则会污染返回值管道
}

function Test-RtssAlive {
    $r = @(Get-Process -Name RTSS, RTSSHooksLoader64, MSIAfterburner -ErrorAction SilentlyContinue)
    return ($r.Count -gt 0)
}

function Get-InjectedModules([int]$procId) {
    try {
        $m = @(Get-Process -Id $procId -Module -ErrorAction SilentlyContinue |
               Where-Object { $_.ModuleName -match 'RTSSHooks|nvspcap' } |
               ForEach-Object { $_.ModuleName })
        return ($m -join '+')
    } catch { return "" }
}

function Invoke-Case([string]$name, $spec, [int]$round, [int]$pos) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $injected = ""
    $cpu = 0.0

    $wd = Split-Path -Parent $spec.exe
    $psi = @{
        FilePath         = $spec.exe
        ArgumentList     = $spec.argList
        WorkingDirectory = $wd
        PassThru         = $true
    }
    $p = Start-Process @psi
    $pid2 = $p.Id

    # 启动后 2 秒抓一次注入模块（此时进程已建好 D3D 设备）
    Start-Sleep -Seconds 2
    if (-not $p.HasExited) { $injected = Get-InjectedModules $pid2 }
    $cpuBase = 0.0
    if (-not $p.HasExited) { try { $cpuBase = (Get-Process -Id $pid2).CPU } catch { } }

    # 等待到点 / 或自行退出
    $waited = 2
    $exited = $false
    while ($waited -lt $DurationSec) {
        Start-Sleep -Seconds 1
        $waited++
        $p.Refresh()
        if ($p.HasExited) { $exited = $true; break }
    }

    $code = $null
    $verdict = ""
    $wall = [math]::Round($sw.Elapsed.TotalSeconds, 1)

    if ($exited) {
        $code = $p.ExitCode
        # 0xC0000xxx 都是 Windows 异常码（AV / 栈溢出 / 中止 等）
        if ($code -lt 0 -and $code -gt -1073741824) { $verdict = "CRASH" }
        elseif ($code -eq 0) { $verdict = "OK" }
        else { $verdict = "OTHER" }
    } else {
        # 到点仍存活：对 autoExit 的程序是异常(HANG)，对交互式播放器是正常完成
        try { $cpu = (Get-Process -Id $pid2).CPU - $cpuBase } catch { }
        Stop-Process -Id $pid2 -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        if ($spec.autoExit) { $verdict = "HANG"; $code = $null } else { $verdict = "OK"; $code = $null }
    }

    $rtss = Test-RtssAlive
    $obj = [PSCustomObject]@{
        time     = (Get-Date -Format "HH:mm:ss")
        arm      = $Label
        round    = $round
        pos      = $pos
        case     = $name
        desc     = $spec.desc
        verdict  = $verdict
        exitCode = $code
        wallSec  = $wall
        cpuSec   = [math]::Round($cpu, 1)
        injected = $injected
        rtssUp   = $rtss
        pid      = $pid2
    }
    $obj | Export-Csv -Path $csv -Append -NoTypeInformation -Encoding UTF8
    Write-Line ("{0,-5} r{1} pos{2}  {3,-14} {4,-6} code={5} wall={6}s cpu={7}s inj=[{8}] rtssUp={9}" -f `
        $Label, $round, $pos, $name, $verdict, $code, $wall, [math]::Round($cpu, 1), $injected, $rtss)
    return $obj
}

Write-Line "======== 臂 $Label 开始：Rounds=$Rounds DurationSec=$DurationSec ========"
Write-Line ("RTSS 当前是否在跑: " + (Test-RtssAlive))
Write-Line ("素材: $media")

$results = @()
for ($r = 1; $r -le $Rounds; $r++) {
    $order = $orders[($r - 1) % $orders.Count]
    Write-Line ("---- 第 $r 轮 顺序: " + ($order -join ' -> ') + " ----")
    for ($pos = 1; $pos -le $order.Count; $pos++) {
        $name = $order[$pos - 1]
        $one = Invoke-Case $name $specs[$name] $r $pos
        if ($one -is [PSCustomObject]) { $results += $one }
        Start-Sleep -Seconds $GapSec
    }
}

Write-Line "======== 臂 $Label 结束 ========"
$sum = $results | Group-Object case | ForEach-Object {
    $c = $_.Name
    $crash = @($_.Group | Where-Object { $_.verdict -eq 'CRASH' }).Count
    $ok    = @($_.Group | Where-Object { $_.verdict -eq 'OK' }).Count
    $oth   = @($_.Group | Where-Object { $_.verdict -eq 'OTHER' }).Count
    $hang  = @($_.Group | Where-Object { $_.verdict -eq 'HANG' }).Count
    "{0}: 崩 {1}/{2}  OK {3}  OTHER {4}  HANG {5}" -f $c, $crash, $_.Count, $ok, $oth, $hang
}
foreach ($s in $sum) { Write-Line $s }
