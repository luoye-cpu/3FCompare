# 过 DWM 那一臂：编译 + 实跑 dup_presented_probe。
# 编不过 / 建不起复制通道都必须让整轮不起，否则磁盘上的旧件或"复制臂其实没跑"会冒充读数。
# 第 1 实参 = 日志后缀，第 2 = 窗口 WxH，第 3 = 媒体路径（缺省为合成条）。
$ErrorActionPreference = 'Stop'
$Suffix = if ($args.Count -ge 1) { $args[0] } else { 'dup1' }
$Window = if ($args.Count -ge 2) { $args[1] } else { '1024x600' }
$Fixtures = 'C:\PLAN\3FCompare\tests\media\video\container-fx'
$Media = if ($args.Count -ge 3) { $args[2] } else { (Join-Path $Fixtures 'bars_640x360_hevc10.mkv') }

$vc = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$kits = 'C:\Program Files (x86)\Windows Kits\10'
$env:PATH = "$vc\bin\Hostx64\x64;$env:PATH"
# wrl/client.h 在 SDK 的 winrt 支下；本件不用 wrl，但保留同一条 include 形状。
$env:INCLUDE = "$vc\include;$kits\Include\10.0.26100.0\ucrt;$kits\Include\10.0.26100.0\um;$kits\Include\10.0.26100.0\shared;$kits\Include\10.0.26100.0\winrt"
$env:LIB = "$vc\lib\x64;$kits\Lib\10.0.26100.0\ucrt\x64;$kits\Lib\10.0.26100.0\um\x64"
$env:TMP = 'C:\PLAN\3FCompare\_3fc_tmp\wintmp'
$env:TEMP = $env:TMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null

$kernel = 'C:\PLAN\3FCompare\3fp\kernel\FFF.Native'
$build = 'C:\PLAN\3FCompare\3fp\build'
Set-Location $build
$exe = Join-Path $build 'dup_presented_probe.exe'
$src = 'C:\PLAN\3FCompare\3fp\tools\dup_presented_probe.cpp'
$hdr = Join-Path $kernel '3FP\Api\FFF.Player.Api.h'
$dll = Join-Path $build 'FFF.Native.dll'

Write-Output "== 被测内核 =="
& certutil -hashfile $dll MD5
Write-Output ("DLL_mtime={0:yyyy-MM-dd HH:mm:ss}" -f (Get-Item $dll).LastWriteTime)

# cl 的 stdout 先落文件再看退出码：直接接到 Select-Object 上会让管道被杀、退出码变 -1 假红。
& cl /nologo /EHsc /std:c++20 /utf-8 /I $kernel /Fe:$exe $src `
   /link (Join-Path $kernel 'x64\Release\FFF.Native.lib') user32.lib gdi32.lib dxgi.lib d3d11.lib `
   > (Join-Path $build 'cl_dup.txt') 2>&1
$rc = $LASTEXITCODE
if ($rc -ne 0) {
    Get-Content (Join-Path $build 'cl_dup.txt') | Select-String -Pattern 'error' | Select-Object -First 6
    Write-Output 'ROUND_ABORT_COMPILE'; exit 3
}
$stamp = ($src, $hdr, $dll | ForEach-Object { (Get-Item $_).LastWriteTime } | Measure-Object -Maximum).Maximum
Write-Output ("CL_RC={0} exe={1:HH:mm:ss} need>{2:HH:mm:ss}" -f $rc, (Get-Item $exe).LastWriteTime, $stamp)
if ((Get-Item $exe).LastWriteTime -lt $stamp) { Write-Output 'ROUND_ABORT_STALE'; exit 4 }

if (-not (Test-Path $Media)) { Write-Output "ROUND_ABORT_NOFIXTURE $Media"; exit 5 }
foreach ($pass in 1..2) {
    $dump = Join-Path 'C:\PLAN\3FCompare\_3fc_tmp\dumps' "${Suffix}_p$pass"
    New-Item -ItemType Directory -Force -Path $dump | Out-Null
    Write-Output "======== dup pass $pass window=$Window media=$Media dump=$dump ========"
    & $exe $Media --sr-window $Window --dump $dump
    $armRc = $LASTEXITCODE
    Write-Output "ARM_RC=$armRc"
    # 复制通道没建起来时探针返回 6：这一臂不成立，不能拿"跑完了"当绿。
    if ($armRc -eq 6) { Write-Output 'ARM_NO_DUPLICATION' }
    # 落盘件数必须数一遍：两臂 × 两个读者 = 4 块，少了就是这一臂没跑成。
    # 注意：@(x | Measure-Object).Count 恒为 1（把计数对象又包了一层数组）——上一版就是这样假红的。
    $blocks = @(Get-ChildItem $dump -Filter '*.f32').Count
    Write-Output "BLOCKS=$blocks/4"
    if ($blocks -lt 4) { Write-Output "ROUND_ABORT_BLOCKS $dump 只有 $blocks 块" }
    Get-ChildItem $dump -Filter '*.meta' | ForEach-Object {
        Write-Output ("meta {0}: {1}" -f $_.Name, ((Get-Content $_.FullName -Raw) -replace "`r?`n", ' '))
    }
    Start-Sleep -Seconds 2
}
Write-Output 'DUP_ROUND_DONE'
exit 0
