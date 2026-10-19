@echo off
set TASK=3FC-chr-chr2
schtasks /delete /tn "%TASK%" /f >nul 2>&1
schtasks /create /tn "%TASK%" /tr "C:\PLAN\3FCompare\_3fc_tmp\run_chroma_chr2.bat" /sc once /st 23:59 /f
echo CREATE_RC=%ERRORLEVEL%
schtasks /run /tn "%TASK%"
echo RUN_RC=%ERRORLEVEL%
