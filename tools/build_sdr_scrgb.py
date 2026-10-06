#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""One-shot driver for the "SDR 10-bit / wide gamut through scRGB" change.

It chains the three steps that MUST happen in this order, because getting the
order wrong produces a silently wrong picture:

  1. regenerate 3FP/Render/ShaderBytecode.h from the HLSL in VideoRenderer.cpp
     (the production shader is precompiled DXBC, so editing HLSL alone does
     nothing);
  2. flip ``kShaderGamutTristate`` to true — only now may a P3 source be
     reported as gamut 2. Flipping first would make the *old* bytecode treat
     P3 as Rec.2020, which is worse than today's Rec.709 passthrough;
  3. rebuild the kernel with tools/build_kernel_3fp.py.

Usage
-----
    python tools/build_sdr_scrgb.py
    python tools/build_sdr_scrgb.py --sync          # also copy the DLL into the app
    python tools/build_sdr_scrgb.py --no-p3         # keep P3 off (step 2 skipped)

--sync copies the fresh DLL next to the app binaries (Debug + Release) and into
3fp/build, keeping a .bak of whatever was there. Per STATUS.md the DLL mtime must
end up newer than the sources, so the copy is followed by an explicit touch.
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VIDEO_RENDERER = os.path.join(
    REPO, "3fp", "kernel", "FFF.Native", "3FP", "Render", "VideoRenderer.cpp"
)
KERNEL_DLL = os.path.join(
    REPO, "3fp", "kernel", "FFF.Native", "x64", "Release_sdr", "FFF.Native.dll"
)
SYNC_TARGETS = [
    os.path.join(REPO, "src", "3FCompare", "bin", "Debug", "net11.0-windows", "FFF.Native.dll"),
    os.path.join(REPO, "src", "3FCompare", "bin", "Release", "net11.0-windows", "FFF.Native.dll"),
    os.path.join(REPO, "3fp", "build", "FFF.Native.dll"),
]

FLAG_PATTERN = re.compile(r"constexpr\s+bool\s+kShaderGamutTristate\s*=\s*(true|false)\s*;")


def step(title: str) -> None:
    print("\n== %s ==" % title)
    sys.stdout.flush()


def run_script(name: str, extra: list[str]) -> bool:
    cmd = [sys.executable, os.path.join(REPO, "tools", name)] + extra
    print("$ " + " ".join(cmd))
    sys.stdout.flush()
    result = subprocess.run(cmd, cwd=REPO)
    return result.returncode == 0


def flip_p3_flag() -> bool:
    with open(VIDEO_RENDERER, "r", encoding="utf-8") as handle:
        text = handle.read()
    matches = FLAG_PATTERN.findall(text)
    if len(matches) != 1:
        print("kShaderGamutTristate occurrences: %d (expected 1) — refusing to guess"
              % len(matches))
        return False
    if matches[0] == "true":
        print("kShaderGamutTristate already true")
        return True
    text = FLAG_PATTERN.sub("constexpr bool kShaderGamutTristate = true;", text, count=1)
    with open(VIDEO_RENDERER, "w", encoding="utf-8", newline="") as handle:
        handle.write(text)
    print("kShaderGamutTristate: false -> true")
    return True


def sync_dll() -> bool:
    if not os.path.exists(KERNEL_DLL):
        print("kernel dll missing: %s" % KERNEL_DLL)
        return False
    for target in SYNC_TARGETS:
        directory = os.path.dirname(target)
        if not os.path.isdir(directory):
            print("skip (no such dir): %s" % directory)
            continue
        if os.path.exists(target):
            shutil.copy2(target, target + ".bak")
        shutil.copy2(KERNEL_DLL, target)
        # mtime must be newer than every source file, otherwise the next build
        # (or a stale-DLL check) believes the binary is up to date.
        os.utime(target, None)
        print("synced: %s (%d bytes)" % (target, os.path.getsize(target)))
    return True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sync", action="store_true",
                        help="copy the freshly built DLL next to the app binaries")
    parser.add_argument("--no-p3", action="store_true",
                        help="do not flip kShaderGamutTristate (P3 stays as today)")
    args = parser.parse_args()

    step("1/3 regenerate ShaderBytecode.h (fxc)")
    if not run_script("generate_shader_bytecode.py", []):
        print("shader regeneration failed — kernel NOT rebuilt")
        return 1

    if args.no_p3:
        step("2/3 P3 gamut switch: left off (--no-p3)")
    else:
        step("2/3 enable the P3 gamut switch")
        if not flip_p3_flag():
            return 1

    step("3/3 rebuild the kernel (3fp/kernel)")
    if not run_script("build_kernel_3fp.py", []):
        print("kernel build failed — DLL not synced")
        return 1

    if args.sync:
        step("sync DLL")
        if not sync_dll():
            return 1

    print("\nall steps done; kernel DLL: %s" % KERNEL_DLL)
    print("next: dotnet build + run host tests, then the V1-V11 checks in 3fp/docs/27 §7.3")
    return 0


if __name__ == "__main__":
    sys.exit(main())
