# -*- coding: utf-8 -*-
"""上传前降档（自适应CPU预缩放）第一次被量：off / on 交替，两枚 8K 素材。

为什么要做：内核把这条路默认关死（header 写 "0 = Disabled (default)"），而宿主唯一的
调用点把 .自适应CPU预缩放 写死 0 —— 本次 investigation 的**所有**读数都是在关闭态下取的
（快照里 降档重采样时长 恒为 0、bytes_per_upload 恒等于整帧字节数就是物证）。
所以"少搬字节"这条最大杠杆此前只有一句"−16% fps"的旧否证，且那批数字来自字节口径未知、
机器是否干净未知的轮次。

gate 打开时阈值同时压到 1%（现网 20%，本机 8K 最好臂只丢约 4%，用 20% 永远不启用）。

判据（都用现成列）：
  bytes_per_upload  —— 降档真发生了吗？应显著小于 99,532,800
  resample_ms       —— 内核头自称"about a millisecond of CPU work"，直接验它
  copy_ms           —— 省下来的搬运时间
  presented/dropped —— 净收益：总帧率上去了还是掉得更多
"""
import sys

OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
REAL = r"C:\PLAN\3FCompare\testmedia\media\real"
XL = r"C:\迅雷下载"
CSVDIR = OUT + r"\player_csv"
SUFFIX = sys.argv[1] if len(sys.argv) > 1 else "ads"

MEDIA = {
    "8k_hevc10": REAL + r"\real_8k_hevc10_150m.mp4",
    "chou_8k_av1": XL + r"\ChouKaguya432048-Part.mkv",
}
ARMS = sys.argv[2].split(",") if len(sys.argv) > 2 else list(MEDIA)
SEQ = [("off", ""), ("on", "1"), ("off2", ""), ("on2", "1")]

L = [
    "@echo off",
    'call :main > "%s\\adaptive_%s.log" 2>&1' % (OUT, SUFFIX),
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
    "set FFF3FP_UPLOAD_RING=2",
    "set FFF3FP_MAX_FRAME_LATENCY=2",
]

for label in ARMS:
    assert label in MEDIA, "unknown arm %r" % label
    for tag, value in SEQ:
        csvp = "%s\\%s_%s_%s.csv" % (CSVDIR, label, tag, SUFFIX)
        L += [
            "set ALIVE=0",
            'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
            'echo WITNESS %s %s alive_players=!ALIVE!' % (label, tag),
            "set FFF3FP_ADAPTIVE_DOWNSCALE=%s" % value,
            'set FFF3FP_PERF_OUT=%s' % csvp,
            'del "%s" >nul 2>&1' % csvp,
            'start /wait "" "%s\\FFF.Player.exe" "%s"' % (RUN, MEDIA[label]),
            "echo LAUNCHED %s %s" % (label, tag),
            'if exist "%s" (echo ARM_VALID %s %s) else (echo ARM_INVALID %s %s)'
            % (csvp, label, tag, label, tag),
            "timeout /t 3 /nobreak >nul",
        ]

L += ["echo ADAPTIVE_DONE", "exit /b 0"]

path = OUT + "\\run_adaptive_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-adaptive-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_adaptive_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "arms=", len(ARMS) * len(SEQ))
