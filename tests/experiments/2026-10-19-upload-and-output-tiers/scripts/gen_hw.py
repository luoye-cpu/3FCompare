# -*- coding: utf-8 -*-
"""硬解直贴 vs 软解上传：把唯一还没量的大杠杆量一次。

代码依据：硬解这条路在本树里已经实现（PlayerSession.cpp:2663 av_hwdevice_ctx_create、
Render 里走 CopySubresourceRegion 把纹理切片搬进着色器可见纹理，快照有 硬件传输时长 列），
只是宿主初始值被设置文件钉在 CPU（Form1.vb:112 读 设置.解码方式），所以无人值守采样碰不到它。
本轮用 FFF3FP_DECODER=gpu 覆盖（不设为原行为）。

要看的东西：硬解臂的 upload_ms / copy_ms 应当塌到接近 0（不再有整帧 CPU 读写），
hw_ms 接住搬运；代价看 presented/dropped 与"这台机的 iGPU 到底能不能解这个格式"——
解不了的话这臂会直接不出帧，所以每臂收 stderr。

⚠ 口径提醒：bytes_per_upload 在硬解臂大概率是 0（那条路不经过我记账的两个平面拷贝点），
这是"没记"，不是"没搬"——别拿它当"硬解零流量"的证据。
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
REAL = r"C:\PLAN\3FCompare\testmedia\media\real"
CSVDIR = OUT + r"\player_csv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "hw"

MEDIA = [
    ("8k_av1_hdr10", REAL + r"\real_8k_av1_hdr10_100m.mp4"),
    ("8k_hevc10", REAL + r"\real_8k_hevc10_150m.mp4"),
    ("4k_hevc10", REAL + r"\real_4k_hevc10_60m.mp4"),
]
CELLS = [("cpu", ""), ("gpu", "gpu")]
PASSES = 2

L = [
    "@echo off",
    'call :main > "%s\\hw_%s.log" 2>&1' % (OUT, SUFFIX),
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
    "set FFF3FP_UPLOAD_COPY_MODE=row",
    "set FFF3FP_UPLOAD_RING=2",
    "set FFF3FP_MAX_FRAME_LATENCY=2",
    "set FFF3FP_ADAPTIVE_DOWNSCALE=",
    "set FFF3FP_UPLOAD_FORCE_DYNAMIC=",
]

for p in range(1, PASSES + 1):
    for label, media in MEDIA:
        for tag, value in CELLS:
            csvp = "%s\\%s_%s_p%d_%s.csv" % (CSVDIR, label, tag, p, SUFFIX)
            errp = "%s\\%s_%s_p%d_%s.stderr.txt" % (OUT, label, tag, p, SUFFIX)
            L += [
                "set ALIVE=0",
                'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
                'echo WITNESS %s %s p%d alive_players=!ALIVE!' % (label, tag, p),
                "set FFF3FP_DECODER=%s" % value,
                'set FFF3FP_PERF_OUT=%s' % csvp,
                'del "%s" >nul 2>&1' % csvp,
                'del "%s" >nul 2>&1' % errp,
                'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, media),
                # 存在且非空才算有效：上一轮 0 字节 CSV 被 if exist 判成"臂有效"过。
                'if exist "%s" (echo ARM_RAN %s %s p%d) else (echo ARM_INVALID %s %s p%d)'
                % (csvp, label, tag, p, label, tag, p),
                "timeout /t 3 /nobreak >nul",
            ]

L += ["echo HW_DONE", "exit /b 0"]

path = OUT + "\\run_hw_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-hw-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_hw_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(MEDIA) * len(CELLS) * PASSES)
