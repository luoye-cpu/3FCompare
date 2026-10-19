# -*- coding: utf-8 -*-
"""双槽环深度 A/B 交替，跑在 C:\\迅雷下载 的重素材上。

上一轮（gen_ab_ring.py）的缺陷：只做一次 A/B/A，而两趟同模式自己差了 2 倍
（8K AV1 copy 13.4 vs 29.2 ms）——机器在这一轮里明显在漂，单对单比不出"深度"的效应。
这一版把深度**交替 3 次**（2,1,2,1,2,1），比较用各自 3 趟的中位数，漂移被摊平；
mapwait 若是 300 倍级别的差，任何漂移都掩盖不了它。

素材口径（ffprobe 实测，两枚都是 AV1、标称 7001/146 ≈ 47.95 fps）：
  ChouKaguya432048-Part.mkv  7680x4320 yuv420p10le  → 每次上传 99,532,800 B
  OP-yuv444p12-SWTest.mkv    3840x2160 yuv444p12le  → 每次上传 49,766,400 B
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
XL = r"C:\迅雷下载"
CSVDIR = OUT + r"\player_csv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "bring2"

MEDIA = {
    "chou_8k_av1": XL + r"\ChouKaguya432048-Part.mkv",
    "op_4k_444p12": XL + r"\OP-yuv444p12-SWTest.mkv",
}
ARMS = sys.argv[2].split(",") if len(sys.argv) > 2 else list(MEDIA)
DEPTHS = [2, 1, 2, 1, 2, 1]     # 交替三轮

L = [
    "@echo off",
    'call :main > "%s\\ring_alt_%s.log" 2>&1' % (OUT, SUFFIX),
    "exit /b %ERRORLEVEL%",
    ":main",
    "chcp 65001 >nul",
    "setlocal enabledelayedexpansion",
    "set HTTP_PROXY=", "set HTTPS_PROXY=", "set http_proxy=", "set https_proxy=",
    "set FTP_PROXY=", "set ftp_proxy=",
    r"set TMP=C:\PLAN\3FCompare\_3fc_tmp\wintmp",
    r"set TEMP=C:\PLAN\3FCompare\_3fc_tmp\wintmp",
    'copy /Y "%s" "%s\\FFF.Native.dll"' % (KERNEL, RUN),
    "echo KERNEL_COPY_RC=%ERRORLEVEL%",
    'certutil -hashfile "%s\\FFF.Native.dll" MD5' % RUN,
    "cd /d " + RUN,
    "set FFF3FP_PERF_SECONDS=30",
    "set FFF3FP_PERF_FULLSCREEN=1",
    "set FFF3FP_PERF_WIDTH=",
    "set FFF3FP_PERF_HEIGHT=",
    "set FFF3FP_UPLOAD_COPY_MODE=row",
]

for label in ARMS:
    assert label in MEDIA, "unknown arm %r" % label
    for i, depth in enumerate(DEPTHS, 1):
        csvp = "%s\\%s_d%d_p%d_%s.csv" % (CSVDIR, label, depth, i, SUFFIX)
        L += [
            "set ALIVE=0",
            'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
            'echo WITNESS %s depth%d pass%d alive_players=!ALIVE!' % (label, depth, i),
            "set FFF3FP_UPLOAD_RING=%d" % depth,
            'set FFF3FP_PERF_OUT=%s' % csvp,
            'del "%s" >nul 2>&1' % csvp,
            'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, MEDIA[label]),
            "echo PLAY_RC arm=%s depth=%d pass=%d" % (label, depth, i),
            'if exist "%s" (echo ARM_VALID %s d%d p%d) else (echo ARM_INVALID %s d%d p%d)'
            % (csvp, label, depth, i, label, depth, i),
            "timeout /t 3 /nobreak >nul",
        ]

L += ["echo RING_ALT_DONE", "exit /b 0"]

path = OUT + "\\run_ring_alt_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-ringalt-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_ring_alt_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(ARMS) * len(DEPTHS))
