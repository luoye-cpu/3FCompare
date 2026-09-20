"""离线决定性检验：用完整内存转储判断那次 AV 是"用户态主动 raise"还是"内核派发"。

为什么这条路可行（且不需要再跑应用）
--------------------------------------
7 个 60MB 的 MiniDumpWithFullMemory（%LOCALAPPDATA%\\CrashDumps）里既有异常流，
也有**故障线程的整段栈内存**。于是可以在事后把 RSP 两侧都翻一遍：

  * 若异常是用户态 `RaiseException` 发起的，栈上必然留有
    `ntdll!RtlRaiseException` 的帧（它会在自己栈上建一个 CONTEXT 并调用
    `NtRaiseException`），以及 `KERNELBASE!RaiseException` 的返回地址。
    —— 这些帧位于**伪造 RSP 之下**（更低的地址），此前的取证只扫了 RSP 之上，
    所以"栈上没有 raise 帧"这句话并不能证伪伪造假设。
  * 若异常是内核直接派发的（硬件访存），栈上不会有这些帧，
    但会有内核在 KiUserExceptionDispatcher 处压下的 CONTEXT/EXCEPTION_RECORD。

同时把 r10=0x546C6148 这个跨多次崩溃恒定的值在全内存里搜一遍，看它到底是什么。

用法：
    python dump_raise_scan.py                 # 扫 CrashDumps 下全部 3FCompare 转储
    python dump_raise_scan.py --dump <路径>
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import breaktrace as bt   # noqa: E402
import minidump as md     # noqa: E402
import symbolize as S     # noqa: E402

ST_MEMORY_INFO_LIST = 16

CTX_EFLAGS = 0x44
CTX_DR0 = 0x48
CTX_DR7 = 0x70
CTX_RIP = 0xF8
CTX_RSP = 0x98

REGS = [("rax", 0x78), ("rcx", 0x80), ("rdx", 0x88), ("rbx", 0x90),
        ("rsp", CTX_RSP), ("rbp", 0xA0), ("rsi", 0xA8), ("rdi", 0xB0),
        ("r8", 0xB8), ("r9", 0xC0), ("r10", 0xC8), ("r11", 0xD0),
        ("r12", 0xD8), ("r13", 0xE0), ("r14", 0xE8), ("r15", 0xF0),
        ("rip", CTX_RIP)]

# 用户态 raise 的"指纹"函数（命中栈上任一即说明 raise 走过这里）
RAISE_FUNCS = ("RtlRaiseException", "NtRaiseException", "ZwRaiseException",
               "RaiseException", "RaiseFailFastException", "KiUserExceptionDispatcher",
               "RtlDispatchException", "RtlpExecuteHandlerForException",
               "RtlUnwindEx", "KiUserCallbackDispatcher")


def mem_info_list(d: md.Dump):
    """解析 MemoryInfoList 流，返回 [(base, size, state, protect, type)]。"""
    if ST_MEMORY_INFO_LIST not in d.streams:
        return []
    rva, _ = d.streams[ST_MEMORY_INFO_LIST]
    data = d.data
    hdr_size, ent_size, count = struct.unpack_from("<IIQ", data, rva)
    p = rva + hdr_size
    out = []
    for _ in range(count):
        base, alloc, aprot, _a1, size, state, protect, typ, _a2 = \
            struct.unpack_from("<QQIIQIIII", data, p)
        p += ent_size
        out.append((base, size, state, protect, typ, alloc, aprot))
    return out


def describe(sz, mods, addr):
    if not addr:
        return None
    m = mods.find(addr)
    r = sz.resolve(addr)
    return {"address": f"0x{addr:016X}",
            "module": os.path.basename(m.path) if m else None,
            "rva": f"0x{addr - m.base:X}" if m else None,
            "function": r.get("function"), "symbol": r.get("symbol"),
            "source": r.get("source"), "line": r.get("line")}


def scan_stack_for_raise(sz, mods, d: md.Dump, stack_lo, stack_hi):
    """扫整段栈，挑出落在 ntdll/kernelbase 的代码指针并符号化，重点看 raise 指纹。"""
    hits = []
    chunk = 0x10000
    addr = stack_lo
    while addr < stack_hi:
        n = min(chunk, stack_hi - addr)
        blob = d.read(addr, n)
        if not blob:
            addr += chunk
            continue
        for i in range(0, len(blob) - 8 + 1, 8):
            v, = struct.unpack_from("<Q", blob, i)
            if not (0x10000 <= v <= 0x00007FFFFFFFFFFF):
                continue
            m = mods.find(v)
            if m is None:
                continue
            name = os.path.basename(m.path).lower()
            if name not in ("ntdll.dll", "kernelbase.dll"):
                continue
            info = describe(sz, mods, v)
            fn = (info.get("function") or "")
            hits.append({"slot": f"0x{addr + i:016X}", "value": f"0x{v:016X}",
                         "module": info["module"], "rva": info["rva"],
                         "function": fn,
                         "is_raise_frame": any(k in fn for k in RAISE_FUNCS)})
        addr += n
    return hits


def scan_stack_for_structures(d: md.Dump, stack_lo, stack_hi):
    """在栈窗口里找 CONTEXT / EXCEPTION_RECORD 结构（两种布局都认）。"""
    ctxs, recs = [], []
    blob = d.read(stack_lo, stack_hi - stack_lo)
    for i in range(0, max(0, len(blob) - 0x100) + 1, 8):
        head = blob[i:i + 0x100]
        if len(head) < 0x100:
            break
        cf, = struct.unpack_from("<I", head, 0x30)
        if (cf & 0x00100000) and (cf & 0xF) and not (cf & ~0x0010007F):
            rip, = struct.unpack_from("<Q", head, CTX_RIP)
            rsp, = struct.unpack_from("<Q", head, CTX_RSP)
            if 0x10000 <= rip <= 0x7FFFFFFFFFFF and 0x10000 <= rsp <= 0x7FFFFFFFFFFF:
                ctxs.append({"at": f"0x{stack_lo + i:016X}",
                             "flags": f"0x{cf:08X}",
                             "rip": f"0x{rip:016X}", "rsp": f"0x{rsp:016X}"})
        if i + 0x98 <= len(blob):
            code, rflags = struct.unpack_from("<II", blob, i)
            addr2, = struct.unpack_from("<Q", blob, i + 0x10)
            npar, = struct.unpack_from("<I", blob, i + 0x18)
            if code >= 0x80000000 and rflags <= 3 and npar <= 15 \
                    and 0x10000 <= addr2 <= 0x7FFFFFFFFFFF:
                recs.append({"at": f"0x{stack_lo + i:016X}",
                             "code": f"0x{code:08X}", "flags": rflags,
                             "address": f"0x{addr2:016X}", "nparam": npar})
    return ctxs, recs


def find_pattern(d: md.Dump, pattern: bytes, limit=12):
    out = []
    for start, size, rva in d._mem_segs:
        seg = d.data[rva:rva + size]
        off = seg.find(pattern)
        while off >= 0 and len(out) < limit:
            out.append(f"0x{start + off:016X}")
            off = seg.find(pattern, off + 1)
        if len(out) >= limit:
            break
    return out


def analyze(path: str, sz: S.Symbolizer):
    d = md.Dump(path)
    er = d.exception
    if er is None:
        return {"dump": path, "error": "没有异常流"}
    t = d.crash_thread()
    if t is None:
        return {"dump": path, "error": "找不到故障线程"}
    ctx = d.data[t.context_rva:t.context_rva + 0x4D0]
    regs = {n: struct.unpack_from("<Q", ctx, o)[0] for n, o in REGS}
    rsp, rip = regs["rsp"], regs["rip"]

    mods = d.modules
    for m in mods:
        if m.path and os.path.isfile(m.path):
            try:
                sz.load(m.path, base=m.base, size=m.size,
                        name=os.path.basename(m.path))
            except Exception:
                pass

    mi = mem_info_list(d)
    def page_of(a):
        for base, size, state, protect, typ, alloc, aprot in mi:
            if base <= a < base + size:
                return {"base": f"0x{base:X}", "size": f"0x{size:X}",
                        "state": hex(state), "protect": hex(protect), "type": hex(typ)}
        return None

    stack_lo = max(t.stack_start, rsp - 0x100000)
    stack_hi = min(t.stack_start + t.stack_size, rsp + 0x20000)
    raise_hits = scan_stack_for_raise(sz, mods, d, stack_lo, stack_hi)
    ctxs, recs = scan_stack_for_structures(d, stack_lo, stack_hi)

    # RSP 之上/之下各看几个槽
    above = []
    for k in range(0, 12):
        b = d.read(rsp + k * 8, 8)
        if len(b) == 8:
            v, = struct.unpack("<Q", b)
            above.append({"slot": f"rsp+0x{k*8:X}", "value": f"0x{v:016X}",
                          **({k2: v2 for k2, v2 in
                              (describe(sz, mods, v) or {}).items()
                              if k2 in ("module", "rva", "function")} if v > 0x10000 else {})})
    below = []
    for k in range(1, 16):
        b = d.read(rsp - k * 8, 8)
        if len(b) == 8:
            v, = struct.unpack("<Q", b)
            below.append({"slot": f"rsp-0x{k*8:X}", "value": f"0x{v:016X}",
                          **({k2: v2 for k2, v2 in
                              (describe(sz, mods, v) or {}).items()
                              if k2 in ("module", "rva", "function")} if v > 0x10000 else {})})

    return {
        "dump": path,
        "dump_size": os.path.getsize(path),
        "exception": {"code": f"0x{er.code:08X}",
                      "name": md.EXC_NAMES.get(er.code, "UNKNOWN"),
                      "address": f"0x{er.address:016X}",
                      "flags": er.flags,
                      "params": [f"0x{p:016X}" for p in er.params],
                      "thread_id": er.thread_id},
        "registers": {n: f"0x{v:016X}" for n, v in regs.items()},
        "rip_in_module": describe(sz, mods, rip),
        "rsp_page": page_of(rsp),
        "rip_page": page_of(rip),
        "stack_range": [f"0x{stack_lo:X}", f"0x{stack_hi:X}"],
        "raise_frames_on_stack": [h for h in raise_hits if h["is_raise_frame"]],
        "ntdll_kernelbase_ptrs_on_stack": raise_hits[:60],
        "context_structs_on_stack": ctxs[:10],
        "exception_records_on_stack": recs[:10],
        "rsp_above": above,
        "rsp_below": below,
        "sentinel_546C6148_locations": find_pattern(d, struct.pack("<Q", 0x546C6148)),
        "r11_value_locations": find_pattern(d, struct.pack("<Q", 0xFFFFFFFFEF2375CF)),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", action="append", default=None)
    ap.add_argument("--out", default=os.path.join(HERE, "scenes", "dump_raise_scan.json"))
    ap.add_argument("--limit", type=int, default=3)
    a = ap.parse_args()

    dumps = a.dump or sorted(
        glob.glob(os.path.join(os.environ.get("LOCALAPPDATA", ""), "CrashDumps",
                               "3FCompare.exe.*.dmp")),
        key=os.path.getmtime, reverse=True)[:a.limit]
    if not dumps:
        print("没找到转储")
        return 1

    sz = S.Symbolizer(sympath=bt.SYMPATH)
    results = []
    try:
        for p in dumps:
            print(f"\n===== {os.path.basename(p)} =====", flush=True)
            r = analyze(p, sz)
            results.append(r)
            if "error" in r:
                print("  ", r["error"])
                continue
            e = r["exception"]
            print(f"  异常 {e['code']} {e['name']} addr={e['address']} "
                  f"params={e['params']} tid={e['thread_id']}")
            print(f"  RIP={r['rip_in_module']}")
            print(f"  RSP 页: {r['rsp_page']}   RIP 页: {r['rip_page']}")
            print(f"  栈上 raise 指纹帧: {r['raise_frames_on_stack'] or '【无】'}")
            print(f"  栈上 ntdll/kernelbase 指针 {len(r['ntdll_kernelbase_ptrs_on_stack'])} 个：")
            for h in r["ntdll_kernelbase_ptrs_on_stack"][:15]:
                print(f"     {h['slot']} -> {h['module']}+{h['rva']} {h['function']}")
            print(f"  栈上 CONTEXT 结构: {r['context_structs_on_stack'] or '无'}")
            print(f"  栈上 EXCEPTION_RECORD: {r['exception_records_on_stack'] or '无'}")
            print("  RSP 之上：")
            for x in r["rsp_above"][:6]:
                print(f"     {x['slot']} {x['value']} "
                      f"{x.get('module','')}+{x.get('rva','')} {x.get('function') or ''}")
            print("  RSP 之下：")
            for x in r["rsp_below"][:8]:
                print(f"     {x['slot']} {x['value']} "
                      f"{x.get('module','')}+{x.get('rva','')} {x.get('function') or ''}")
            print(f"  0x546C6148 出现位置: {r['sentinel_546C6148_locations']}")
            print(f"  r11 值出现位置: {r['r11_value_locations']}")
    finally:
        sz.close()
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(results, f, ensure_ascii=False, indent=2)
    print(f"\n-> {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
