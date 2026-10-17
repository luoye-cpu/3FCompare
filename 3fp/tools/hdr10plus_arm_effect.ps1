# What HDR10+ dynamic metadata actually does, per presentation arm.
#
# The two files are the same clip, identical decoded pixels (proven by full-stream md5 in
# make_true_control.py), differing ONLY by the ST 2094-40 SEI. So anything this gate
# observes is attributable to the dynamic metadata and nothing else.
#
# The two arms have DIFFERENT documented jobs (docs/58 §4.5 / T3):
#   MapToSdr : the renderer applies the per-window curve itself
#              -> the drawn picture MUST differ between the arms. A SAME here is the
#                 RE-03 class of bug (metadata accepted, never reaches pixels).
#   MapToHdr : the renderer hands the picture to DWM/the display and instead updates
#              DXGI_HDR_METADATA_HDR10 per frame -> pixels identical in our own readback
#              (expected), but the per-frame SIGNALLING VALUES must differ, otherwise
#              "the metadata guides the display mapping" buys nothing on this arm.
param(
    [string]$Control = 'C:\PLAN\3FCompare\tests\output\perf-hdr-20261010\media\4k-hevc-hdr10stripped.mp4',
    [string]$Dynamic = 'C:\PLAN\3FCompare\tests\output\perf-hdr-20261010\media\4k-hevc-hdr10plus.mp4',
    [int]$Frame = 120,
    [int]$Row = 170,
    [string]$Bin = 'C:\PLAN\3FCompare\tests\output\perf-hdr-20261010\bin'
)

$ErrorActionPreference = 'Continue'
$exe = Join-Path $Bin 'hdr10plus_arm_effect_probe.exe'
$logF = 'C:\PLAN\3FCompare\tests\output\perf-hdr-20261010\logs\arm-effect.log'
if (-not (Test-Path $exe)) {
    "missing $exe  (build: py 3fp/tools/build_fork_probe.py hdr10plus_arm_effect_probe)" | Tee-Object $logF
    exit 2
}
Set-Content -Path $logF -Value ("=== HDR10+ arm effect gate {0} ===" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')) -Encoding UTF8

function RunProbe([string]$media, [bool]$hdr) {
    $color = if ($hdr) { 'hdr' } else { 'sdr' }
    $force = if ($hdr) { '1' } else { '0' }
    $out = & $exe $media --color $color --forcehdr $force --frame $Frame --rows 7 --width 256 2>&1 | Out-String
    Add-Content -Path $logF -Value ("--- {0} arm={1}`n{2}" -f (Split-Path $media -Leaf), $color, $out) -Encoding UTF8
    [pscustomobject]@{
        digest = [regex]::Match($out, 'digest=([0-9a-f]{16})').Groups[1].Value
        landed = [regex]::Match($out, 'landedFrame=(\d+)').Groups[1].Value
        nonzero= [regex]::Match($out, 'nonZero=(\d+)/').Groups[1].Value
        path   = [regex]::Match($out, 'claims path=(\d+)').Groups[1].Value
        bits   = [regex]::Match($out, 'outbits=(\d+)').Groups[1].Value
        src    = [regex]::Match($out, 'peaks sourceNits=(\d+)').Groups[1].Value
        tgt    = [regex]::Match($out, 'effectiveTargetNits=(\d+)').Groups[1].Value
        disp   = [regex]::Match($out, 'displayPeakNits=(\d+)').Groups[1].Value
        rc     = $LASTEXITCODE
    }
}

$bad = @()
foreach ($armHd in @($false, $true)) {
    $label = if ($armHd) { 'MapToHdr (display-referred)' } else { 'MapToSdr (renderer curve)' }
    $c = RunProbe $Control $armHd
    $d = RunProbe $Dynamic $armHd
    if (-not $c.digest -or -not $d.digest) {
        $bad += "$label : no digest (rc $($c.rc)/$($d.rc)) - wrongly-landed or all-black sample, see $logF"
        continue
    }
    # Comparability first: two runs that landed on different frames, or that sampled a
    # black region, cannot say anything about the metadata at all.
    if ($c.landed -ne $d.landed -or $c.landed -ne "$Frame") {
        $bad += "$label : frames did not match (control=$($c.landed) dynamic=$($d.landed) requested=$Frame)"
        continue
    }
    $pixelsDiffer = ($c.digest -ne $d.digest)
    $peaksDiffer = ($c.src -ne $d.src -or $c.tgt -ne $d.tgt)
    "{0,-30} pixels {1}  landed={2} srcPeak {3}->{4} effTarget {5}->{6} path={7} outbits={8}" -f `
        $label, $(if ($pixelsDiffer) { 'DIFFER' } else { 'SAME  ' }), `
        $d.landed, $c.src, $d.src, $c.tgt, $d.tgt, $d.path, $d.bits | Tee-Object -FilePath $logF -Append

    if (-not $armHd) {
        if (-not $pixelsDiffer) {
            $bad += "$label : the renderer accepted ST 2094-40 but the picture did not change (curve never reached the shader)"
        }
    } else {
        if ($d.bits -ne '16') { $bad += "$label : arm is not the 16-bit chain (outbits=$($d.bits))" }
        if (-not $peaksDiffer) {
            $bad += "$label : per-frame signalling did not move either (srcPeak/effTarget identical) -> HDR10+ contributes nothing on this arm"
        }
        if ($pixelsDiffer) {
            # docs/58 T3 says the scRGB branch hands the picture to the display, so the
            # renderer's own output should not reshape with content-adaptive peaks. If it
            # does, that coupling is undocumented -- fail rather than quietly bless it.
            $bad += "$label : pixels changed on the display-referred arm although docs/58 T3 says the renderer does not map there -- find which constant couples them"
        }
    }
}

if ($bad.Count) {
    ("GATE: FAIL ({0})" -f $bad.Count) | Tee-Object -FilePath $logF -Append
    $bad | ForEach-Object { "   $_" | Tee-Object -FilePath $logF -Append }
    exit 1
}
"GATE: PASS - curve reaches pixels on MapToSdr; display signalling moves per frame on MapToHdr" |
    Tee-Object -FilePath $logF -Append
