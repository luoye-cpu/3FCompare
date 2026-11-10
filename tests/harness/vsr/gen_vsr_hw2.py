# -*- coding: utf-8 -*-
"""VSR 硬解臂矩阵：同一张静帧 SMPTE 条，编成**能被硬解**的 h264（lossless），
CPU 与 GPU 两臂各跑 vsr off/on。

为什么必须换容器/编码器：上一轮（vsr3）的 ffv1 条无论 CPU/GPU 臂都报 reason=5
（UnsupportedSourceFormat）——ffv1 交出来的是**平面** yuv420p（layout 1），
而 VP 那道门写的是 `sourceInputLayout_ != 2` 直接 false；换 h264 才有 NV12 这条路。

拿到的判据：active / reason / src→tgt / 每个读点的 scl=（Shader=0，D3D11VideoProcessor=1）。
scl 是"这一帧到底谁产的"应用内证据，不依赖任何驱动谎报。
"""
import os
import sys

OUT = r"C:\PLAN\3FCompare\tests\output\vsr"
BUILD = r"C:\PLAN\3FCompare\3fp\build"
FIX = os.path.join(OUT, "fixtures")
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "vsrhw3"

MEDIA = [("hevc10", os.path.join(FIX, "bars_640x360_hevc10.mkv"))]
DECODES = [("cpu", ""), ("gpu", "--gpu")]
VSRS = [("off", ""), ("on", "--vsr")]
PASSES = 2

L = [
    "@echo off",
    'call :main > "{}\\vsr_{}.log" 2>&1'.format(OUT, SUFFIX),
    "exit /b %ERRORLEVEL%",
    ":main",
    "chcp 65001 >nul",
    "setlocal enabledelayedexpansion",
    "set HTTP_PROXY=", "set HTTPS_PROXY=", "set http_proxy=", "set https_proxy=",
    "set FTP_PROXY=", "set ftp_proxy=",
    r"set TMP=C:\PLAN\3FCompare\_3fc_tmp\wintmp",
    "set TEMP=%TMP%",
    "cd /d " + BUILD,
    'certutil -hashfile "{}\\FFF.Native.dll" MD5'.format(BUILD),
    'powershell -NoProfile -ExecutionPolicy Bypass -File "{}\\compile_probe.ps1"'.format(OUT),
    "echo PROBE_COMPILE_RC=%ERRORLEVEL%",
    'if not "%ERRORLEVEL%"=="0" (echo ROUND_ABORT_PROBE_COMPILE & exit /b 3)',
    "set FFF3FP_SDR_SCRGB=",
]

for p in range(1, PASSES + 1):
    for dname, dflag in DECODES:
        for vname, vflag in VSRS:
            for mname, media in MEDIA:
                tag = "%s_%s_%s_p%d" % (mname, dname, vname, p)
                log = os.path.join(OUT, "vp_%s_%s.txt" % (tag, SUFFIX))
                L += [
                    "set ALIVE=0",
                    'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c',
                    'echo WITNESS %s decode=%s vsr=%s p%d alive=!ALIVE!' % (mname, dname, vname, p),
                    'del "%s" >nul 2>&1' % log,
                    'read_pixel_probe.exe "%s" %s %s --at-frame 12 --grid 8 > "%s" 2>&1'
                    % (media, dflag, vflag, log),
                    'echo ARM_EXIT %s exit=%%ERRORLEVEL%%' % tag,
                    'if exist "%s" (find "open:" < "%s" >nul && echo ARM_RAN %s || echo ARM_NOOPEN %s) else (echo ARM_INVALID %s)'
                    % (log, log, tag, tag, tag),
                    'type "%s"' % log,
                    "timeout /t 2 /nobreak >nul",
                ]

L += ["echo VSR_DONE", "exit /b 0"]

path = os.path.join(OUT, "run_vsr_%s.bat" % SUFFIX)
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"
assert b.count(b"\x08") == 0, "批处理里混进了退格符（上次就是 heredoc 吞了 \\b）"

task = "3FC-vsr-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
with open(os.path.join(OUT, "launch_vsr_%s.bat" % SUFFIX), "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "planned arms =", len(MEDIA) * len(DECODES) * len(VSRS) * PASSES)
