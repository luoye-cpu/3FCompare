# -*- coding: utf-8 -*-
"""latency × ring 的 2×2：把"能不能用主动限制帧同步替掉双槽环"变成读数。

已量的两格：
  L2R2 -> mapwait 0.012-0.019 ms（环在起作用）
  L2R1 -> mapwait 3.75-4.80 ms（环关掉就露出等待）
缺的是 L1R1（你说的方案：队列只允许一帧在途，单缓冲按定义不会撞）和 L1R2（冗余对照）。
L1R1 若 mapwait≈0 且 dropped/presentwait 不变差，限制同步就是更省内存的等价物；
若 presentwait/dropped 涨，说明它是用"渲染与 Present 重叠"换的，不免费。

牙：每臂起跑前见证 alive_players=0、跑完要求 CSV 存在；同格跑 2 趟，两趟对不上就判漂移。
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
CSVDIR = OUT + r"\player_csv"
MEDIA = r"C:\迅雷下载\ChouKaguya432048-Part.mkv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "lat2x2"

# (cell name, latency, ring) —— 名字必须同时带两个数，否则 L2R2 与 L2R1 会写进同一份 CSV。
CELLS = [("L2R2", "2", "2"), ("L1R1", "1", "1"), ("L2R1", "2", "1"), ("L1R2", "1", "2")]
PASSES = 2

L = [
    "@echo off",
    'call :main > "%s\\lat2x2_%s.log" 2>&1' % (OUT, SUFFIX),
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

for p in range(1, PASSES + 1):
    for name, latency, ring in CELLS:
        csvp = "%s\\chou_%s_p%d_%s.csv" % (CSVDIR, name, p, SUFFIX)
        L += [
            "set ALIVE=0",
            'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
            'echo WITNESS %s pass%d alive_players=!ALIVE!' % (name, p),
            "set FFF3FP_MAX_FRAME_LATENCY=%s" % latency,
            "set FFF3FP_UPLOAD_RING=%s" % ring,
            'set FFF3FP_PERF_OUT=%s' % csvp,
            'del "%s" >nul 2>&1' % csvp,
            'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, MEDIA),
            "echo LAUNCHED %s pass=%d latency=%s ring=%s" % (name, p, latency, ring),
            'if exist "%s" (echo ARM_VALID %s p%d) else (echo ARM_INVALID %s p%d)'
            % (csvp, name, p, name, p),
            "timeout /t 3 /nobreak >nul",
        ]

L += ["echo LAT2X2_DONE", "exit /b 0"]

path = OUT + "\\run_lat2x2_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-lat2x2-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_lat2x2_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", PASSES * len(CELLS))
