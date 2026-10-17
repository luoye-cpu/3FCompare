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
  全部 `[LibraryImport("FFF.Native")]` / `[DllImport(...)]` 的入口名（2026-09-21 去重 25 个；
  2026-09-29 加 `FFF3FP_SetFitLimitToNative` 后 26 个；
  2026-10-10 加 `FFF3FP_GetHdrSignalDiagnostics` 后 27 个；
  2026-10-10 晚加 `FFF3FP_SetDynamicMetadata` / `FFF3FP_ClearDynamicMetadata` 后 29 个；
  2026-10-13 按上面的扫描器重新对账，发现清单**落后托管侧 6 条**（VSR 两条、旧版
  HDR10+ 专用三条、GetHdrMetadataSource 一条），加上中性帧边数据环新入口
  `FFF3FP_CopyFrameSideDataAfter` 后为 35 个），
  再逐个到 FFF.Native.dll 的导出表里查证存在（2026-09-21 实测：Release 与 Release_pr9
  均为 82 导出、25 个候选**全部存在**，无一落空；2026-10-13 实测 3fp/build 内核
  104 导出、35 条**全部存在**）。

  2026-10-13 起这份清单不再是"手工维护的信任假设"：脚本每次运行都重扫托管源码并与
  REQUIRED 逐名对账（缺更多一条即 FAIL，`--no-interop-scan` 才降级成显式警告），
  所以"宿主加了 P/Invoke、清单忘了跟"这一类漂移自己会判红。

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

2026-10-14 加固（J2，按树声明的"已知缺失"对账）：
    出货树 TP（third_party/fff_project）与内核树 KF（3fp/kernel）本来就分叉：VSR 两条
    入口 KF 有、TP 没有（实测 TP 导出 94 / KF 导出 104），于是门禁 `[2/11]` **恒红**——
    而恒红的门禁只教会读者忽略红。现引入 KNOWN_MISSING_BY_TREE 这张**显式声明表**
    （哪棵树、缺哪几条、为什么），并把它做成**对账**而不是放宽：
      python tools/check_kernel_exports.py <dll>                  # 严格 35 条（默认，行为不变）
      python tools/check_kernel_exports.py <dll> --known-missing-tree auto
      python tools/check_kernel_exports.py <dll> --known-missing-tree third_party/fff_project
      python tools/check_kernel_exports.py --self-test-exemption  # 比较器自检（构造用例）
    实测缺失集合与声明集合**必须完全相等**：多缺一条=真回归判红；少缺一条=豁免过期判红并提示删条目。
    不传参数时仍是严格 35 条；`auto` 反查不到已声明的树时**不豁免**（fail-closed）。
    绝不做"缺了就警告"那种放宽。
"""
import argparse
import ctypes
import io
import os
import struct
import sys

# 托管侧 P/Invoke 引用的全量入口。来源见模块 docstring；每个都已实测存在于内核导出表。
# 条数不写在这里——写死的条数会过期（本文件就有过"注释 27 / 实表 29"的现案），
# 由末尾的 REQUIRED 自扫（--no-interop-scan 关不掉的那一段）与托管源码逐名对账。
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
    "FFF3FP_SetFitLimitToNative",
    "FFF3FP_SetColorMode",
    "FFF3FP_SetPresentConfig",
    # ---- 读取 ----
    "FFF3FP_GetSnapshot",
    "FFF3FP_GetHdrSignalDiagnostics",
    "FFF3FP_GetMediaInfo",
    "FFF3FP_GetLastError",
    "FFF3FP_ReadVideoPixel",
    "FFF3FP_ReadVideoPixelRegion",
    "FFF3FP_GetRenderTargetInfo",
    "FFF3FP_Redraw",
    # ---- 通用动态元数据注入通道（2026-10-10，Fff3FpNative.cs 同步导入）----
    "FFF3FP_SetDynamicMetadata",
    "FFF3FP_ClearDynamicMetadata",
    # ---- RTX VSR 与旧版 HDR10+ 专用入口（漏网的四条，2026-10-13 补齐）----
    # 这四条早已是托管侧声明的 P/Invoke，却没进必需清单：内核改名/删掉时
    # 门禁不响，要到运行时才 EntryPointNotFoundException。
    "FFF3FP_SetVideoSuperResolution",
    "FFF3FP_GetVideoSuperResolutionStatus",
    "FFF3FP_SetHdrDynamicMetadata",
    "FFF3FP_ClearHdrDynamicMetadata",
    "FFF3FP_GetHdrMetadataSource",
    # ---- 中性帧边数据环（2026-10-13，kind=3 外部色彩变换的取数入口）----
    "FFF3FP_CopyFrameSideDataAfter",
    # ---- 呈现节律锁（2026-10-15，网格标定的回读面）----
    # 没有这条，"锁到底锁住了什么"只能靠肉眼，而 FFF3FP_SetPresentConfig 的返回值
    # 早就被证明不能当判据（docs/03 A8：它只说明交换链接受了请求）。
    # 刻意**不设** FFF3FP_SetMaxFrameRate：窗口翻帧上限恒取 min(面板率, 视频率)，
    # 配个 setter 就等于给恒开的行为装开关，与既定口径冲突。
    "FFF3FP_GetPresentPacingStatus",
    # ---- 日志回调（Fff3FpNativeProbe.cs）----
    "FFF3FP_SetLogCallback",
)

# 托管侧声明 P/Invoke 的源文件（相对仓库根）。逐名对账用，见 managed_entry_points()。
INTEROP_SOURCES = (
    "src/3FCompare.Core/Backend/Interop/Fff3FpNative.cs",
    "src/3FCompare.Core/Backend/Interop/Fff3FpNativeProbe.cs",
)

# ── 按树声明的"已知缺失"清单（2026-10-14，J2）────────────────────────────────
# 为什么需要：出货树（third_party/fff_project）与内核树（3fp/kernel）**本来就分叉**，
#   REQUIRED 里的 RTX VSR 两条入口只在后者有实现（实测：VSR 在 TP 源码 0 命中、
#   在 3fp/kernel 源码 86 命中；导出面 TP=94 / KF=104）。于是门禁 `[2/11]` 恒红——
#   而"恒红的门禁"教给读者的第一课就是学会忽略红，那比没有门禁更坏。
# 为什么不是"缺了就警告"：那等于把这 2 条的牙永久拔掉，内核真少一条也照样绿。
#   ⇒ 这里的语义是**对账**：实测缺失集合必须与声明集合**完全相等**。
#     · 实测比声明多缺 ⇒ 红（真回归，或本批把别的东西也弄丢了）
#     · 声明比实测多挂 ⇒ 红（内核已补齐却还留着豁免 = 过期条目，提示删）
#     · 两边相等 ⇒ 绿，且读数里点名"是哪几条、为什么"
#   未列在这里的树**一律不豁免**（fail-closed）：换一棵树/换个检出目录就要先把
#   分叉事实写进这张表，不能靠传参数绕过。
KNOWN_MISSING_BY_TREE = {
    "third_party/fff_project": {
        "symbols": ("FFF3FP_SetVideoSuperResolution",
                    "FFF3FP_GetVideoSuperResolutionStatus"),
        "why": "RTX VSR 两条入口在出货树 TP 里本就没有实现：实测 `VideoSuperResolution` "
               "在 third_party/fff_project 源码 0 命中、在 3fp/kernel 源码 86 命中，"
               "导出面 TP=94 / KF=104。托管侧 Fff3FpNative.cs 已声明这两个 P/Invoke，"
               "所以 REQUIRED 里必须留着（删了=托管侧引用不再被对账），豁免只挂在**这棵树**上。"
               "该树一旦补上实现，本条目立即过期并判红——那时该做的是删条目，不是加参数。",
    },
}


def tree_id_for(path):
    """按 DLL 所在路径反查它属于哪棵**已声明**的树（仓库相对路径最长前缀匹配）。

    匹配不到返回 ""，调用方按"不豁免"处理——绝不因为"看起来像某棵树"就放行。
    """
    ap = os.path.abspath(path).replace("\\", "/").lower()
    root = os.path.abspath(repo_root()).replace("\\", "/").lower()
    rel = ap[len(root) + 1:] if ap.startswith(root + "/") else ap
    best = ""
    for tid in KNOWN_MISSING_BY_TREE:
        prefix = tid.lower().rstrip("/") + "/"
        if rel.startswith(prefix) and len(prefix) > len(best):
            best = tid
    return best


def reconcile_missing(measured, declared):
    """实测缺失集合与声明集合的对账。返回 (多缺的, 过期挂着的)。

    两个都必须为空才算一致。纯函数，被 --self-test-exemption 用构造用例验过牙，
    以免比较器本身是死的（死的比较器会把"恒红"换成"恒绿"，更坏）。
    """
    mset, dset = set(measured), set(declared)
    return sorted(mset - dset), sorted(dset - mset)


def self_test_reconciler():
    """构造四个用例验比较器：相等/多一条/少一条/两边各错一条，各自必须判对。"""
    req = set(REQUIRED)
    a, b = sorted(req)[0], sorted(req)[1]
    cases = (
        ("实测=声明（2 条）", (a, b), (a, b), True),
        ("实测多缺 1 条（真回归）", (a, b, sorted(req)[2]), (a, b), False),
        ("声明多挂 1 条（已补齐还留着）", (a,), (a, b), False),
        ("实测少 1 且声明多 1（双向都错）", (b, sorted(req)[2]), (a, b), False),
    )
    bad = []
    for name, measured, declared, want_ok in cases:
        extra, stale = reconcile_missing(measured, declared)
        got_ok = not extra and not stale
        if got_ok != want_ok:
            bad.append("%s（判成 %s）" % (name, "绿" if got_ok else "红"))
    print("豁免比较器自检：构造 %d 例（2 绿向 2 红向），判定错误 %d 例" % (len(cases), len(bad)))
    if bad:
        print("   [FAIL] 比较器是死的/反的：%s" % "; ".join(bad))
        return False
    print("   判定：比较器活着（多缺判红、过期挂红都抓得到）")
    return True


def repo_root():
    """本脚本住在 <root>/tools/，仓库根即其父目录。"""
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def managed_entry_points():
    """扫托管源码里真正声明出来的原生入口名。

    只认方法声明（`partial <返回类型> FFF3FP_X(`），注释/文档里提到的名字不算——
    否则 `FFF3FP_SetPacingConfig` 那种"并未导出"的反例会被当成必需项，门禁恒红。
    返回 (names, missing_files)。
    """
    import re
    names = set()
    missing = []
    for rel in INTEROP_SOURCES:
        path = os.path.join(repo_root(), rel.replace("/", os.sep))
        if not os.path.exists(path):
            missing.append(rel)
            continue
        with io.open(path, encoding="utf-8-sig") as handle:
            text = handle.read()
        for match in re.finditer(r"partial\s+[\w<>.\[\]]+\s+(FFF3FP_[A-Za-z0-9_]+)\s*\(", text):
            names.add(match.group(1))
        for match in re.finditer(
                r"(?:internal|public)\s+static\s+.*?\b(FFF3FP_[A-Za-z0-9_]+)\s*\(", text):
            names.add(match.group(1))
    return names, missing


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
    ap.add_argument("dll", nargs="*", help="待检查的 FFF.Native.dll 路径")
    # 下界：内核合法新增导出是正常演进，只要不少于 N 即通过
    # 默认 82 = 本项目内核基线（fail-closed：不传参数也按基线校验）
    ap.add_argument("--min-exports", type=int, default=82,
                    help="导出个数下界，>=N 即通过；默认 82（内核基线），0=不校验")
    # 精确相等：保留旧语义，向后兼容既有调用/文档；0=不校验
    ap.add_argument("--expect", type=int, default=0,
                    help="期望导出个数（恰好相等）；0=不校验。旧写法，新调用请用 --min-exports")
    ap.add_argument("--min-api", type=int, default=15,
                    help="最低 API 版本；默认 15（内核基线），0=不校验")
    ap.add_argument("--no-interop-scan", action="store_true",
                    help="跳过「REQUIRED 与托管侧 P/Invoke 声明」的逐名对账（打包目录里没有源码时用）")
    # J2：**默认不传=严格 REQUIRED 全量**（主仓那条链路的严格度不被稀释）。
    # 传了才启用"对账式豁免"，且只认 KNOWN_MISSING_BY_TREE 里写死的那张表；
    # `auto` = 按 DLL 所在路径反查树，查不到就**不豁免**（fail-closed）。
    ap.add_argument("--known-missing-tree", default="",
                    help="启用按树声明的已知缺失对账（树 id 见 KNOWN_MISSING_BY_TREE，或写 auto 由路径反查）；"
                         "不传即按严格清单判")
    ap.add_argument("--self-test-exemption", action="store_true",
                    help="只跑豁免比较器的构造用例自检（证明它多缺判红、过期也判红）")
    a = ap.parse_args(argv)

    if a.self_test_exemption:
        if not self_test_reconciler():
            return 1
        print("已声明的树：%s" % (", ".join(sorted(KNOWN_MISSING_BY_TREE)) or "（空）"))
        for tid, entry in sorted(KNOWN_MISSING_BY_TREE.items()):
            print("   %s 声明缺 %d 条：%s" % (tid, len(entry["symbols"]),
                                             ", ".join(entry["symbols"])))
        return 0
    if not a.dll:
        print("用法：见文件头。至少要给一个 dll（或 --self-test-exemption）。")
        return 2

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

        # ── J2 对账式豁免（不传 --known-missing-tree 时 uncovered==missing，行为不变）──
        uncovered = list(missing)
        if a.known_missing_tree:
            tree = a.known_missing_tree
            if tree.strip().lower() == "auto":
                found = tree_id_for(p)
                if found:
                    print("   豁免对账·树=auto→%s（按 DLL 路径反查）" % found)
                    tree = found
                else:
                    # 查不到声明的树 ⇒ 不豁免。绝不"看起来像某棵树"就放行。
                    print("   豁免对账·树=auto 未命中任何已声明的树 —— 不豁免，按严格 %d 条判"
                          % len(REQUIRED))
                    tree = ""
            if tree:
                if tree not in KNOWN_MISSING_BY_TREE:
                    print("   豁免对账·树=%s [FAIL] 这张树没在 KNOWN_MISSING_BY_TREE 里声明过 "
                          "（已知：%s）——新增分叉要先把事实和理由写进表里，不能靠传参数绕过"
                          % (tree, ", ".join(sorted(KNOWN_MISSING_BY_TREE)) or "（空）"))
                    ok = False
                else:
                    if not self_test_reconciler():
                        ok = False
                    declared = set(KNOWN_MISSING_BY_TREE[tree]["symbols"])
                    extra, stale = reconcile_missing(missing, declared)
                    uncovered = extra
                    print("   豁免对账·树=%s 声明 %d 条 / 实测缺 %d 条：多缺 %d 条、过期挂着 %d 条"
                          % (tree, len(declared), len(missing), len(extra), len(stale)))
                    for sym in sorted(declared & set(missing)):
                        print("     声明且实测缺失：%s" % sym)
                    if extra:
                        print("     [FAIL] 真回归：实测比声明多缺 %d 条：%s"
                              % (len(extra), ", ".join(extra)))
                        ok = False
                    if stale:
                        print("     [FAIL] 豁免过期：声明里 %d 条其实**并不缺**（已补齐还挂着）：%s"
                              " ⇒ 请删 KNOWN_MISSING_BY_TREE[%s] 里这几条"
                              % (len(stale), ", ".join(stale), tree))
                        ok = False
                    if not extra and not stale:
                        print("   豁免对账·判定：集合完全相等（该树既有分叉，非本批引入）。理由：%s"
                              % KNOWN_MISSING_BY_TREE[tree]["why"])
        if uncovered:
            print("   [FAIL] 缺少必需导出(%d/%d): %s"
                  % (len(uncovered), len(REQUIRED), ", ".join(uncovered)))
            ok = False
        if a.min_api and (api is None or api < a.min_api):
            print("   [FAIL] API version %s < 最低 %d" % (api, a.min_api))
            ok = False

    # REQUIRED 自己先自证：条数与去重后条数不等 = 清单里有重名，
    # 重名会让"缺一个"的计数读数（missing/len(REQUIRED)）说谎。
    duplicates = len(REQUIRED) - len(set(REQUIRED))
    if duplicates:
        print("[FAIL] REQUIRED 有 %d 个重名条目，清单自身不可信" % duplicates)
        ok = False
    else:
        print("REQUIRED 条目 %d 个，无重名" % len(REQUIRED))

    # 逐名对账：托管侧声明了什么，必需清单就得是什么。
    # 这一条是门禁的牙——此前它只核"清单里的在不在 DLL 里"，而清单本身是不是
    # 托管侧的全量没人查，于是宿主新增 P/Invoke 而清单漏更（2026-10-13 实测漏了 6 条）
    # 永远判绿，要到运行时才 EntryPointNotFoundException。
    if a.no_interop_scan:
        print("[WARN] 已按 --no-interop-scan 跳过托管侧对账：必需清单是否全量属未判定")
    else:
        names, missing_files = managed_entry_points()
        if missing_files:
            print("[WARN] 托管源码缺失，无法对账（未判定，不算绿）：%s" % ", ".join(missing_files))
        elif not names:
            print("[FAIL] 托管源码在盘上却一个入口都没扫出来——扫描器失效了，不信这个绿")
            ok = False
        else:
            not_required = sorted(names - set(REQUIRED))
            not_managed = sorted(set(REQUIRED) - names)
            print("托管侧声明入口 %d 个（%s）" % (len(names), ", ".join(INTEROP_SOURCES)))
            if not_required:
                print("[FAIL] 托管侧引用了、必需清单却没有(%d): %s"
                      % (len(not_required), ", ".join(not_required)))
                ok = False
            if not_managed:
                print("[FAIL] 必需清单里有、托管侧已不再引用(%d): %s"
                      % (len(not_managed), ", ".join(not_managed)))
                ok = False

    print("结论: %s" % ("OK" if ok else "FAILED"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
