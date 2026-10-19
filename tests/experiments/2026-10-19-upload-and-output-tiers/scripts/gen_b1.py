# -*- coding: utf-8 -*-
"""B1 对照轮：新入表的两枚平面格式 vs 仍在 CPU 路上的对照 vs 早就直传的参考。

同一份二进制、同一几何（1920x1080）、交替 2 趟，四枚素材：
  yuv420p   早就在表里（layout 1 直传）—— 下界参考
  yuv410p   本轮新入表（shifts 2/2）—— 之前落 default 走 CPU
  yuv440p   本轮新入表（shifts 0/1）—— 同上
  bgr0      **对照组**：仍然不在表里，仍走 CPU sws
这样 sw_ms 一列同时给出"直传=0"与"CPU 路=~3.7 ms"，不需要跨轮借数。

⚠ 已知不能靠这轮证明的东西（写在这里，别让读数夸它）：
chroma 位移的正确性**不能用 bytes_per_upload 自检**——那个数就是用同一组位移算出来的，
自己证自己是恒等式（本仓踩过）。所以这轮只判"有没有走上直传路、有没有崩、帧率与耗时"，
**色彩/几何等价性另需一条像素判据**。
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
FIX = OUT + r"\fixtures"
CSVDIR = OUT + r"\player_csv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "b1"

MEDIA = [
    ("ref_yuv420p", FIX + r"\fx_yuv420p_1920x1080.mkv"),
    ("new_yuv440p", FIX + r"\fx_yuv440p_1920x1080.mkv"),
    ("ctrl_bgr0", FIX + r"\fx_bgr0_1920x1080.mkv"),
    ("new_yuv410p", FIX + r"\fx_yuv410p_1920x1080.mkv"),
]
PASSES = 2

L = [
    "@echo off",
    'call :main > "%s\\b1_%s.log" 2>&1' % (OUT, SUFFIX),
    "exit /b %ERRORLEVEL%",
    ":main",
    "chcp 65001 >nul",
    "setlocal enabledelayedexpansion",
    "set HTTP_PROXY=", "set HTTPS_PROXY=", "set http_proxy=", "set https_proxy=",
    "set FTP_PROXY=", "set ftp_proxy=",
    r"set TMP=C:\PLAN\3FCompare\_3fc_tmp\wintmp",
    "set TEMP=%TMP%",
    'copy /Y "%s" "%s\\FFF.Native.dll"' % (KERNEL, RUN),
    "echo KERNEL_COPY_RC=%ERRORLEVEL%",
    'certutil -hashfile "%s\\FFF.Native.dll" MD5' % RUN,
    "cd /d " + RUN,
    "set FFF3FP_PERF_SECONDS=20",
    "set FFF3FP_PERF_FULLSCREEN=1",
    "set FFF3FP_PERF_WIDTH=",
    "set FFF3FP_PERF_HEIGHT=",
    "set FFF3FP_UPLOAD_COPY_MODE=row",
    "set FFF3FP_UPLOAD_RING=2",
    "set FFF3FP_MAX_FRAME_LATENCY=2",
    "set FFF3FP_ADAPTIVE_DOWNSCALE=",
    "set FFF3FP_UPLOAD_FORCE_DYNAMIC=",
    "set FFF3FP_DECODER=",
]

for p in range(1, PASSES + 1):
    for label, media in MEDIA:
        csvp = "%s\\%s_p%d_%s.csv" % (CSVDIR, label, p, SUFFIX)
        errp = "%s\\%s_p%d_%s.stderr.txt" % (OUT, label, p, SUFFIX)
        L += [
            "set ALIVE=0",
            'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
            'echo WITNESS %s p%d alive_players=!ALIVE!' % (label, p),
            'set FFF3FP_PERF_OUT=%s' % csvp,
            'del "%s" >nul 2>&1' % csvp,
            'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, media),
            'if exist "%s" (echo ARM_RAN %s p%d) else (echo ARM_INVALID %s p%d)'
            % (csvp, label, p, label, p),
            "timeout /t 3 /nobreak >nul",
        ]

L += ["echo B1_DONE", "exit /b 0"]

path = OUT + "\\run_b1_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-b1-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_b1_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(MEDIA) * PASSES)
