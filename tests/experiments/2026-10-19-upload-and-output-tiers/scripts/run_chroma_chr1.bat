@echo off
call :main > "C:\PLAN\3FCompare\_3fc_tmp\chr_chr1.log" 2>&1
exit /b %ERRORLEVEL%
:main
chcp 65001 >nul
setlocal enabledelayedexpansion
set HTTP_PROXY=
set HTTPS_PROXY=
set http_proxy=
set https_proxy=
set FTP_PROXY=
set ftp_proxy=
set TMP=C:\PLAN\3FCompare\_3fc_tmp\wintmp
set TEMP=%TMP%
cd /d C:\PLAN\3FCompare\3fp\build
certutil -hashfile "C:\PLAN\3FCompare\3fp\build\FFF.Native.dll" MD5
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\PLAN\3FCompare\_3fc_tmp\compile_probe.ps1"
echo PROBE_COMPILE_RC=%ERRORLEVEL%
if not "%ERRORLEVEL%"=="0" (echo ROUND_ABORT_PROBE_COMPILE & exit /b 3)
set FFF3FP_SDR_SCRGB=0
set FFF3FP_DECODER=
certutil -hashfile "C:\PLAN\3FCompare\3fp\build\FFF.Native.dll" MD5
echo WITNESS a410col p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_col_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p1_chr1.txt" 2>&1
echo ARM_EXIT a410col p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p1_chr1.txt" >nul && echo ARM_RAN a410col p1 || echo ARM_NOSCAN a410col p1) else (echo ARM_INVALID a410col p1)
timeout /t 1 /nobreak >nul
echo WITNESS r410col p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_col_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p1_chr1.txt" 2>&1
echo ARM_EXIT r410col p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p1_chr1.txt" >nul && echo ARM_RAN r410col p1 || echo ARM_NOSCAN r410col p1) else (echo ARM_INVALID r410col p1)
timeout /t 1 /nobreak >nul
echo WITNESS a410row p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_row_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p1_chr1.txt" 2>&1
echo ARM_EXIT a410row p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p1_chr1.txt" >nul && echo ARM_RAN a410row p1 || echo ARM_NOSCAN a410row p1) else (echo ARM_INVALID a410row p1)
timeout /t 1 /nobreak >nul
echo WITNESS r410row p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_row_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p1_chr1.txt" 2>&1
echo ARM_EXIT r410row p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p1_chr1.txt" >nul && echo ARM_RAN r410row p1 || echo ARM_NOSCAN r410row p1) else (echo ARM_INVALID r410row p1)
timeout /t 1 /nobreak >nul
echo WITNESS a440col p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_col_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p1_chr1.txt" 2>&1
echo ARM_EXIT a440col p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p1_chr1.txt" >nul && echo ARM_RAN a440col p1 || echo ARM_NOSCAN a440col p1) else (echo ARM_INVALID a440col p1)
timeout /t 1 /nobreak >nul
echo WITNESS r440col p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_col_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p1_chr1.txt" 2>&1
echo ARM_EXIT r440col p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p1_chr1.txt" >nul && echo ARM_RAN r440col p1 || echo ARM_NOSCAN r440col p1) else (echo ARM_INVALID r440col p1)
timeout /t 1 /nobreak >nul
echo WITNESS a440row p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_row_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p1_chr1.txt" 2>&1
echo ARM_EXIT a440row p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p1_chr1.txt" >nul && echo ARM_RAN a440row p1 || echo ARM_NOSCAN a440row p1) else (echo ARM_INVALID a440row p1)
timeout /t 1 /nobreak >nul
echo WITNESS r440row p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_row_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p1_chr1.txt" 2>&1
echo ARM_EXIT r440row p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p1_chr1.txt" >nul && echo ARM_RAN r440row p1 || echo ARM_NOSCAN r440row p1) else (echo ARM_INVALID r440row p1)
timeout /t 1 /nobreak >nul
echo WITNESS a420col p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_col_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p1_chr1.txt" 2>&1
echo ARM_EXIT a420col p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p1_chr1.txt" >nul && echo ARM_RAN a420col p1 || echo ARM_NOSCAN a420col p1) else (echo ARM_INVALID a420col p1)
timeout /t 1 /nobreak >nul
echo WITNESS r420col p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_col_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p1_chr1.txt" 2>&1
echo ARM_EXIT r420col p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p1_chr1.txt" >nul && echo ARM_RAN r420col p1 || echo ARM_NOSCAN r420col p1) else (echo ARM_INVALID r420col p1)
timeout /t 1 /nobreak >nul
echo WITNESS a420row p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_row_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p1_chr1.txt" 2>&1
echo ARM_EXIT a420row p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p1_chr1.txt" >nul && echo ARM_RAN a420row p1 || echo ARM_NOSCAN a420row p1) else (echo ARM_INVALID a420row p1)
timeout /t 1 /nobreak >nul
echo WITNESS r420row p1
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p1_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_row_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p1_chr1.txt" 2>&1
echo ARM_EXIT r420row p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p1_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p1_chr1.txt" >nul && echo ARM_RAN r420row p1 || echo ARM_NOSCAN r420row p1) else (echo ARM_INVALID r420row p1)
timeout /t 1 /nobreak >nul
echo WITNESS a410col p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_col_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p2_chr1.txt" 2>&1
echo ARM_EXIT a410col p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a410col_p2_chr1.txt" >nul && echo ARM_RAN a410col p2 || echo ARM_NOSCAN a410col p2) else (echo ARM_INVALID a410col p2)
timeout /t 1 /nobreak >nul
echo WITNESS r410col p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_col_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p2_chr1.txt" 2>&1
echo ARM_EXIT r410col p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r410col_p2_chr1.txt" >nul && echo ARM_RAN r410col p2 || echo ARM_NOSCAN r410col p2) else (echo ARM_INVALID r410col p2)
timeout /t 1 /nobreak >nul
echo WITNESS a410row p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_row_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p2_chr1.txt" 2>&1
echo ARM_EXIT a410row p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a410row_p2_chr1.txt" >nul && echo ARM_RAN a410row p2 || echo ARM_NOSCAN a410row p2) else (echo ARM_INVALID a410row p2)
timeout /t 1 /nobreak >nul
echo WITNESS r410row p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv410p_row_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p2_chr1.txt" 2>&1
echo ARM_EXIT r410row p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r410row_p2_chr1.txt" >nul && echo ARM_RAN r410row p2 || echo ARM_NOSCAN r410row p2) else (echo ARM_INVALID r410row p2)
timeout /t 1 /nobreak >nul
echo WITNESS a440col p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_col_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p2_chr1.txt" 2>&1
echo ARM_EXIT a440col p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a440col_p2_chr1.txt" >nul && echo ARM_RAN a440col p2 || echo ARM_NOSCAN a440col p2) else (echo ARM_INVALID a440col p2)
timeout /t 1 /nobreak >nul
echo WITNESS r440col p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_col_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p2_chr1.txt" 2>&1
echo ARM_EXIT r440col p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r440col_p2_chr1.txt" >nul && echo ARM_RAN r440col p2 || echo ARM_NOSCAN r440col p2) else (echo ARM_INVALID r440col p2)
timeout /t 1 /nobreak >nul
echo WITNESS a440row p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_row_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p2_chr1.txt" 2>&1
echo ARM_EXIT a440row p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a440row_p2_chr1.txt" >nul && echo ARM_RAN a440row p2 || echo ARM_NOSCAN a440row p2) else (echo ARM_INVALID a440row p2)
timeout /t 1 /nobreak >nul
echo WITNESS r440row p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv440p_row_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p2_chr1.txt" 2>&1
echo ARM_EXIT r440row p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r440row_p2_chr1.txt" >nul && echo ARM_RAN r440row p2 || echo ARM_NOSCAN r440row p2) else (echo ARM_INVALID r440row p2)
timeout /t 1 /nobreak >nul
echo WITNESS a420col p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_col_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p2_chr1.txt" 2>&1
echo ARM_EXIT a420col p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a420col_p2_chr1.txt" >nul && echo ARM_RAN a420col p2 || echo ARM_NOSCAN a420col p2) else (echo ARM_INVALID a420col p2)
timeout /t 1 /nobreak >nul
echo WITNESS r420col p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_col_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p2_chr1.txt" 2>&1
echo ARM_EXIT r420col p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r420col_p2_chr1.txt" >nul && echo ARM_RAN r420col p2 || echo ARM_NOSCAN r420col p2) else (echo ARM_INVALID r420col p2)
timeout /t 1 /nobreak >nul
echo WITNESS a420row p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_row_640x360.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p2_chr1.txt" 2>&1
echo ARM_EXIT a420row p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_a420row_p2_chr1.txt" >nul && echo ARM_RAN a420row p2 || echo ARM_NOSCAN a420row p2) else (echo ARM_INVALID a420row p2)
timeout /t 1 /nobreak >nul
echo WITNESS r420row p2
del "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p2_chr1.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\pat_yuv420p_row_640x360_up444.mkv" --at-frame 30 --scan --grid 8 > "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p2_chr1.txt" 2>&1
echo ARM_EXIT r420row p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p2_chr1.txt" (find "scan: H B=" < "C:\PLAN\3FCompare\_3fc_tmp\scan_r420row_p2_chr1.txt" >nul && echo ARM_RAN r420row p2 || echo ARM_NOSCAN r420row p2) else (echo ARM_INVALID r420row p2)
timeout /t 1 /nobreak >nul
echo CHROMA_DONE
certutil -hashfile "C:\PLAN\3FCompare\3fp\build\FFF.Native.dll" MD5
exit /b 0
