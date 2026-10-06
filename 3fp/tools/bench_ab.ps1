# 上传优化 A/B 对比（before = merged/PR#15，after = 优化后）
# 用法：pwsh -File tools/bench_ab.ps1
$ErrorActionPreference = 'Continue'
$root = 'C:\PLAN\3FCompare\3fp'
$build = Join-Path $root 'build'
$probe = Join-Path $build 'upload_layout_probe.exe'
$old = Join-Path $build 'FFF.Native.merged.dll'
$new = Join-Path $build 'FFF.Native.opt1.dll'
$target = Join-Path $build 'FFF.Native.dll'

# 素材：覆盖 layout 1(planar 软解) / layout 2(NV12) / 硬解 / HDR
$cases = @(
    @{ Name = '720p-软解-yuv420p';   Path = "$build\vidtest\sdr_720p.mp4";       Args = @('--seconds','3') },
    @{ Name = '1080pHDR-软解';       Path = "$build\vidtest\hdr10_1080p.mp4";     Args = @('--seconds','3') },
    @{ Name = '720p-硬解';           Path = "$build\vidtest\sdr_720p.mp4";       Args = @('--seconds','3','--hw') },
    @{ Name = '4K-h264-硬解';        Path = 'C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4'; Args = @('--seconds','3','--hw') },
    @{ Name = '8K-hevc-硬解';        Path = 'C:\PLAN\3FCompare\testmedia\media\real\real_8k_hevc10_150m.mp4'; Args = @('--seconds','3','--hw') }
)

function Run-One($dll, $case, $runs) {
    Copy-Item $dll $target -Force
    $ups = @(); $frames = @()
    for ($i = 0; $i -lt $runs; $i++) {
        $o = & $probe $case.Path @($case.Args) 2>&1
        if (($o | Select-String '上传累计:.*增量 ([\d.]+)') -match '增量 ([\d.]+)') { $ups += [double]$matches[1] }
        if (($o | Select-String '呈现帧: 0 -> (\d+)') -match '0 -> (\d+)') { $frames += [int]$matches[1] }
    }
    $upMed = if ($ups.Count) { ($ups | Sort-Object)[[int]($ups.Count/2)] } else { -1 }
    $frMed = if ($frames.Count) { ($frames | Sort-Object)[[int]($frames.Count/2)] } else { -1 }
    return @{ Up = $upMed; Frames = $frMed }
}

if (-not (Test-Path $old)) { Write-Host "缺少基线 DLL: $old"; exit 1 }
if (-not (Test-Path $new)) { Write-Host "缺少优化 DLL: $new"; exit 1 }

$lines = @()
$lines += "=== 上传优化 A/B  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
$lines += "before: $old"
$lines += "after : $new"
$lines += ""

foreach ($c in $cases) {
    if (-not (Test-Path $c.Path)) { $lines += "$($c.Name): SKIP"; continue }
    # 交替顺序跑，降低漂移影响
    $b = Run-One $old $c 3
    $a = Run-One $new $c 3
    $b2 = Run-One $old $c 3
    $a2 = Run-One $new $c 3
    $beforeUp = [math]::Min($b.Up, $b2.Up)
    $afterUp  = [math]::Min($a.Up, $a2.Up)
    $beforeFr = $b.Frames
    $afterFr  = $a.Frames
    $ratio = if ($afterUp -gt 0) { $beforeUp / $afterUp } else { 0 }
    $line = "{0,-20} 上传 before={1,7:N1} ms  after={2,6:N1} ms  提速 {3,5:N1}x   帧 {4} -> {5}" -f `
        $c.Name, $beforeUp, $afterUp, $ratio, $beforeFr, $afterFr
    $lines += $line
    Write-Host $line
}

$outFile = Join-Path $build 'bench-ab.txt'
$lines | Set-Content -Path $outFile -Encoding UTF8
Write-Host "`n已写入 $outFile"
