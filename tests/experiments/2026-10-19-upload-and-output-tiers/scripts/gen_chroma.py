# -*- coding: utf-8 -*-
"""色度定点判据轮（周期量法）：静止棋盘夹具 + 过均值次数，配对与分叉一起跑。

par1/par2 的教训都收在这里：
  * 素材静止 ⇒ "两臂同一张画"由构造保证，不再依赖 position100ns
    （实测它是 wall-clock：6 秒素材回读 12.7~12.9 s，且与 presented≈98~107 帧不自洽）
  * 量具换成"沿一条线数跨过均值的次数" ⇒ 位移差一档，次数就差一倍，
    格均值那套（par2 分叉对照只有 0.0074，比噪声底还小）对周期根本不敏感
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
BUILD = r"C:\PLAN\3FCompare\3fp\build"
FIX = OUT + r"\fixtures"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "chr1"
W, H = 640, 360

MEDIA = []
for fmt, tag in (("yuv410p", "410"), ("yuv440p", "440"), ("yuv420p", "420")):
    for pattern in ("col", "row"):
        MEDIA.append(("a%s%s" % (tag, pattern), FIX + r"\pat_%s_%s_%dx%d.mkv" % (fmt, pattern, W, H)))
        MEDIA.append(("r%s%s" % (tag, pattern), FIX + r"\pat_%s_%s_%dx%d_up444.mkv" % (fmt, pattern, W, H)))
PASSES = 2

L = [
    "@echo off",
    'call :main > "%s\\chr_%s.log" 2>&1' % (OUT, SUFFIX),
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
    "set FFF3FP_SDR_SCRGB=0",
    "set FFF3FP_DECODER=",
    # 整轮结束时再量一次内核件哈希：另有 agent 在改这棵树，读数必须能归因到同一代二进制
    'certutil -hashfile "%s\\FFF.Native.dll" MD5' % BUILD,
]

for p in range(1, PASSES + 1):
    for label, media in MEDIA:
        log = "%s\\scan_%s_p%d_%s.txt" % (OUT, label, p, SUFFIX)
        L += [
            'echo WITNESS %s p%d' % (label, p),
            'del "%s" >nul 2>&1' % log,
            '"%s\\probe_bin\\read_pixel_probe.exe" "%s" --at-frame 30 --scan --grid 8 > "%s" 2>&1' % (OUT, media, log),
            'echo ARM_EXIT %s p%d exit=%%ERRORLEVEL%%' % (label, p),
            'if exist "%s" (find "scan: H B=" < "%s" >nul && echo ARM_RAN %s p%d || echo ARM_NOSCAN %s p%d) else (echo ARM_INVALID %s p%d)'
            % (log, log, label, p, label, p, label, p),
            "timeout /t 1 /nobreak >nul",
        ]

L += ["echo CHROMA_DONE", 'certutil -hashfile "%s\\FFF.Native.dll" MD5' % BUILD, "exit /b 0"]

path = OUT + "\\run_chroma_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-chr-" + SUFFIX
launch = ["@echo off", "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%", 'schtasks /run /tn "%TASK%"', "echo RUN_RC=%ERRORLEVEL%"]
with open(OUT + "\\launch_chroma_%s.bat" % SUFFIX, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(MEDIA) * PASSES)
