@echo off
set TASK=3FC-scrgb-scrgb5
schtasks /delete /tn "%TASK%" /f >/dev/null 2>&1
schtasks /create /tn "%TASK%" /tr "C:\PLAN\3FCompare\_3fc_tmp\run_scrgb_scrgb5.bat" /sc once /st 23:59 /f
echo CREATE_RC=%ERRORLEVEL%
schtasks /run /tn "%TASK%"
echo RUN_RC=%ERRORLEVEL%
