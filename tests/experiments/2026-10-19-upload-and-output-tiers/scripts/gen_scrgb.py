# -*- coding: utf-8 -*-
"""输出档位 A/B：同一枚内核，FFF3FP_SDR_SCRGB=0/1 两臂，读回 outBits 与像素。

要证的事：默认策略从"从不"改成"自动"之后，>8bit 的源确实落 16bit scRGB 档；
而 8bit 源不动。拿不到的情况（显示器未被报为高级色彩 / 驱动拒 G10）应退回 10bit ——
这一臂本机是不是这样，必须由读数说，不能由代码注释说。

探针件必须现编：FFF3FPSnapshot 从 440 涨到 464，10-15 那份旧 exe 传的 size 会被内核
`output.size < sizeof(FFF3FPSnapshot)` 守卫判废，读出来一片 0 会被误读成"产品坏了"。
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
BUILD = r"C:\PLAN\3FCompare\3fp\build"
KERNEL_DIR = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native"
FIX = OUT + r"\fixtures"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "scrgb"

VC = r"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231"
KITS = r"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0"
LIBS = r"C:\Program Files (x86)\Windows Kits\10\Lib\10.0.26100.0"

MEDIA = [
    ("s8_yuv420p", FIX + r"\fx_yuv420p_1920x1080.mkv"),        # 8bit：两档都不该动
    ("s10_hevc", r"C:\PLAN\3FCompare\testmedia\media\real\real_4k_hevc10_60m.mp4"),  # 10bit
]
POLICIES = ["0", "1"]
FLAGS = [("", "plain"), ("--force-hdr", "fhdr")]   # 能力对照臂：强制越过显示探测
PASSES = 2

L = [
    "@echo off",
    'call :main > "%s\\scrgb_%s.log" 2>&1' % (OUT, SUFFIX),
    "exit /b %ERRORLEVEL%",
    ":main",
    "chcp 65001 >nul",
    "setlocal enabledelayedexpansion",
    "set HTTP_PROXY=", "set HTTPS_PROXY=", "set http_proxy=", "set https_proxy=",
    "set FTP_PROXY=", "set ftp_proxy=",
    r"set TMP=C:\PLAN\3FCompare\_3fc_tmp\wintmp",
    "set TEMP=%TMP%",
    "cd /d " + BUILD,
    # 内核与 FFmpeg 的 DLL 就在 BUILD 里。编译走 PowerShell 那份验过的 env：
    # .bat 里 set INCLUDE 直接报 C1083 找不到 windows.h，而 cl 一失败，BUILD 里那份
    # 10-15 的旧探针会原地顶上（旧 ABI 440，快照会被内核判废）——所以编不过就整轮不起。
    'certutil -hashfile "%s\\FFF.Native.dll" MD5' % BUILD,
    'powershell -NoProfile -ExecutionPolicy Bypass -File "%s\\compile_probe.ps1"' % OUT,
    "echo PROBE_COMPILE_RC=%ERRORLEVEL%",
    'if not "%ERRORLEVEL%"=="0" (echo ROUND_ABORT_PROBE_COMPILE & exit /b 3)',
]

for p in range(1, PASSES + 1):
    for flag, flagtag in FLAGS:
        for pol in POLICIES:
            for label, media in MEDIA:
                log = "%s\\pix_%s_%s_p%s_s%s_%s.txt" % (OUT, label, flagtag, p, pol, SUFFIX)
                L += [
                    'set FFF3FP_SDR_SCRGB=%s' % pol,
                    "set ALIVE=0",
                    'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq read_pixel_probe.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "read_pixel_probe.exe"`) do set ALIVE=%%c',
                    'echo WITNESS %s %s scrgb=%s p%d probes_already_alive=!ALIVE!' % (label, flagtag, pol, p),
                    'del "%s" >nul 2>&1' % log,
                    'read_pixel_probe.exe "%s" %s > "%s" 2>&1' % (media, flag, log),
                    'echo ARM_EXIT %s %s scrgb=%s p%d exit=%%ERRORLEVEL%%' % (label, flagtag, pol, p),
                    'if exist "%s" (find "open:" < "%s" >nul && echo ARM_RAN %s %s scrgb=%s p%d || echo ARM_NOOPEN %s %s scrgb=%s p%d) else (echo ARM_INVALID %s %s scrgb=%s p%d)'
                    % (log, log, label, flagtag, pol, p, label, flagtag, pol, p, label, flagtag, pol, p),
                    'type "%s"' % log,
                    "timeout /t 2 /nobreak >nul",
                ]

L += ["echo SCRGB_DONE", "exit /b 0"]

path = OUT + "\\run_scrgb_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-scrgb-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_scrgb_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(MEDIA) * len(POLICIES) * PASSES)
