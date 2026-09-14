#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
最小转储（minidump）三件套解析：异常记录 + 模块表 + 崩溃线程栈的模块归属。

为什么需要这个：本机没有 cdb/windbg，也装不上（沙箱无 NuGet/网络安装器）。
但回答「崩溃落在哪个模块」只需要读 minidump 的三个流，不需要任何符号：

  * ExceptionStream  (type=6)  异常码 + 异常地址 + 崩溃线程 ID
  * ModuleListStream (type=4)  模块基址/大小/路径 —— 用于把地址映射回模块
  * ThreadListStream (type=3)  线程栈内存 —— 用于按模块还原近似调用序列
  * MemoryInfoListStream (type=16) 页面保护属性 —— 用于识别「不属于任何模块的可执行内存」

无符号栈回溯的局限（必须知道）：minidump 迷你转储不含模块映像，
因此无法校验「某个栈槽是不是真的返回地址」（即无法检查前一条指令是否为 call）。
这里采用标准 triage 启发式：在崩溃线程栈里按地址从低到高扫描落在模块映像区间内的
8 字节值，输出去重后的模块序列。它给出的是**候选调用序列**，不是精确栈回溯。

用法：
    python tools/dump_triage.py <dump.dmp> [更多.dmp...]
    python tools/dump_triage.py --dir <目录>
"""

import os
import struct
import sys

STREAM_MODULE_LIST = 4
STREAM_THREAD_LIST = 3
STREAM_EXCEPTION = 6
STREAM_MEMORY_INFO = 16

# MINIDUMP_MODULE: 8+4+4+4+4+52+8+8+8+8
MODULE_SIZE = 108
# MINIDUMP_THREAD: 4+4+4+4+8+16+8
THREAD_SIZE = 48
# MINIDUMP_EXCEPTION_STREAM: 4+4+152+4
EXCEPTION_STREAM_SIZE = 164
# MINIDUMP_EXCEPTION 内 ExceptionInformation[15] 之前的部分
EXC_CODE_OFF = 0
EXC_ADDR_OFF = 16


class Dump:
    def __init__(self, path):
        self.path = path
        with open(path, "rb") as f:
            self.data = f.read()
        sig, _ver, _nstreams, dir_rva = struct.unpack_from("<IIII", self.data, 0)
        if sig != 0x504D444D:  # 'MDMP'
            raise ValueError("不是 minidump：签名 0x%08X" % sig)
        self.streams = {}
        nstreams = struct.unpack_from("<I", self.data, 12)[0]
        for i in range(nstreams):
            off = dir_rva + i * 12
            stype, _size, rva = struct.unpack_from("<III", self.data, off)
            self.streams[stype] = rva

    def rva(self, off, n):
        return self.data[off:off + n]

    # ---------- 模块表 ----------
    def modules(self):
        rva = self.streams.get(STREAM_MODULE_LIST)
        if not rva:
            return []
        count = struct.unpack_from("<I", self.data, rva)[0]
        out = []
        for i in range(count):
            base = rva + 4 + i * MODULE_SIZE
            img_base, img_size, _cs, _ts, name_rva = struct.unpack_from(
                "<QIIII", self.data, base)
            path = self._read_ministring(name_rva)
            out.append({
                "base": img_base,
                "end": img_base + max(img_size, 1),
                "size": img_size,
                "path": path,
                "name": os.path.basename(path) if path else "?",
            })
        out.sort(key=lambda m: m["base"])
        return out

    def _read_ministring(self, off):
        if off <= 0 or off + 4 > len(self.data):
            return ""
        n = struct.unpack_from("<I", self.data, off)[0]
        try:
            return self.data[off + 4:off + 4 + n].decode("utf-16-le").rstrip("\x00")
        except Exception:
            return ""

    def module_of(self, addr, mods):
        for m in mods:
            if m["base"] <= addr < m["end"]:
                return m
        return None

    # ---------- 异常记录 ----------
    def exception(self):
        rva = self.streams.get(STREAM_EXCEPTION)
        if not rva:
            return None
        tid = struct.unpack_from("<I", self.data, rva)[0]
        code = struct.unpack_from("<I", self.data, rva + 8 + EXC_CODE_OFF)[0]
        addr = struct.unpack_from("<Q", self.data, rva + 8 + EXC_ADDR_OFF)[0]
        # MINIDUMP_EXCEPTION: NumberParameters @+24，ExceptionInformation[15] @+32
        nparams = struct.unpack_from("<I", self.data, rva + 8 + 24)[0]
        info = []
        for i in range(min(nparams, 15)):
            info.append(struct.unpack_from("<Q", self.data, rva + 8 + 32 + i * 8)[0])
        # MINIDUMP_EXCEPTION_STREAM: ThreadId(4) align(4) ExceptionRecord(152)
        #   → ThreadContext 是 MINIDUMP_LOCATION_DESCRIPTOR{DataSize(4), Rva(4)}，
        #     所以 Rva 在 +164，+160 是 DataSize（曾在这里踩坑：读到 0 导致上下文全 0）
        ctx_rva = struct.unpack_from("<I", self.data, rva + 164)[0]
        # 寄存器偏移：标准 CONTEXT_AMD64 是 Rax 136 / Rsp 168 / Rbp 176 / Rip 264，
        # 但**这批转储实测整体前移 16 字节**（Rip 248 / Rsp 152 / Rbp 160 / Rax 120）。
        # 的两个独立证据：① Rip 位置的取值在 7/7 个非 FailFast 转储中恒等于 ExceptionAddress
        # （SIGILL 与 DEP 类异常的 RIP 必然等于它，这是硬约束）；
        # ② Rsp / Rbp 位置的取值落在崩溃线程记录的栈区间内。
        # 按标准布局读会得到 RIP=0，切勿"改回去"。
        ctx = {}
        if ctx_rva:
            ctx["rax"] = struct.unpack_from("<Q", self.data, ctx_rva + 120)[0]
            ctx["rsp"] = struct.unpack_from("<Q", self.data, ctx_rva + 152)[0]
            ctx["rbp"] = struct.unpack_from("<Q", self.data, ctx_rva + 160)[0]
            ctx["rip"] = struct.unpack_from("<Q", self.data, ctx_rva + 248)[0]
        return {"tid": tid, "code": code, "addr": addr, "ctx": ctx, "info": info}

    # ---------- 线程 ----------
    def threads(self):
        rva = self.streams.get(STREAM_THREAD_LIST)
        if not rva:
            return []
        count = struct.unpack_from("<I", self.data, rva)[0]
        out = []
        for i in range(count):
            base = rva + 4 + i * THREAD_SIZE
            tid, _sus, _pc, _pri, _teb = struct.unpack_from("<IIIII", self.data, base)
            stack_addr, data_size, data_rva = struct.unpack_from(
                "<QII", self.data, base + 24)
            out.append({
                "tid": tid,
                "stack_start": stack_addr,
                "size": data_size,
                "rva": data_rva,
            })
        return out

    # ---------- 内存区域 ----------
    def region_of(self, addr):
        rva = self.streams.get(STREAM_MEMORY_INFO)
        if not rva:
            return None
        size_of_header, size_of_entry, count = struct.unpack_from("<III", self.data, rva)
        base = rva + size_of_header
        # MINIDUMP_MEMORY_INFO: BaseAddress(8) AllocationBase(8) Protect(4)
        #   AllocationProtect(4) RegionSize(8) State(4) Protect... -> 实际布局：
        #   ULONG64 BaseAddress; ULONG64 AllocationBase; ULONG32 AllocationProtect;
        #   ULONG32 __alignment1; ULONG64 RegionSize; ULONG32 State;
        #   ULONG32 Protect; ULONG32 Type; ULONG32 __alignment2;  = 48
        entry = size_of_entry if size_of_entry else 48
        for i in range(min(count, 200000)):
            off = base + i * entry
            if off + 48 > len(self.data):
                break
            ba, _ab, alloc_protect, _al, rsize, state, protect, _ty = struct.unpack_from(
                "<QQIIQIII", self.data, off)
            if ba <= addr < ba + rsize:
                return {"base": ba, "size": rsize, "state": state,
                        "protect": protect, "alloc_protect": alloc_protect}
        return None


PROTECT_NAMES = {
    0x01: "NOACCESS", 0x02: "READONLY", 0x04: "READWRITE",
    0x08: "WRITECOPY", 0x10: "EXECUTE", 0x20: "EXECUTE_READ",
    0x40: "EXECUTE_READWRITE", 0x80: "EXECUTE_WRITECOPY",
}
STATE_NAMES = {0x1000: "COMMIT", 0x2000: "RESERVE", 0x10000: "FREE"}

# 只把这些模块当作「值得报告」的栈帧候选；系统底层 DLL 噪声太大，单独标注
NOISE = {
    "ntdll.dll", "kernel32.dll", "kernelbase.dll", "user32.dll", "win32u.dll",
    "gdi32.dll", "gdi32full.dll", "msvcp140.dll", "vcruntime140.dll",
    "vcruntime140_1.dll", "ucrtbase.dll", "msvcrt.dll", "advapi32.dll",
    "sechost.dll", "rpcrt4.dll", "combase.dll", "ole32.dll", "oleaut32.dll",
    "shell32.dll", "shcore.dll", "imm32.dll", "msctf.dll", "ws2_32.dll",
    "cryptbase.dll", "bcrypt.dll", "bcryptprimitives.dll", "cfgmgr32.dll",
    "propsys.dll", "windows.storage.dll", "powrprof.dll", "umpdc.dll",
    "profapi.dll", "msvcp_win.dll", "win32u.dll", "winmm.dll", "version.dll",
    "uxtheme.dll", "dwmapi.dll", "dxgi.dll", "d3d11.dll", "dxcore.dll",
}


def scan_stack(dump, thr, mods, limit=4096, from_va=None):
    """在崩溃线程栈里找落在模块映像区间内的 8 字节值，按栈地址升序遍历。

    from_va 给定时（应为崩溃线程的 RSP），只扫描 RSP 之上的部分——
    栈向低地址增长，RSP 以下的内容是上一次调用的死值，扫进去只会引入假帧。
    """
    if not thr["rva"] or not thr["size"]:
        return []
    start = thr["rva"]
    end = min(start + thr["size"], len(dump.data))
    if from_va and thr["stack_start"] <= from_va < thr["stack_start"] + thr["size"]:
        start = thr["rva"] + (from_va - thr["stack_start"])
    seq = []
    for off in range(start, end - 7, 8):
        val = struct.unpack_from("<Q", dump.data, off)[0]
        m = None
        for mm in mods:
            if mm["base"] <= val < mm["end"]:
                m = mm
                break
        if m:
            seq.append((off - start, val, m))
            if len(seq) >= limit:
                break
    return seq


def thread_census(d, mods):
    """崩溃时刻的线程构成快照。

    关心两件事：① 有多少线程是 FFF.Native 自建的原生线程（栈底出现 _beginthreadex
    且最外层非系统帧是 FFF.Native）；② 这些线程是否与托管侧（coreclr）有交集。
    用于判断「每路一个渲染线程」这类并发结构假设。
    """
    rows = []
    for t in d.threads():
        seq = scan_stack(d, t, mods, limit=100000)
        names = [m["name"].lower() for _o, _v, m in seq]
        rows.append({
            "tid": t["tid"],
            "size": t["size"],
            "native": "fff.native.dll" in names,
            "managed": "coreclr.dll" in names,
            "dxgi": "dxgi.dll" in names,
            "d3d11": "d3d11.dll" in names,
            "outer": seq[-1][2]["name"] if seq else "",
        })
    return rows


def report(path):
    d = Dump(path)
    mods = d.modules()
    exc = d.exception()
    print("=" * 78)
    print("转储: %s" % os.path.basename(path))
    print("大小: %.1f MB   模块数: %d" % (len(d.data) / 1048576.0, len(mods)))
    if not exc:
        print("  无异常流（该转储可能不是崩溃转储）")
        return
    m = d.module_of(exc["addr"], mods)
    print("异常码: 0x%08X (%s)" % (exc["code"], exc_name(exc["code"])))
    print("异常地址: 0x%016X" % exc["addr"])
    info = exc.get("info") or []
    if code_is_av(exc["code"]) and len(info) >= 2:
        rw = {0: "读", 1: "写", 8: "执行/DEP"}.get(info[0], "?")
        tgt = info[1]
        tm = d.module_of(tgt, mods)
        print("访问类型: %s    被访问地址: 0x%016X" % (rw, tgt))
        if tm:
            print("  → 目标落在模块 %s +0x%X" % (tm["name"], tgt - tm["base"]))
        elif tgt < 0x10000:
            print("  → **空指针/小偏移解引用**（典型的「对象已释放或句柄为空」特征）")
        else:
            print("  → 目标不在任何模块内（堆/已释放内存）")
    if m:
        print("  → 崩溃指令所在模块: %s  (RVA +0x%X)" % (m["name"], exc["addr"] - m["base"]))
        print("    路径: %s" % m["path"])
    else:
        reg = d.region_of(exc["addr"])
        print("  → **不属于任何已加载模块**")
        if reg:
            print("    区域: base=0x%X size=0x%X state=%s protect=%s" % (
                reg["base"], reg["size"],
                STATE_NAMES.get(reg["state"], hex(reg["state"])),
                PROTECT_NAMES.get(reg["protect"], hex(reg["protect"]))))
        else:
            print("    区域: 未在 MemoryInfoList 中找到（可能已释放/未提交）")
    ctx = exc["ctx"]
    if ctx:
        cm = d.module_of(ctx["rip"], mods)
        print("崩溃线程: TID=%d  RIP=0x%016X  %s" % (
            exc["tid"], ctx["rip"],
            (cm["name"] + " +0x%X" % (ctx["rip"] - cm["base"])) if cm else "<无模块>"))
        print("           RSP=0x%016X  RBP=0x%016X" % (ctx["rsp"], ctx["rbp"]))

    # 崩溃线程栈
    thr = None
    for t in d.threads():
        if t["tid"] == exc["tid"]:
            thr = t
            break
    if not thr:
        return
    seq = scan_stack(d, thr, mods, from_va=ctx.get("rsp") if ctx else None)
    print("崩溃线程栈候选帧（自 RSP 向上，去重相邻同模块）:")
    last = None
    shown = 0
    for off, val, mm in seq:
        nm = mm["name"]
        if nm == last:
            continue
        last = nm
        tag = "" if nm.lower() not in NOISE else "  [系统]"
        print("   +0x%04X  0x%016X  %-28s +0x%-7X%s" % (
            off, val, nm, val - mm["base"], tag))
        shown += 1
        if shown >= 40:
            print("   ...（截断）")
            break
    if not seq:
        print("   （栈内未找到落在模块映像区间的指针）")

    if DO_CENSUS:
        rows = thread_census(d, mods)
        nat = [r for r in rows if r["native"]]
        print("线程构成: 共 %d 个线程；含 FFF.Native 帧 %d 个；含 coreclr 帧 %d 个" % (
            len(rows), len(nat), len([r for r in rows if r["managed"]])))
        print("  %-8s %-8s %-6s %-6s %-6s %s" % ("TID", "栈大小", "原生", "托管", "D3D", "最外层模块"))
        for r in rows:
            if not (r["native"] or r["dxgi"] or r["d3d11"]):
                continue
            mark = "  ← 崩溃线程" if r["tid"] == exc["tid"] else ""
            print("  %-8d %-8d %-6s %-6s %-6s %s%s" % (
                r["tid"], r["size"],
                "Y" if r["native"] else ".",
                "Y" if r["managed"] else ".",
                "Y" if (r["dxgi"] or r["d3d11"]) else ".",
                r["outer"], mark))

    # 列出项目/内核相关模块，便于确认版本
    interesting = [x for x in mods if x["name"].lower() in (
        "fff.native.dll", "3fcompare.exe", "dxgi.dll", "d3d11.dll",
        "coreclr.dll", "hostpolicy.dll", "hostfxr.dll", "libSkiaSharp.dll",
        "avcodec-61.dll", "avutil-59.dll", "swscale-8.dll", "avformat-61.dll")]
    if interesting:
        print("关键模块:")
        for x in sorted(interesting, key=lambda y: y["base"]):
            print("   0x%016X - 0x%016X  %s" % (x["base"], x["end"], x["path"]))


def code_is_av(code):
    return code == 0xC0000005


def exc_name(code):
    return {
        0xC0000005: "访问违例 ACCESS_VIOLATION",
        0xC000001D: "非法指令 ILLEGAL_INSTRUCTION",
        0xC0000602: "FailFast",
        0xC000000D: "无效参数 INVALID_PARAMETER",
        0xC0000417: "STATUS_INVALID_CRUNTIME_PARAMETER",
        0xC00000FD: "栈溢出 STACK_OVERFLOW",
        0xC0000094: "除零",
        0xC0000374: "堆损坏 HEAP_CORRUPTION",
        0xE0434352: "CLR 托管异常",
        0xC0000409: "栈缓冲区溢出 / __fastfail",
    }.get(code, "?")


DO_CENSUS = False


def main():
    global DO_CENSUS
    args = sys.argv[1:]
    if "--threads" in args:
        DO_CENSUS = True
        args = [a for a in args if a != "--threads"]
    if not args:
        print(__doc__)
        return 2
    if args[0] == "--dir":
        root = args[1] if len(args) > 1 else "."
        files = sorted(
            (os.path.join(root, f) for f in os.listdir(root) if f.lower().endswith(".dmp")),
            key=os.path.getmtime)
    else:
        files = args
    for f in files:
        try:
            report(f)
        except Exception as e:
            print("解析失败 %s: %s" % (f, e))
    return 0


if __name__ == "__main__":
    sys.exit(main())
