# 放大阶段实测（重点）：4 路真实 4K 素材，逐组合多次尝试直到"放大阶段"跑完。
# 背景：4 路真实播放会**间歇性**在 dxgi.dll 崩（0xc0000005），已用未经改动的 --multitest 4 路复现
# ⇒ 与本功能无关。故缩短阶段、允许重试：只要有一轮打印出 magnified 阶段的显存/帧数即采用。
param([int]$Chunk = 0)
$ErrorActionPreference = 'Continue'
$probe = Join-Path $PSScriptRoot 'gpu_probe.ps1'
$video = 'C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4'
$sec = 4
$tries = 5

$cases = @(
    # 默认窗口（对比区 2.25 Mpx）：z=2 → 8.9 Mpx；z=3 → 20.0 Mpx；z=4(覆盖闸门) → 35.7 Mpx
    @{ Tag = 'm2_4r_z2';        Routes = 4; Zoom = 2; Budget = 0;   Max = 0 },
    @{ Tag = 'm3_4r_z3';        Routes = 4; Zoom = 3; Budget = 0;   Max = 0 },
    @{ Tag = 'm4_4r_z4_nogate'; Routes = 4; Zoom = 4; Budget = 400; Max = 0 },
    # 最大化窗口（对比区 6.3 Mpx）：z=2 → 25.1 Mpx；z=4(覆盖) → 100.6 Mpx
    @{ Tag = 'n2_4r_z2_max';    Routes = 4; Zoom = 2; Budget = 0;   Max = 1 },
    @{ Tag = 'n4_4r_z4_max';    Routes = 4; Zoom = 4; Budget = 400; Max = 1 }
)

$i = 0
foreach ($c in $cases) {
    $i++
    if ($Chunk -eq 1 -and $i -gt 3) { continue }
    if ($Chunk -eq 2 -and $i -le 3) { continue }
    if ($c.Max -eq 1) { $env:_3FC_BENCH_MAXIMIZE = '1' } else { Remove-Item Env:_3FC_BENCH_MAXIMIZE -ErrorAction SilentlyContinue }
    $ok = $false
    for ($try = 1; $try -le $tries -and -not $ok; $try++) {
        Write-Host ">>> $($c.Tag) 第 $try/$tries 次"
        $tag = if ($try -eq 1) { $c.Tag } else { "$($c.Tag)_r$try" }
        $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $probe -Video $video `
            -Routes $c.Routes -Zoom $c.Zoom -Seconds $sec -BudgetMpx $c.Budget -Tag $tag -TimeoutSec 120
        $out | Write-Host
        if ($out | Select-String -Pattern '^magnified') { $ok = $true }
    }
    if (-not $ok) { Write-Host ">>> $($c.Tag) $tries 次均未取到放大阶段" }
    Write-Host ""
}
Write-Host "=== 放大阶段实测结束（chunk=$Chunk）==="
