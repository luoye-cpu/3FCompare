# -*- coding: utf-8 -*-
"""VSR 矩阵轮：素材(8bit/10bit 静帧条) × 解码(CPU/GPU) × VSR(off/on)。

要拿到的读数（此前一个都没有）：
  · vsr: 那一行的 requested/nvidia/active/rejected/reason 与 src→tgt —— 内核自己怎么说；
  · 每个读点的 scl=（Shader=0 / D3D11VideoProcessor=1）—— 这一帧到底是谁产出的；
  · 静帧 + --at-frame 钉帧 ⇒ 两臂像素可比（上一轮就是被"两趟不同帧"骗过一次）。

前置门是硬编码事实：软解上传面没有 D3D11_BIND_DECODER ⇒ CPU 臂预期 reason=5
（UnsupportedSourceFormat），GPU 臂才可能 active=1。这两条各自都是一条判据，不是失败。
"""
import sys

OUT = r"C:\PLAN\3FCompare\tests\output\vsr"
BUILD = r"C:\PLAN\3FCompare\3fp\build"
FIX = r"C:\PLAN\3FCompare\tests\media\video\container-fx"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "vsr1"

MEDIA = [
    ("bars8", FIX + r"\bars_640x360_8bit.mkv"),
    ("bars10", FIX + r"\bars_640x360_10bit.mkv"),
]
DECODES = [("cpu", ""), ("gpu", "--gpu")]
VSRS = [("off", ""), ("on", "--vsr")]
PASSES = 2

L = [
    "@echo off",
    'call :main > "%s\\vsr_%s.log" 2>&1' % (OUT, SUFFIX),
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
    "set FFF3FP_SDR_SCRGB=",   # 用产品默认（Auto）
]

for p in range(1, PASSES + 1):
    for dname, dflag in DECODES:
        for vname, vflag in VSRS:
            for mname, media in MEDIA:
                tag = "%s_%s_%s_p%d" % (mname, dname, vname, p)
                log = "%s\\vp_%s_%s.txt" % (OUT, tag, SUFFIX)
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

path = OUT + "\\run_vsr_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

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
with open(OUT + "\\launch_vsr_%s.bat" % SUFFIX, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "planned arms =", len(MEDIA) * len(DECODES) * len(VSRS) * PASSES)
