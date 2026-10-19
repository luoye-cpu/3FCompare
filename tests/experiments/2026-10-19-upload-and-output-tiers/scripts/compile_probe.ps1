# 编译 read_pixel_probe.cpp（只编，不跑）。
# 为什么走 PowerShell 而不是在 .bat 里 set INCLUDE：同一套 env 赋值在
# 3fp/build/run_pixel_probe.ps1 里是验过能编过的；.bat 版刚实测报
# `fatal error C1083: 无法打开包括文件: "windows.h"`（PROBE_CL_RC=2），
# 而 cl 一旦失败，BUILD 里 10-15 那份旧 exe 会原地顶上 —— 那是旧 ABI(440) 的件，
# 读快照会被内核判废，读数全是假的。所以这里失败必须让整轮不起。
$ErrorActionPreference = 'Stop'
$vc = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$kits = 'C:\Program Files (x86)\Windows Kits\10'
$env:PATH = "$vc\bin\Hostx64\x64;$env:PATH"
$env:INCLUDE = "$vc\include;$kits\Include\10.0.26100.0\ucrt;$kits\Include\10.0.26100.0\um;$kits\Include\10.0.26100.0\shared"
$env:LIB = "$vc\lib\x64;$kits\Lib\10.0.26100.0\ucrt\x64;$kits\Lib\10.0.26100.0\um\x64"
Set-Location 'C:\PLAN\3FCompare\3fp\build'
& cl /nologo /EHsc /std:c++20 /I 'C:\PLAN\3FCompare\3fp\kernel\FFF.Native' `
   /Fe:'C:\PLAN\3FCompare\_3fc_tmp\probe_bin\read_pixel_probe.exe' `
   'C:\PLAN\3FCompare\3fp\tools\read_pixel_probe.cpp' `
   /link 'C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.lib' user32.lib gdi32.lib
$rc = $LASTEXITCODE
$exe = Get-Item 'C:\PLAN\3FCompare\_3fc_tmp\probe_bin\read_pixel_probe.exe'
$dll = Get-Item 'C:\PLAN\3FCompare\3fp\build\FFF.Native.dll'
# 只看"比 DLL 新"是不够的：cl 失败时磁盘上留的是**上一次成功编出来的旧件**，
# 它照样可能比刚重编的内核新（本轮就撞上过：C2065 之后 CL_RC=2 而 probe_fresh 仍真）。
# 真正的判据是"比源文件与内核都新"。
$src = Get-Item 'C:\PLAN\3FCompare\3fp\tools\read_pixel_probe.cpp'
$hdr = Get-Item 'C:\PLAN\3FCompare\3fp\kernel\FFF.Native\3FP\Api\FFF.Player.Api.h'
$stamp = ($src.LastWriteTime, $hdr.LastWriteTime, $dll.LastWriteTime | Measure-Object -Maximum).Maximum
Write-Output ("CL_RC={0} probe={1:HH:mm:ss} need>{2:HH:mm:ss} probe_fresh={3}" -f `
    $rc, $exe.LastWriteTime, $stamp, ($exe.LastWriteTime -ge $stamp))
if ($rc -ne 0) { exit 1 }
if ($exe.LastWriteTime -lt $stamp) { Write-Output 'PROBE_STALE'; exit 2 }
exit 0
