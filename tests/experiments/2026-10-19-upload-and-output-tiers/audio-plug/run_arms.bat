@echo off
rem Temporary runner for the seam verification arms. Not part of any project file.
rem CWD is 3fp\build so the kernel's delay-loaded FFmpeg dlls resolve, whichever
rem kernel copy (before/after) the arm names.
setlocal
cd /d "C:\PLAN\3FCompare\3fp\build"
set DIR=C:\PLAN\3FCompare\tests\experiments\2026-10-19-upload-and-output-tiers\audio-plug\run
set PROBE=%DIR%\plug_seam_probe.exe
set AFTER=C:\PLAN\3FCompare\3fp\build\FFF.Native.dll
set BEFORE=%DIR%\FFF.Native.before.dll
set BED=%DIR%\bed_7_1_4_12ch.wav
set MONO=%DIR%\mono_1_0.wav
set STEREO=%DIR%\stereo_2_0.wav

echo ===== A1 after.dll  12ch bed  NO plug =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 6 --mode none
echo ===== A2 BEFORE.dll 12ch bed  NO plug (equivalence control) =====
"%PROBE%" "%BEFORE%" "%BED%" --seconds 6 --mode none
echo ===== A3 after.dll  MONO  NO plug =====
"%PROBE%" "%AFTER%" "%MONO%" --seconds 6 --mode none
echo ===== A4 BEFORE.dll MONO  NO plug (equivalence control) =====
"%PROBE%" "%BEFORE%" "%MONO%" --seconds 6 --mode none
echo ===== B1 after.dll  MONO  plug declines the bed (supports=0) =====
"%PROBE%" "%AFTER%" "%MONO%" --seconds 6 --mode decline
echo ===== B2 after.dll  MONO  plug accepts then bails every frame =====
"%PROBE%" "%AFTER%" "%MONO%" --seconds 6 --mode bail
echo ===== B3 after.dll  MONO  plug accepts and writes the pair =====
"%PROBE%" "%AFTER%" "%MONO%" --seconds 6 --mode accept
echo ===== C1 after.dll  12ch bed  plug accepts and writes the pair =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 6 --mode accept
echo ===== C2 after.dll  12ch bed  plug declines the bed =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 6 --mode decline
echo ===== D1 after.dll  STEREO 2.0  plug installed: must never enter the seam =====
"%PROBE%" "%AFTER%" "%STEREO%" --seconds 6 --mode decline
echo ===== E1 after.dll  bad size/version must be refused =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 1 --mode badsize
echo RUN_RC=%ERRORLEVEL%
endlocal
