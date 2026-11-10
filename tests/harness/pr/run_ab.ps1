# P1 改造实测：并发 presenter 管线 N 条 -> 1 条
#
# A 臂（对照）= 基线 FFF.Native.dll（Sep 18，每路一条 TimedTextThread）
# B 臂（实验）= 新 FFF.Native.dll（进程级单 presenter pump）
#
# 同批次交错，每轮各跑 1 次 A + 1 次 B；次序按轮次交替（AB/BA 拉丁方）以平衡次序效应。
# RTSS / MSI Afterburner 必须保持运行（这是复现条件，不要退出）。
param(
    [int]$Rounds = 8,
    [int]$DurationSec = 30,
    [int]$GapSec = 3,
    [switch]$Fresh
)

$ErrorActionPreference = "Continue"

$media  = "C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4"
$binDir = "C:\PLAN\3FCompare\src\3FCompare\bin\Release\net11.0-windows"
$fcExe  = Join-Path $binDir "3FCompare.exe"
$target = Join-Path $binDir "FFF.Native.dll"
$outDir = "C:\PLAN\3FCompare\.review_pr\single_presenter"
$logDir = Join-Path $outDir "logs"
$csv    = Join-Path $outDir "results_ab.csv"
$log    = Join-Path $outDir "run_ab.log"
$appLog = Join-Path $binDir "logs\app-$(Get-Date -Format 'yyyy-MM-dd').log"

$armDll = @{
    'A' = Join-Path $outDir "FFF.Native.baseline.dll"
    'B' = "C:\PLAN\3FCompare\third_party\fff_project\FFF.Native\x64\Release_pr9\FFF.Native.dll"
}

if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
if (-not (Test-Path $media))  { throw "素材不存在: $media" }
if (-not (Test-Path $fcExe))  { throw "可执行文件不存在: $fcExe" }
foreach ($k in 'A','B') { if (-not (Test-Path $armDll[$k])) { throw "臂 $k 的 DLL 不存在: $($armDll[$k])" } }

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

function Get-InjectedModules([int]$procId) {
    try {
        $m = @(Get-Process -Id $procId -Module -ErrorAction SilentlyContinue |
               Where-Object { $_.ModuleName -match 'RTSSHooks|nvspcap' } |
               ForEach-Object { $_.ModuleName })
        return ($m -join '+')
    } catch { return "" }
}

function Get-LastEmbedVerdict {
    # 应用启动时会把「FFF.Native.dll 与内嵌资源一致 / 不一致」写进 app 日志：
    # 基线 DLL == 内嵌资源 ⇒ "一致"；新 DLL ⇒ "不一致"。作为替换生效的第二重取证。
    if (-not (Test-Path $appLog)) { return "" }
    try {
        $hit = Select-String -Path $appLog -Pattern '内嵌资源' -Encoding UTF8 |
               Select-Object -Last 1
        if ($null -eq $hit) { return "" }
        if ($hit.Line -match '一致，保留') { return "一致(基线)" }
        if ($hit.Line -match '不一致')     { return "不一致(新内核)" }
        return "其他"
    } catch { return "" }
}

# $arm: 'A' = 基线 DLL；'B' = 新 DLL
function Invoke-Case([string]$arm, [int]$round, [int]$pos) {
    $tag = "r{0}_{1}_{2}" -f $round, $arm, (Get-Date -Format "HHmmss")
    $outFile = Join-Path $logDir "$tag.out.txt"
    $errFile = Join-Path $logDir "$tag.err.txt"

    # ── 替换内核并验证替换生效（sha256 逐次核对）──
    $srcHash = (Get-FileHash -Algorithm SHA256 -Path $armDll[$arm]).Hash
    Copy-Item -Path $armDll[$arm] -Destination $target -Force
    Start-Sleep -Milliseconds 300
    $diskHash = (Get-FileHash -Algorithm SHA256 -Path $target).Hash
    $dllOk = ($srcHash -eq $diskHash)

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

    Start-Sleep -Seconds 2
    $injected = ""
    $thrEarly = -1
    if (-not $p.HasExited) {
        $injected = Get-InjectedModules $procId
        try { $p.Refresh(); $thrEarly = $p.Threads.Count } catch { }
    }
    # 稳态再采一次：单 presenter 改造应把"每路一条 presenter 线程"降为进程一条。
    Start-Sleep -Seconds 14
    $thrMid = -1
    if (-not $p.HasExited) {
        try { $p.Refresh(); $thrMid = $p.Threads.Count } catch { }
    }

    $exited = $p.WaitForExit(($DurationSec + 30) * 1000)
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
    Set-Content -Path $outFile -Value $outText -Encoding UTF8
    Set-Content -Path $errFile -Value $errText -Encoding UTF8
    $embed = Get-LastEmbedVerdict

    # ── 判读：exit<0 = 崩溃；exit=1 = 漂移断言失败（非崩溃）；exit=0 = 通过 ──
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

    $rtss = Test-RtssAlive
    $obj = [PSCustomObject]@{
        time      = (Get-Date -Format "HH:mm:ss")
        arm       = $arm
        round     = $round
        pos       = $pos
        verdict   = $verdict
        exitCode  = $code
        codeHex   = if ($code -ne $null -and $code -lt 0) { "0x{0:X8}" -f ([uint32]$code) } else { "" }
        wallSec   = $wall
        dllOk     = $dllOk
        embedLog  = $embed
        injected  = $injected
        thrEarly  = $thrEarly
        thrMid    = $thrMid
        rtssUp    = $rtss
        sawPass   = $sawPass
        sawAssert = $sawAssert
        sawCleanup= $sawCleanup
        pid       = $procId
        outFile   = (Split-Path -Leaf $outFile)
    }
    $obj | Export-Csv -Path $csv -Append -NoTypeInformation -Encoding UTF8
    Write-Line ("{0} r{1} pos{2}  {3,-7} code={4} hex={5} wall={6}s dllOk={7} thr={8}/{9} embed={10} inj=[{11}] rtssUp={12}" -f `
        $arm, $round, $pos, $verdict, $code, $obj.codeHex, $wall, $dllOk, $thrEarly, $thrMid, $embed, $injected, $rtss)
    return $obj
}

Write-Line "======== 单 presenter 改造 A/B：轮次 1..$Rounds DurationSec=$DurationSec ========"
Write-Line ("RTSS 是否在跑: " + (Test-RtssAlive))
Write-Line ("A 臂 = 基线 DLL ($($armDll['A']))")
Write-Line ("B 臂 = 新 DLL ($($armDll['B']))")
Write-Line ("A sha256 = " + (Get-FileHash -Algorithm SHA256 -Path $armDll['A']).Hash)
Write-Line ("B sha256 = " + (Get-FileHash -Algorithm SHA256 -Path $armDll['B']).Hash)

if ($Fresh -and (Test-Path $csv)) { Remove-Item $csv -Force }

$results = @()
for ($r = 1; $r -le $Rounds; $r++) {
    $order = if ($r % 2 -eq 1) { @('A', 'B') } else { @('B', 'A') }
    Write-Line ("---- 第 $r 轮 顺序: " + ($order -join ' -> ') + " ----")
    for ($pos = 1; $pos -le $order.Count; $pos++) {
        $one = Invoke-Case $order[$pos - 1] $r $pos
        if ($one -is [PSCustomObject]) { $results += $one }
        Start-Sleep -Seconds $GapSec
    }
}

Write-Line "======== 本批结束 ========"
$sum = $results | Group-Object arm | Sort-Object Name | ForEach-Object {
    $n = $_.Count
    $crash  = @($_.Group | Where-Object { $_.verdict -eq 'CRASH' }).Count
    $ok     = @($_.Group | Where-Object { $_.verdict -eq 'OK' }).Count
    $assert = @($_.Group | Where-Object { $_.verdict -eq 'ASSERT' }).Count
    $other  = @($_.Group | Where-Object { $_.verdict -eq 'OTHER' }).Count
    $hang   = @($_.Group | Where-Object { $_.verdict -eq 'HANG' }).Count
    "{0} 臂: 崩 {1}/{2} = {3:N1}%  OK={4} ASSERT={5} OTHER={6} HANG={7}" -f `
        $_.Name, $crash, $n, (100.0 * $crash / $n), $ok, $assert, $other, $hang
}
foreach ($s in $sum) { Write-Line $s }
