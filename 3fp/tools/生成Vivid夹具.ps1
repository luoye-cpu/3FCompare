# 生成 Vivid 双基线验证夹具
#
# 背景：FFF.Native 的 Vivid 峰值派生按**基线**分支 ——
#   const bool vividBaselineIsHlg = streamState_.format == FFF3FPHdrFormat::Hlg;
#   if (!vividBaselineIsHlg) { peak = maximum_maxrgb * 10000; }   // 仅 PQ 基线
#
# 而仓库里原本**只有 HLG 基线**的 Vivid 样本（hdr_vivid_h265_sample.hevc，
# color_transfer=arib-std-b67），于是 `!vividBaselineIsHlg` 的**真分支从未被执行**，
# 仅由代码文本等价性保证 —— 这是 docs/63 §七.2 明确标记的覆盖缺口。
#
# 本脚本用 bitstream filter 只改 VUI 的 transfer_characteristics，
# **保留 Vivid 的 CUVA 005.1 SEI**，从而造出 PQ 基线的 Vivid 样本。
# （实测：hevc_metadata=transfer_characteristics=16 不改动 SEI。）
#
# 用法：pwsh -File 3fp/tools/生成Vivid夹具.ps1
#
# 产物（testmedia 被 gitignore，故用脚本而非提交二进制）：
#   $dir\hdr_vivid_pq_baseline.hevc   PQ(smpte2084) + Vivid SEI   <- 覆盖真分支
#   （HLG 样本 hdr_vivid_h265_sample.hevc 已存在于该目录）

$ErrorActionPreference = 'Stop'

$ff = 'C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\ffmpeg.exe'
$fp = 'C:\PLAN\ffmpegPictureUI\publish\PLAN\ffmpeg-full\ffprobe.exe'
$dir = 'C:\PLAN\3FCompare\testmedia\media\hdrmeta'
$hlg = Join-Path $dir 'hdr_vivid_h265_sample.hevc'
$pq = Join-Path $dir 'hdr_vivid_pq_baseline.hevc'

if (-not (Test-Path $hlg)) {
    throw "缺少 HLG 基线源样本：$hlg（PQ 版本由它派生）"
}

Write-Host '=== 生成 PQ 基线 Vivid 夹具 ===' -ForegroundColor Cyan
Write-Host "源: $hlg"

# 只改 VUI 的 transfer_characteristics=16(smpte2084)；-c copy 保证 SEI 原样保留。
& $ff -y -loglevel error -i $hlg -c copy -bsf:v hevc_metadata=transfer_characteristics=16 $pq
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $pq)) { throw '生成失败' }

Write-Host '=== 校验 ===' -ForegroundColor Cyan
$transfer = (& $fp -v error -select_streams v:0 -show_entries stream=color_transfer -of default=nw=1 $pq 2>&1) -join ''
Write-Host "  transfer      : $transfer"

$sideData = (& $fp -v error -select_streams v:0 -show_frames -read_intervals '%+#1' `
    -show_entries frame=side_data_list -of default=nw=1 $pq 2>&1) -join "`n"
$hasVivid = $sideData -match 'CUVA 005\.1 2021 \(Vivid\)'
Write-Host "  Vivid SEI     : $(if ($hasVivid) { '存在' } else { '**缺失**' })"

if ($transfer -notmatch 'smpte2084') { throw "transfer 未改为 PQ：$transfer" }
if (-not $hasVivid) { throw 'Vivid SEI 在转码中丢失，夹具无法覆盖 Vivid 分支' }

Write-Host ''
Write-Host '夹具就绪：' -ForegroundColor Green
Write-Host "  HLG 基线: $hlg  (期望 peak=4000，修复前为 10000)"
Write-Host "  PQ  基线: $pq  (期望 peak=10000，修复前后均应不变)"
Write-Host ''
Write-Host '验证命令（工作目录 3fp\build）：' -ForegroundColor DarkGray
Write-Host "  .\read_pixel_probe.exe `"$hlg`" --hdr" -ForegroundColor DarkGray
Write-Host "  .\read_pixel_probe.exe `"$pq`" --hdr" -ForegroundColor DarkGray
