#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build the 3FCompare kernel from 3fp/kernel/FFF.Native (MSBuild.exe is blocked
by policy on this machine, so the vcxproj settings are replayed by hand).

Why a second build script
-------------------------
tools/build_kernel_manual.py replays the MSBuild tlog of
``third_party/fff_project/FFF.Native`` — that checkout is the *upstream PR*
repository and does not carry the 3FCompare private extensions (for example the
wide-gamut scRGB gate ``IsWideGamutSource``). The kernel the app actually runs
is the local, gitignored copy under ``3fp/kernel/FFF.Native``; STATUS.md records
its build as 776192 bytes. This script builds **that** tree.

Configuration mirrors FFF.Native.vcxproj "Release|x64":
  * include: $(ProjectDir), $(ProjectDir)3FR, ..\\third_party\\ffmpeg\\include,
             $(LibassRoot)\\include
  * lib dirs: ..\\third_party\\ffmpeg\\lib\\x64, $(LibassRoot)\\lib
  * libs: ass + FFmpeg (delay-loaded) + d3d11/d2d/dwrite/wasapi/...
  * std:c++20, /utf-8, precompiled header pch.h, unicode, NDEBUG

Usage
-----
    python tools/build_kernel_3fp.py                 # build into x64/Release_sdr
    python tools/build_kernel_3fp.py --config Debug  # (Debug needs the debug libass dir)

The output directory is deliberately *not* x64/Release so an existing good DLL is
never clobbered by a partial build; copy the result into place only after a green
build (see STATUS.md: sync the DLL images and verify mtime > source mtime).
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

MSVC = r"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231"
SDK = r"C:\Program Files (x86)\Windows Kits\10"
SDKVER = "10.0.28000.0"
CL = os.path.join(MSVC, "bin", "Hostx64", "x64", "cl.exe")
LINK = os.path.join(MSVC, "bin", "Hostx64", "x64", "link.exe")
RC = os.path.join(SDK, "bin", SDKVER, "x64", "rc.exe")

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NAT = os.path.join(REPO, "3fp", "kernel", "FFF.Native")
VCXPROJ = os.path.join(NAT, "FFF.Native.vcxproj")

# Dependency roots: the vcxproj resolves them relative to the project dir, but on
# this machine they may live in either the 3fp tree or the third_party checkout.
FFMPEG_CANDIDATES = [
    os.path.join(REPO, "3fp", "kernel", "third_party", "ffmpeg"),
    os.path.join(REPO, "third_party", "fff_project", "third_party", "ffmpeg"),
]
VCPKG_CANDIDATES = [
    os.path.join(REPO, "3fp", "kernel", "third_party", "vcpkg_installed", "x64-windows"),
    os.path.join(REPO, "third_party", "fff_project", "third_party", "vcpkg_installed", "x64-windows"),
]

LINK_LIBS = [
    "ass.lib", "avcodec-63.lib", "avformat-63.lib", "avutil-61.lib",
    "swresample-7.lib", "swscale-10.lib", "avfilter-12.lib", "delayimp.lib",
    # libbluray 在 FFF.Native.vcxproj 的 AdditionalDependencies 里（DiscInput.cpp 的 bd_*
    # 16 个符号全靠它）。本清单过去漏了它 ⇒ 22 个编译步全过、链接才报 LNK2001×16。
    "bluray.lib",
    "d3d11.lib", "d3dcompiler.lib", "dxgi.lib", "dxguid.lib", "d2d1.lib",
    "dwrite.lib", "ole32.lib", "runtimeobject.lib", "propsys.lib", "avrt.lib",
    "kernel32.lib", "user32.lib", "gdi32.lib", "winspool.lib", "comdlg32.lib",
    "advapi32.lib", "shell32.lib", "oleaut32.lib", "uuid.lib", "odbc32.lib",
    "odbccp32.lib",
]
DELAY_LOAD = [
    "ass-9.dll", "avcodec-63.dll", "avformat-63.dll", "avutil-61.dll",
    "swresample-7.dll", "swscale-10.dll", "avfilter-12.dll",
    # 与 vcxproj 的 DelayLoadedDLLs 对齐：bluray 不延迟加载的话，进程启动就要在 exe
    # 同目录找到 bluray-3.dll，否则整个内核 LoadLibrary 失败 ⇒ 托管侧静默降级演示模式。
    "bluray-3.dll",
]

COMMON = [
    "/nologo", "/c", "/W3", "/sdl", "/O2", "/Oi", "/GL", "/Gy",
    "/D", "NDEBUG", "/D", "FFFNATIVE_EXPORTS", "/D", "_WINDOWS", "/D", "_USRDLL",
    "/D", "_WINDLL", "/D", "_UNICODE", "/D", "UNICODE",
    "/Gm-", "/EHsc", "/MD", "/GS", "/fp:precise",
    "/Zc:wchar_t", "/Zc:forScope", "/Zc:inline", "/std:c++20", "/permissive-",
    "/Gd", "/TP", "/FC", "/utf-8", "/Zi", "/diagnostics:column",
]


def first_existing(candidates, subdir=""):
    for base in candidates:
        path = os.path.join(base, subdir) if subdir else base
        if os.path.isdir(path):
            return path
    return None


def sources_from_vcxproj():
    ns = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}
    tree = ET.parse(VCXPROJ)
    found = []
    for node in tree.iter("{http://schemas.microsoft.com/developer/msbuild/2003}ClCompile"):
        include = node.get("Include")
        if not include:
            continue
        include = include.replace("\\", os.sep)
        if os.path.basename(include) == "pch.cpp":
            continue  # compiled first with /Yc
        found.append(include)
    return found


def run(cmd, label, env, cwd, index, total):
    started = time.time()
    proc = subprocess.run(cmd, env=env, cwd=cwd, shell=False,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    text = proc.stdout.decode("utf-8", errors="replace")
    print("[%2d/%2d] %-28s exit=%d  %.1fs" % (index, total, label, proc.returncode,
                                               time.time() - started))
    if text.strip():
        print(text.strip()[:3000])
    sys.stdout.flush()
    return proc.returncode == 0, text


def build_probe(env, out_dir) -> bool:
    """Compile 3fp/tools/verify_image_mode.cpp against the freshly built DLL.

    The probe needs a real window and a real FFmpeg, so it is the only way to
    read back what the swap chain actually decided (outBits == 16 means scRGB).
    """
    src = os.path.join(REPO, "3fp", "tools", "verify_image_mode.cpp")
    exe = os.path.join(REPO, "3fp", "build", "verify_image_mode.exe")
    os.makedirs(os.path.dirname(exe), exist_ok=True)
    import_lib = os.path.join(out_dir, "FFF.Native.lib")
    if not os.path.exists(import_lib):
        print("import library missing: %s" % import_lib)
        return False
    # user32 is not in the console default library set: the probe owns a real
    # window (RegisterClassW / CreateWindowExW / message pump).
    cmd = [CL, "/nologo", "/EHsc", "/std:c++20", "/I", NAT,
           "/Fo:" + os.path.join(REPO, "3fp", "build") + os.sep,
           "/Fe:" + exe, src, import_lib, "user32.lib"]
    ok, out = run(cmd, "verify_image_mode.cpp", env, NAT, 1, 1)
    if ok:
        print("probe: %s" % exe)
    return ok


def build_white_probe(env) -> bool:
    """Compile 3fp/tools/sdr_white_probe.cpp (self-contained, C++/WinRT).

    The V7 verdict probe: an FP16 scRGB window outputs a known linear value and
    a Float16 Windows.Graphics.Capture pool reads back what DWM actually put on
    screen. Needs cppwinrt headers (already in the include list) and
    windowsapp.lib for the WinRT activation factory.
    """
    src = os.path.join(REPO, "3fp", "tools", "sdr_white_probe.cpp")
    exe = os.path.join(REPO, "3fp", "build", "sdr_white_probe.exe")
    os.makedirs(os.path.dirname(exe), exist_ok=True)
    cmd = [CL, "/nologo", "/EHsc", "/std:c++20", "/utf-8",
           "/Fo:" + os.path.join(REPO, "3fp", "build") + os.sep,
           "/Fe:" + exe, src,
           "d3d11.lib", "dxgi.lib", "windowsapp.lib", "user32.lib", "gdi32.lib"]
    ok, out = run(cmd, "sdr_white_probe.cpp", env, NAT, 1, 1)
    if ok:
        print("probe: %s" % exe)
    return ok


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", default="Release_sdr",
                        help="output directory name under x64/ (default Release_sdr)")
    parser.add_argument("--probe", action="store_true",
                        help="after the kernel build, also build the verify_image_mode probe")
    parser.add_argument("--only-probe", action="store_true",
                        help="skip the kernel build, only build the probe")
    parser.add_argument("--white-probe", action="store_true",
                        help="build the sdr_white_probe V7 verdict tool (C++/WinRT)")
    args = parser.parse_args()

    ffmpeg = first_existing(FFMPEG_CANDIDATES)
    vcpkg = first_existing(VCPKG_CANDIDATES)
    if ffmpeg is None or vcpkg is None:
        print("missing dependencies: ffmpeg=%s vcpkg=%s" % (ffmpeg, vcpkg))
        return 2
    if not (os.path.isdir(os.path.join(ffmpeg, "include"))
            and os.path.isdir(os.path.join(ffmpeg, "lib", "x64"))):
        print("ffmpeg tree incomplete: %s" % ffmpeg)
        return 2

    obj_dir = os.path.join(NAT, "obj", "x64", args.config)
    out_dir = os.path.join(NAT, "x64", args.config)
    # Probe-only runs must not wipe the kernel's incremental state: --only-probe
    # / --white-probe skip the build, so the clean belongs after those branches.
    os.makedirs(out_dir, exist_ok=True)

    include = ";".join([
        os.path.join(MSVC, "include"),
        os.path.join(SDK, "Include", SDKVER, "ucrt"),
        os.path.join(SDK, "Include", SDKVER, "um"),
        os.path.join(SDK, "Include", SDKVER, "shared"),
        os.path.join(SDK, "Include", SDKVER, "winrt"),
        os.path.join(SDK, "Include", SDKVER, "cppwinrt"),
        NAT,
        os.path.join(NAT, "3FR"),
        os.path.join(ffmpeg, "include"),
        os.path.join(vcpkg, "include"),
    ])
    lib = ";".join([
        os.path.join(MSVC, "lib", "x64"),
        os.path.join(SDK, "Lib", SDKVER, "ucrt", "x64"),
        os.path.join(SDK, "Lib", SDKVER, "um", "x64"),
        os.path.join(ffmpeg, "lib", "x64"),
        os.path.join(vcpkg, "lib"),
    ])
    env = dict(os.environ)
    env.update({
        "INCLUDE": include,
        "LIB": lib,
        "PATH": ";".join([
            os.path.join(MSVC, "bin", "Hostx64", "x64"),
            os.path.join(SDK, "bin", SDKVER, "x64"),
            r"C:\Windows\System32",
            r"C:\Windows",
        ]),
        "TMP": r"C:\Windows\Temp",
    })

    if args.only_probe:
        return 0 if build_probe(env, out_dir) else 1
    if args.white_probe:
        return 0 if build_white_probe(env) else 1

    # Start from a clean object tree. A previous failed build leaves a .obj that
    # is newer than the PCH, which /LTCG then reports as the confusing
    # "C1382: PCH ... regenerated after ...obj was produced" instead of the
    # original error; a stale .pch also hides real compile failures.
    if os.path.isdir(obj_dir):
        shutil.rmtree(obj_dir)
    os.makedirs(obj_dir, exist_ok=True)

    sources = sources_from_vcxproj()
    pch_path = os.path.join(obj_dir, "FFF.Native.pch")
    total = len(sources) + 2
    errors = []

    index = 1
    # /Yc 与 /Yu 的头名**不能带引号**：subprocess 走 shell=False 时 Python 会把内嵌的 "
    # 转义成 \" ，cl 于是把要匹配的 include 语句当成字面量 `"pch.h"`（含引号），
    # 逐个源文件报 C2857/C1010 —— 22 个文件 0.0s 全灭，且第一个失败的是 pch.cpp 本身。
    ok, out = run([CL] + COMMON + ["/Ycpch.h", "/Fp" + pch_path,
                                   "/Fo" + obj_dir + "\\", "/Fd" + os.path.join(obj_dir, "vc145.pdb"),
                                   os.path.join(NAT, "pch.cpp")],
                  "pch.cpp (/Yc)", env, NAT, index, total)
    if not ok:
        errors.append(("pch.cpp", out))

    for source in sources:
        index += 1
        ok, out = run([CL] + COMMON + ["/Yupch.h", "/Fp" + pch_path,
                                       "/Fo" + obj_dir + "\\", "/Fd" + os.path.join(obj_dir, "vc145.pdb"),
                                       os.path.join(NAT, source)],
                      os.path.basename(source), env, NAT, index, total)
        if not ok:
            errors.append((source, out))

    res = os.path.join(obj_dir, "FFF.Native.res")
    index += 1
    ok, out = run([RC, "/D", "_UNICODE", "/D", "UNICODE", "/l", "0x0409", "/nologo",
                   "/fo" + res, os.path.join(NAT, "FFF.Native.rc")],
                  "FFF.Native.rc", env, NAT, index, total)
    if not ok:
        errors.append(("rc", out))

    objects = [os.path.join(obj_dir, name) for name in os.listdir(obj_dir)
               if name.lower().endswith(".obj")]
    dll = os.path.join(out_dir, "FFF.Native.dll")
    link_cmd = [LINK, "/OUT:" + dll, "/NOLOGO",
                "/LIBPATH:" + os.path.join(ffmpeg, "lib", "x64"),
                "/LIBPATH:" + os.path.join(vcpkg, "lib")] + LINK_LIBS
    for name in DELAY_LOAD:
        link_cmd.append("/DELAYLOAD:" + name)
    link_cmd += ["/MANIFEST", "/MANIFESTUAC:NO", "/manifest:embed",
                 "/manifestinput:" + os.path.join(MSVC, "include", "manifest", "segmentheap.manifest"),
                 "/DEBUG", "/PDB:" + os.path.join(out_dir, "FFF.Native.pdb"),
                 "/SUBSYSTEM:WINDOWS", "/OPT:REF", "/OPT:ICF", "/LTCG",
                 "/IMPLIB:" + os.path.join(out_dir, "FFF.Native.lib"),
                 "/MACHINE:X64", "/DLL"] + objects + [res]
    index += 1
    ok, out = run(link_cmd, "link", env, NAT, index, total)
    if not ok:
        errors.append(("link", out))

    if args.probe and not errors:
        if not build_probe(env, out_dir):
            errors.append(("probe", "see above"))

    print("\n================ result ================")
    print("failures: %d" % len(errors))
    for name, text in errors:
        print("---- %s ----" % name)
        print(text[:3000])
    if os.path.exists(dll):
        print("dll: %s  %d bytes" % (dll, os.path.getsize(dll)))
    else:
        print("dll missing: %s" % dll)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
