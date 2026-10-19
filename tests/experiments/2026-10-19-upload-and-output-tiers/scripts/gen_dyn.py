# -*- coding: utf-8 -*-
"""最后一次：≥4K 现网的"两笔传输"环 vs <4K 的"一笔传输"DYNAMIC 路径。

已量的：path 1（STAGING memcpy + CopyResource）在 8K 是 9.75–10.35 ms/帧（安静机器），
mapwait 0.012 ms。今天要判的是 path 2：Map(WRITE_DISCARD) 直接映射**可采样**的 DYNAMIC
纹理，只有一笔 CPU 写，没有随后的 CopyResource——#25 之前那句"8K 下 Map 22.27 ms"
说的就是这条路，但它没有任何一臂能证实或证伪（这份代码在 ≥4K 从不建 DYNAMIC 纹理）。

第三臂是关键的新组合：DYNAMIC 映射出来的是 write-combined 内存，而之前那次
"stream(MOVNTQ) 反而更慢"的 A/B 是在 STAGING(写回内存)上做的 —— 非临时存储本来就不该
在 WB 上赢。所以"stream 无用"这条结论对 path 2 **不成立**，这一臂才是它的正题。

格子（每素材 5 臂）：base(row,path1) / dyn(row,path2) / dyncopy(row,path2) 交替 2 趟 + stream 1 趟
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
REAL = r"C:\PLAN\3FCompare\testmedia\media\real"
CSVDIR = OUT + r"\player_csv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "dyn"

MEDIA = [
    ("8k_av1_hdr10", REAL + r"\real_8k_av1_hdr10_100m.mp4"),
    ("8k_hevc10", REAL + r"\real_8k_hevc10_150m.mp4"),
]
# (tag, FFF3FP_UPLOAD_FORCE_DYNAMIC, FFF3FP_UPLOAD_COPY_MODE, passes)
CELLS = [("base", "", "row", 2), ("dyn", "1", "row", 2), ("dynstream", "1", "stream", 1)]

L = [
    "@echo off",
    'call :main > "%s\\dyn_%s.log" 2>&1' % (OUT, SUFFIX),
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
    "set FFF3FP_PERF_SECONDS=25",
    "set FFF3FP_PERF_FULLSCREEN=1",
    "set FFF3FP_PERF_WIDTH=",
    "set FFF3FP_PERF_HEIGHT=",
    "set FFF3FP_UPLOAD_RING=2",
    "set FFF3FP_MAX_FRAME_LATENCY=2",
    "set FFF3FP_ADAPTIVE_DOWNSCALE=",
]

passes = max(c[3] for c in CELLS)
for p in range(1, passes + 1):
    for label, media in MEDIA:
        for tag, force, mode, limit in CELLS:
            if p > limit:
                continue
            csvp = "%s\\%s_%s_p%d_%s.csv" % (CSVDIR, label, tag, p, SUFFIX)
            L += [
                "set ALIVE=0",
                'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
                'echo WITNESS %s %s p%d alive_players=!ALIVE!' % (label, tag, p),
                "set FFF3FP_UPLOAD_FORCE_DYNAMIC=%s" % force,
                "set FFF3FP_UPLOAD_COPY_MODE=%s" % mode,
                'set FFF3FP_PERF_OUT=%s' % csvp,
                'del "%s" >nul 2>&1' % csvp,
                'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, media),
                'if exist "%s" (echo ARM_VALID %s %s p%d) else (echo ARM_INVALID %s %s p%d)'
                % (csvp, label, tag, p, label, tag, p),
                "timeout /t 3 /nobreak >nul",
            ]

L += ["echo DYN_DONE", "exit /b 0"]

path = OUT + "\\run_dyn_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-dyn-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_dyn_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", sum(len(MEDIA) * (1 if tag == "dynstream" else 2) for tag, *_ in CELLS))
