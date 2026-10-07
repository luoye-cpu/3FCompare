#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建 3FP 内核（FFF.Native）。

## 历史与现状

本脚本**曾经**是一个 tlog 复放器（276 行手工解析 CL/link/rc 命令并逐条复放），
原因写在旧版头部：

    本机 MSBuild.exe 被安全策略判为 LOLBin（bash 与 PowerShell 下均被拒），
    而 dotnet build 不支持 .vcxproj，故只能复放 MSBuild 上次真实下发的命令。

复放器把产物写到 `x64/Release_anim/`（全新目录，不破坏 `obj/x64/Release` 的增量状态），
再由**人工**确认后拷回 `x64/Release/`。docs 里把这一步称为"人工双镜像纪律"。

**该纪律正是版本漂移的根源**：探针构建指向 `Release_anim`，而发布使用 `Release`，
两者长期不是同一份产物。实测后果 —— `Release|x64` 缺少 `FFF_WITH_LIBIAMF` 定义，
出货版**完全没有 IAMF 支持**，而所有 IAMF 验证都跑在 `Release_anim` 上因而全绿。

## 现在

MSBuild 已可正常调用（连续多次 `/t:Rebuild` 均 0 错误通过），
`dotnet build` 也不支持 vcxproj，所以本脚本改为**对 MSBuild 的薄封装**：
不再有旁路目录、不再需要人工拷贝、探针与出货使用**同一个** `x64/Release`。

旧的复放器代码保留在本文件的 git 历史里（`git log -p tools/build_kernel_manual.py`）——
它记录了若干真实踩过的坑（LNK2001 因只传参数不传 .obj、CVT1100 因 .res 重复传入、
`Release_anim` 被二次替换成 `Release_anim_ANIM`）。若将来 MSBuild 再次不可用，
可从历史中取回。

用法：
    python tools/build_kernel_manual.py            # Release|x64，增量
    python tools/build_kernel_manual.py --rebuild  # 干净重建
"""

import os
import subprocess
import sys

ROOT = r"C:\PLAN\3FCompare\3fp"
PROJECT = os.path.join(ROOT, "kernel", "FFF.Native", "FFF.Native.vcxproj")
OUTPUT_DLL = os.path.join(ROOT, "kernel", "FFF.Native", "x64", "Release", "FFF.Native.dll")
DEPLOY_DIR = os.path.join(ROOT, "build")
CONFIGURATION = "Release"
PLATFORM = "x64"


def find_msbuild():
    """定位 MSBuild。用 vswhere 而不是硬编码版本号 ——
    硬编码会在换 Visual Studio 版本后静默失效。"""
    vswhere = os.path.join(
        os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)"),
        "Microsoft Visual Studio", "Installer", "vswhere.exe")
    if os.path.exists(vswhere):
        try:
            result = subprocess.run(
                [vswhere, "-latest", "-requires", "Microsoft.Component.MSBuild",
                 "-find", r"MSBuild\**\Bin\MSBuild.exe"],
                capture_output=True, text=True, timeout=30)
            for line in result.stdout.splitlines():
                line = line.strip()
                if line and os.path.exists(line):
                    return line
        except Exception:
            pass

    # 兜底：扫常见安装路径
    import glob
    for pattern in (
        r"C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe",
        r"C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\MSBuild.exe",
    ):
        hits = sorted(glob.glob(pattern))
        if hits:
            return hits[-1]
    return None


def main():
    rebuild = "--rebuild" in sys.argv[1:]

    if not os.path.exists(PROJECT):
        print("找不到工程：%s" % PROJECT)
        return 1

    msbuild = find_msbuild()
    if not msbuild:
        print("找不到 MSBuild。请安装 Visual Studio（含 C++ 生成工具），")
        print("或参考本文件头部说明，从 git 历史取回旧的 tlog 复放器。")
        return 1

    print("MSBuild : %s" % msbuild)
    print("工程    : %s" % PROJECT)
    print("配置    : %s|%s%s" % (CONFIGURATION, PLATFORM, "  (干净重建)" if rebuild else ""))

    command = [msbuild, PROJECT,
               "/p:Configuration=%s" % CONFIGURATION,
               "/p:Platform=%s" % PLATFORM,
               "/m", "/nologo", "/v:minimal"]
    if rebuild:
        command.append("/t:Rebuild")

    started = None
    import time
    started = time.time()
    result = subprocess.run(command, cwd=ROOT)
    elapsed = time.time() - started

    if result.returncode != 0:
        print("\n构建失败（exit=%d，%.1fs）" % (result.returncode, elapsed))
        return result.returncode

    if not os.path.exists(OUTPUT_DLL):
        print("\n构建报成功但未找到产物：%s" % OUTPUT_DLL)
        return 1

    size = os.path.getsize(OUTPUT_DLL)
    print("\n产物: %s  (%d 字节, %.1fs)" % (OUTPUT_DLL, size, elapsed))

    marker = os.path.join(DEPLOY_DIR, "FFF.Native.dll")
    if os.path.isdir(DEPLOY_DIR):
        import shutil
        shutil.copy2(OUTPUT_DLL, marker)
        print("部署: %s" % marker)

    return 0


if __name__ == "__main__":
    sys.exit(main())
