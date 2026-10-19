# -*- coding: utf-8 -*-
"""layout 0（CPU sws 转 RGB + 4 B/px 上传）第一次被量：bgr0 vs yuv420p，同一几何，交替 4 次。

代码依据（只读 lane 复核过）：DescribeInput 的 default 分支 `return {}` ⇒ 任何未列出的像素
格式都落到 layout 0 ⇒ 走 VideoRenderer.cpp:5959 的整帧 CPU sws_scale，再按 4 B/px（≥10bit 8 B/px）
上传。而 yuv420p 是 layout 1（直传），yuv422p 被 static_assert 钉在 layout 1。

夹具口径（都是 ffprobe 回读出来的，不是命令行说成功就算）：
  fx_yuv420p_1920x1080.mkv  实际 pix_fmt = yuv420p（layout 1 参考臂）
  fx_bgr0_1920x1080.mkv     实际 pix_fmt = bgr0   （layout 0 被测臂）
⚠ 造 packed yuyv422 失败两次：ffv1 和 utvideo 都把它规范化成 yuv422p（=layout 1），
  所以"打包 4:2:2"这一格本表没有读数，不能拿 yuv422p 冒充。

判据列：sw_ms（softwareConvert100ns，此前 11 臂全为 0）、bytes_per_upload、copy_ms、
upload_ms、presented/dropped。1920x1080 下 yuv420p 每帧应约 3.11 MB、bgr0 应约 8.29 MB。
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
FIX = OUT + r"\fixtures"
CSVDIR = OUT + r"\player_csv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "l0"

MEDIA = [
    ("planar_yuv420p", FIX + r"\fx_yuv420p_1920x1080.mkv"),
    ("layout0_bgr0", FIX + r"\fx_bgr0_1920x1080.mkv"),
]
# 交替跑，抹平机器漂移：A B A B A B A B
SEQ = ["p1", "p2", "p3", "p4"]

L = [
    "@echo off",
    'call :main > "%s\\layout0_%s.log" 2>&1' % (OUT, SUFFIX),
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
]

for i, tag in enumerate(SEQ):
    for label, media in MEDIA:
        csvp = "%s\\%s_%s_%s.csv" % (CSVDIR, label, tag, SUFFIX)
        L += [
            "set ALIVE=0",
            'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
            'echo WITNESS %s %s alive_players=!ALIVE!' % (label, tag),
            'set FFF3FP_PERF_OUT=%s' % csvp,
            'del "%s" >nul 2>&1' % csvp,
            'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, media),
            'if exist "%s" (echo ARM_VALID %s %s) else (echo ARM_INVALID %s %s)'
            % (csvp, label, tag, label, tag),
            "timeout /t 3 /nobreak >nul",
        ]

L += ["echo LAYOUT0_DONE", "exit /b 0"]

path = OUT + "\\run_layout0_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-layout0-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_layout0_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(SEQ) * len(MEDIA))
