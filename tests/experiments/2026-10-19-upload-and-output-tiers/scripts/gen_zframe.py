# -*- coding: utf-8 -*-
"""查 chou + 上传前降档 那一臂"零帧"的成因。

已排除的：不是任务没起（log 里有 WITNESS、ARM_VALID 说 CSV 建了）；不是磁盘（同目录别臂正常）。
CSV 是 0 行，意味着采样器写过一次"空缓冲"——只有 收尾 会写，而 表头 是在第一次 写入一行
才写，所以实况是：**整个进程生命周期内一帧都没呈现**，收尾把空 StringBuilder 落盘。
采样器有三条收尾路径：窗口数够 / 连续 3 个 tick 没新帧 / 墙钟超时。所以要么它 3 秒就放弃，
要么进程根本没活到出帧。

这一版把两件事补上，才能分得开：
  1) 直调 exe 并把 stdout/stderr 落盘 —— 内核在 4389 行会 fprintf(stderr, "[adaptive] ...")，
     前几轮全用 start /wait 把这两股丢了，所以"策略到底有没有 engage"从来没有见证。
  2) 每臂打 T_START/T_END 墙钟，退出时长一目了然。

⚠ 口径代价：直调会让进程挂在一个控制台上新增少量 stderr 写，时序读数和之前的
`start /wait` 臂不完全可比；本轮只用来定性"零帧"，不拿来引用毫秒数。
"""
OUT = r"C:\PLAN\3FCompare\_3fc_tmp"
RUN = OUT + r"\player_run"
KERNEL = r"C:\PLAN\3FCompare\3fp\kernel\FFF.Native\x64\Release\FFF.Native.dll"
XL = r"C:\迅雷下载"
CSVDIR = OUT + r"\player_csv"
MEDIA = XL + r"\ChouKaguya432048-Part.mkv"
SUFFIX = "zframe"

L = [
    "@echo off",
    'call :main > "%s\\zframe_%s.log" 2>&1' % (OUT, SUFFIX),
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
    "cd /d " + RUN,
    "set FFF3FP_PERF_SECONDS=20",
    "set FFF3FP_PERF_FULLSCREEN=1",
    "set FFF3FP_PERF_WIDTH=",
    "set FFF3FP_PERF_HEIGHT=",
    "set FFF3FP_UPLOAD_COPY_MODE=row",
]

for tag, value in [("off_a", ""), ("on_a", "1"), ("off_b", ""), ("on_b", "1")]:
    csvp = "%s\\chou_%s_%s.csv" % (CSVDIR, tag, SUFFIX)
    errp = "%s\\chou_%s_%s.stderr.txt" % (OUT, tag, SUFFIX)
    L += [
        "set ALIVE=0",
        'for /f "usebackq delims=" %%c in (`tasklist /FI "IMAGENAME eq FFF.Player.exe" /FO CSV ^| %SystemRoot%\\System32\\find.exe /c "FFF.Player.exe"`) do set ALIVE=%%c',
        'echo WITNESS %s alive_players=!ALIVE!' % tag,
        "set FFF3FP_ADAPTIVE_DOWNSCALE=%s" % value,
        'set FFF3FP_PERF_OUT=%s' % csvp,
        'del "%s" >nul 2>&1' % csvp,
        'del "%s" >nul 2>&1' % errp,
        "echo T_START_%s=%%TIME%%" % tag,
        # 直调而非 start /wait：只有这样才能收到内核自己打的 [adaptive] 行。
        'cmd /c ""%s\\FFF.Player.exe" "%s" > "%s" 2>&1"' % (RUN, MEDIA, errp),
        "echo RC_%s=%%ERRORLEVEL%%" % tag,
        "echo T_END_%s=%%TIME%%" % tag,
        # `%%~za` aborted the whole batch after one arm ("substitution is invalid").
        # Presence only here; sizes are read from outside batch afterwards.
        'if exist "%s" (echo CSVYES_%s) else (echo CSVNO_%s)' % (csvp, tag, tag),
        'if exist "%s" (echo ERRYES_%s) else (echo ERRNO_%s)' % (errp, tag, tag),
        "timeout /t 4 /nobreak >nul",
    ]

L += ["echo ZFRAME_DONE", "exit /b 0"]

path = OUT + "\\run_zframe_%s.bat" % SUFFIX
with open(path, "wb") as f:
    f.write(("\r\n".join(L) + "\r\n").encode("utf-8"))
b = open(path, "rb").read()
assert b.count(b"\n") == b.count(b"\r\n"), "LF 泄漏"

task = "3FC-zframe-" + SUFFIX
launch = [
    "@echo off",
    "set TASK=" + task,
    'schtasks /delete /tn "%TASK%" /f >nul 2>&1',
    'schtasks /create /tn "%TASK%" /tr "' + path + '" /sc once /st 23:59 /f',
    "echo CREATE_RC=%ERRORLEVEL%",
    'schtasks /run /tn "%TASK%"',
    "echo RUN_RC=%ERRORLEVEL%",
]
lpath = OUT + "\\launch_zframe_%s.bat" % SUFFIX
with open(lpath, "wb") as f:
    f.write(("\r\n".join(launch) + "\r\n").encode("utf-8"))
print("wrote", path, "lines=", b.count(b"\r\n"))
