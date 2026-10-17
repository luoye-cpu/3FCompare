<#
.SYNOPSIS
    验缩放锚定.ps1 —— 跑 zoom_anchor_probe 并把退出码变成判定（docs/79 P0）。

.DESCRIPTION
    为什么要有：本区域的图片探针此前**只打印不判定**（zoom_anchor_probe 无论漂移多大都
    return 0），`rebuild_probes.ps1` 只编译不运行，`FFF.Player.Tests` 里图片/缩放断言 0 条
    ⇒ 缩放这一路没有任何带牙的门。本脚本把"跑 + 判 + 留痕"接成一条命令。

    退出码语义（与 zoom_anchor_probe.cpp 抬头一致）：
      0 = PASS 锚定成立
      3 = FAIL 锚点漂移超容差
      4 = **无牙 / 未判定** —— 这不是通过，是"这一轮量不出结论"（媒体取到平色区、
          整图已入窗没有平移余量、或回读通道不通）。脚本会显式印「未判定」。
      1/2 = 建立失败 / 用法错

    ⚠ 读数归因：探针链接的是盘上的 FFF.Native.dll。跑之前请确认那件 DLL 是哪个树建出来的
      —— 内核工作树脏时，本脚本的读数归因于**混合树**，不能写成"HEAD 的行为"。

.PARAMETER Media
    要量的图片。默认用 docs/79 P1 造的大面积条纹夹具。

.PARAMETER Anchor
    光标锚点（客户端归一化），逗号分隔。默认 0.75,0.30。

.EXAMPLE
    pwsh -File 3fp/tools/验缩放锚定.ps1
    pwsh -File 3fp/tools/验缩放锚定.ps1 -Media <路径> -Anchor 0.5,0.5
#>
param(
    [string]$Media = '',
    [string]$Anchor = '0.75,0.30',
    [string]$ProbeSourceDir = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tools = Join-Path $repo '3fp\tools'
$build = Join-Path $repo '3fp\build'
$probeExe = Join-Path $build 'zoom_anchor_probe.exe'

if ([string]::IsNullOrWhiteSpace($Media)) {
    $Media = Join-Path $repo '3fp\samples\media\edge\09c_stripe_12000x9000.jpg'
}
if (-not (Test-Path -LiteralPath $Media)) {
    Write-Host "缺媒体夹具：$Media" -ForegroundColor Red
    Write-Host '先跑 bash 3fp/samples/tools/generate_samples.sh 造 09c/09d/09e（docs/79 P1）。'
    exit 2
}

if (-not $SkipBuild) {
    Write-Host '[1/3] 编译探针' -ForegroundColor Cyan
    # 本机 python 会被拦成 exit 49，用 py。
    & py (Join-Path $tools 'build_probe.py') zoom_anchor_probe
    if ($LASTEXITCODE -ne 0) { Write-Host '探针编译失败' -ForegroundColor Red; exit 2 }
}

Write-Host '[2/3] 跑锚定缩放' -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $probeExe)) { Write-Host "缺 $probeExe" -ForegroundColor Red; exit 2 }
$logDir = Join-Path $build 'zoom_gate_logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$log = Join-Path $logDir "anchor_$stamp.log"

& $probeExe $Media --anchor $Anchor 2>&1 | Tee-Object -FilePath $log
$code = $LASTEXITCODE

Write-Host '[3/3] 判定' -ForegroundColor Cyan
$verdict = switch ($code) {
    0 { @{ Text = 'PASS 锚定成立'; Color = 'Green' } }
    3 { @{ Text = 'FAIL 锚点漂移超容差'; Color = 'Red' } }
    4 { @{ Text = '未判定：判据无牙（不是通过）'; Color = 'DarkYellow' } }
    default { @{ Text = "未取得读数（探针退出码 $code）"; Color = 'Red' } }
}
Write-Host ("媒体 = {0}" -f (Split-Path -Leaf $Media))
Write-Host ("锚点 = {0}" -f $Anchor)
Write-Host ("退出码 = {0}  判定 = {1}" -f $code, $verdict.Text) -ForegroundColor $verdict.Color
Write-Host ("留痕 = {0}" -f $log)
exit $code
