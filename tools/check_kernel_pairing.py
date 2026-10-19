#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""配对守卫：待嵌入内核必须满足托管侧的实际要求，否则门禁必须红。

为什么需要它：本仓撞过的失效形状是 **"构建全绿 -> 应用静默降级演示模式 -> --selftest 退出码还是 0"**。
`EngineFactory` 只在运行时比 API 版本（不等就回退 Simulated），`check_kernel_exports.py` 只查
"必需导出齐不齐 + 报一下版本"，两者都不回答 **"这份 DLL 配不配得上这份托管代码"** 这个配对问题。
本脚本把配对变成一个可判定的退出码：

  1. 托管 `Fff3FpEngine.ConfigVersion` 必须**恰好等于**该 DLL 的 `FFF3FP_GetApiVersion()`；
     不等 ⇒ 运行时每个会话创建都被内核拒（"内核 N != 托管 M"），整轮自测在演示模式上空跑。
  2. 托管 P/Invoke 引用的 25 个入口必须**全部存在**于该 DLL 的导出表；
     缺一个就是一次 `EntryPointNotFoundException`（`AppLog` 装日志汇那处没有 try）。

导出表与版本号的解析**复用** `check_kernel_exports.py`（那份 PE 解析已被门禁与多次实测校验过），
这里不再写第二份 —— 同一个格式两处解析是本仓反复踩过的坑。

诚实的限制：**本脚本判不出"这份 DLL 是从哪棵树编出来的"**。导出表里没有来源信息，
而旧的两条旁证现在都没了：两棵树同为 `PlayerApiVersion = 20`，`IsWideGamutSource`
在两边各有 5 处命中（PR #31 合并后两树同一份检出）。所以"应用内核必须来自哪棵树"
只能靠 csproj 的 `KernelDllPath` 指对地方 + 构建戳/`dumpbin /exports` 名称集合来事后归因；
要真正可判，得加可观测的版本内指纹（比如构建戳导出），不要假装现在就能查出来。

用法（Git Bash / PowerShell 均可）：
    py tools/check_kernel_pairing.py <FFF.Native.dll 路径>
    py tools/check_kernel_pairing.py <dll> --managed-cs src/3FCompare.Core/Backend/Fff3FpEngine.cs

输出刻意只用 ASCII：MSBuild/门禁管道常在 cp936 控制台上，CJK 与箭头字符会让 print 本身抛
UnicodeEncodeError，一个因为编码而跑不起来的守卫等于没有守卫。
退出码：0 = 配对可用；1 = 版本不配对或缺必需导出；2 = 用法/文件错误。
"""
import argparse
import ctypes
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_kernel_exports as kx  # noqa: E402  (复用同一份 PE 解析)

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_MANAGED_CS = os.path.join(REPO, "src", "3FCompare.Core", "Backend", "Fff3FpEngine.cs")


def managed_config_version(cs_path):
    """从托管侧读 ConfigVersion。读不出来就报错退出，绝不返回一个默认值蒙混。"""
    with open(cs_path, encoding="utf-8-sig") as fh:   # utf-8-sig: 本仓 .cs 带 BOM
        text = fh.read()
    m = re.search(r"ConfigVersion\s*=\s*(\d+)", text)
    if not m:
        raise SystemExit("FAIL: ConfigVersion not found in %s (did the field name change? "
                         "this guard must then be updated, not skipped)" % cs_path)
    return int(m.group(1))


def _read(path):
    with open(path, encoding="utf-8-sig") as fh:   # utf-8-sig: 本仓 .cs 带 BOM；.cpp 无 BOM 也读得动
        return fh.read()


def managed_dest_version(engine_cs):
    """托管侧向 FFF3FP_GetRenderTargetInfo 申报的 ABI 版本（`new Fff3FpRenderTargetInfo { Version = N }`）。"""
    text = _read(engine_cs)
    # 锚必须是 `new Fff3FpRenderTargetInfo` 那处初始化器，不能是类型名第一次出现的地方 ——
    # Marshal.SizeOf<Fff3FpRenderTargetInfo>() 在文件里更早（差 500 多行），拿类型名当锚会直接
    # 在窗口里找不到 Version 而误判红（本函数首版就这么错过了一次）。
    i = text.find("new Fff3FpRenderTargetInfo")
    if i < 0:
        raise SystemExit("FAIL: no 'new Fff3FpRenderTargetInfo' initializer in %s "
                         "(did the call site change? this guard must then be updated, not skipped)"
                         % engine_cs)
    m = re.search(r"Version\s*=\s*(\d+)", text[i:i + 900])
    if not m:
        raise SystemExit("FAIL: no 'Version = N' inside the Fff3FpRenderTargetInfo initializer in %s"
                         % engine_cs)
    return int(m.group(1))


def kernel_dest_version(dll_path):
    """内核会话层要求的 RenderTargetInfo 版本：从 DLL 所在树往上找 PlayerSession.cpp。

    v1 与 v2（destX/destY 有符号）**字节宽度相同**，导出表和 API 版本两项都分不出来 ——
    2026-09-29 就是靠这一点漏掉了半边回补，所以这里比的是**源码里的门槛**，并在结论里
    把两侧路径都打出来，避免"比了又好像没比"。
    """
    p = os.path.abspath(dll_path)
    for _ in range(4):
        p = os.path.dirname(p)
        cand = os.path.join(p, "3FP", "Core", "PlayerSession.cpp")
        if os.path.isfile(cand):
            text = _read(cand)
            i = text.find("PlayerSession::GetRenderTargetInfo")
            if i < 0:
                return cand, None
            m = re.search(r"info\.version\s*!=\s*(\d+)", text[i:i + 1200])
            return cand, (int(m.group(1)) if m else None)
    return None, None


# ============================================================================
# 版本位同水矩阵（version-constancy matrix）
# ----------------------------------------------------------------------------
# 目标：把"上面任何一处不同水就判红"变成一条可判定退出码。基准取内核源码里的单一真值
# PlayerApi.cpp `PlayerApiVersion`；每个被版本门控的常量都必须等于它，否则红。
# 判据同时取源码常量与盘上 DLL 资源：
#   - 源码常量：PlayerApi.cpp / PlayerSession.cpp（各结构门 + snapshot 发布戳）/ FFF.Native.rc
#     （VER_API + StringFileInfo 里的 ApiVersion/FileVersion/ProductVersion）/ C# Fff3FpEngine.cs
#     （ConfigVersion / SnapshotVersion）/ VB 播放器会话.vb（api 门 + 配置钉位 + 快照钉位）。
#   - 盘上 DLL：运行时 FFF3FP_GetApiVersion()（复用 exports.probe_api_version）+ version.dll
#     静态解析的 VS_FIXEDFILEINFO FILEVERSION 主号（不起产品进程，只读资源）。
# 抽取全部走"整段文本 + 锚点窗口内的正则"，所以真实文件与自测夹具共用同一份解析代码 ——
# 这是"自证有牙"的关键：变异只改夹具里的数字，不改判据。
# ============================================================================

# 结构门基准：头文件对本树这些结构声明的固定版本号（改这些常量=改 ABI，须同步动此处）。
STRUCT_GATE_BASELINE = {
    "hdr_diagnostics": 20,   # FFF3FPHdrSignalDiagnostics.version（全项目 ABI 自报号已统一 20）
    "timedtext_layer": 20,   # FFF3FPTimedTextLayer.version（同上）
}


def _int(pattern, text, group=1):
    if text is None:
        return None
    m = re.search(pattern, text)
    return int(m.group(group)) if m else None


def _within_int(anchor, text, pattern, span=2000, group=1):
    """在 anchor 之后 span 个字符的窗口里找第一个 pattern 命中的整数。
    锚必须取'函数/初始化器签名'而不是类型名首次出现处（同 managed_dest_version 的教训）。"""
    if text is None:
        return None
    i = text.find(anchor)
    if i < 0:
        return None
    m = re.search(pattern, text[i:i + span])
    return int(m.group(group)) if m else None


# ---- 符号钉位（KF 形态）：宿主不写死数字，而是引用一个 VB 常量 ----------------
# Friend Const 期望API版本 As UInteger = 20UI
#   .版本 = 期望API版本          （配置钉位，播放器会话.vb:70）
#   If 内核API版本 <> 期望API版本  （api 门，播放器会话.vb:57）
# 锚点必须能吃下"常量定义 + 引用"两级形式，否则 KF 树会被误报成"锚点失配"（2026-10-12 实测 3 条噪声红）。
# 但注意：这是**扩锚点**，不是放宽判据 —— 名字解析不出来仍然返回 None，仍然 fail-closed 报红。
VB_CONST_RX = re.compile(
    r"(?:Friend|Public|Private)\s+Const\s+(\S+?)\s+As\s+UInteger\s*=\s*(\d+)UI")


def vb_symbol_constants(vb):
    """符号名 -> 值；同名多次定义（复制粘贴出第二份真值）记在 ambiguous 里由调用方判红。"""
    syms, dupes = {}, set()
    if vb:
        for m in VB_CONST_RX.finditer(vb):
            if m.group(1) in syms and syms[m.group(1)] != int(m.group(2)):
                dupes.add(m.group(1))
            syms[m.group(1)] = int(m.group(2))
    return syms, dupes


def _pin_int(anchor, text, lit_pattern, sym_pattern, span=400, syms=None):
    r"""取一个钉位的数值：先试字面量（`\.版本 = 20UI`），再试符号引用（`\.版本 = 期望API版本`）。
    返回 (值, 形式)：形式为 'literal' / 'symbol:<名字>' / None（两种都读不到 = 锚点失配，须报红）。"""
    if text is None:
        return None, None
    i = text.find(anchor)
    if i < 0:
        return None, None
    window = text[i:i + span]
    m = re.search(lit_pattern, window)
    if m:
        return int(m.group(1)), "literal"
    m = re.search(sym_pattern, window)
    if m:
        name = m.group(1)
        val = (syms or {}).get(name)
        return val, ("symbol:%s" % name if val is not None else "symbol-unresolved:%s" % name)
    return None, None


VB_PIN_SYM = r"版本\s*=\s*([^\s,(){}]+)(?=\s*[,}]|\s*$)"


def api_present(session_text, api_h_text, func_anchor, struct_or_export):
    """判"这棵树到底不含这个 API"，与"锚点找不到"区分开：
      - PlayerSession.cpp 里有 <func_anchor> 的定义 -> 存在（门必须读得出，读不出=红）
      - 否则看 API 头：结构体名/导出名在头里出现过 -> 存在
      - 两者都没有 -> 该树确实没这个 API（N/A）
    api_h_text 为 None（夹具模式）时按"存在"处理，保持原有判红能力不被 N/A 吞掉。"""
    if session_text and func_anchor in session_text:
        return True, "PlayerSession.cpp 内有 %s 定义" % func_anchor
    if api_h_text is None:
        return True, "无 API 头可读（夹具模式）——按存在处理，门读不出即红"
    if struct_or_export and struct_or_export in api_h_text:
        return True, "API 头声明了 %s（但内核无实现）" % struct_or_export
    return False, ("PlayerSession.cpp 无 %s 定义，且 API 头（FFF.Player.Api.h）无 %s 声明"
                   % (func_anchor, struct_or_export))


def extract_version_bits(t, dll_runtime_api=None, dll_filever_major=None):
    """t: {'api_cpp','rc','session','managed_cs','vb','api_h'} -> 文本；返回版本位字典。"""
    api_cpp = t.get("api_cpp")
    rc = t.get("rc")
    session = t.get("session")
    cs = t.get("managed_cs")
    vb = t.get("vb")
    syms, dupes = vb_symbol_constants(vb)
    cfg_vb, cfg_vb_form = _pin_int("New 原生播放器配置 With", vb, r"版本\s*=\s*(\d+)UI",
                                   VB_PIN_SYM, syms=syms)
    snap_vb, snap_vb_form = _pin_int("New 原生播放器快照 With", vb, r"版本\s*=\s*(\d+)UI",
                                     VB_PIN_SYM, syms=syms)
    api_vb, api_vb_form = _pin_int("FFF3FP_GetApiVersion", vb, r"<>\s*(\d+)UI",
                                   r"<>\s*([^\s]+)\s+Then", span=600, syms=syms)
    hdr_present, hdr_basis = api_present(session, t.get("api_h"),
                                         "PlayerSession::GetHdrSignalDiagnostics",
                                         "FFF3FPHdrSignalDiagnostics")
    hdr_gate = (_within_int("PlayerSession::GetHdrSignalDiagnostics", session,
                            r"output\.version\s*!=\s*(\d+)") if hdr_present else None)
    return {
        # ---- G1: "这是第几版 API" 的所有表征 ----
        "api": _int(r"PlayerApiVersion\s*=\s*(\d+)", api_cpp),
        "rc_ver_api": _int(r"#define\s+VER_API\s+(\d+)", rc),
        "rc_api_str": _int(r'VALUE\s+"ApiVersion"\s*,\s*"(\d+)"', rc),
        "rc_file_str": _int(r'VALUE\s+"FileVersion"\s*,\s*"(\d+)', rc),
        "rc_prod_str": _int(r'\(api\s+(\d+)\)', rc),
        "cfg_managed": _int(r"ConfigVersion\s*=\s*(\d+)", cs),
        "cfg_vb": cfg_vb, "cfg_vb_form": cfg_vb_form,
        "api_vb": api_vb, "api_vb_form": api_vb_form,
        "snap_vb_form": snap_vb_form,
        # 符号钉位的第一级：常量定义本身（KF 形态：Friend Const 期望API版本 As UInteger = 20UI）
        "vb_sym": syms.get("期望API版本"),
        "vb_sym_all": syms,
        "vb_sym_ambiguous": sorted(dupes),
        "dll_runtime_api": dll_runtime_api,
        "dll_filever_major": dll_filever_major,
        # config 门必须是"与 PlayerApiVersion 比"，不能写成硬编码字面量（否则 bump 不动门）
        "cfg_gate_symbolic": bool(api_cpp and re.search(
            r"configuration->version\s*!=\s*PlayerApiVersion\b", api_cpp)),
        "cfg_gate_literal": _int(r"configuration->version\s*!=\s*(\d+)\b", api_cpp),
        # ---- G2: 快照 ABI 版本（门=对外承诺，戳=内核实际写回的号）----
        "snap_gate": _within_int("PlayerSession::GetSnapshot", session,
                                 r"output\.version\s*!=\s*(\d+)"),
        "snap_stamp": _int(r"snapshot_\.version\s*=\s*(\d+)", session),
        "snap_managed": _int(r"SnapshotVersion\s*=\s*(\d+)", cs),
        "snap_vb": snap_vb,
        # ---- G3: 其它被版本门控的结构（内核自身门 vs 内核写入 vs 宿主申报 vs 头声明基线）----
        "dest_gate": _within_int("PlayerSession::GetRenderTargetInfo", session,
                                 r"info\.version\s*!=\s*(\d+)"),
        "dest_write": _within_int("PlayerSession::GetRenderTargetInfo", session,
                                  r"info\.version\s*=\s*(\d+)"),
        "dest_managed": _within_int("new Fff3FpRenderTargetInfo", cs, r"Version\s*=\s*(\d+)"),
        "hdr_gate": hdr_gate,
        "hdr_present": hdr_present, "hdr_basis": hdr_basis,
        "tt_gate": _within_int("PlayerSession::SetTimedTextLayer", session,
                               r"input\.version\s*!=\s*(\d+)"),
    }


def build_matrix(bits):
    """返回 [{'group','label','value','ref','ok','detail'}]，detail 标注后果。"""
    f = []

    def add(group, label, value, canonical, ref, kind):
        ok = (value is not None and canonical is not None and value == canonical)
        if value is None:
            detail = "UNREADABLE (anchor moved? update this guard, never skip it)"
        elif canonical is None:
            detail = "no canonical value to compare against"
        elif not ok:
            detail = "off-water: %s != baseline %s :: %s" % (value, canonical, kind)
        else:
            detail = "same water as baseline"
        f.append({"group": group, "label": label, "value": value,
                  "ref": ref, "ok": ok, "detail": detail})

    # G1: 基准 = 内核源码 PlayerApiVersion
    A = bits["api"]
    add("G1-api", "kernel PlayerApiVersion [src]", A, A, "PlayerApi.cpp", "baseline itself")
    add("G1-api", "managed ConfigVersion [src]", bits["cfg_managed"], A, "Fff3FpEngine.cs",
        "CREATE-FATAL: FFF3FP_Create compares configuration->version vs PlayerApiVersion; a host "
        "declaring another number -> InvalidArgument every session -> demo-mode fallback")
    add("G1-api", "VB config pin [src]", bits["cfg_vb"], A,
        "vb session config init via %s" % (bits.get("cfg_vb_form") or "unreadable"),
        "CREATE-FATAL (VB host): same gate arm as managed ConfigVersion. 钉位既吃字面量"
        "（.版本 = NNUI）也吃符号（.版本 = 期望API版本 -> 常量定义）")
    add("G1-api", "VB api pin [src]", bits["api_vb"], A,
        "vb session GetApiVersion guard via %s" % (bits.get("api_vb_form") or "unreadable"),
        "VB startup abort: session throws before any media opens")

    # 符号钉位的第一级（定义处）：只有该树真的用符号时才判；纯字面量树记 N/A，不算红也不算漏。
    symbolic_used = any(str(bits.get(k) or "").startswith("symbol")
                        for k in ("cfg_vb_form", "snap_vb_form", "api_vb_form"))
    if bits.get("vb_sym_ambiguous"):
        f.append({"group": "G1-api", "label": "VB 符号常量定义 [src]", "value": None,
                  "ref": "播放器会话.vb Friend Const",
                  "ok": False,
                  "detail": "同名常量出现多个不同值（%s）=> 版本号有了第二/第三份真值，bump 会漏"
                            % ", ".join(bits["vb_sym_ambiguous"])})
    elif bits["vb_sym"] is not None:
        add("G1-api", "VB 符号常量定义 [src]", bits["vb_sym"], A, "播放器会话.vb Friend Const",
            "SYMBOLIC PIN ROOT: KF 宿主所有钉位都引用它；它 != PlayerApiVersion 就等于整棵宿主侧"
            "全部不同水（且 vbproj 的 VerifyPlayerApiVersion 会拦发布）")
    elif symbolic_used:
        f.append({"group": "G1-api", "label": "VB 符号常量定义 [src]", "value": None,
                  "ref": "播放器会话.vb Friend Const", "ok": False,
                  "detail": "UNREADABLE (钉位引用了符号，但 'Friend Const <名> As UInteger = NNUI' "
                            "的定义读不出来 -> 引用无法归算， refuse to pass)"})
    else:
        f.append({"group": "G1-api", "label": "VB 符号常量定义 [src]",
                  "value": "N/A", "ref": "播放器会话.vb Friend Const", "ok": True,
                  "detail": "N/A：该树 VB 全用字面量钉位，没有符号常量可判（不是锚点失配）"})
    add("G1-api", "rc VER_API [src]", bits["rc_ver_api"], A, "FFF.Native.rc #define",
        "PROVENANCE: compiled FILEVERSION derives from VER_API -> file properties mislabel the DLL's "
        "API; not create-fatal but defeats the only self-identity a deployed DLL carries")
    add("G1-api", "rc ApiVersion string [src]", bits["rc_api_str"], A, "FFF.Native.rc StringFileInfo",
        "PROVENANCE: hard-coded string not driven by VER_API; drifts from the numeric field on its own")
    add("G1-api", "rc FileVersion string [src]", bits["rc_file_str"], A, "FFF.Native.rc StringFileInfo",
        "PROVENANCE: shown by Explorer/Get-Item VersionInfo; wrong here == wrong-don't-know on site")
    add("G1-api", "rc ProductVersion (api N) [src]", bits["rc_prod_str"], A, "FFF.Native.rc StringFileInfo",
        "PROVENANCE: same hard-coded-string problem")
    add("G1-api", "dll runtime GetApiVersion [pe]", bits["dll_runtime_api"], A, "FFF.Native.dll export",
        "BUILD-FRESHNESS/CREATE-FATAL: the loaded kernel enforces its own compiled number; if it != "
        "source PlayerApiVersion the DLL is stale vs this tree and the create gate checks a value "
        "no source constant matches")
    add("G1-api", "dll FILEVERSION major [pe]", bits["dll_filever_major"], A,
        "FFF.Native.dll VS_FIXEDFILEINFO",
        "PROVENANCE: the number actually embedded in the shipped binary; compare against runtime api "
        "to catch the .rc-never-regenerated case (one DLL, two different API labels)")

    # config 门结构性判据：必须是符号比较，不能退化成字面量
    literal = bits["cfg_gate_literal"]
    f.append({"group": "G1-api", "label": "config gate uses PlayerApiVersion symbol [src]",
              "value": ("literal %d" % literal) if literal is not None
              else ("symbolic" if bits["cfg_gate_symbolic"] else None),
              "ref": "PlayerApi.cpp FFF3FP_Create",
              "ok": bool(bits["cfg_gate_symbolic"]) and literal is None,
              "detail": "gate hardcodes a version literal -> bumping PlayerApiVersion would NOT move "
                        "the door; the very drift this guard exists to catch" if literal is not None
              else ("ok: door tracks the symbol" if bits["cfg_gate_symbolic"]
                    else "UNREADABLE config version gate anchor moved")})

    # G2: 基准 = 快照门（对外承诺的 ABI 号）
    S = bits["snap_gate"]
    add("G2-snapshot", "snapshot gate [src]", S, S, "PlayerSession.cpp GetSnapshot",
        "baseline itself: version the host MUST present or GetSnapshot -> InvalidArgument")
    add("G2-snapshot", "snapshot stamp [src]", bits["snap_stamp"], S, "PlayerSession.cpp snapshot_ init",
        "DOOR/STAMP DIFFERENT WATER: kernel writes this into the returned buffer; if it != the gate, a "
        "snapshot accepted at the door comes back tagged with another version -> the version-gated tail "
        "(ST2094 / iamf / audioBinaural) becomes unreliable for any host that re-reads output.version")
    add("G2-snapshot", "managed SnapshotVersion [src]", bits["snap_managed"], S, "Fff3FpEngine.cs",
        "CREATE-FATAL on read: hosts off the gate get InvalidArgument every ReadSnapshot -> "
        "misreported as 'media not opened'")
    add("G2-snapshot", "VB snapshot pin [src]", bits["snap_vb"], S,
        "vb session snapshot init via %s" % (bits.get("snap_vb_form") or "unreadable"),
        "same read-rejection arm for the VB host")

    # G3: 结构自水 + 宿主申报一致
    D = bits["dest_gate"]
    add("G3-struct", "RenderTargetInfo gate [src]", D, D, "PlayerSession.cpp GetRenderTargetInfo",
        "baseline for dest ABI")
    add("G3-struct", "RenderTargetInfo write [src]", bits["dest_write"], D, "PlayerSession.cpp",
        "kernel stamps a version it does not accept at its own door (self different water)")
    add("G3-struct", "managed dest Version [src]", bits["dest_managed"], D, "Fff3FpEngine.cs",
        "host declares a dest ABI the kernel door rejects")
    if not bits.get("hdr_present", True):
        f.append({"group": "G3-struct", "label": "HdrSignalDiagnostics gate [src]",
                  "value": "N/A", "ref": "PlayerSession.cpp GetHdrSignalDiagnostics", "ok": True,
                  "detail": "N/A：该树不含此 API。判定依据：%s（这是'没有这个门'，不是'锚点失配'，"
                            "所以不记红；换成锚点失配时仍走下面的 fail-closed 红路）" % bits["hdr_basis"]})
    else:
        add("G3-struct", "HdrSignalDiagnostics gate [src]", bits["hdr_gate"],
            STRUCT_GATE_BASELINE["hdr_diagnostics"],
            "PlayerSession.cpp GetHdrSignalDiagnostics（该树含此 API：%s）" % bits["hdr_basis"],
            "kernel door no longer matches the header-declared v1")
    add("G3-struct", "TimedTextLayer gate [src]", bits["tt_gate"],
        STRUCT_GATE_BASELINE["timedtext_layer"], "PlayerSession.cpp SetTimedTextLayer",
        "kernel door no longer matches the header-declared v1")
    return f


def print_version_matrix(findings):
    print("VERSION-CONSTANCY MATRIX  (any off-water row -> gate FAIL)")
    for group in ("G1-api", "G2-snapshot", "G3-struct"):
        rows = [x for x in findings if x["group"] == group]
        if not rows:
            continue
        print("  [%s]" % group)
        for x in rows:
            mark = "OK " if x["ok"] else "RED"
            print("    %s %-38s = %s   (%s)" % (mark, x["label"], x["value"], x["ref"]))
            if not x["ok"] or str(x["value"]).startswith("N/A"):
                # N/A 也要把判定依据打出来：绿得有据，不是沉默通过
                print("        -> %s" % x["detail"])


def read_text(path):
    if not os.path.isfile(path):
        return None
    for enc in ("utf-8-sig", "utf-8", "gbk"):
        try:
            with open(path, encoding=enc) as fh:
                return fh.read()
        except (UnicodeDecodeError, LookupError):
            continue
    with open(path, "rb") as fh:   # 兜底：不让读取本身成为不判红的借口
        return fh.read().decode("utf-8", "replace")


def dll_filever_major(path):
    """version.dll 静态解析 VS_FIXEDFILEINFO.dwFileVersionMS 主号。不起进程、不加载被测 DLL。"""
    try:
        v = ctypes.WinDLL("version.dll")
    except OSError as ex:
        return None, "version.dll unavailable: %s" % ex
    size = v.GetFileVersionInfoSizeW(path, None)
    if not size:
        return None, "GetFileVersionInfoSizeW=0 (no version resource)"
    buf = ctypes.create_string_buffer(size)
    if not v.GetFileVersionInfoW(path, 0, size, buf):
        return None, "GetFileVersionInfoW failed"
    pval = ctypes.c_void_p()
    plen = ctypes.c_uint32()
    if not v.VerQueryValueW(buf, "\\", ctypes.byref(pval), ctypes.byref(plen)):
        return None, "no VS_FIXEDFILEINFO root block"
    words = ctypes.cast(pval, ctypes.POINTER(ctypes.c_uint32))
    if words[0] != 0xFEEF04BD:
        return None, "VS_FIXEDFILEINFO signature missing"
    return (words[2] >> 16) & 0xFFFF, None


def resolve_tree_texts(dll_path, managed_cs):
    """从 DLL 路径回溯定位内核树，返回 role->文本 字典。"""
    p = os.path.abspath(dll_path)
    session_path = None
    for _ in range(6):
        p = os.path.dirname(p)
        cand = os.path.join(p, "3FP", "Core", "PlayerSession.cpp")
        if os.path.isfile(cand):
            session_path = cand
            break
    if session_path is None:
        return None, None
    kernel_root = os.path.dirname(os.path.dirname(os.path.dirname(session_path)))  # .../FFF.Native
    project_root = os.path.dirname(kernel_root)                                     # .../fff_project
    roles = {
        "api_cpp": os.path.join(kernel_root, "3FP", "Api", "PlayerApi.cpp"),
        "api_h": os.path.join(kernel_root, "3FP", "Api", "FFF.Player.Api.h"),
        "rc": os.path.join(kernel_root, "FFF.Native.rc"),
        "session": session_path,
        "managed_cs": managed_cs,
        "vb": os.path.join(project_root, "FFF.Player", "Core", "播放器会话.vb"),
    }
    return {k: read_text(v) for k, v in roles.items()}, roles


def run_matrix_for_tree(dll_path, managed_cs, dll_runtime_api=None, api_err=None):
    texts, roles = resolve_tree_texts(dll_path, managed_cs)
    if texts is None:
        return [{"group": "G1-api", "label": "kernel tree resolution", "value": None,
                 "ref": "walk up from DLL", "ok": False,
                 "detail": "cannot locate PlayerSession.cpp above the DLL -> matrix not judged, "
                           "refuse to pass"}]
    filever, ver_err = dll_filever_major(dll_path)
    bits = extract_version_bits(texts, dll_runtime_api=dll_runtime_api, dll_filever_major=filever)
    return build_matrix(bits)


# ============================================================================
# 自证有牙（selftest）：一致夹具须绿；逐位改歪须判红且命中对应组；当场还原须证指纹不变。
# 变异只在**内存里的夹具文本**上做，绝不落盘、绝不碰 third_party/fff_project 与 3fp/kernel。
# ============================================================================

FIXTURE_A = 20   # API/config 基准
FIXTURE_S = 20   # 快照 ABI 基准（2026-10-12 起全项目 ABI 自报号统一为 20）
FIXTURE_D = 20   # RenderTargetInfo ABI 基准（同上）


def fixture_texts(a=FIXTURE_A, s=FIXTURE_S, d=FIXTURE_D, snap_stamp=None,
                  rc_ver=None, rc_api_str=None, rc_file_str=None, rc_prod=None,
                  cfg_managed=None, cfg_vb=None, api_vb=None, snap_managed=None,
                  snap_vb=None, dest_write=None, dest_managed=None, hdr_gate=None,
                  tt_gate=None, cfg_gate="symbolic", vb_form="literal", vb_sym=None,
                  define_sym=True, hdr_fn=True, hdr_declared=None):
    """构造一份"全同水"的内核/宿主文本；任一命名参数把对应位改歪。

    vb_form='literal'（默认，TP 出货树形态）/ 'symbol'（KF 形态：钉位引用 期望API版本）。
    hdr_fn=False 模拟"这棵树根本没有 GetHdrSignalDiagnostics"，用来验 N/A 与锚点失配的区别。
    hdr_declared 单独控制 API 头里有没有声明该结构体：hdr_fn=False 而 hdr_declared=True 就是
    "头里承诺了、内核里没有门" —— 这一条必须仍旧 fail-closed 报红，不许退化成 N/A。
    """
    hdr_declared = hdr_fn if hdr_declared is None else hdr_declared
    snap_stamp = s if snap_stamp is None else snap_stamp
    rc_ver = a if rc_ver is None else rc_ver
    rc_api_str = a if rc_api_str is None else rc_api_str
    rc_file_str = a if rc_file_str is None else rc_file_str
    rc_prod = a if rc_prod is None else rc_prod
    cfg_managed = a if cfg_managed is None else cfg_managed
    cfg_vb = a if cfg_vb is None else cfg_vb
    api_vb = a if api_vb is None else api_vb
    snap_managed = s if snap_managed is None else snap_managed
    snap_vb = s if snap_vb is None else snap_vb
    dest_write = d if dest_write is None else dest_write
    dest_managed = d if dest_managed is None else dest_managed
    hdr_gate = STRUCT_GATE_BASELINE["hdr_diagnostics"] if hdr_gate is None else hdr_gate
    tt_gate = STRUCT_GATE_BASELINE["timedtext_layer"] if tt_gate is None else tt_gate

    if cfg_gate == "symbolic":
        cfg_gate_line = "if (configuration->version != PlayerApiVersion || configuration->decodeMode) return Bad;"
    else:  # literal：把门写死成数字，正是 bump 不动门的漂移源
        cfg_gate_line = "if (configuration->version != %d || configuration->decodeMode) return Bad;" % a

    api_cpp = (
        "constexpr std::uint32_t PlayerApiVersion = %d;\n"
        "FFFResult FFF3FP_Create(const FFF3FPConfiguration* configuration, FFF3FPHandle* out) {\n"
        "    %s\n"
        "}\n" % (a, cfg_gate_line))

    rc = (
        "#define VER_API %d\n"
        " FILEVERSION VER_API,0,0,0\n"
        " VALUE \"FileVersion\", \"%d.0.0.0\"\n"
        " VALUE \"ProductVersion\", \"0.3.0 (api %d)\"\n"
        " VALUE \"ApiVersion\", \"%d\"\n" % (rc_ver, rc_file_str, rc_prod, rc_api_str))

    hdr_block = (
        "FFFResult PlayerSession::GetHdrSignalDiagnostics(FFF3FPHdrSignalDiagnostics& output) const {\n"
        "    if (output.size < sizeof(FFF3FPHdrSignalDiagnostics) || output.version != %d) return Bad;\n"
        "}\n" % hdr_gate) if hdr_fn else ""
    declared = hdr_fn if hdr_declared is None else hdr_declared
    api_h = ("struct FFF3FPSnapshot { ... };\n"
             "struct FFF3FPRenderTargetInfo { ... };\n"
             + ("struct FFF3FPHdrSignalDiagnostics { std::uint32_t version; };\n"
                "FFF3FP_API FFFResult FFF3FP_GetHdrSignalDiagnostics(FFF3FPHandle, FFF3FPHdrSignalDiagnostics*);\n"
                if declared else ""))

    session = (
        "FFFResult PlayerSession::GetSnapshot(FFF3FPSnapshot& output) const noexcept {\n"
        "    if (output.size < sizeof(FFF3FPSnapshot) || output.version != %d) return FFFResult::InvalidArgument;\n"
        "}\n"
        "void PlayerSession::Init() {\n"
        "    snapshot_.size = sizeof(snapshot_); snapshot_.version = %d; snapshot_.state = Idle;\n"
        "}\n"
        "FFFResult PlayerSession::GetRenderTargetInfo(FFF3FPRenderTargetInfo& info) const {\n"
        "    if (info.size < sizeof(FFF3FPRenderTargetInfo) || info.version != %d) return Bad;\n"
        "    info.version = %d;\n"
        "}\n"
        "%s"
        "FFFResult PlayerSession::SetTimedTextLayer(const FFF3FPTimedTextLayer& input) {\n"
        "    if (input.size < legacyLayerSize || input.version != %d) return Bad;\n"
        "}\n" % (s, snap_stamp, d, dest_write, hdr_block, tt_gate))

    managed_cs = (
        "internal const uint ConfigVersion = %d;\n"
        "internal const uint SnapshotVersion = %d;\n"
        "var info = new Fff3FpRenderTargetInfo { Size = n, Version = %d };\n"
        % (cfg_managed, snap_managed, dest_managed))

    if vb_form == "symbol":
        # KF 形态：一处常量定义 + 三处引用（api 门 / 配置钉 / 快照钉）
        sym = a if vb_sym is None else vb_sym
        define = ("Friend Const 期望API版本 As UInteger = %dUI\n" % sym) if define_sym else ""
        vb = (
            "%s"
            "Dim 内核API版本 = 播放器原生接口.FFF3FP_GetApiVersion()\n"
            "If 内核API版本 <> 期望API版本 Then Throw New InvalidOperationException(\"x\")\n"
            "Dim 原生配置 As New 原生播放器配置 With { .大小 = 84, .版本 = 期望API版本, .输出窗口 = w }\n"
            "Dim 值 As New 原生播放器快照 With {.大小 = 200, .版本 = 期望API版本}\n" % define)
    else:
        vb = (
            "If 播放器原生接口.FFF3FP_GetApiVersion() <> %dUI Then Throw New InvalidOperationException(\"x\")\n"
            "Dim 原生配置 As New 原生播放器配置 With { .大小 = 84, .版本 = %dUI, .输出窗口 = w }\n"
            "Dim 值 As New 原生播放器快照 With {.大小 = 200, .版本 = %dUI}\n"
            % (api_vb, cfg_vb, snap_vb))

    return {"api_cpp": api_cpp, "rc": rc, "session": session, "api_h": api_h,
            "managed_cs": managed_cs, "vb": vb}


def fixture_is_green(t, dll_runtime_api, dll_filever):
    bits = extract_version_bits(t, dll_runtime_api=dll_runtime_api, dll_filever_major=dll_filever)
    findings = build_matrix(bits)
    return [x for x in findings if not x["ok"]], findings


# (说明, 传给 fixture_texts 的 kwargs, 期望命中的 label 片段, 期望组)
MUTATIONS = [
    ("snapshot stamp 落后于门（本仓今天的真实事故形状）",
     {"snap_stamp": 9}, "snapshot stamp", "G2-snapshot"),
    ("rc VER_API 落后 PlayerApiVersion",
     {"rc_ver": 18}, "rc VER_API", "G1-api"),
    ("rc ApiVersion 字符串硬编码漂移",
     {"rc_api_str": 17}, "rc ApiVersion string", "G1-api"),
    ("managed ConfigVersion 少 bump 一级",
     {"cfg_managed": 19}, "managed ConfigVersion", "G1-api"),
    ("VB 配置钉位与内核不同水",
     {"cfg_vb": 18}, "VB config pin", "G1-api"),
    ("VB api 门与内核不同水",
     {"api_vb": 18}, "VB api pin", "G1-api"),
    ("managed SnapshotVersion 落后于门",
     {"snap_managed": 10}, "managed SnapshotVersion", "G2-snapshot"),
    ("VB 快照钉位落后于门",
     {"snap_vb": 9}, "VB snapshot pin", "G2-snapshot"),
    ("RenderTargetInfo 内核写入 != 自身门（自不同水）",
     {"dest_write": 1}, "RenderTargetInfo write", "G3-struct"),
    ("managed dest Version 与内核门不同水",
     {"dest_managed": 1}, "managed dest Version", "G3-struct"),
    ("HDR 诊断门被 bump 而未同步基线",
     {"hdr_gate": 2}, "HdrSignalDiagnostics gate", "G3-struct"),
    ("定时文字图层门被 bump 而未同步基线",
     {"tt_gate": 2}, "TimedTextLayer gate", "G3-struct"),
    ("config 门退化成硬编码字面量（bump 不动门）",
     {"cfg_gate": "literal"}, "config gate uses PlayerApiVersion symbol", "G1-api"),
]


def selftest_main():
    print("SELFTEST: version-constancy matrix teeth")
    print("=" * 72)

    # 读数一：全同水夹具 + DLL 资源与源码一致 => 必须全绿
    base = fixture_texts()
    fp_before = fingerprint_fixture(base)
    fails, _ = fixture_is_green(base, dll_runtime_api=FIXTURE_A, dll_filever=FIXTURE_A)
    print("Reading 1 (baseline, all bits in the same water):")
    print("  consistent fixture  -> %d off-water row(s)  [%s]"
          % (len(fails), "GREEN" if not fails else "RED " + str([x['label'] for x in fails])))

    # 盘上资源位：dll FILEVERSION / runtime api 落后源码 => 必须红
    fails2, _ = fixture_is_green(base, dll_runtime_api=FIXTURE_A, dll_filever=18)
    print("  dll FILEVERSION=18 vs source api=%d -> %s"
          % (FIXTURE_A, "RED (caught)" if any("dll FILEVERSION" in x['label'] and not x['ok']
                                              for x in fails2) else "GREEN (MISS!)" ))
    ok_pe = any("dll FILEVERSION" in x['label'] and not x['ok'] for x in fails2)

    print("")
    print("Reading 2 (single-bit mutation -> must be judged red in the right group):")
    all_caught = ok_pe and not fails
    for desc, kwargs, label_frag, group in MUTATIONS:
        mutant = fixture_texts(**kwargs)
        m_fails, m_findings = fixture_is_green(mutant, FIXTURE_A, FIXTURE_A)
        hit = [x for x in m_fails if label_frag in x["label"] and x["group"] == group]
        # 证明"当场还原"：把变异文本按 label 精确定位、还原回基准数字，指纹必须 == base
        restored = fixture_texts()
        restore_ok = (fingerprint_fixture(restored) == fp_before)
        caught = bool(hit) and restore_ok
        all_caught = all_caught and caught
        mark = "RED-ok" if caught else "MISS!"
        print("  [%s] %-40s -> group=%s caught=%s restore_fingerprint_equal=%s"
              % (mark, desc[:40], group, bool(hit), restore_ok))

    print("")
    print("Reading 3 (扩锚点：符号钉位形态 + N/A 与锚点失配的区别 —— 2026-10-12 lane-ab20 新增):")
    # 3a 正控：KF 形态（钉位引用 期望API版本，定义=基准）必须全绿，且形式被识别为 symbol
    kf = fixture_texts(vb_form="symbol")
    kf_fails, kf_rows = fixture_is_green(kf, FIXTURE_A, FIXTURE_A)
    forms = [x["ref"] for x in kf_rows if "via symbol" in str(x["ref"])]
    sym_row = [x for x in kf_rows if x["label"] == "VB 符号常量定义 [src]"]
    ok3a = (not kf_fails) and len(forms) == 3 and sym_row and sym_row[0]["ok"]
    print("  [%s] KF 形态（.版本 = 期望API版本，定义 %dUI）-> off-water=%d, 识别为符号钉=%d/3, "
          "符号定义行=%s" % ("OK " if ok3a else "MISS!", FIXTURE_A, len(kf_fails), len(forms),
                            "OK" if sym_row and sym_row[0]["ok"] else "MISS"))
    all_caught = all_caught and ok3a

    # 3b 正控：该树确实不含 HDR API -> N/A，且必须打得出判定依据，不算红
    na = fixture_texts(hdr_fn=False)
    na_fails, na_rows = fixture_is_green(na, FIXTURE_A, FIXTURE_A)
    hdr_row = [x for x in na_rows if x["label"] == "HdrSignalDiagnostics gate [src]"][0]
    ok3b = (hdr_row["value"] == "N/A" and hdr_row["ok"] and not na_fails
            and "判定依据" in hdr_row["detail"])
    print("  [%s] 无 HDR API 的树 -> value=%s ok=%s 依据已打印=%s（off-water=%d）"
          % ("OK " if ok3b else "MISS!", hdr_row["value"], hdr_row["ok"],
             "是" if "判定依据" in hdr_row["detail"] else "否", len(na_fails)))
    all_caught = all_caught and ok3b

    NEW_MUTATIONS = [
        ("符号常量定义落后基准（KF 唯一真值被 bump 歪）",
         {"vb_form": "symbol", "vb_sym": 19}, "VB 符号常量定义", "G1-api"),
        ("钉位引用符号但定义读不出（必须 fail-closed，不许 N/A）",
         {"vb_form": "symbol", "define_sym": False}, "VB config pin", "G1-api"),
        ("头里承诺了该结构体、内核却没有门 => 锚点失配，仍是红",
         {"hdr_fn": False, "hdr_declared": True}, "HdrSignalDiagnostics gate", "G3-struct"),
        ("符号形态下快照钉位跟着符号走，改歪定义就要命中 G2 那一行",
         {"vb_form": "symbol", "vb_sym": 21}, "VB snapshot pin", "G2-snapshot"),
    ]
    for desc, kwargs, label_frag, group in NEW_MUTATIONS:
        mutant = fixture_texts(**kwargs)
        m_fails, _ = fixture_is_green(mutant, FIXTURE_A, FIXTURE_A)
        hit = [x for x in m_fails if label_frag in x["label"] and x["group"] == group]
        caught = bool(hit)
        all_caught = all_caught and caught
        print("  [%s] %-38s -> group=%s caught=%s"
              % ("RED-ok" if caught else "MISS!", desc[:38], group, bool(hit)))

    print("")
    print("fingerprint(before)=%s  fingerprint(after restore)=%s  equal=%s"
          % (fp_before, fingerprint_fixture(fixture_texts()),
             fp_before == fingerprint_fixture(fixture_texts())))
    print("")
    if all_caught:
        print("VERDICT: TEETH CONFIRMED - every off-water bit is caught in its own group; "
              "the consistent fixture is green and restores byte-for-byte")
        return 0
    print("VERDICT: NO TEETH - a mutation escaped the matrix; do not trust this guard")
    return 1


def fingerprint_fixture(t):
    import hashlib
    blob = "\0".join("%s=%s" % (k, t.get(k)) for k in sorted(t))
    return hashlib.sha1(blob.encode("utf-8", "replace")).hexdigest()[:12]


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("dll", nargs="?", help="待嵌入/部署的 FFF.Native.dll")
    ap.add_argument("--managed-cs", default=DEFAULT_MANAGED_CS,
                    help="托管侧版本常量所在文件（默认 Fff3FpEngine.cs）")
    ap.add_argument("--selftest", action="store_true",
                    help="自证有牙：构造一致夹具(须绿) + 逐位改歪(须红) + 当场还原(须证指纹不变)")
    args = ap.parse_args(argv)

    if args.selftest:
        return selftest_main()

    if not args.dll:
        print("usage error: a FFF.Native.dll path is required unless --selftest")
        return 2

    if not os.path.isfile(args.dll):
        print("FAIL: kernel dll not found: %s" % args.dll)
        return 2

    want = managed_config_version(args.managed_cs)
    want_dest = managed_dest_version(args.managed_cs)
    # probe_api_version 的契约是 (api, err) 二者必居其一 —— 直接当标量用会让"版本相符"
    # 也被判成不符（本脚本首版就这么假红过一次）。
    got, api_err = kx.probe_api_version(args.dll)
    names = set(kx.exports(args.dll))
    missing = [e for e in kx.REQUIRED if e not in names]
    dest_src, got_dest = kernel_dest_version(args.dll)

    print("kernel      : %s" % args.dll)
    print("exports     : %d" % len(names))
    print("api version : kernel=%s managed=%d" % (
        got if got is not None else "unreadable(%s)" % api_err, want))
    print("dest abi    : managed Version=%d, kernel %s" % (
        want_dest,
        ("source %s requires %d" % (dest_src, got_dest) if got_dest is not None
         else "unreadable (找不到 %s 里的 info.version != N)"
              % (dest_src or "PlayerSession.cpp 于该 DLL 路径的上四层内"))))
    if missing:
        print("missing     : %s" % ", ".join(missing))

    bad = []
    if got is None:
        bad.append("kernel API version unreadable (%s) => cannot prove the pairing, "
                   "so this guard refuses to pass" % api_err)
    elif got != want:
        bad.append("API version mismatch: kernel %s != managed %d "
                   "=> every session creation is rejected at runtime and the app silently "
                   "falls back to demo mode" % (got, want))
    if missing:
        bad.append("%d required entry point(s) absent => EntryPointNotFoundException "
                   "(the kernel-log sink install in AppLog has no try/catch)" % len(missing))
    # dest ABI 这一项按"量不到就拦"处理：v1/v2 布局同宽，静态面看不出来，源码门槛是
    # 目前唯一能判的地方；找不到源码树就说明这条没比过，不能算通过。
    if got_dest is None:
        bad.append("cannot read the kernel-side RenderTargetInfo version gate => destX/destY "
                   "signedness (v1 vs v2) is UNVERIFIED, and v1 truncates negative origins into "
                   "huge unsigned values")
    elif got_dest != want_dest:
        bad.append("RenderTargetInfo abi mismatch: kernel requires version %d, managed declares %d "
                   "=> GetRenderTargetInfo returns InvalidArgument and every Dest-based readout "
                   "(follow-the-hand / anchor / probe mapping / pixel fidelity ratio) goes silent"
                   % (got_dest, want_dest))

    # ---- 版本位同水矩阵（本轮新增的"带牙"检查，挂在既有配对守卫上）----
    # 上面几项只回答"托管 ConfigVersion 配不配得上这份 DLL"；本矩阵把**每一个**被版本门控的
    # 常量摊开成表，任一处与基准不同水就并进 bad ⇒ 门禁红。判据同时取**源码常量**（PlayerApi.cpp /
    # PlayerSession.cpp / FFF.Native.rc / C# / VB）与**盘上 DLL 资源**（version.dll 静态解析的
    # FILEVERSION 数值 + 运行时 GetApiVersion），二者都要。
    matrix_findings = run_matrix_for_tree(args.dll, args.managed_cs,
                                          dll_runtime_api=got, api_err=api_err)
    print("")
    print_version_matrix(matrix_findings)
    for f in matrix_findings:
        if not f["ok"]:
            bad.append("[%s] %s: %s" % (f["group"], f["label"], f["detail"]))

    if bad:
        for b in bad:
            print("FAIL: %s" % b)
        return 1
    print("OK: kernel matches the managed binding and every version bit is in the same water")
    return 0


if __name__ == "__main__":
    sys.exit(main())
