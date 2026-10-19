@echo off
call :main > "C:\PLAN\3FCompare\_3fc_tmp\b1_b1.log" 2>&1
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
copy /Y "C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Native.dll"
echo KERNEL_COPY_RC=%ERRORLEVEL%
certutil -hashfile "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Native.dll" MD5
cd /d C:\PLAN\3FCompare\_3fc_tmp\player_run
set FFF3FP_PERF_SECONDS=20
set FFF3FP_PERF_FULLSCREEN=1
set FFF3FP_PERF_WIDTH=
set FFF3FP_PERF_HEIGHT=
set FFF3FP_UPLOAD_COPY_MODE=row
set FFF3FP_UPLOAD_RING=2
set FFF3FP_MAX_FRAME_LATENCY=2
set FFF3FP_ADAPTIVE_DOWNSCALE=
set FFF3FP_UPLOAD_FORCE_DYNAMIC=
set FFF3FP_DECODER=
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS ref_yuv420p p1 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\ref_yuv420p_p1_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ref_yuv420p_p1_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ref_yuv420p_p1_b1.csv" (echo ARM_RAN ref_yuv420p p1) else (echo ARM_INVALID ref_yuv420p p1)
timeout /t 3 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS new_yuv440p p1 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv440p_p1_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv440p_p1_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv440p_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv440p_p1_b1.csv" (echo ARM_RAN new_yuv440p p1) else (echo ARM_INVALID new_yuv440p p1)
timeout /t 3 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS ctrl_bgr0 p1 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\ctrl_bgr0_p1_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ctrl_bgr0_p1_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_bgr0_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ctrl_bgr0_p1_b1.csv" (echo ARM_RAN ctrl_bgr0 p1) else (echo ARM_INVALID ctrl_bgr0 p1)
timeout /t 3 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS new_yuv410p p1 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv410p_p1_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv410p_p1_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv410p_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv410p_p1_b1.csv" (echo ARM_RAN new_yuv410p p1) else (echo ARM_INVALID new_yuv410p p1)
timeout /t 3 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS ref_yuv420p p2 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\ref_yuv420p_p2_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ref_yuv420p_p2_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv420p_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ref_yuv420p_p2_b1.csv" (echo ARM_RAN ref_yuv420p p2) else (echo ARM_INVALID ref_yuv420p p2)
timeout /t 3 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS new_yuv440p p2 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv440p_p2_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv440p_p2_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv440p_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv440p_p2_b1.csv" (echo ARM_RAN new_yuv440p p2) else (echo ARM_INVALID new_yuv440p p2)
timeout /t 3 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS ctrl_bgr0 p2 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\ctrl_bgr0_p2_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ctrl_bgr0_p2_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_bgr0_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\ctrl_bgr0_p2_b1.csv" (echo ARM_RAN ctrl_bgr0 p2) else (echo ARM_INVALID ctrl_bgr0 p2)
timeout /t 3 /nobreak >nul
set ALIVE=0
for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\System32\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c
echo WITNESS new_yuv410p p2 alive_players=!ALIVE!
set FFF3FP_PERF_OUT=C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv410p_p2_b1.csv
del "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv410p_p2_b1.csv" >nul 2>&1
start /wait "" "C:\PLAN\3FCompare\_3fc_tmp\player_run\FFF.Player.exe" "C:\PLAN\3FCompare\_3fc_tmp\fixtures\fx_yuv410p_1920x1080.mkv"
if exist "C:\PLAN\3FCompare\_3fc_tmp\player_csv\new_yuv410p_p2_b1.csv" (echo ARM_RAN new_yuv410p p2) else (echo ARM_INVALID new_yuv410p p2)
timeout /t 3 /nobreak >nul
echo B1_DONE
exit /b 0
