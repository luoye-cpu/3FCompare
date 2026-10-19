@echo off
call :main > "C:\PLAN\3FCompare\_3fc_tmp\scrgb_scrgb3.log" 2>&1
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
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p plain scrgb=0 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p plain scrgb=0 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s0_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p plain scrgb=0 p1 || echo ARM_NOOPEN s8_yuv420p plain scrgb=0 p1) else (echo ARM_INVALID s8_yuv420p plain scrgb=0 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=0
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc plain scrgb=0 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc plain scrgb=0 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s0_scrgb3.txt" >nul && echo ARM_RAN s10_hevc plain scrgb=0 p1 || echo ARM_NOOPEN s10_hevc plain scrgb=0 p1) else (echo ARM_INVALID s10_hevc plain scrgb=0 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p plain scrgb=1 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p plain scrgb=1 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s1_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p plain scrgb=1 p1 || echo ARM_NOOPEN s8_yuv420p plain scrgb=1 p1) else (echo ARM_INVALID s8_yuv420p plain scrgb=1 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p1_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc plain scrgb=1 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc plain scrgb=1 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s1_scrgb3.txt" >nul && echo ARM_RAN s10_hevc plain scrgb=1 p1 || echo ARM_NOOPEN s10_hevc plain scrgb=1 p1) else (echo ARM_INVALID s10_hevc plain scrgb=1 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p1_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=0
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p fhdr scrgb=0 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p fhdr scrgb=0 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s0_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p fhdr scrgb=0 p1 || echo ARM_NOOPEN s8_yuv420p fhdr scrgb=0 p1) else (echo ARM_INVALID s8_yuv420p fhdr scrgb=0 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=0
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc fhdr scrgb=0 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc fhdr scrgb=0 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s0_scrgb3.txt" >nul && echo ARM_RAN s10_hevc fhdr scrgb=0 p1 || echo ARM_NOOPEN s10_hevc fhdr scrgb=0 p1) else (echo ARM_INVALID s10_hevc fhdr scrgb=0 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p fhdr scrgb=1 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p fhdr scrgb=1 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s1_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p fhdr scrgb=1 p1 || echo ARM_NOOPEN s8_yuv420p fhdr scrgb=1 p1) else (echo ARM_INVALID s8_yuv420p fhdr scrgb=1 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p1_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc fhdr scrgb=1 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc fhdr scrgb=1 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s1_scrgb3.txt" >nul && echo ARM_RAN s10_hevc fhdr scrgb=1 p1 || echo ARM_NOOPEN s10_hevc fhdr scrgb=1 p1) else (echo ARM_INVALID s10_hevc fhdr scrgb=1 p1)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p1_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=0
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p plain scrgb=0 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p plain scrgb=0 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s0_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p plain scrgb=0 p2 || echo ARM_NOOPEN s8_yuv420p plain scrgb=0 p2) else (echo ARM_INVALID s8_yuv420p plain scrgb=0 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=0
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc plain scrgb=0 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc plain scrgb=0 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s0_scrgb3.txt" >nul && echo ARM_RAN s10_hevc plain scrgb=0 p2 || echo ARM_NOOPEN s10_hevc plain scrgb=0 p2) else (echo ARM_INVALID s10_hevc plain scrgb=0 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p plain scrgb=1 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p plain scrgb=1 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s1_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p plain scrgb=1 p2 || echo ARM_NOOPEN s8_yuv420p plain scrgb=1 p2) else (echo ARM_INVALID s8_yuv420p plain scrgb=1 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_plain_p2_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc plain scrgb=1 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4"  > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc plain scrgb=1 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s1_scrgb3.txt" >nul && echo ARM_RAN s10_hevc plain scrgb=1 p2 || echo ARM_NOOPEN s10_hevc plain scrgb=1 p2) else (echo ARM_INVALID s10_hevc plain scrgb=1 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_plain_p2_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=0
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p fhdr scrgb=0 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p fhdr scrgb=0 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s0_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p fhdr scrgb=0 p2 || echo ARM_NOOPEN s8_yuv420p fhdr scrgb=0 p2) else (echo ARM_INVALID s8_yuv420p fhdr scrgb=0 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=0
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc fhdr scrgb=0 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s0_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s0_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc fhdr scrgb=0 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s0_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s0_scrgb3.txt" >nul && echo ARM_RAN s10_hevc fhdr scrgb=0 p2 || echo ARM_NOOPEN s10_hevc fhdr scrgb=0 p2) else (echo ARM_INVALID s10_hevc fhdr scrgb=0 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s0_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s8_yuv420p fhdr scrgb=1 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s8_yuv420p fhdr scrgb=1 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s1_scrgb3.txt" >nul && echo ARM_RAN s8_yuv420p fhdr scrgb=1 p2 || echo ARM_NOOPEN s8_yuv420p fhdr scrgb=1 p2) else (echo ARM_INVALID s8_yuv420p fhdr scrgb=1 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s8_yuv420p_fhdr_p2_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
set FFF3FP_SDR_SCRGB=1
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS s10_hevc fhdr scrgb=1 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s1_scrgb3.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4" --force-hdr > "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s1_scrgb3.txt" 2>&1
echo ARM_EXIT s10_hevc fhdr scrgb=1 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s1_scrgb3.txt" (find "open:" < "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s1_scrgb3.txt" >nul && echo ARM_RAN s10_hevc fhdr scrgb=1 p2 || echo ARM_NOOPEN s10_hevc fhdr scrgb=1 p2) else (echo ARM_INVALID s10_hevc fhdr scrgb=1 p2)
type "C:\PLAN\3FCompare\_3fc_tmp\pix_s10_hevc_fhdr_p2_s1_scrgb3.txt"
timeout /t 2 /nobreak >nul
echo SCRGB_DONE
exit /b 0
