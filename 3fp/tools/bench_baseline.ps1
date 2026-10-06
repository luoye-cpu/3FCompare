# 性能基线采集脚本
# 用法：pwsh -File tools/bench_baseline.ps1 -Label before
param([string]$Label = "before")

$ErrorActionPreference = 'Continue'
$root = 'C:\PLAN\3FCompare\3fp'
$bench = Join-Path $root 'build\upload_bench.exe'
$outFile = Join-Path $root "build\bench-$Label.txt"

$cases = @(
    @{ Name = '720p-cpu';  Media = "$root\build\vidtest\sdr_720p.mp4";                          Decode = 'cpu' },
    @{ Name = '720p-gpu';  Media = "$root\build\vidtest\sdr_720p.mp4";                          Decode = 'gpu' },
    @{ Name = '1080p-hdr-cpu'; Media = "$root\build\vidtest\hdr10_1080p.mp4";                   Decode = 'cpu' },
    @{ Name = '4k-h264-gpu';  Media = 'C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4';      Decode = 'gpu' },
    @{ Name = '4k-av1-cpu';   Media = 'C:\PLAN\3FCompare\testmedia\media\real\real_4k_av1_10bit_60m.mp4'; Decode = 'cpu' },
    @{ Name = '8k-hevc-gpu';  Media = 'C:\PLAN\3FCompare\testmedia\media\real\real_8k_hevc10_150m.mp4';   Decode = 'gpu' },
    @{ Name = '8k-av1-gpu';   Media = 'C:\PLAN\3FCompare\testmedia\media\real\real_8k_av1_hdr10_100m.mp4'; Decode = 'gpu' }
)

$lines = @()
$lines += "=== bench label=$Label  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
$lines += "内核: " + (Get-Item (Join-Path $root 'build\FFF.Native.dll')).LastWriteTime.ToString('HH:mm:ss') +
          "  SHA256=" + (Get-FileHash (Join-Path $root 'build\FFF.Native.dll') -Algorithm SHA256).Hash.Substring(0,16)

foreach ($c in $cases) {
    if (-not (Test-Path $c.Media)) { $lines += "$($c.Name): SKIP (媒体缺失)"; continue }
    $o = & $bench $c.Media --decode $c.Decode --seconds 3 --runs 5 2>&1 |
         Where-Object { $_ -notmatch 'libpng' }
    $fps = ($o | Select-String 'FPS_median=([\d.]+)').Matches.Groups[1].Value
    $others = ($o | Select-String 'sws_ms_median').Line
    $lines += "$($c.Name): FPS=$fps"
    $lines += "    $others"
    Write-Host "$($c.Name): FPS=$fps"
}

$lines | Set-Content -Path $outFile -Encoding UTF8
Write-Host "`n已写入 $outFile"
Get-Content $outFile
