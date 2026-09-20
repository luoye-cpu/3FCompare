# 无缝放大实测矩阵：默认窗口 / 最大化窗口 × 路数 × z（含越过闸门的预算覆盖）
# -Chunk 1|2 分两段跑（单段耗时约 2.5 分钟，避免单次调用过长）
param([int]$Chunk = 0)
$ErrorActionPreference = 'Continue'
$probe = Join-Path $PSScriptRoot 'gpu_probe.ps1'
$video = 'C:\PLAN\3FCompare\testmedia\media\real\real_4k_h264_60m.mp4'
$sec = 8

$cases = @(
    # 默认窗口（对比区 ~2.25 Mpx）：4 路 z=1/2/3/4；z=4 默认预算应被拒，再覆盖预算实测
    @{ Tag = 'a1_4r_z1';        Routes = 4; Zoom = 1; Budget = 0;   Max = 0 },
    @{ Tag = 'a2_4r_z2';        Routes = 4; Zoom = 2; Budget = 0;   Max = 0 },
    @{ Tag = 'a3_4r_z3';        Routes = 4; Zoom = 3; Budget = 0;   Max = 0 },
    @{ Tag = 'a4_4r_z4_gate';   Routes = 4; Zoom = 4; Budget = 0;   Max = 0 },
    @{ Tag = 'a5_4r_z4_nogate'; Routes = 4; Zoom = 4; Budget = 400; Max = 0 },
    # 最大化窗口（对比区 ~6.3 Mpx）：4 路 z=1/2；z=3 默认应被拒；z=4 覆盖预算实测
    @{ Tag = 'b1_4r_z1_max';    Routes = 4; Zoom = 1; Budget = 0;   Max = 1 },
    @{ Tag = 'b2_4r_z2_max';    Routes = 4; Zoom = 2; Budget = 0;   Max = 1 },
    @{ Tag = 'b3_4r_z3_gate';   Routes = 4; Zoom = 3; Budget = 0;   Max = 1 },
    @{ Tag = 'b4_4r_z4_nogate'; Routes = 4; Zoom = 4; Budget = 400; Max = 1 },
    # 极端：2 路 z=4（最大化，~100 Mpx）
    @{ Tag = 'c1_2r_z4_max';    Routes = 2; Zoom = 4; Budget = 400; Max = 1 }
)

$i = 0
foreach ($c in $cases) {
    $i++
    if ($Chunk -eq 1 -and $i -gt 5) { continue }
    if ($Chunk -eq 2 -and $i -le 5) { continue }
    if ($c.Max -eq 1) { $env:_3FC_BENCH_MAXIMIZE = '1' } else { Remove-Item Env:_3FC_BENCH_MAXIMIZE -ErrorAction SilentlyContinue }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $probe -Video $video `
        -Routes $c.Routes -Zoom $c.Zoom -Seconds $sec -BudgetMpx $c.Budget -Tag $c.Tag
    Write-Host ""
}
Write-Host "=== 矩阵结束（chunk=$Chunk）==="
