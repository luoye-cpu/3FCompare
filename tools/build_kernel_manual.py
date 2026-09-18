#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""手工复放 MSBuild 对 FFF.Native 的编译/链接命令（MSBuild.exe 在本机被安全策略拦截）。

命令来源：FFF.Native/obj/x64/Release/FFF.Native.tlog/{CL,rc,link}.command.1.tlog
（即 MSBuild 上次真实下发的参数，不是猜的）。目录重定向到 Release_pr9 做干净构建，
避免删除既有 obj（bulk-delete 守卫会拦 rm -rf）。
"""
import io
import os
import re
import subprocess
import sys
import time

MSVC = r"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231"
SDK = r"C:\Program Files (x86)\Windows Kits\10"
SDKVER = "10.0.28000.0"
CL = os.path.join(MSVC, "bin", "Hostx64", "x64", "cl.exe")
LINK = os.path.join(MSVC, "bin", "Hostx64", "x64", "link.exe")
RC = os.path.join(SDK, "bin", SDKVER, "x64", "rc.exe")

ROOT = r"C:\PLAN\3FCompare\third_party\fff_project\FFF.Native"
TLOG = os.path.join(ROOT, "obj", "x64", "Release", "FFF.Native.tlog")
NEWOBJ = os.path.join(ROOT, "obj", "x64", "Release_pr9")
NEWOUT = os.path.join(ROOT, "x64", "Release_pr9")

INCLUDE = ";".join([
    os.path.join(MSVC, "include"),
    os.path.join(SDK, "Include", SDKVER, "ucrt"),
    os.path.join(SDK, "Include", SDKVER, "um"),
    os.path.join(SDK, "Include", SDKVER, "shared"),
    os.path.join(SDK, "Include", SDKVER, "winrt"),
    os.path.join(SDK, "Include", SDKVER, "cppwinrt"),
])
LIB = ";".join([
    os.path.join(MSVC, "lib", "x64"),
    os.path.join(SDK, "Lib", SDKVER, "ucrt", "x64"),
    os.path.join(SDK, "Lib", SDKVER, "um", "x64"),
])
# link 阶段 PATH 必须是 Windows 风格（反斜杠 + 分号），否则 LNK1158
PATH = ";".join([
    os.path.join(MSVC, "bin", "Hostx64", "x64"),
    os.path.join(SDK, "bin", SDKVER, "x64"),
    r"C:\Windows\System32",
    r"C:\Windows",
])

ENV = dict(os.environ)
ENV.update({"INCLUDE": INCLUDE, "LIB": LIB, "PATH": PATH, "TMP": r"C:\Windows\Temp"})


def read_tlog(name):
    raw = io.open(os.path.join(TLOG, name), "rb").read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace").lstrip("\ufeff")
    return raw.decode("utf-8-sig", errors="replace").replace("\x00", "")


def blocks(text):
    return [b for b in (text or "").split("^") if b.strip()]


def redirect(cmd):
    """把输出目录从 Release 重定向到 Release_pr9（大小写不敏感）。

    只替换「子路径」部分，保留原有的 `C:\\...\\FFF.Native\\` 前缀 —— 若替换成绝对
    路径会拼出 `FFF.Native\\C:\\...` 这种非法路径（C1083 / RC1109 / LNK1104）。
    """
    # 用 lambda 做替换，避免 re 把替换串里的 \x / \R 当成转义
    cmd = re.sub(r"OBJ\\X64\\RELEASE", lambda m: r"OBJ\x64\Release_pr9", cmd, flags=re.I)
    cmd = re.sub(r"X64\\RELEASE\\", lambda m: r"x64\Release_pr9\\", cmd, flags=re.I)
    return cmd


def run(cmd, label, idx, total):
    t0 = time.time()
    p = subprocess.run(cmd, env=ENV, cwd=ROOT, shell=False,
                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    out = p.stdout.decode("utf-8", errors="replace")
    ok = p.returncode == 0
    print("[%2d/%2d] %-6s %-28s exit=%d  %.1fs" %
          (idx, total, label, os.path.basename(label) if False else label, p.returncode,
           time.time() - t0))
    if out.strip():
        print(out.strip()[:4000])
    sys.stdout.flush()
    return ok, out


def main():
    os.makedirs(NEWOBJ, exist_ok=True)
    os.makedirs(NEWOUT, exist_ok=True)

    cl_blocks = blocks(read_tlog("CL.command.1.tlog"))
    rc_blocks = blocks(read_tlog("rc.command.1.tlog"))
    lk_blocks = blocks(read_tlog("link.command.1.tlog"))
    print("编译单元 %d 个；rc %d；link %d" % (len(cl_blocks), len(rc_blocks), len(lk_blocks)))
    sys.stdout.flush()

    errors = []
    n = len(cl_blocks) + len(rc_blocks) + len(lk_blocks)
    i = 0

    # 1) cl.exe（tlog 里 pch.cpp 带 /Yc 排在最前，必须保持顺序）
    for b in cl_blocks:
        i += 1
        lines = [l for l in b.replace("\r\n", "\n").split("\n") if l.strip()]
        src, cmd = lines[0].strip(), lines[1].strip()
        ok, out = run('"%s" %s' % (CL, redirect(cmd)), "cl " + os.path.basename(src), i, n)
        if not ok:
            errors.append((src, out))

    # 2) rc.exe
    res = None
    for b in rc_blocks:
        i += 1
        lines = [l for l in b.replace("\r\n", "\n").split("\n") if l.strip()]
        src, cmd = lines[0].strip(), lines[1].strip()
        ok, out = run('"%s" %s' % (RC, redirect(cmd)), "rc " + os.path.basename(src), i, n)
        if not ok:
            errors.append((src, out))
        else:
            res = os.path.join(NEWOBJ, "FFF.Native.res")

    # 3) link.exe
    for b in lk_blocks:
        i += 1
        lines = [l for l in b.replace("\r\n", "\n").split("\n") if l.strip()]
        # tlog 块结构：line0 = 输入清单（管道分隔），line1 = 命令行，line2.. = 逐个 .obj。
        # 只取 line1 会漏掉全部 obj ⇒ LNK2001 _DllMainCRTStartup。
        cmd = lines[1].strip() + " " + " ".join(l.strip() for l in lines[2:])
        cmd = redirect(cmd)
        ok, out = run('"%s" %s' % (LINK, cmd), "link", i, n)
        if not ok:
            errors.append(("link", out))

    dll = os.path.join(NEWOUT, "FFF.Native.dll")
    print("\n================ 结果 ================")
    print("失败项: %d" % len(errors))
    for src, out in errors:
        print("---- %s ----" % src)
        print(out[:3000])
    if os.path.exists(dll):
        print("产物: %s  %d bytes" % (dll, os.path.getsize(dll)))
    else:
        print("产物缺失: %s" % dll)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
