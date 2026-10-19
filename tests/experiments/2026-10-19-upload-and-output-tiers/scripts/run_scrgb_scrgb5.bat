@echo off
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\PLAN\3FCompare\_3fc_tmp\scrgb_vsr_round.ps1" scrgb5 1024x600 > "C:\PLAN\3FCompare\_3fc_tmp\scrgb_scrgb5.log" 2>&1
echo ROUND_PS_RC=%ERRORLEVEL%
