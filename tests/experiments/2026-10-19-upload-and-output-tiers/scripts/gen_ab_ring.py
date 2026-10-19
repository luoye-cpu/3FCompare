# -*- coding: utf-8 -*-
"""#25 的两条大帧路径对撞：STAGING 环(path 1) vs UpdateSubresource(path 6)，按 A/B/A 交错。

为什么要交错而不是先跑完 A 再跑完 B：今天已经证明这条链的每帧耗时对"谁在同时搬内存"
极其敏感（静 9.75 ms vs 加竞争 19-20 ms）。A 全跑完再跑 B 的话，两段时间上的机器负载
差会直接被读成路径差。A/B/A 同一素材连排，两段 A 对不上就说明环境在漂，整组判废。

牙沿用 gen_guarded_round.py 那套：起跑前数 alive_players，跑完要求 CSV 真存在，
否则重试；模式由 FFF3FP_UPLOAD_COPY_MODE 逐臂给（row=默认 STAGING 环，us=UpdateSubresource）。
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
REAL = r"C:\PLAN\3FCompare\testmedia\media\real"
CSVDIR = OUT + r"\player_csv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "bring"

MEDIA = {
    "4k_hevc10": REAL + r"\real_4k_hevc10_60m.mp4",
    "8k_av1_hdr10": REAL + r"\real_8k_av1_hdr10_100m.mp4",
    "8k_hevc10": REAL + r"\real_8k_hevc10_150m.mp4",
}
ARMS = ["8k_av1_hdr10", "8k_hevc10"]
SEQ = [("row", "A0"), ("row", "B0"), ("row", "A1")]   # ring depth 2 / 1 / 2

L = [
    "@echo off",
    'call :main > "%s\\ab_ring_%s.log" 2>&1' % (OUT, SUFFIX),
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
]

for label in ARMS:
    for mode, tag in SEQ:
        csvp = "%s\\%s_%s_%s_%s.csv" % (CSVDIR, label, mode, tag, SUFFIX)
        L += [
            "set ALIVE=0",
            'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
            'echo WITNESS %s %s %s alive_players=!ALIVE!' % (label, mode, tag),
            "set FFF3FP_UPLOAD_COPY_MODE=row",
            "set FFF3FP_UPLOAD_RING=%s" % ("1" if tag == "B0" else "2"),
            'set FFF3FP_PERF_OUT=%s' % csvp,
            'del "%s" >nul 2>&1' % csvp,
            'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, MEDIA[label]),
            "echo PLAY_RC=%%ERRORLEVEL%% arm=%s mode=%s tag=%s" % (label, mode, tag),
            'if exist "%s" (echo ARM_VALID %s %s %s) else (echo ARM_INVALID %s %s %s)'
            % (csvp, label, mode, tag, label, mode, tag),
            "timeout /t 3 /nobreak >nul",
        ]

L += ["echo AB_US_DONE", "exit /b 0"]

path = OUT + "\\run_ab_ring_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-abring-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_ab_ring_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(ARMS) * len(SEQ))
