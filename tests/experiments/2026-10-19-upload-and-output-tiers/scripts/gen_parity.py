# -*- coding: utf-8 -*-
"""B1 的像素判据轮：同画面、两臂网格对撞。

为什么必须钉帧：不钉帧时同一素材两趟在同一坐标能读到 0.00000 与 0.39697（实测），
那种"两臂对比"量的不是几何而是时序。这里用 FFF3FP_SeekFrame(60) 把两臂钉到同一张画，
并把回读的 pos100ns / presented 打印出来当见证，不靠假设。

对撞设计（三条，缺一不可）：
  配对 a410 ↔ r410  ：yuv410p 直传 vs FFmpeg 自己按描述符 neighbor 扩成 4:4:4 的同画面
  配对 a440 ↔ r440  ：同上，位移 (0,1)
  对照 a420 ↔ r420  ：早就在表里的 yuv420p，用它标定"这个判据本来的噪声底"
  分叉 a410 ↔ a440  ：两枚新行互比，必须明显不等，否则判据是空的
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
BUILD = r"C:\PLAN\3FCompare\3fp\build"
FIX = OUT + r"\fixtures"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "par1"

MEDIA = [
    ("a410", FIX + r"\fx_yuv410p_1920x1080.mkv"),
    ("r410", FIX + r"\ref_yuv410p_up444_1920x1080.mkv"),
    ("a440", FIX + r"\fx_yuv440p_1920x1080.mkv"),
    ("r440", FIX + r"\ref_yuv440p_up444_1920x1080.mkv"),
    ("a420", FIX + r"\fx_yuv420p_1920x1080.mkv"),
    ("r420", FIX + r"\ref_yuv420p_up444_1920x1080.mkv"),
]
PASSES = 3

L = [
    "@echo off",
    'call :main > "%s\\par_%s.log" 2>&1' % (OUT, SUFFIX),
    "exit /b %ERRORLEVEL%",
    ":main",
    "chcp 65001 >nul",
    "setlocal enabledelayedexpansion",
    "set HTTP_PROXY=", "set HTTPS_PROXY=", "set http_proxy=", "set https_proxy=",
    "set FTP_PROXY=", "set ftp_proxy=",
    r"set TMP=C:\PLAN\3FCompare\_3fc_tmp\wintmp",
    "set TEMP=%TMP%",
    "cd /d " + BUILD,
    'certutil -hashfile "%s\\FFF.Native.dll" MD5' % BUILD,
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%s\\compile_probe.ps1"' % OUT,
    "echo PROBE_COMPILE_RC=%ERRORLEVEL%",
    'if not "%ERRORLEVEL%"=="0" (echo ROUND_ABORT_PROBE_COMPILE & exit /b 3)',
    # 8bit 素材本来就不该被升档；把 Auto 关掉，免得档位与两臂比较混在一起
    "set FFF3FP_SDR_SCRGB=0",
    "set FFF3FP_DECODER=",
]

for p in range(1, PASSES + 1):
    for label, media in MEDIA:
        log = "%s\\grid_%s_p%d_%s.txt" % (OUT, label, p, SUFFIX)
        L += [
            "set ALIVE=0",
            'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c',
            'echo WITNESS %s p%d probes_already_alive=!ALIVE!' % (label, p),
            'del "%s" >nul 2>&1' % log,
            'read_pixel_probe.exe "%s" --at-frame 60 --grid 6 > "%s" 2>&1' % (media, log),
            'echo ARM_EXIT %s p%d exit=%%ERRORLEVEL%%' % (label, p),
            'if exist "%s" (find "grid: outBits" < "%s" >nul && echo ARM_RAN %s p%d || echo ARM_NOGRID %s p%d) else (echo ARM_INVALID %s p%d)'
            % (log, log, label, p, label, p, label, p),
            "timeout /t 2 /nobreak >nul",
        ]

L += ["echo PARITY_DONE", "exit /b 0"]

path = OUT + "\\run_parity_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-par-" + SUFFIX
launch = ["@echo off", "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%", 'schtasks /run /tn "%TASK%"', "echo RUN_RC=%ERRORLEVEL%"]
lpath = OUT + "\\launch_parity_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(MEDIA) * PASSES)
