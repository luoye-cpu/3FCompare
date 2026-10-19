@echo off
call :main > "C:\PLAN\3FCompare\_3fc_tmp\par_par2.log" 2>&1
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
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a410 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p1_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv410p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p1_par2.txt" 2>&1
echo ARM_EXIT a410 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p1_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p1_par2.txt" >nul && echo ARM_RAN a410 p1 || echo ARM_NOGRID a410 p1) else (echo ARM_INVALID a410 p1)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r410 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p1_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv410p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p1_par2.txt" 2>&1
echo ARM_EXIT r410 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p1_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p1_par2.txt" >nul && echo ARM_RAN r410 p1 || echo ARM_NOGRID r410 p1) else (echo ARM_INVALID r410 p1)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a440 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p1_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv440p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p1_par2.txt" 2>&1
echo ARM_EXIT a440 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p1_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p1_par2.txt" >nul && echo ARM_RAN a440 p1 || echo ARM_NOGRID a440 p1) else (echo ARM_INVALID a440 p1)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r440 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p1_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv440p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p1_par2.txt" 2>&1
echo ARM_EXIT r440 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p1_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p1_par2.txt" >nul && echo ARM_RAN r440 p1 || echo ARM_NOGRID r440 p1) else (echo ARM_INVALID r440 p1)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a420 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p1_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p1_par2.txt" 2>&1
echo ARM_EXIT a420 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p1_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p1_par2.txt" >nul && echo ARM_RAN a420 p1 || echo ARM_NOGRID a420 p1) else (echo ARM_INVALID a420 p1)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r420 p1 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p1_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv420p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p1_par2.txt" 2>&1
echo ARM_EXIT r420 p1 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p1_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p1_par2.txt" >nul && echo ARM_RAN r420 p1 || echo ARM_NOGRID r420 p1) else (echo ARM_INVALID r420 p1)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a410 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p2_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv410p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p2_par2.txt" 2>&1
echo ARM_EXIT a410 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p2_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p2_par2.txt" >nul && echo ARM_RAN a410 p2 || echo ARM_NOGRID a410 p2) else (echo ARM_INVALID a410 p2)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r410 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p2_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv410p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p2_par2.txt" 2>&1
echo ARM_EXIT r410 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p2_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p2_par2.txt" >nul && echo ARM_RAN r410 p2 || echo ARM_NOGRID r410 p2) else (echo ARM_INVALID r410 p2)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a440 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p2_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv440p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p2_par2.txt" 2>&1
echo ARM_EXIT a440 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p2_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p2_par2.txt" >nul && echo ARM_RAN a440 p2 || echo ARM_NOGRID a440 p2) else (echo ARM_INVALID a440 p2)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r440 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p2_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv440p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p2_par2.txt" 2>&1
echo ARM_EXIT r440 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p2_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p2_par2.txt" >nul && echo ARM_RAN r440 p2 || echo ARM_NOGRID r440 p2) else (echo ARM_INVALID r440 p2)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a420 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p2_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p2_par2.txt" 2>&1
echo ARM_EXIT a420 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p2_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p2_par2.txt" >nul && echo ARM_RAN a420 p2 || echo ARM_NOGRID a420 p2) else (echo ARM_INVALID a420 p2)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r420 p2 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p2_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv420p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p2_par2.txt" 2>&1
echo ARM_EXIT r420 p2 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p2_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p2_par2.txt" >nul && echo ARM_RAN r420 p2 || echo ARM_NOGRID r420 p2) else (echo ARM_INVALID r420 p2)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a410 p3 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p3_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv410p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p3_par2.txt" 2>&1
echo ARM_EXIT a410 p3 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p3_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a410_p3_par2.txt" >nul && echo ARM_RAN a410 p3 || echo ARM_NOGRID a410 p3) else (echo ARM_INVALID a410 p3)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r410 p3 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p3_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv410p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p3_par2.txt" 2>&1
echo ARM_EXIT r410 p3 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p3_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r410_p3_par2.txt" >nul && echo ARM_RAN r410 p3 || echo ARM_NOGRID r410 p3) else (echo ARM_INVALID r410 p3)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a440 p3 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p3_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv440p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p3_par2.txt" 2>&1
echo ARM_EXIT a440 p3 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p3_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a440_p3_par2.txt" >nul && echo ARM_RAN a440 p3 || echo ARM_NOGRID a440 p3) else (echo ARM_INVALID a440 p3)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r440 p3 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p3_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv440p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p3_par2.txt" 2>&1
echo ARM_EXIT r440 p3 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p3_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r440_p3_par2.txt" >nul && echo ARM_RAN r440 p3 || echo ARM_NOGRID r440 p3) else (echo ARM_INVALID r440 p3)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS a420 p3 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p3_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p3_par2.txt" 2>&1
echo ARM_EXIT a420 p3 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p3_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_a420_p3_par2.txt" >nul && echo ARM_RAN a420 p3 || echo ARM_NOGRID a420 p3) else (echo ARM_INVALID a420 p3)
timeout /t 2 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c
echo WITNESS r420 p3 probes_already_alive=!ALIVE!
del "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p3_par2.txt" >nul 2>&1
read_pixel_probe.exe "C:\PLAN\3FCompare\_3fc_tmp\fixtures\ref_yuv420p_up444_1920x1080.mkv" --at-frame 60 --grid 6 > "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p3_par2.txt" 2>&1
echo ARM_EXIT r420 p3 exit=%ERRORLEVEL%
if exist "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p3_par2.txt" (find "grid: outBits" < "C:\PLAN\3FCompare\_3fc_tmp\grid_r420_p3_par2.txt" >nul && echo ARM_RAN r420 p3 || echo ARM_NOGRID r420 p3) else (echo ARM_INVALID r420 p3)
timeout /t 2 /nobreak >nul
echo PARITY_DONE
exit /b 0
