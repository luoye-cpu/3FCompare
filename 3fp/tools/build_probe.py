#!/usr/bin/env python3
# ============================================================================
# build_probe.py —— 编译 3fp/tools/ 下的 C++ 探针（2026-09-26）
#
# 为什么需要：探针（verify_image_mode / loop_stutter_probe 等）此前**没有编译脚本**，
#   每次都是手敲 cl 命令行（且必须带 /I 指向 FFF.Native/3FP/Api）。内核构建走
#   build_kernel_manual.py，但它只编内核、不编探针。
#
# 与内核工具链一致：从内核 tlog 的 CL.read.1.tlog 里拿 MSVC/SDK 路径
#   （不是 CL.command.1.tlog —— 那里没有工具链 include，见 build_kernel_manual.py 注释）。
#
# 用法：python tools/build_probe.py <探针名，不含 .cpp> [更多…]
#   例：python tools/build_probe.py loop_stutter_probe
# 输出：3fp/build/<探针名>.exe
# ⚠ 跑 exe 要用 Bash（真实终端），且必须把 FFF.Native.dll 放在 exe 同目录。
# ============================================================================
import glob
import os
import re
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
KERNEL = os.path.join(ROOT, "kernel", "FFF.Native")
API_DIR = os.path.join(KERNEL, "3FP", "Api")
TOOLS = os.path.join(ROOT, "tools")
OUT = os.path.join(ROOT, "build")
TLOG = os.path.join(KERNEL, "obj", "x64", "Release", "FFF.Native.tlog")
# FFmpeg SDK（内核同款）：用于直接调 libavformat 的独立复现探针（.c）
FFMPEG = os.path.join(ROOT, "kernel", "third_party", "ffmpeg")
FFMPEG_INC = os.path.join(FFMPEG, "include")
FFMPEG_LIB = os.path.join(FFMPEG, "lib", "x64")


def read_tlog(name):
    path = os.path.join(TLOG, name)
    if not os.path.exists(path):
        return None
    raw = open(path, "rb").read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace").lstrip("\ufeff")
    return raw.decode("utf-8-sig", errors="replace").replace("\x00", "")


def find_toolchain():
    blob = read_tlog("CL.read.1.tlog") or ""
    msvc = re.search(r"([A-Z]:\\[^\";\r\n]*?\\VC\\Tools\\MSVC\\[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    sdk = re.search(r"Windows Kits\\10\\Include\\([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    if msvc and sdk:
        return msvc.group(1).strip(), sdk.group(1).strip()
    roots = sorted(
        [r for r in glob.glob(r"C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*") if os.path.isdir(r)],
        key=lambda p: [int(x) for x in os.path.basename(p).split(".") if x.isdigit()])
    sdks = sorted(
        [os.path.basename(r) for r in glob.glob(r"C:\Program Files*\Windows Kits\10\Include\*") if os.path.isdir(r)],
        key=lambda v: [int(x) for x in v.split(".") if x.isdigit()])
    return (roots[-1] if roots else None), (sdks[-1] if sdks else None)


def main():
    if len(sys.argv) < 2:
        print("usage: python tools/build_probe.py <probe-name> [more...]")
        return 2
    msvc_root, sdk_ver = find_toolchain()
    if not msvc_root:
        print("找不到 MSVC 工具链（内核 obj 下的 tlog 缺失？先跑一次内核构建）")
        return 1
    cl = os.path.join(msvc_root, "bin", "Hostx64", "x64", "cl.exe")
    sdk_inc = r"C:\Program Files (x86)\Windows Kits\10\Include\%s" % sdk_ver
    sdk_lib = r"C:\Program Files (x86)\Windows Kits\10\Lib\%s" % sdk_ver
    msvc_inc = os.path.join(msvc_root, "include")
    msvc_lib = os.path.join(msvc_root, "lib", "x64")
    print("MSVC : %s" % msvc_root)
    print("SDK  : %s" % sdk_ver)
    if not os.path.exists(cl):
        print("  cl.exe 缺失: %s" % cl)
        return 1

    os.makedirs(OUT, exist_ok=True)
    env = dict(os.environ)
    include = ";".join([
        KERNEL,          # 让 `#include "3FR/Api/FFF.Native.Api.h"` 能解析
        API_DIR,         # 让 `#include "FFF.Player.Api.h"` 能解析
        msvc_inc,
        os.path.join(sdk_inc, "ucrt"), os.path.join(sdk_inc, "shared"),
        os.path.join(sdk_inc, "um"),
    ])
    lib = ";".join([
        msvc_lib,
        os.path.join(sdk_lib, "ucrt", "x64"),
        os.path.join(sdk_lib, "um", "x64"),
    ])
    env["INCLUDE"] = include
    env["LIB"] = lib
    # ⚠ cl.exe 依赖同目录的 c1xx.dll / mspdb*.dll ⇒ PATH 必须含它所在目录
    env["PATH"] = os.path.dirname(cl) + os.pathsep + env.get("PATH", "")

    failures = 0
    # 探针静态导入 FFF3FP_* ⇒ 需要内核的导入库
    kernel_lib = os.path.join(KERNEL, "x64", "Release", "FFF.Native.lib")
    if not os.path.exists(kernel_lib):
        print("  ⚠ 找不到导入库: %s（先构建内核）" % kernel_lib)
        return 1
    for name in sys.argv[1:]:
        # 自动识别源语言：.c = 纯 C（可直连 FFmpeg SDK，不依赖 3FC 内核）；.cpp = 3FC 探针
        src_cpp = os.path.join(TOOLS, name + ".cpp")
        src_c = os.path.join(TOOLS, name + ".c")
        if os.path.exists(src_cpp):
            src, is_c = src_cpp, False
        elif os.path.exists(src_c):
            src, is_c = src_c, True
        else:
            print("  [%s] 源文件不存在（试过 .cpp / .c）" % name)
            failures += 1
            continue
        exe = os.path.join(OUT, name + ".exe")
        if is_c:
            # 独立 FFmpeg 探针：只吃 FFmpeg SDK
            cmd = [cl, "/nologo", "/O2", "/W3", "/DNDEBUG", "/D_CRT_SECURE_NO_WARNINGS",
                   "/I" + FFMPEG_INC,
                   src, "/Fe:" + exe, "/Fo:" + os.path.join(OUT, name + ".obj"),
                   "/link", os.path.join(FFMPEG_LIB, "avformat-63.lib"),
                   os.path.join(FFMPEG_LIB, "avcodec-63.lib"),
                   os.path.join(FFMPEG_LIB, "avutil-61.lib"),
                   "ws2_32.lib", "secur32.lib", "bcrypt.lib", "user32.lib", "ole32.lib"]
        else:
            cmd = [cl, "/nologo", "/std:c++17", "/O2", "/EHsc", "/W3", "/DNDEBUG",
                   "/D_CRT_SECURE_NO_WARNINGS",
                   src, "/Fe:" + exe, "/Fo:" + os.path.join(OUT, name + ".obj"),
                   "/link", kernel_lib,
                   "user32.lib", "gdi32.lib", "ole32.lib", "shell32.lib", "advapi32.lib"]
        print("\n[compile] %s%s" % (name, "  (C / FFmpeg SDK)" if is_c else ""))
        r = subprocess.run(cmd, env=env, capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout)
            print(r.stderr)
            failures += 1
        else:
            print(r.stdout.strip())
            print("  ✅ %s  (%d 字节)" % (exe, os.path.getsize(exe)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
