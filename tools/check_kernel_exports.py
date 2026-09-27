#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""列出 FFF.Native.dll 的导出符号，核对"必需导出是否齐全" + 导出总数下界 + API 版本。

2026-09-20（docs/41 P0-3）：原先**只打印、永远 exit 0**，且全仓没有任何脚本调用它
⇒ 导出面被裁 / ABI 错位要到运行时才炸。现加断言，失败时 exit 1，并接入 tools/发布门禁.ps1。

2026-09-21（docs/41 W5 §六#3 + §13.6）两处加固：
  1. `--expect N`（**恰好等于**）→ 新增 `--min-exports N`（**≥**）。
     理由：内核合法新增导出是正常演进，硬相等会让每次内核升级都假红。
     `--expect` **保留且语义不变**（向后兼容既有调用/文档）。
  2. `REQUIRED` 由 5 个扩到 **25 个** = 托管侧 P/Invoke 引用的全量。
     理由（§13.6）：旧清单只列 5 个 ⇒ 内核"删一个同时改名/新增一个"（总数仍 82）时
     计数相等、5 项全在 ⇒ 门禁判绿而运行时 `EntryPointNotFoundException`。

REQUIRED 的生成方式（**不是猜的**，逐名实测）：
  扫描 src/3FCompare.Core/Backend/Interop/Fff3FpNative.cs 与 Fff3FpNativeProbe.cs 里
  全部 `[LibraryImport("FFF.Native")]` / `[DllImport(...)]` 的入口名（去重 25 个），
  再逐个到 FFF.Native.dll 的导出表里查证存在（2026-09-21 实测：Release 与 Release_pr9
  均为 82 导出、25 个候选**全部存在**，无一落空）。

  ⚠ 反例：`FFF3FP_SetPacingConfig` **并未导出**（旧脚本把它打印成 "no" 却不影响退出码）
    —— 不要加进必需清单，否则门禁恒红。实测 Debug 内核（2026-08-28 构建，78 导出）
    也缺 `FFF3FP_GetRenderTargetInfo` / `FFF3FP_Redraw` 两个，故 `-KernelConfiguration Debug`
    跑门禁会判红 —— 那是**正确**的：那份内核确实会让托管侧抛 EntryPointNotFoundException。

基线（实测 third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll）：导出 82 个、API 15。

用法：
    python tools/check_kernel_exports.py <dll>     # 默认即按基线校验：导出 >= 82、API >= 15
    python tools/check_kernel_exports.py <dll> [--min-exports 82] [--min-api 15]
    python tools/check_kernel_exports.py <dll> [--expect 82]        # 旧写法，仍可用

2026-09-22 加固（两处）：
  1. `--min-exports` / `--min-api` 默认值由 0 改为 **82 / 15**。
     原默认 0 = fail-open：漏传参数就静默不校验，门禁形同虚设。
  2. API 版本探测不再用裸 `ctypes.CDLL`：Python 3.8+ 默认
     LOAD_LIBRARY_SEARCH_DEFAULT_DIRS，**不搜 DLL 自身目录** ⇒ 依赖 DLL 在
     同目录时也会 load 失败，被判成"假红"。现显式把 DLL 目录加进搜索路径，
     并把「加载失败」与「导出缺失」分开诊断。导出表本身始终走 PE 解析，
     与加载成功与否无关。
"""
import argparse
import ctypes
import os
import struct
import sys

# 托管侧 P/Invoke 引用的全量入口（25 个）。来源见模块 docstring；每个都已实测存在于内核导出表。
# 分组顺序与 src/3FCompare.Core/Backend/Interop/Fff3FpNative.cs 一致，便于对照。
REQUIRED = (
    # ---- 生命周期 ----
    "FFF3FP_GetApiVersion",
    "FFF3FP_Create",
    "FFF3FP_Destroy",
    # ---- 控制 ----
    "FFF3FP_Open",
    "FFF3FP_Play",
    "FFF3FP_Pause",
    "FFF3FP_Stop",
    "FFF3FP_Seek",
    "FFF3FP_SeekFrame",
    "FFF3FP_StepFrame",
    "FFF3FP_SelectVideoStream",
    "FFF3FP_SelectAudioStream",
    "FFF3FP_SetVolume",
    "FFF3FP_SetOutputWindow",
    "FFF3FP_SetViewTransform",
    "FFF3FP_SetColorMode",
    "FFF3FP_SetPresentConfig",
    # ---- 读取 ----
    "FFF3FP_GetSnapshot",
    "FFF3FP_GetMediaInfo",
    "FFF3FP_GetLastError",
    "FFF3FP_ReadVideoPixel",
    "FFF3FP_ReadVideoPixelRegion",
    "FFF3FP_GetRenderTargetInfo",
    "FFF3FP_Redraw",
    # ---- 日志回调（Fff3FpNativeProbe.cs）----
    "FFF3FP_SetLogCallback",
)


def exports(path):
    data = open(path, "rb").read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    mach, nsec, _, _, _, optsz, _ = struct.unpack_from("<HHIIIHH", data, pe + 4)
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    dd_off = opt + (112 if magic == 0x20B else 96)
    rva, size = struct.unpack_from("<II", data, dd_off)
    # 节表：找 .rdata 等所在节做 RVA->FOA
    secs = []
    so = opt + optsz
    for i in range(nsec):
        name = data[so + i * 40: so + i * 40 + 8].rstrip(b"\0")
        vs, va, rs, ra = struct.unpack_from("<IIII", data, so + i * 40 + 8)
        secs.append((va, vs, rs, ra))
    def r2o(r):
        # 用虚拟大小（不是 SizeOfRawData）界定范围，否则节区间重叠会错配
        for va, vs, rs, ra in secs:
            if va <= r < va + max(vs, rs):
                return ra + (r - va)
        return None
    o = r2o(rva)
    # IMAGE_EXPORT_DIRECTORY 是 10 个 DWORD（含 Major/Minor 合并），不是 8 个
    hdr = struct.unpack_from("<10I", data, o)
    _, _, _, _, _, _nfun, nname, _afun, aname, _aord = hdr
    names = []
    for i in range(nname):
        nr = struct.unpack_from("<I", data, r2o(aname) + i * 4)[0]
        s = r2o(nr)
        e = data.index(b"\0", s)
        names.append(data[s:e].decode("ascii", "replace"))
    return sorted(names)


def probe_api_version(path):
    """尝试读取 API 版本，返回 (api, err)。二者必有一个为 None。

    必须把「加载失败」与「导出缺失」分开报：
      - 加载失败：多为依赖 DLL 不在搜索路径（原实现的"假红"来源）/ 位数不符；
      - 导出缺失：内核真的少了入口，是实打实的 ABI 断裂。
    Python 3.8+ 的 CDLL/WinDLL 默认用 LOAD_LIBRARY_SEARCH_DEFAULT_DIRS，
    **不搜 DLL 自身所在目录**，所以先显式 add_dll_directory。
    """
    dll_dir = os.path.dirname(os.path.abspath(path))
    try:
        os.add_dll_directory(dll_dir)
    except (AttributeError, OSError):
        pass  # 非 Windows 或该目录不可加：退回默认搜索顺序
    try:
        d = ctypes.WinDLL(path)
    except OSError as ex:
        return None, ("DLL 加载失败（依赖 DLL 缺失或位数不符？已把 %s 加入搜索路径）: %s"
                      % (dll_dir, ex))
    try:
        fn = d.FFF3FP_GetApiVersion
    except AttributeError:
        return None, "DLL 已加载，但未导出 FFF3FP_GetApiVersion（导出缺失）"
    try:
        return int(fn()), None
    except Exception as ex:
        return None, "调用 FFF3FP_GetApiVersion 失败: %s" % ex


def main(argv):
    ap = argparse.ArgumentParser(description="核对 FFF.Native.dll 导出面与 API 版本")
    ap.add_argument("dll", nargs="+", help="待检查的 FFF.Native.dll 路径")
    # 下界：内核合法新增导出是正常演进，只要不少于 N 即通过
    # 默认 82 = 本项目内核基线（fail-closed：不传参数也按基线校验）
    ap.add_argument("--min-exports", type=int, default=82,
                    help="导出个数下界，>=N 即通过；默认 82（内核基线），0=不校验")
    # 精确相等：保留旧语义，向后兼容既有调用/文档；0=不校验
    ap.add_argument("--expect", type=int, default=0,
                    help="期望导出个数（恰好相等）；0=不校验。旧写法，新调用请用 --min-exports")
    ap.add_argument("--min-api", type=int, default=15,
                    help="最低 API 版本；默认 15（内核基线），0=不校验")
    a = ap.parse_args(argv)

    ok = True
    for p in a.dll:
        try:
            xs = exports(p)
        except Exception as ex:
            print("%s -> 解析失败: %s" % (p, ex))
            ok = False
            continue

        print("%s -> 导出 %d 个" % (p, len(xs)))
        missing = [w for w in REQUIRED if w not in xs]
        for want in REQUIRED:
            print("   %-32s %s" % (want, "YES" if want in xs else "MISSING"))

        api, api_err = probe_api_version(p)
        if api_err:
            print("   API version 读取失败: %s" % api_err)
            ok = False
        else:
            print("   API version = %d" % api)

        if a.expect and len(xs) != a.expect:
            print("   [FAIL] 导出数 %d != 期望 %d" % (len(xs), a.expect))
            ok = False
        if a.min_exports and len(xs) < a.min_exports:
            print("   [FAIL] 导出数 %d < 下界 %d" % (len(xs), a.min_exports))
            ok = False
        if missing:
            print("   [FAIL] 缺少必需导出(%d/%d): %s"
                  % (len(missing), len(REQUIRED), ", ".join(missing)))
            ok = False
        if a.min_api and (api is None or api < a.min_api):
            print("   [FAIL] API version %s < 最低 %d" % (api, a.min_api))
            ok = False

    print("结论: %s" % ("OK" if ok else "FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
