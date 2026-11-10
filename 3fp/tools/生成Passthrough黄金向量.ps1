#requires -Version 5.1
<#
.SYNOPSIS
  生成 Passthrough golden vector 集（docs/138 §5 P0-③、§7.9、§8）——模块 IEC 61937 打包器的唯一真源对照。

.DESCRIPTION
  素材说明（重要，外部堵点）：
  - 仓内没有真实 E-AC-3 JOC(Atmos) / TrueHD+Atmos 片源，属于 P0 外部堵点（docs/138 §7.8）——
    需要用户提供 Atmos 片源或授权取流（TODO：拿到片源后接入本脚本，ffprobe 验 profile 后追加用例）。
  - 本脚本用 ffmpeg lavfi 合成音（440Hz + 660Hz 双正弦 amix，s16/48k/stereo）替代真实样本：
    合成音足以锚定打包器的逐字节行为（帧边界、burst 周期、padding、字节序），
    但不携带 JOC/Atmos 元数据位；JOC/Atmos 分支的 golden 必须等真实片源。
  - 编码器：AC-3（ac3，384k）、E-AC-3（eac3，448k）、DTS core（实验编码器 dca，768k，
    失败则退 -c:a dts 别名；两试都失败如实记录并跳过）。
  - 额外产出：TrueHD（实验编码器 truehd，无 Atmos 位）——本构建的 ffmpeg-full（git-2026-10-11）含
    实验性 truehd 编码器，可给 P3 的 MAT 1.0 打包提供 golden；spdifdec 不能回读 TrueHD
    （docs/138 §2.5），该用例只做结构断言（burst 周期 61440B + Pa/Pb 头）。
    TODO(真实样本)：TrueHD+Atmos 与 DD+JOC 需外部片源。
  - golden burst：`ffmpeg -i <elementary> -c copy -f spdif <golden.spdif>`，输出即 s16le burst 序列，
    模块打包器输出必须与之逐字节一致（docs/138 §0.2/§4.2）。
  - 自洽检查：AC-3/E-AC-3/DTS 用 spdif demuxer 回读（-f spdif -i ... -c copy）并 diff 回原
    elementary 流（md5）——「FFmpeg spdifenc 打包 → spdifdec 解包 == 原流」作为 golden 可信性检查。
  - 每路素材再出 1s 短版：边界/攒帧用例（尤其 E-AC-3 攒帧与 EOF flush 行为、TrueHD MAT 环边界）。

  产物（默认 3fp\samples\passthrough\golden\）：
    - _src_10s.wav / _src_1s.wav           合成音源（中间产物，供复现）
    - ac3_384k[.ac3|.spdif] / ac3_384k_1s.*
    - eac3_448k[.eac3|.spdif] / eac3_448k_1s.*
    - dts_768k[.dts|.spdif]  / dts_768k_1s.*
    - truehd[.thd|.spdif]    / truehd_1s.*   （额外，无 Atmos 位）
    - manifest.json                          Lane B selftest 读取（见下）

  Lane B selftest 约定：环境变量 CTP_PASSTHROUGH_GOLDEN 指向本目录，读 manifest.json。
  manifest 顶层：{"version":1,"cases":[{"name","codec","elementary","golden","burstBytes",
  "linkRate","frames","md5Elementary","md5Golden",...}]}。
  codec 取值（docs/138 §4.2 契约头 codecId 语义，Lane B 需对齐此枚举；本脚本以此为准）：
    1=FFF3FP_PT_CODEC_AC3, 2=FFF3FP_PT_CODEC_EAC3, 3=FFF3FP_PT_CODEC_DTS,
    4=FFF3FP_PT_CODEC_TRUEHD, 5=FFF3FP_PT_CODEC_DTS_HD, 6=FFF3FP_PT_CODEC_MAT20, 7=FFF3FP_PT_CODEC_MAT21。

.PARAMETER OutputDir
  输出目录。默认 <repo>\3fp\samples\passthrough\golden。

.EXAMPLE
  pwsh -File tools\生成Passthrough黄金向量.ps1
.EXAMPLE
  pwsh -File tools\生成Passthrough黄金向量.ps1 -OutputDir D:\tmp\golden

.NOTES
  幂等：重复执行覆盖同名产物（全部 ffmpeg 调用带 -y；manifest 整体重写）。
  ffmpeg-full 的 DLL 与 exe 同目录，就地可跑；脚本仍把该目录前置到 PATH 兜底。
#>
param([string]$OutputDir = '')

$ErrorActionPreference = 'Stop'
$script:Log = New-Object System.Collections.Generic.List[string]

function Invoke-Ffmpeg {
    param([string[]]$FfArgs, [string]$Desc)
    $ffOut = & $ffmpeg @FfArgs 2>&1
    $script:Log.Add("[$Desc] OK (exit=$LASTEXITCODE)") | Out-Null
    foreach ($l in $ffOut) { $script:Log.Add("[$Desc] $l") | Out-Null }
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg 失败（$Desc）exit=$LASTEXITCODE；详见输出目录 _build-log.txt"
    }
}

function Get-Md5([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm MD5).Hash.ToLower()
}

function Get-FrameCount([string]$Path) {
    $v = & $ffprobe -v error -count_frames -select_streams a:0 `
        -show_entries stream=nb_read_frames -of default=nokey=1:noprint_wrappers=1 $Path 2>$null
    if (-not $v) { return $null }
    return [int64]($v | Select-Object -Last 1)
}

# ---- ffmpeg 定位 ----
$repoRoot  = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # 3fp/tools -> 3fp -> 仓库根
$ffmpegDir = Join-Path $repoRoot 'publish\PLAN\ffmpeg-full'
$ffmpeg    = Join-Path $ffmpegDir 'ffmpeg.exe'
$ffprobe   = Join-Path $ffmpegDir 'ffprobe.exe'
if (-not (Test-Path $ffmpeg))  { throw "未找到 ffmpeg：$ffmpeg" }
if (-not (Test-Path $ffprobe)) { throw "未找到 ffprobe：$ffprobe" }
$env:PATH = "$ffmpegDir;$env:PATH"   # DLL 就地解析；前置 PATH 兜底
$ffVersion = (& $ffprobe -version | Select-Object -First 1)
Write-Host "ffprobe: $ffVersion"

# ---- 输出目录 ----
if (-not $OutputDir) { $OutputDir = '..\samples\passthrough\golden' }   # 注意：空串给 Join-Path 会直接返回基目录
if ([IO.Path]::IsPathRooted($OutputDir)) { $out = $OutputDir }
else { $out = Join-Path $PSScriptRoot $OutputDir }
$out = [IO.Path]::GetFullPath($out)
New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Host "输出目录: $out"

# ================= 1) 素材合成（合成音替代真实样本，样本获取是外部堵点） =================
# TODO(外部样本)：接入真实 E-AC-3 JOC / TrueHD+Atmos 片源后，在此追加 "sample" 类用例
#                 （ffprobe 验 profile=DD+ Atmos / TrueHD+Atmos），替换/补充合成音。
$src10 = Join-Path $out '_src_10s.wav'
$src1  = Join-Path $out '_src_1s.wav'
$lavfiArgs = @('-y','-hide_banner','-loglevel','error',
    '-f','lavfi','-i','sine=frequency=440:duration={0}:sample_rate=48000',
    '-f','lavfi','-i','sine=frequency=660:duration={0}:sample_rate=48000',
    '-filter_complex','[0:a][1:a]amix=inputs=2:duration=first,aformat=sample_fmts=s16:sample_rates=48000:channel_layouts=stereo',
    '-c:a','pcm_s16le')
$args10 = @(); for ($i=0; $i -lt $lavfiArgs.Count; $i++) { $args10 += ($lavfiArgs[$i] -f 10) }; $args10 += $src10
$args1  = @(); for ($i=0; $i -lt $lavfiArgs.Count; $i++) { $args1  += ($lavfiArgs[$i] -f 1)  }; $args1  += $src1
Invoke-Ffmpeg $args10 '合成10s源(440+660Hz,s16/48k/stereo)'
Invoke-Ffmpeg $args1  '合成1s源'

# ================= 2) 编码矩阵 =================
# codec 值 = FFF3FP_PT_CODEC_*（见头注）；burstBytes：AC3=6144 / EAC3=24576 / DTS=实测(blocks<<7) / TrueHD=61440
$encodes = @(
    @{ name='ac3_384k';  ext='.ac3';  encArgs=@('-c:a','ac3','-b:a','384k');
       codec=1; codecName='FFF3FP_PT_CODEC_AC3';   linkRate=48000;  burst=6144;  outFmt='ac3' },
    @{ name='eac3_448k'; ext='.eac3'; encArgs=@('-c:a','eac3','-b:a','448k');
       codec=2; codecName='FFF3FP_PT_CODEC_EAC3';  linkRate=192000; burst=24576; outFmt='eac3' },
    # dca 是实验编码器（需 -strict -2）；不可用则退 -c:a dts；两试都失败如实记录并跳过
    @{ name='dts_768k';  ext='.dts';  encArgs=@('-c:a','dca','-strict','-2','-b:a','768k');
       altArgs=@('-c:a','dts','-strict','-2','-b:a','768k');
       codec=3; codecName='FFF3FP_PT_CODEC_DTS';   linkRate=48000;  burst=-1;    outFmt='dts' },
    # 额外产出：实验 truehd 编码器（无 Atmos 位；spdifdec 不能回读，仅结构断言）
    @{ name='truehd';    ext='.thd';  encArgs=@('-c:a','truehd','-strict','-2');
       codec=4; codecName='FFF3FP_PT_CODEC_TRUEHD'; linkRate=192000; burst=61440; outFmt=$null }
)

$cases = @()
$failedEncoders = @()

foreach ($e in $encodes) {
    $encArgs = $e.encArgs
    # dca 试编 1s 探路；失败换别名重试；都失败则跳过该编码并记录
    $probeFile = Join-Path $out ("_probe" + $e.ext)
    $ok = $false; $lastErr = ''
    foreach ($tryArgs in @($e.encArgs, $e.altArgs)) {
        if (-not $tryArgs) { continue }
        try {
            Invoke-Ffmpeg (@('-y','-hide_banner','-loglevel','error','-i',$src1) + $tryArgs + @($probeFile)) ("试编 " + ($tryArgs -join ' '))
            if ((Get-Item $probeFile).Length -gt 0) { $encArgs = $tryArgs; $ok = $true; break }
        } catch {
            $lastErr = $_.Exception.Message
            $script:Log.Add("[试编失败] $($e.name) $($tryArgs -join ' '): $lastErr") | Out-Null
        }
    }
    if (-not $ok) {
        $failedEncoders += "$($e.name)（两试皆败：$lastErr）——如实记录，未产出 golden"
        Write-Warning "编码器不可用，跳过 $($e.name)：$lastErr"
        # TODO(dts 编码器)：dca/dts 均不可用时需另寻 DTS core 参照流（外部样本或 HeSitt/dcaenc 工具链）
        continue
    }
    Remove-Item -LiteralPath $probeFile -Force -ErrorAction SilentlyContinue

    foreach ($suffix in @('', '_1s')) {
        $src = if ($suffix -eq '') { $src10 } else { $src1 }
        $elemName = "$($e.name)$suffix$($e.ext)"
        $elemPath = Join-Path $out $elemName
        $goldenName = "$($e.name)$suffix.spdif"
        $goldenPath = Join-Path $out $goldenName

        Invoke-Ffmpeg (@('-y','-hide_banner','-loglevel','error','-i',$src) + $encArgs + @($elemPath)) "编码 $elemName"
        Invoke-Ffmpeg @('-y','-hide_banner','-loglevel','error','-i',$elemPath,'-c','copy','-f','spdif',$goldenPath) "golden burst $goldenName"

        $elemBytes = (Get-Item -LiteralPath $elemPath).Length
        $goldenBytes = (Get-Item -LiteralPath $goldenPath).Length
        $frames = Get-FrameCount $elemPath

        # burstBytes 终值与校验
        $burst = $e.burst
        $notes = @()
        if ($burst -lt 0) {
            # DTS：实测 golden 字节数/帧数（blocks<<7 族）；必须整除
            if ($frames -and ($goldenBytes % $frames -eq 0)) {
                $burst = [int64]($goldenBytes / $frames)
                $notes += "burstBytes=实测 goldenBytes/frames"
                if (@(2048,4096,8192,16384) -notcontains $burst) { $notes += "警告:实测值不在 DTS 周期族{2048,4096,8192,16384}" }
            } else {
                $burst = $null; $notes += "警告:goldenBytes 不能被 frames 整除，burstBytes 未定（需人工核对 spdifenc DTS 攒帧）"
            }
        } else {
            if ($goldenBytes % $burst -ne 0) { $notes += "警告:goldenBytes($goldenBytes) 不是 burst 周期($burst)整数倍——EOF flush/边界行为，Lane B 对照用例" }
            else { $notes += "goldenBytes=burst 周期整数倍" }
        }

        # ---- 自洽检查 ----
        $selfCheck = 'n/a'; $selfDetail = ''
        if ($e.outFmt) {
            # spdifdec 回读（AC-3/E-AC-3/DTS 支持）→ md5 diff 回原 elementary 流
            $rtPath = Join-Path $out ("_rt_" + "$($e.name)$suffix" + $e.ext)
            Invoke-Ffmpeg @('-y','-hide_banner','-loglevel','error','-f','spdif','-i',$goldenPath,'-c','copy','-f',$e.outFmt,$rtPath) "自洽回读 $($e.name)$suffix"
            $md5Rt = Get-Md5 $rtPath
            $md5Elem = Get-Md5 $elemPath
            if ($md5Rt -eq $md5Elem) { $selfCheck = 'pass'; $selfDetail = "spdifdec 回读 md5 == 原 elementary" }
            else { $selfCheck = 'FAIL'; $selfDetail = "spdifdec 回读 md5 不一致（回读=$md5Rt）" }
            Remove-Item -LiteralPath $rtPath -Force -ErrorAction SilentlyContinue
        } else {
            # TrueHD/MAT1.0：spdifdec 不支持回读（docs/138 §2.5）——结构断言。
            # FFmpeg spdifenc.c 真值：burst 头 Pa=0xF872 Pb=0x4E1F（第二同步字）Pc=0x16(TrueHD)
            #   Pd=61424(MAT_FRAME_SIZE)；payload 按 s16le 摆放（逻辑 BE 字节对在文件中两两互换，
            #   见 docs/138 §2.5 字节序条目）；MAT codes：start 20B@payload+0 / middle 12B@+30708 / end 16B@+61408。
            $matStart = [byte[]](0x07,0x9E,0x00,0x03,0x84,0x01,0x01,0x01,0x80,0x00,0x56,0xA5,0x3B,0xF4,0x81,0x83,0x49,0x80,0x77,0xE0)
            $matEnd   = [byte[]](0xC3,0xC2,0xC0,0xC4,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x97,0x11)
            # s16le 文件里字节两两互换后的期望序列
            $matStartLe = [byte[]]::new(20); $matEndLe = [byte[]]::new(16)
            for ($i = 0; $i -lt 20; $i += 2) { $matStartLe[$i] = $matStart[$i+1]; $matStartLe[$i+1] = $matStart[$i] }
            for ($i = 0; $i -lt 16; $i += 2) { $matEndLe[$i]   = $matEnd[$i+1];   $matEndLe[$i+1]   = $matEnd[$i] }
            $gb = [IO.File]::ReadAllBytes($goldenPath)
            $headOk  = ($gb[0] -eq 0x72 -and $gb[1] -eq 0xF8 -and $gb[2] -eq 0x1F -and $gb[3] -eq 0x4E -and
                        $gb[4] -eq 0x16 -and $gb[5] -eq 0x00 -and $gb[6] -eq 0xF0 -and $gb[7] -eq 0xEF)
            $startOk = $true; for ($i = 0; $i -lt 20; $i++) { if ($gb[8+$i] -ne $matStartLe[$i]) { $startOk = $false; break } }
            $endOk   = $true; $endOff = 8 + 61408; for ($i = 0; $i -lt 16; $i++) { if ($gb[$endOff+$i] -ne $matEndLe[$i]) { $endOk = $false; break } }
            $selfCheck = 'structural-only'
            $selfDetail = "spdifdec 不支持 TrueHD；结构断言: burst头72F8-1F4E-1600-F0EF=$headOk, mat_start@8=$startOk, mat_end@$endOff=$endOk, goldenBytes%61440=$($goldenBytes % 61440)"
            if (-not ($headOk -and $startOk -and $endOk -and ($goldenBytes % 61440 -eq 0))) { $selfCheck = 'FAIL-structural'; $notes += '警告:TrueHD 结构断言未过' }
        }

        $cases += [ordered]@{
            name           = "$($e.name)$suffix"
            codec          = $e.codec
            codecName      = $e.codecName
            elementary     = $elemName
            golden         = $goldenName
            elementaryBytes= $elemBytes
            goldenBytes    = $goldenBytes
            burstBytes     = $burst
            linkRate       = $e.linkRate
            frames         = $frames
            md5Elementary  = (Get-Md5 $elemPath)
            md5Golden      = (Get-Md5 $goldenPath)
            selfCheck      = $selfCheck
            notes          = ($notes -join '; ')
        }
        Write-Host ("  产出 {0,-22} elem={1,9}B golden={2,9}B burst={3,6} link={4} frames={5} 自洽={6}" -f `
            "$($e.name)$suffix", $elemBytes, $goldenBytes, $burst, $e.linkRate, $frames, $selfCheck)
        if ($selfDetail) { Write-Host "      ↳ $selfDetail" }
    }
}

# ================= manifest =================
$manifest = [ordered]@{
    version       = 1
    generatedAt   = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    generator     = '3fp\tools\生成Passthrough黄金向量.ps1'
    ffprobeVersion= $ffVersion
    source        = 'lavfi 合成音（sine440+sine660 amix, s16/48k/stereo）——真实 E-AC-3 JOC/TrueHD+Atmos 片源为外部堵点（docs/138 §7.8），待用户提供'
    codecEnum     = [ordered]@{
        FFF3FP_PT_CODEC_AC3 = 1; FFF3FP_PT_CODEC_EAC3 = 2; FFF3FP_PT_CODEC_DTS = 3
        FFF3FP_PT_CODEC_TRUEHD = 4; FFF3FP_PT_CODEC_DTS_HD = 5; FFF3FP_PT_CODEC_MAT20 = 6; FFF3FP_PT_CODEC_MAT21 = 7
    }
    note          = 'Lane B selftest: $env:CTP_PASSTHROUGH_GOLDEN 指向本目录读 manifest.json；模块打包器输出须与 *.spdif 逐字节一致'
    cases         = @($cases)
}
$json = ConvertTo-Json -InputObject $manifest -Depth 5
# 无 BOM UTF-8（JSON 规范兼容；PS 默认 Out-File 会带 BOM，某些 C++ JSON 解析器不吞 BOM）
[IO.File]::WriteAllText((Join-Path $out 'manifest.json'), $json, [Text.UTF8Encoding]::new($false))
Write-Host "`nmanifest.json 已写入（$($cases.Count) 条用例）"

if ($failedEncoders.Count) {
    Write-Host '编码器失败记录:'
    $failedEncoders | ForEach-Object { Write-Host "  - $_" }
}

Write-Host ''
Write-Host "Lane B selftest 接入：`$env:CTP_PASSTHROUGH_GOLDEN = '$out'"
[IO.File]::WriteAllLines((Join-Path $out '_build-log.txt'), $script:Log, [Text.UTF8Encoding]::new($false))
