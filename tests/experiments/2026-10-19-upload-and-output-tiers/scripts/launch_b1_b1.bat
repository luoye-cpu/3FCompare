@echo off
set TASK=3FC-b1-b1
schtasks /delete /tn "%TASK%" /f >nul 2>&1
schtasks /create /tn "%TASK%" /tr "C:\PLAN\3FCompare\_3fc_tmp\run_b1_b1.bat" /sc once /st 23:59 /f
echo CREATE_RC=%ERRORLEVEL%
schtasks /run /tn "%TASK%"
echo RUN_RC=%ERRORLEVEL%
