# TrueHDR 像素闭环：编译 + 双臂实跑（关 -> 开），两遍。
#
# 与 scrgb_vsr_round.ps1 的分工差别：那一轮是"配置 × 遍次"矩阵（三档输出政策），
# 本轮只一个配置（MapToHdr + D3D11 硬解 + 10bit SDR 夹具），因为 TrueHDR 的开关
# 就在探针进程内完成（SetVideoAutoHdr off/auto），不需要拆进程。
#
# 判据的前提是**夹具内容恒定**：跨臂差图只在静态夹具上成立（10-19 第 8 条坑：
# 动素材两臂读到不同帧，互相关读数已撤回）。默认夹具就是静态平色条。
#
# 编不过 / 落盘块数不够都必须让整轮不起，否则磁盘上的旧件或"某一臂其实没跑"会冒充读数。
# 第 1 实参 = 日志后缀，第 2 = 窗口 WxH，第 3 = 媒体路径。
$ErrorActionPreference = 'Stop'
$Suffix = if ($args.Count -ge 1) { $args[0] } else { 'thdr1' }
$Window = if ($args.Count -ge 2) { $args[1] } else { '1024x600' }
$Fixtures = 'C:\PLAN\3FCompare\tests\media\video\container-fx'
$Media = if ($args.Count -ge 3 -and $args[2]) { $args[2] } else { (Join-Path $Fixtures 'bars_640x360_hevc10.mkv') }

$vc = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$kits = 'C:\Program Files (x86)\Windows Kits\10'
$env:PATH = "$vc\bin\Hostx64\x64;$env:PATH"
# wrl/client.h 在 SDK 的 winrt 支下（不在 um），少这一条就 C1083 —— 10-19 实测。
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
$exe = Join-Path $build 'truehdr_pixel_probe.exe'
$src = 'C:\PLAN\3FCompare\3fp\tools\truehdr_pixel_probe.cpp'
$hdr = Join-Path $kernel '3FP\Api\FFF.Player.Api.h'
$dll = Join-Path $build 'FFF.Native.dll'

Write-Output "== 被测内核 =="
& certutil -hashfile $dll MD5
Write-Output ("DLL_mtime={0:yyyy-MM-dd HH:mm:ss}" -f (Get-Item $dll).LastWriteTime)

# cl 的 stdout 先落文件再看退出码：直接接到 Select-Object 上会让管道被杀、退出码变 -1 假红。
& cl /nologo /EHsc /std:c++20 /utf-8 /I $kernel /Fe:$exe $src `
   /link (Join-Path $kernel 'x64\Release\FFF.Native.lib') user32.lib gdi32.lib dxgi.lib `
   > (Join-Path $build 'cl_truehdr.txt') 2>&1
$rc = $LASTEXITCODE
if ($rc -ne 0) {
    Get-Content (Join-Path $build 'cl_truehdr.txt') | Select-String -Pattern 'error' | Select-Object -First 6
    Write-Output 'ROUND_ABORT_COMPILE'; exit 3
}
$stamp = ($src, $hdr, $dll | ForEach-Object { (Get-Item $_).LastWriteTime } | Measure-Object -Maximum).Maximum
Write-Output ("CL_RC={0} exe={1:HH:mm:ss} need>{2:HH:mm:ss}" -f $rc, (Get-Item $exe).LastWriteTime, $stamp)
if ((Get-Item $exe).LastWriteTime -lt $stamp) { Write-Output 'ROUND_ABORT_STALE'; exit 4 }

if (-not (Test-Path $Media)) { Write-Output "ROUND_ABORT_NOFIXTURE $Media"; exit 5 }
foreach ($pass in 1..2) {
    $dump = Join-Path 'C:\PLAN\3FCompare\_3fc_tmp\dumps' "${Suffix}_p$pass"
    New-Item -ItemType Directory -Force -Path $dump | Out-Null
    Write-Output "======== truehdr pass $pass window=$Window media=$Media dump=$dump ========"
    & $exe $Media --sr-window $Window --dump $dump
    $armRc = $LASTEXITCODE
    Write-Output "ARM_RC=$armRc"
    # 探针返回 6 = 像素回读失败，这一臂没有读数，不能拿"跑完了"当绿。
    if ($armRc -eq 6) { Write-Output 'ARM_NO_PIXELS' }
    # 落盘件数必须数一遍：两臂 × 两遍（含各臂噪声地板）= 4 块，少了就是某一臂没跑成。
    # 注意：@(x | Measure-Object).Count 恒为 1（把计数对象又包了一层数组）——用第二种实现重数。
    $blocks = @(Get-ChildItem $dump -Filter '*.f32').Count
    Write-Output "BLOCKS=$blocks/4"
    if ($blocks -lt 4) { Write-Output "ROUND_ABORT_BLOCKS $dump 只有 $blocks 块" }
    Get-ChildItem $dump -Filter '*.meta' | ForEach-Object {
        Write-Output ("meta {0}: {1}" -f $_.Name, ((Get-Content $_.FullName -Raw) -replace "`r?`n", ' '))
    }
    Start-Sleep -Seconds 2
}
Write-Output 'TRUEHDR_ROUND_DONE'
exit 0
