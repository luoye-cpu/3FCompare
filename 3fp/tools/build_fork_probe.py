#!/usr/bin/env python3
# ============================================================================
# build_fork_probe.py —— 把 3fp/tools 下的探针编到**出货内核树**上
#
# 为什么单独一个脚本：tools/build_probe.py 的 KERNEL 常量指向 3fp/kernel，而那一棵树
#   里没有任何 ST 2094-40（HDR10+）代码 —— 用它测"HDR10+ 有没有额外开销"，量的是
#   一台没接传感器的表。出货树 third_party/fff_project 才有 HdrMetadataParse /
#   HdrToneCurve / BuildDynamicToneSettings。这里不改 build_probe.py（并行的 agent
#   正在用那棵树），只加一条指向 fork 的构建路径。
#
# 用法：py 3fp/tools/build_fork_probe.py <探针名，不含 .cpp> [更多…]
# 输出：tests/output/perf-<date>/bin/<探针名>.exe，并把要加载的 DLL 摆到同目录：
#   FFF.Native.dll = 出货树那份；FFmpeg 运行时 = 3fp/build 那套（与 runtime/ 同版本）。
#   目录里另写 dll-manifest.txt，记每个 DLL 的 sha256 前 16 位 —— 读数必须能追溯到
#   "到底是哪个内核跑出来的"。
# ============================================================================
import glob
import hashlib
import os
import re
import shutil
import subprocess
import sys
import time

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FORK = os.path.join(REPO, "third_party", "fff_project", "FFF.Native")
KERNEL = os.path.join(REPO, "3fp", "kernel", "FFF.Native")
TOOLS = os.path.join(REPO, "3fp", "tools")
BUILD = os.path.join(REPO, "3fp", "build")
OUT = os.path.join(REPO, "tests", "output", "perf-hdr-20261010", "bin")
STUB = os.path.join(REPO, "tests", "output", "perf-hdr-20261010", "stub")
FFMPEG = os.path.join(REPO, "third_party", "fff_project", "third_party", "ffmpeg")

# Probes that compile kernel sources directly instead of calling the DLL need an
# empty pch.h, because HdrMetadataParse.cpp / HdrToneCurve.cpp open with
# #include "pch.h". The stub dir goes first on INCLUDE so it wins.
EXTRA_SOURCES = {
    "hdr10plus_cost_microbench": [
        os.path.join(FORK, "3FP", "Hdr", "HdrMetadataParse.cpp"),
        os.path.join(FORK, "3FP", "Hdr", "HdrToneCurve.cpp"),
    ],
}


def read_tlog(tlog_dir, name):
    path = os.path.join(tlog_dir, name)
    if not os.path.exists(path):
        return ""
    raw = open(path, "rb").read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace").lstrip("\ufeff")
    return raw.decode("utf-8-sig", errors="replace").replace("\x00", "")


def find_toolchain():
    # 工具链路径只用于找 cl.exe/SDK 版本，机器上唯一，取自哪棵树的 tlog 都一样。
    blob = ""
    for tree in (FORK, KERNEL):
        blob = read_tlog(os.path.join(tree, "obj", "x64", "Release", "FFF.Native.tlog"),
                         "CL.read.1.tlog")
        if blob:
            break
    msvc = re.search(r"([A-Z]:\\[^\";\r\n]*?\\VC\\Tools\\MSVC\\[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    sdk = re.search(r"Windows Kits\\10\\Include\\([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)", blob, re.I)
    if msvc and sdk:
        return msvc.group(1).strip(), sdk.group(1).strip()
    roots = sorted(
        [r for r in glob.glob(r"C:\Program Files*\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*")
         if os.path.isdir(r)],
        key=lambda p: [int(x) for x in os.path.basename(p).split(".") if x.isdigit()])
    sdks = sorted(
        [os.path.basename(r) for r in glob.glob(r"C:\Program Files*\Windows Kits\10\Include\*")
         if os.path.isdir(r)],
        key=lambda v: [int(x) for x in v.split(".") if x.isdigit()])
    return (roots[-1] if roots else None), (sdks[-1] if sdks else None)


def sha16(path):
    return hashlib.sha256(open(path, "rb").read()).hexdigest()[:16]


def stage_runtime():
    """摆出运行目录：出货内核 + 当前 FFmpeg 运行时。绝不写 3fp/build。"""
    os.makedirs(OUT, exist_ok=True)
    kernel_dll = os.path.join(FORK, "x64", "Release", "FFF.Native.dll")
    if not os.path.exists(kernel_dll):
        print("  x 出货内核缺失: %s" % kernel_dll)
        return None
    staged = os.path.join(OUT, "FFF.Native.dll")
    os.makedirs(OUT, exist_ok=True)
    # Skip the copy when the staged DLL already matches the tree: rewriting a file that a
    # running sample has loaded fails with a sharing violation, and an identical copy
    # buys nothing anyway.
    if not os.path.exists(staged) or sha16(kernel_dll) != sha16(staged):
        shutil.copy2(kernel_dll, staged)
    manifest = ["FFF.Native.dll  %s  %d  (third_party/fff_project)" %
                (sha16(staged), os.path.getsize(staged))]
    for name in sorted(os.listdir(BUILD)):
        if not name.lower().endswith(".dll") or name == "FFF.Native.dll":
            continue
        src = os.path.join(BUILD, name)
        dst = os.path.join(OUT, name)
        if not os.path.exists(dst) or sha16(src) != sha16(dst):
            shutil.copy2(src, dst)
        manifest.append("%s  %s  %d" % (name, sha16(dst), os.path.getsize(dst)))
    with open(os.path.join(OUT, "dll-manifest.txt"), "w", encoding="utf-8") as handle:
        handle.write("# staged at %s\n" % time.strftime("%Y-%m-%d %H:%M:%S"))
        handle.write("\n".join(manifest) + "\n")
    print("  运行目录 %s（%d 个 DLL，清单 dll-manifest.txt）" % (OUT, len(manifest)))
    return OUT


def main():
    if len(sys.argv) < 2:
        print("usage: py 3fp/tools/build_fork_probe.py <probe-name> [more...]")
        return 2
    msvc_root, sdk_ver = find_toolchain()
    if not msvc_root:
        print("找不到 MSVC 工具链")
        return 1
    cl = os.path.join(msvc_root, "bin", "Hostx64", "x64", "cl.exe")
    if not os.path.exists(cl):
        print("  cl.exe 缺失: %s" % cl)
        return 1
    sdk_inc = r"C:\Program Files (x86)\Windows Kits\10\Include\%s" % sdk_ver
    sdk_lib = r"C:\Program Files (x86)\Windows Kits\10\Lib\%s" % sdk_ver
    if not os.path.isdir(sdk_inc):
        # 部分机器 SDK 在 Program Files 而非 (x86)
        alt = sdk_inc.replace("(x86)\\", "")
        if os.path.isdir(alt):
            sdk_inc = alt
            sdk_lib = sdk_lib.replace("(x86)\\", "")
    print("MSVC : %s\nSDK  : %s" % (msvc_root, sdk_ver))

    kernel_lib = os.path.join(FORK, "x64", "Release", "FFF.Native.lib")
    if not os.path.exists(kernel_lib):
        print("  x 出货树导入库缺失: %s" % kernel_lib)
        return 1

    env = dict(os.environ)
    os.makedirs(STUB, exist_ok=True)
    stub_pch = os.path.join(STUB, "pch.h")
    # The kernel TUs rely on std::min/max/clamp reaching them through the real pch.h.
    # The stub must therefore carry those standard headers, otherwise the shipping
    # source fails to compile for reasons unrelated to what is being measured.
    want = '#pragma once\n#include <algorithm>\n#include <cmath>\n#include <cstdint>\n'
    if not os.path.exists(stub_pch) or open(stub_pch, "r", encoding="utf-8").read() != want:
        with open(stub_pch, "w", encoding="utf-8") as handle:
            handle.write(want)
    env["INCLUDE"] = ";".join([
        STUB,
        FORK, os.path.join(FORK, "3FP", "Api"),
        os.path.join(FFMPEG, "include"),
        os.path.join(msvc_root, "include"),
        os.path.join(sdk_inc, "ucrt"), os.path.join(sdk_inc, "shared"),
        os.path.join(sdk_inc, "um"),
    ])
    env["LIB"] = ";".join([
        os.path.join(msvc_root, "lib", "x64"),
        os.path.join(sdk_lib, "ucrt", "x64"),
        os.path.join(sdk_lib, "um", "x64"),
        os.path.join(FFMPEG, "lib", "x64"),
    ])
    env["PATH"] = os.path.dirname(cl) + os.pathsep + env.get("PATH", "")

    if stage_runtime() is None:
        return 1

    failures = 0
    for name in sys.argv[1:]:
        src = os.path.join(TOOLS, name + ".cpp")
        if not os.path.exists(src):
            print("  [%s] 源文件不存在: %s" % (name, src))
            failures += 1
            continue
        exe = os.path.join(OUT, name + ".exe")
        extra = EXTRA_SOURCES.get(name, [])
        missing = [s for s in extra if not os.path.exists(s)]
        if missing:
            print("  x %s 附加源文件缺失: %s" % (name, ", ".join(missing)))
            failures += 1
            continue
        if len(extra) > 0:
            # /Fo must be a directory when more than one translation unit is compiled
            obj_target = OUT + "\\"
        else:
            obj_target = os.path.join(OUT, name + ".obj")
        cmd = [cl, "/nologo", "/std:c++17", "/O2", "/EHsc", "/W3", "/DNDEBUG",
               "/D_CRT_SECURE_NO_WARNINGS",
               src, "/Fo:" + obj_target,
               ] + extra + [
               "/Fe:" + exe,
               "/link", kernel_lib,
               "user32.lib", "gdi32.lib", "ole32.lib", "shell32.lib", "advapi32.lib",
               "avformat-63.lib", "avcodec-63.lib", "avutil-61.lib",
               "ws2_32.lib", "secur32.lib", "bcrypt.lib"]
        print("\n[compile] %s  (fork tree: %s%s)" % (
            name, FORK, "  +%d kernel sources compiled in" % len(extra) if extra else ""))
        r = subprocess.run(cmd, env=env, capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout)
            print(r.stderr)
            failures += 1
        else:
            print(r.stdout.strip())
            print("  OK %s  (%d 字节)  exe sha256=%s" %
                  (exe, os.path.getsize(exe), sha16(exe)))
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
