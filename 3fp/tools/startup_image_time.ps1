# startup_image_time.ps1 - end-to-end image-mode open timing for the 3FP host.
#
# Two criteria, both bound to the spawned pid:
#   T1 titled    : first top-level window of the pid that is visible AND has a title.
#                  (docs/57 lesson: window identity recorded; RunDLL-style fake windows
#                   are excluded by pid binding + class/title evidence.)
#   T2 onscreen  : the client area of that window actually shows picture content,
#                  verified by screen-capture pixel statistics (stddev of sampled
#                  grayscale > threshold, 2 consecutive positive polls).
#
# Protocol: warmup runs discarded, formal runs to CSV, SpawnMs guard (>50ms marked),
# kill + 1.2s settle between rounds (IsSingleInstance). When -TraceDir is set, the
# child's stderr (FPT_OPEN_TRACE lines from the kernel) and the host FFFPROF log
# (%TEMP%\fffprof_<pid>.log, FFFPROF=1) are captured per round.
#
# Usage:
#   pwsh -File startup_image_time.ps1 -Exe <FFF.Player.exe> [-Image <path>] [-Runs 9]
#        [-WarmupRuns 2] [-CsvOut <csv>] [-TraceDir <dir>] [-Tag <label>]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Image = '',
    [int]$Runs = 9,
    [int]$WarmupRuns = 2,
    [int]$TimeoutSec = 60,
    [int]$PollMs = 4,
    [int]$PixelPollMs = 8,
    [double]$ContentStdDevMin = 8.0,
    # A solid-colour fixture makes "picture is on screen" measurable instead of guessed:
    # stddev alone is satisfied by window chrome (proven 2026-10-30 - empty mode fires T2
    # at the same time as an image). Set this to a mean above the chrome-only mean to gate
    # on actual content. 0 keeps the legacy behaviour.
    [double]$ContentMeanMin = 0.0,
    [int]$MaxSpawnMs = 50,
    [int]$HoldMs = 0,
    [string]$CsvOut = '',
    [string]$TraceDir = '',
    [string]$Tag = 'run'
)
$ErrorActionPreference = 'Stop'

# --- self-contained Win32 pixel/window probes ---------------------------------
if (-not ('StartupImage.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace StartupImage {
    public static class Native {
        delegate bool EnumCb(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

        // Returns the first visible titled top-level window of the pid, as hwnd +
        // class + title evidence. Fake-window safe: pid-bound, evidence recorded.
        public static bool FindTitledWindow(uint wantedPid, out IntPtr hwnd, out string cls, out string title) {
            hwnd = IntPtr.Zero; cls = ""; title = "";
            IntPtr found = IntPtr.Zero; string fCls = "", fTitle = "";
            EnumWindows((h, l) => {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != wantedPid) return true;
                if (!IsWindowVisible(h)) return true;
                int len = GetWindowTextLength(h);
                if (len <= 0) return true;
                var t = new StringBuilder(len + 2); GetWindowTextW(h, t, t.Capacity);
                if (t.Length == 0) return true;
                var c = new StringBuilder(256); GetClassNameW(h, c, 256);
                found = h; fCls = c.ToString(); fTitle = t.ToString();
                return false;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) { hwnd = found; cls = fCls; title = fTitle; return true; }
            return false;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);

        // Captures the client rect of hwnd from the screen and returns 8-bit
        // grayscale stats over a coarse grid (one sample per step pixels).
        public static bool SampleClientPixels(IntPtr hwnd, int step, out double mean, out double stdDev) {
            mean = 0; stdDev = 0;
            RECT rc;
            if (!GetClientRect(hwnd, out rc)) return false;
            int w = rc.R - rc.L, h = rc.B - rc.T;
            if (w < 160 || h < 120) return false;
            int cw = Math.Min(w, 640), ch = Math.Min(h, 400);   // cap the capture cost
            var pt = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(hwnd, ref pt)) return false;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr bmp = CreateCompatibleBitmap(screenDc, cw, ch);
            IntPtr old = SelectObject(memDc, bmp);
            bool ok = false;
            try {
                if (!StretchBlt(memDc, 0, 0, cw, ch, screenDc, pt.X, pt.Y, w, h, SRCCOPY)) return false;
                var bmi = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER { biSize = 40, biWidth = cw, biHeight = -ch, biPlanes = 1, biBitCount = 32, biCompression = 0 } };
                var bytes = new byte[cw * ch * 4];
                IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
                try {
                    if (GetDIBits(memDc, bmp, 0, (uint)ch, ptr, ref bmi, 0) == 0) return false;
                    Marshal.Copy(ptr, bytes, 0, bytes.Length);
                } finally { Marshal.FreeHGlobal(ptr); }
                // Coarse grid: every `step`-th pixel of the capped bitmap.
                long sum = 0; long sum2 = 0; long n = 0;
                for (int y = 0; y < ch; y += step) {
                    int row = y * cw * 4;
                    for (int x = 0; x < cw; x += step) {
                        int i = row + x * 4;
                        int g = (bytes[i] * 299 + bytes[i + 1] * 587 + bytes[i + 2] * 114) / 1000;
                        sum += g; sum2 += (long)g * g; n++;
                    }
                }
                if (n < 100) return false;
                mean = (double)sum / n;
                double var = (double)sum2 / n - mean * mean;
                stdDev = var > 0 ? Math.Sqrt(var) : 0;
                ok = true;
            } finally {
                SelectObject(memDc, old);
                DeleteObject(bmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc);
            }
            return ok;
        }
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int dx, int dy, int dw, int dh, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, IntPtr bits, ref BITMAPINFO bmi, uint usage);
        const int SRCCOPY = 0x00CC0020;

        // ---- 启动期 CPU 放置自证（docs/151，P1 启动期 A/B）----------------------
        // GetProcessDefaultCpuSets 是 OS 侧读数：只要 FFF_STARTUP_PLACEMENT=1 真的
        // 通过了宿主→内核，启动窗口内该进程就会持有 P-core 的默认 CPU 集合（本机 12）。
        // 计数为 0 说明策略没生效（或 env 没进 GUI）—— 用来把"注入失败"和"无差异"分开。
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessDefaultCpuSets(IntPtr process, UIntPtr[] cpuSetIds, uint count, out uint required);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public static uint ProcessDefaultCpuSetCount(uint pid) {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0xFFFFFFFF;
            try {
                // Same contract the kernel's own query uses: TRUE = no explicit default;
                // FALSE + ERROR_INSUFFICIENT_BUFFER = required count is the set size.
                uint required;
                if (GetProcessDefaultCpuSets(h, null, 0, out required)) return 0;
                if (Marshal.GetLastWin32Error() == 122) return required;
                return 0xFFFFFFFF;
            } finally { CloseHandle(h); }
        }

        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
    }
}
'@ | Out-Null
    # SetProcessDPIAware must run before any coordinate math.
    [StartupImage.Native]::SetProcessDPIAware() | Out-Null
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "exe not found: $Exe" }
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if ($TraceDir -ne '') { New-Item -ItemType Directory -Force -Path $TraceDir | Out-Null }

$existing = Get-Process FFF.Player -ErrorAction SilentlyContinue
if ($existing) { throw "FFF.Player already running (pid $($existing.Id -join ',')) - close it first (single instance)." }

# Env vars are set in THIS shell's environment so the child inherits them.
# (Adding keys via ProcessStartInfo.EnvironmentVariables does not reach the GUI
# apphost on this machine - measured 2026-10-28: a pre-existing parent var
# (DOTNET_ROOT) reached the child while newly-added keys did not.)
$env:FFFPROF = '1'
$env:FPT_OPEN_TRACE = '1'
$env:DOTNET_ROOT = 'C:\Program Files\dotnet'
$env:DOTNET_ROOT_X64 = 'C:\Program Files\dotnet'

# Marker-file gates (see the note above): the instrumentation also honors
# <exe dir>\fffprof.enable and <native dll dir>\open-trace.enable. Create them for
# the run and clean them up at the end, whatever happens.
$exeDir = Split-Path -Parent $Exe
$markerFffprof = Join-Path $exeDir 'fffprof.enable'
$markerOpenTrace = Join-Path $exeDir 'open-trace.enable'
New-Item -ItemType File -Force -Path $markerFffprof | Out-Null
New-Item -ItemType File -Force -Path $markerOpenTrace | Out-Null
$script:MarkersLeft = @($markerFffprof, $markerOpenTrace)

function Measure-One {
    [int]$round = $script:RoundCounter
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Exe
    $psi.Arguments = if ($Image -ne '') { '"' + $Image + '"' } else { '' }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardError = $true
    # FFFPROF / FPT_OPEN_TRACE / DOTNET_ROOT are inherited from this shell (set above).
    # Self-proof columns (docs/151): the FFF_* values THIS shell will hand to the GUI,
    # the system load of the round, and the child's process-default CPU-set size
    # sampled while it starts (FFF_STARTUP_PLACEMENT=1 must show P-core ids here).
    $envStartupPlacement = if ($null -eq $env:FFF_STARTUP_PLACEMENT) { '(unset)' } else { [string]$env:FFF_STARTUP_PLACEMENT }
    $envPlacement = if ($null -eq $env:FFF_PLACEMENT) { '(unset)' } else { [string]$env:FFF_PLACEMENT }
    $loadPct = try { [double](Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime } catch { -1.0 }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $spawnSw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $spawnMs = [math]::Round($spawnSw.Elapsed.TotalMilliseconds, 1)
    $procId = [uint32]$p.Id
    $errTask = $p.StandardError.ReadToEndAsync()

    $titledMs = -1.0; $pixelsMs = -1.0
    # Machine-wide monotonic clock at each criterion, so the child's own [LT] trace
    # lines (same clock) can be aligned to T1/T2 without assuming any offset.
    $stT1 = -1L; $stT2 = -1L
    $meanAtT2 = -1.0; $stdAtT2 = -1.0; $meanLate = -1.0
    $cls = ''; $titleAtT1 = ''; $titleAtT2 = ''
    $contentStreak = 0
    $startupSetMax = 0
    $cpuAtT2 = -1.0
    $note = ''
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($p.HasExited) { $note = "early-exit exit=$($p.ExitCode)"; break }
        $setCount = [StartupImage.Native]::ProcessDefaultCpuSetCount($procId)
        if ($setCount -ne 0xFFFFFFFF -and $setCount -gt $startupSetMax) { $startupSetMax = [int]$setCount }
        if ($titledMs -lt 0) {
            [IntPtr]$hwnd = [IntPtr]::Zero
            if ([StartupImage.Native]::FindTitledWindow($procId, [ref]$hwnd, [ref]$cls, [ref]$titleAtT1)) {
                $titledMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
                $stT1 = [Diagnostics.Stopwatch]::GetTimestamp() * 1000.0 / [Diagnostics.Stopwatch]::Frequency
                $script:LastHwnd = $hwnd
            }
        } else {
            $m = 0.0; $s = 0.0
            if ([StartupImage.Native]::SampleClientPixels($script:LastHwnd, 3, [ref]$m, [ref]$s)) {
                if ($s -ge $ContentStdDevMin -and ($ContentMeanMin -le 0.0 -or $m -ge $ContentMeanMin)) {
                    $contentStreak++
                    if ($contentStreak -ge 2) {
                        $pixelsMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
                        $stT2 = [Diagnostics.Stopwatch]::GetTimestamp() * 1000.0 / [Diagnostics.Stopwatch]::Frequency
                        $meanAtT2 = [math]::Round($m, 1); $stdAtT2 = [math]::Round($s, 1)
                        $t2 = '';$c2 = ''
                        [StartupImage.Native]::FindTitledWindow($procId, [ref]$script:LastHwnd, [ref]$c2, [ref]$t2) | Out-Null
                        $titleAtT2 = $t2
                        # CPU time consumed by the child up to T2: the stage-0 ratio
                        # (CPU / wall) that docs/146 §八 uses to kill this direction.
                        try { $cpuAtT2 = [math]::Round($p.TotalProcessorTime.TotalMilliseconds, 1) } catch { $cpuAtT2 = -1.0 }
                        break
                    }
                } else { $contentStreak = 0 }
            }
        }
        if ($pixelsMs -lt 0) { Start-Sleep -Milliseconds ($titledMs -lt 0 ? $PollMs : $PixelPollMs) }
    }
    if ($pixelsMs -lt 0 -and $titledMs -ge 0 -and $note -eq '') { $note = 'timeout-no-picture-content' }
    if ($titledMs -lt 0 -and $note -eq '') { $note = 'timeout-no-titled-window' }
    if ($spawnMs -gt $MaxSpawnMs -and $note -eq '') { $note = "spawn-slow($spawnMs ms)" }
    # -HoldMs keeps the child alive after T2 was recorded, so (a) its own [LT] trace is
    # fully flushed before the kill, and (b) the pixels seen AT T2 can be compared with
    # the steady-state frame. Judging is unaffected - T2/T1 were already stamped.
    $stdLate = -1.0
    if ($HoldMs -gt 0 -and $pixelsMs -ge 0) {
        Start-Sleep -Milliseconds $HoldMs
        $m2 = 0.0; $s2 = 0.0
        if ([StartupImage.Native]::SampleClientPixels($script:LastHwnd, 3, [ref]$m2, [ref]$s2)) {
            $stdLate = [math]::Round($s2, 1); $meanLate = [math]::Round($m2, 1)
        }
    }
    try { if (-not $p.HasExited) { $p.Kill() } } catch { }
    try { $p.WaitForExit(5000) | Out-Null } catch { }
    $errText = ''
    try { if ($errTask.Wait(3000)) { $errText = $errTask.Result } } catch { }
    Start-Sleep -Milliseconds 1200

    if ($TraceDir -ne '') {
        $base = Join-Path $TraceDir ("{0}_{1}_{2}" -f $Tag, $round, $procId)
        if ($errText.Length -gt 0) { [IO.File]::WriteAllText("$base.opentrace.log", $errText) }
        $profLog = Join-Path ([IO.Path]::GetTempPath()) "fffprof_$procId.log"
        if (Test-Path $profLog) { Copy-Item $profLog "$base.fffprof.log" -Force; Remove-Item $profLog -Force }
    }
    return [pscustomobject]@{
        Tag = $Tag; Round = $round; Mode = ($(if ($Image -ne '') { 'image' } else { 'empty' }))
        SpawnMs = $spawnMs; TitledMs = $titledMs; OnScreenMs = $pixelsMs
        StT1 = $stT1; StT2 = $stT2
        ContentStdDev = $s; StdDevLate = $stdLate; MeanAtT2 = $meanAtT2; StdAtT2 = $stdAtT2; MeanLate = $meanLate; Class = $cls; TitleAtT1 = $titleAtT1; TitleAtT2 = $titleAtT2; Note = $note
        StartupDefaultSetMax = $startupSetMax; CpuAtT2Ms = $cpuAtT2; LoadPct = $loadPct
        EnvStartupPlacement = $envStartupPlacement; EnvPlacement = $envPlacement
    }
}

$script:RoundCounter = 0
if ($WarmupRuns -gt 0) {
    Write-Host "== warmup x$WarmupRuns (discarded) =="
    for ($i = 1; $i -le $WarmupRuns; $i++) {
        $script:RoundCounter++
        $r = Measure-One
        Write-Host ("  warmup {0}: titled={1} onscreen={2} {3}" -f $i, $r.TitledMs, $r.OnScreenMs, $r.Note)
    }
}
$results = @()
Write-Host "== formal x$Runs =="
for ($i = 1; $i -le $Runs; $i++) {
    $script:RoundCounter++
    $r = Measure-One
    $results += $r
    Write-Host ("  run {0}: titled={1} onscreen={2} stddev={3:N1} cpu={4} setmax={5} {6}" -f $i, $r.TitledMs, $r.OnScreenMs, $r.ContentStdDev, $r.CpuAtT2Ms, $r.StartupDefaultSetMax, $r.Note)
}

$valid = @($results | Where-Object { $_.TitledMs -ge 0 -and $_.OnScreenMs -ge 0 -and $_.Note -eq '' })
# Remove the marker files whatever happens next (best effort).
foreach ($m in $script:MarkersLeft) { Remove-Item $m -Force -ErrorAction SilentlyContinue }
function Median($arr) {
    if ($arr.Count -eq 0) { return -1 }
    $s = $arr | Sort-Object
    if ($s.Count % 2 -eq 1) { return [math]::Round($s[[int][math]::Floor($s.Count / 2)], 1) }
    return [math]::Round(($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2, 1)
}
$ratioValues = @($valid | ForEach-Object {
        if ($_.OnScreenMs -gt 0 -and $_.CpuAtT2Ms -ge 0) { [math]::Round($_.CpuAtT2Ms / $_.OnScreenMs * 100, 1) }
    })
Write-Host "`n===== summary ($($valid.Count)/$($results.Count) valid) ====="
[pscustomobject]@{
    Exe = $Exe; Image = $Image; Tag = $Tag
    ValidN = $valid.Count
    TitledMedian = Median @($valid | ForEach-Object { $_.TitledMs })
    OnScreenMedian = Median @($valid | ForEach-Object { $_.OnScreenMs })
    OpenGapMedian = Median @($valid | ForEach-Object { $_.OnScreenMs - $_.TitledMs })
    CpuAtT2MedianMs = Median @($valid | ForEach-Object { $_.CpuAtT2Ms })
    CpuOverWallMedianPct = Median $ratioValues
    StartupSetMaxMedian = Median @($valid | ForEach-Object { $_.StartupDefaultSetMax })
    LoadMedianPct = Median @($valid | ForEach-Object { $_.LoadPct })
    EnvStartupPlacement = $env:FFF_STARTUP_PLACEMENT
    TitledAll = (($valid | ForEach-Object { $_.TitledMs }) -join ',')
    OnScreenAll = (($valid | ForEach-Object { $_.OnScreenMs }) -join ',')
} | Format-List
if ($CsvOut -ne '') {
    $results | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8
    Write-Host "detail csv: $CsvOut"
}
