@echo off
rem Temporary runner, round 2: mid-stream install/clear, refusal-keeps-previous,
rem and the repeated no-plug arms that give the equivalence criterion a spread to
rem be measured against. Not part of any project file.
setlocal
cd /d "C:\PLAN\3FCompare\3fp\build"
set DIR=C:\PLAN\3FCompare\tests\experiments\2026-10-19-upload-and-output-tiers\audio-plug\run
set PROBE=%DIR%\plug_seam_probe.exe
set AFTER=C:\PLAN\3FCompare\3fp\build\FFF.Native.dll
set BEFORE=%DIR%\FFF.Native.before.dll
set BED=%DIR%\bed_7_1_4_12ch.wav
set MONO=%DIR%\mono_1_0.wav

echo ===== S1 after.dll MONO plug installed at t=1.0s, cleared at t=3.0s =====
"%PROBE%" "%AFTER%" "%MONO%" --seconds 6 --mode accept --install-at 1 --clear-at 3
echo ===== S2 after.dll 12ch bed plug installed at t=1.0s, cleared at t=3.0s =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 6 --mode accept --install-at 1 --clear-at 3
echo ===== R1 after.dll bad size refused while a good plug is live =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 5 --mode badafter --bad-at 2
echo ===== N1 after.dll 12ch NO plug, repeat 1 =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 6 --mode none
echo ===== N2 after.dll 12ch NO plug, repeat 2 =====
"%PROBE%" "%AFTER%" "%BED%" --seconds 6 --mode none
echo ===== N3 BEFORE.dll 12ch NO plug, repeat 1 =====
"%PROBE%" "%BEFORE%" "%BED%" --seconds 6 --mode none
echo ===== N4 BEFORE.dll 12ch NO plug, repeat 2 =====
"%PROBE%" "%BEFORE%" "%BED%" --seconds 6 --mode none
echo ===== N5 after.dll MONO NO plug, repeat 1 =====
"%PROBE%" "%AFTER%" "%MONO%" --seconds 6 --mode none
echo ===== N6 BEFORE.dll MONO NO plug, repeat 1 =====
"%PROBE%" "%BEFORE%" "%MONO%" --seconds 6 --mode none
echo RUN_RC=%ERRORLEVEL%
endlocal
