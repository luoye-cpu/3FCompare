# Same-batch, order-balanced A/B replication of the single-presenter fix.
#
#   A arm = pre-fix kernel (Sep 18 build, one presenter thread per pane)
#           .3fc_dumps/kernel-pre-single-presenter-20260920-130012/FFF.Native.dll
#   B arm = current fixed kernel (single presenter pump)
#           third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll
#
# Design: 12 rounds, each round runs A once and B once; the intra-round order
# alternates AB / BA / AB ... (latin square) so order effects cancel.
# Total 24 runs (hard cap 24).
#
# RTSS / MSI Afterburner MUST stay running (reproduction condition).
# No git writes, no source edits: this script only swaps the kernel DLL,
# runs the test, and records evidence.
#
# NOTE: this file is intentionally ASCII-only so it is safe to run under
# Windows PowerShell 5.1 regardless of file encoding. The Chinese app-log
# line is captured verbatim into the CSV and interpreted by analyze.py.

param(
    [int]$Rounds = 12,
    [int]$StartRound = 1,
    [int]$StartPos = 1,
    [int]$DurationSec = 30,
    [int]$GapSec = 3,
    [switch]$Fresh
)

$ErrorActionPreference = "Continue"

$root     = "C:\PLAN\3FCompare"
$media    = "$root\testmedia\media\real\real_4k_h264_60m.mp4"
$binDir   = "$root\src\3FCompare\bin\Release\net11.0-windows"
$fcExe    = Join-Path $binDir "3FCompare.exe"
$target   = Join-Path $binDir "FFF.Native.dll"
$outDir   = "$root\.review_pr\ab_now"
$logDir   = Join-Path $outDir "logs"
$csv      = Join-Path $outDir "results_ab.csv"
$log      = Join-Path $outDir "run_ab.log"
$appLog   = Join-Path $binDir ("logs\app-{0}.log" -f (Get-Date -Format 'yyyy-MM-dd'))
$miniDir  = "C:\Windows\Minidump"

$armDll = @{
    'A' = "$root\.3fc_dumps\kernel-pre-single-presenter-20260920-130012\FFF.Native.dll"
    'B' = "$root\third_party\fff_project\FFF.Native\x64\Release\FFF.Native.dll"
}
# Expected sha256 per arm -- a mismatch means the whole experiment is void.
$expectHash = @{
    'A' = "67454DE21AD69BE3F10EF35ACE3CAD7797E18784E20220D9849329CF787CF1B8"
    'B' = "6029FD95806F02A0D6C743A3322A53F45224F50609DC126B938B9B76BD07EBB9"
}

if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
if (-not (Test-Path $media))  { throw "media missing: $media" }
if (-not (Test-Path $fcExe))  { throw "exe missing: $fcExe" }
foreach ($k in 'A','B') {
    if (-not (Test-Path $armDll[$k])) { throw "arm $k dll missing: $($armDll[$k])" }
    $h = (Get-FileHash -Algorithm SHA256 -Path $armDll[$k]).Hash
    if ($h -ne $expectHash[$k]) { throw "arm $k sha256 mismatch: got $h expected $($expectHash[$k])" }
}

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

function Get-MinidumpSet {
    try { return @(Get-ChildItem -Path $miniDir -Filter *.dmp -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) }
    catch { return @() }
}

function Get-InjectedModules([int]$procId) {
    try {
        $m = @(Get-Process -Id $procId -Module -ErrorAction SilentlyContinue |
               Where-Object { $_.ModuleName -match 'RTSSHooks|nvspcap' } |
               ForEach-Object { $_.ModuleName })
        return ($m -join '+')
    } catch { return "" }
}

function Get-LastEmbedLine {
    # App startup logs whether the on-disk kernel matches the embedded resource.
    # Embedded resource == B arm, so A should read "not consistent" and B "consistent".
    # We capture the raw line only; classification happens in analyze.py.
    if (-not (Test-Path $appLog)) { return "" }
    try {
        # Only the embed-verdict line mentions both the logger tag and the dll file name.
        $hit = Select-String -Path $appLog -Pattern 'NativeRuntime.*FFF\.Native\.dll' -Encoding UTF8 |
               Select-Object -Last 1
        if ($null -eq $hit) { return "" }
        return $hit.Line.Trim()
    } catch { return "" }
}

# $arm: 'A' = pre-fix dll, 'B' = fixed dll
function Invoke-Case([string]$arm, [int]$round, [int]$pos) {
    $tag = "r{0}_{1}_p{2}_{3}" -f $round, $arm, $pos, (Get-Date -Format "HHmmss")
    $outFile = Join-Path $logDir "$tag.out.txt"
    $errFile = Join-Path $logDir "$tag.err.txt"

    # --- swap kernel and prove the swap took effect (sha256 every single run) ---
    $srcHash = (Get-FileHash -Algorithm SHA256 -Path $armDll[$arm]).Hash
    Copy-Item -Path $armDll[$arm] -Destination $target -Force
    Start-Sleep -Milliseconds 400
    $diskHash = (Get-FileHash -Algorithm SHA256 -Path $target).Hash
    $dllOk = ($srcHash -eq $diskHash) -and ($diskHash -eq $expectHash[$arm])

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
    Start-Sleep -Seconds 13
    $thrMid = -1
    if (-not $p.HasExited) {
        try { $p.Refresh(); $thrMid = $p.Threads.Count } catch { }
    }

    $exited = $p.WaitForExit(($DurationSec + 40) * 1000)
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
    $embed = Get-LastEmbedLine

    # --- verdict: exit<0 = crash; exit=1 = drift assertion failure (NOT a crash) ---
    # Chinese markers are built via -join of char codes so this file stays ASCII-only.
    $patAllPass = -join @([char]0x5168, [char]0x90E8, [char]0x901A, [char]0x8FC7)
    $patCleanup = -join @([char]0x51C6, [char]0x5907, [char]0x9000, [char]0x51FA)
    $sawAllPass = ($outText -match $patAllPass)
    $sawCleanup = ($errText -match $patCleanup)
    $verdict = ""
    if (-not $exited) {
        $verdict = "HANG"
    } elseif ($code -ne $null) {
        if ($code -lt 0)      { $verdict = "CRASH" }
        elseif ($code -eq 0)  { $verdict = "OK" }
        elseif ($code -eq 1)  { $verdict = "ASSERT" }
        else                  { $verdict = "OTHER" }
    } else {
        if ($sawAllPass)   { $verdict = "OK" }
        elseif ($sawCleanup) { $verdict = "ASSERT" }
        else               { $verdict = "CRASH" }
    }

    $rtss = Test-RtssAlive
    $hex = ""
    if ($code -ne $null -and $code -lt 0) { $hex = "0x{0:X8}" -f ([uint32]([int64]$code -band 0xFFFFFFFFL)) }
    $obj = [PSCustomObject]@{
        time       = (Get-Date -Format "HH:mm:ss")
        arm        = $arm
        round      = $round
        pos        = $pos
        verdict    = $verdict
        exitCode   = $code
        codeHex    = $hex
        wallSec    = $wall
        dllOk      = $dllOk
        diskHash   = $diskHash
        embedRaw   = $embed
        injected   = $injected
        thrEarly   = $thrEarly
        thrMid     = $thrMid
        rtssUp     = $rtss
        sawAllPass = $sawAllPass
        sawCleanup = $sawCleanup
        pid        = $procId
        outFile    = (Split-Path -Leaf $outFile)
    }
    $obj | Export-Csv -Path $csv -Append -NoTypeInformation -Encoding UTF8
    Write-Line ("{0} r{1} pos{2}  {3,-7} code={4} hex={5} wall={6}s dllOk={7} thr={8}/{9} inj=[{10}] rtssUp={11}" -f `
        $arm, $round, $pos, $verdict, $code, $obj.codeHex, $wall, $dllOk, $thrEarly, $thrMid, $injected, $rtss)
    return $obj
}

if ($Fresh -and (Test-Path $csv)) { Remove-Item $csv -Force }
if ($Fresh -and (Test-Path $log)) { Remove-Item $log -Force }

$dumpsBefore = Get-MinidumpSet

Write-Line "======== single-presenter A/B (same batch): rounds 1..$Rounds, DurationSec=$DurationSec ========"
Write-Line ("RTSS alive at start: " + (Test-RtssAlive))
Write-Line ("A arm (pre-fix) = " + $armDll['A'])
Write-Line ("B arm (fixed)   = " + $armDll['B'])
Write-Line ("A sha256 = " + $expectHash['A'])
Write-Line ("B sha256 = " + $expectHash['B'])
Write-Line ("minidumps at start: " + ($dumpsBefore -join ','))

$results = @()
$abort = ""
for ($r = $StartRound; $r -le $Rounds; $r++) {
    $order = if ($r % 2 -eq 1) { @('A', 'B') } else { @('B', 'A') }
    Write-Line ("---- round $r order: " + ($order -join ' -> ') + " ----")
    $firstPos = 1
    if ($r -eq $StartRound) { $firstPos = $StartPos }
    for ($pos = $firstPos; $pos -le $order.Count; $pos++) {
        $one = Invoke-Case $order[$pos - 1] $r $pos
        if ($one -is [PSCustomObject]) { $results += $one }

        # --- guards: BSOD / RTSS loss ---
        if (-not (Test-RtssAlive)) { $abort = "RTSS/Afterburner process disappeared after run $r/$pos"; break }
        $now = Get-MinidumpSet
        $new = @($now | Where-Object { $dumpsBefore -notcontains $_ })
        if ($new.Count -gt 0) { $abort = "new BSOD minidump after run $r/${pos}: " + ($new -join ','); break }

        Start-Sleep -Seconds $GapSec
    }
    if ($abort -ne "") { Write-Line ("!!! ABORT: " + $abort); break }
}

Write-Line "======== batch finished ========"
$sum = $results | Group-Object arm | Sort-Object Name | ForEach-Object {
    $n = $_.Count
    $crash  = @($_.Group | Where-Object { $_.verdict -eq 'CRASH' }).Count
    $ok     = @($_.Group | Where-Object { $_.verdict -eq 'OK' }).Count
    $assert = @($_.Group | Where-Object { $_.verdict -eq 'ASSERT' }).Count
    $other  = @($_.Group | Where-Object { $_.verdict -eq 'OTHER' }).Count
    $hang   = @($_.Group | Where-Object { $_.verdict -eq 'HANG' }).Count
    "{0} arm: crash {1}/{2} = {3:N1}%  OK={4} ASSERT={5} OTHER={6} HANG={7}" -f `
        $_.Name, $crash, $n, (100.0 * $crash / $n), $ok, $assert, $other, $hang
}
foreach ($s in $sum) { Write-Line $s }

# --- always leave the fixed (B) kernel in bin ---
Copy-Item -Path $armDll['B'] -Destination $target -Force
Start-Sleep -Milliseconds 400
$finalHash = (Get-FileHash -Algorithm SHA256 -Path $target).Hash
Write-Line ("restore B kernel to bin: sha256=$finalHash ok=" + ($finalHash -eq $expectHash['B']))
