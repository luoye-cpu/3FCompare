@echo off
rem Temporary: compile the bed-render-plug seam probe. Not part of any project file.
setlocal
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" > "C:\PLAN\3FCompare\tests\experiments\2026-10-19-upload-and-output-tiers\audio-plug\run\vcvars.log" 2>&1
if not errorlevel 1 goto compile
echo VCVARS_RC=%ERRORLEVEL%
exit /b 1
:compile
cd /d "C:\PLAN\3FCompare\tests\experiments\2026-10-19-upload-and-output-tiers\audio-plug"
if not exist run\obj mkdir run\obj
cl /nologo /EHsc /std:c++20 /O2 /MD /I "C:\PLAN\3FCompare\3fp\kernel\FFF.Native" /Ferun\plug_seam_probe.exe /Forun\obj\ plug_seam_probe.cpp user32.lib ole32.lib >run\cl.log 2>&1
echo CL_RC=%ERRORLEVEL%
if exist run\plug_seam_probe.exe (echo PROBE_EXE=present) else (echo PROBE_EXE=absent)
endlocal
