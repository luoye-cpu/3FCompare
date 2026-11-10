# scRGB 路径的 VSR 像素闭环：编译 + 双臂实跑。
# 为什么单独成轮：显示器现在处于 HDR（PQ/BT.2020，10-18 已由 dxgi_display_probe 证实），
# vsr_scrgb_probe 的 MapToHdr 分支此前因为面板不开 HDR 而拿不到 outBits=16，
# 盘上那份 exe 是 10-15 的，链接的是当时的 FFF.Native.lib —— 必须重编再跑。
# 编译失败一定让整轮不起（旧 exe 会原地顶上，读数是假的）。
$ErrorActionPreference = 'Stop'
$Suffix = if ($args.Count -ge 1) { $args[0] } else { 'scrgb1' }
$Window = if ($args.Count -ge 2) { $args[1] } else { '1024x600' }

$vc = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$kits = 'C:\Program Files (x86)\Windows Kits\10'
$env:PATH = "$vc\bin\Hostx64\x64;$env:PATH"
# wrl/client.h 在 SDK 的 winrt 支下（不在 um），少这一条就 C1083 —— 上一轮就是这么死的。
$env:INCLUDE = "$vc\include;$kits\Include\10.0.26100.0\ucrt;$kits\Include\10.0.26100.0\um;$kits\Include\10.0.26100.0\shared;$kits\Include\10.0.26100.0\winrt"
$env:LIB = "$vc\lib\x64;$kits\Lib\10.0.26100.0\ucrt\x64;$kits\Lib\10.0.26100.0\um\x64"
# 沙箱按进程拒写 %TEMP%，探针建窗会被连累成假失败 —— 一律重定向进工作区。
$env:TMP = 'C:\PLAN\3FCompare\_3fc_tmp\wintmp'
$env:TEMP = $env:TMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

$kernel = 'C:\PLAN\3FCompare\3fp\kernel\FFF.Native'
$build = 'C:\PLAN\3FCompare\3fp\build'
Set-Location $build
# exe 落在 build 里：与 FFF.Native.dll 同目录，加载的就是被测内核，不靠 PATH 兜底。
$exe = Join-Path $build 'vsr_scrgb_probe.exe'
$src = 'C:\PLAN\3FCompare\3fp\tools\vsr_scrgb_probe.cpp'
$hdr = Join-Path $kernel '3FP\Api\FFF.Player.Api.h'
$dll = Join-Path $build 'FFF.Native.dll'

Write-Output "== 被测内核 =="
& certutil -hashfile $dll MD5
Write-Output ("DLL_mtime={0:yyyy-MM-dd HH:mm:ss}" -f (Get-Item $dll).LastWriteTime)

& cl /nologo /EHsc /std:c++20 /I $kernel /Fe:$exe $src `
   /link (Join-Path $kernel 'x64\Release\FFF.Native.lib') user32.lib gdi32.lib dxgi.lib
$rc = $LASTEXITCODE
$stamp = ($src, $hdr, $dll | ForEach-Object { (Get-Item $_).LastWriteTime } | Measure-Object -Maximum).Maximum
Write-Output ("CL_RC={0} exe={1:HH:mm:ss} need>{2:HH:mm:ss}" -f $rc, (Get-Item $exe).LastWriteTime, $stamp)
if ($rc -ne 0) { Write-Output 'ROUND_ABORT_COMPILE'; exit 3 }
if ((Get-Item $exe).LastWriteTime -lt $stamp) { Write-Output 'ROUND_ABORT_STALE'; exit 4 }

$media = 'C:\PLAN\3FCompare\tests\media\video\container-fx\bars_640x360_hevc10.mkv'
if (-not (Test-Path $media)) { Write-Output 'ROUND_ABORT_NOFIXTURE'; exit 5 }
# 两档配置 + 各两遍：
#   scrgb_on  = 不覆写 env（内核默认 sdrScRgbMode=1，10bit SDR 源应升 16bit scRGB）
#   scrgb_off = FFF3FP_SDR_SCRGB=0（按今天的档位政策，非 8bit 该落 10bit 档而不是 scRGB）
# 这一对比同时是"三档输出"政策的端到端读数，不只是 VSR 的读数。
$matrix = @(
    @{ Tag = 'scrgb_on';  Override = $null },
    @{ Tag = 'scrgb_off'; Override = '0' }
)
foreach ($cfg in $matrix) {
    if ($null -eq $cfg.Override) { Remove-Item Env:FFF3FP_SDR_SCRGB -ErrorAction SilentlyContinue }
    else { Set-Item Env:FFF3FP_SDR_SCRGB $cfg.Override }
    foreach ($pass in 1..2) {
        # 落盘目录按"配置×遍次"分：同一配置的两遍互相对撞就是噪声地板，
        # 没有这个对照，"平色区也变了 0.002"这种读数无法归因给 VSR 还是给抖动。
        $dump = Join-Path 'C:\PLAN\3FCompare\_3fc_tmp\dumps' "$($cfg.Tag)_p$pass"
        New-Item -ItemType Directory -Force -Path $dump | Out-Null
        Write-Output "======== $($cfg.Tag) pass $pass window=$Window env=${env:FFF3FP_SDR_SCRGB} dump=$dump ========"
        & $exe $media --sr-window $Window --dump $dump
        Write-Output "ARM_RC=$LASTEXITCODE"
        Get-ChildItem $dump -Filter '*.meta' | ForEach-Object { Write-Output ("meta {0}: {1}" -f $_.Name, ((Get-Content $_.FullName -Raw) -replace "`r?`n", ' ')) }
        Start-Sleep -Seconds 2
    }
    Remove-Item Env:FFF3FP_SDR_SCRGB -ErrorAction SilentlyContinue
}
Write-Output 'SCRGB_ROUND_DONE'
exit 0
