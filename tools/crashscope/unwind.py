"""真实栈回溯：用 dbghelp 的 StackWalk64 对崩溃现场做 unwind。

为什么不用"栈扫描"
----------------
crashscope 早期的 `stack_scan` 是把 rsp 起每个 8 字节槽当成候选返回地址，再看它落
不落在某个模块的可执行节里。它有两个硬伤：
  1. 数据里碰巧长得像代码指针的整数会被当成帧（假帧）；
  2. 它只认"槽里的值"，对 `sub rsp, N` / 保存寄存器 / 尾调用一概不知，跳帧很常见。
StackWalk64 走的是操作系统同款展开算法：按 RIP 查模块的 RUNTIME_FUNCTION(.pdata)，
执行 UNWIND_INFO 里的展开码，逐帧还原 RSP/RBP/被调用者保存寄存器。得到的才是真正的
调用链。

两种用法
-------
1) 实时（首选）：被调试进程在异常点被冻结时直接回溯。dbghelp 通过被调试进程的句柄
   读栈，所以不需要把栈拷出来。
       from unwind import unwind_live
       info = unwind_live(h_process, pid, tid, modules, log=print)

2) 离线：从 sidecar（unwind_live 落盘的低现场）重放。用 ctypes 回调把"读进程内存"
   换成"读 sidecar 里的栈字节"，StackWalk64 逻辑完全不变。
       python unwind.py --sidecar scenes/final.raw.json

sidecar 里存什么：CONTEXT 的通用寄存器 + 从 RSP 起 128KB 原始栈字节 + 那一刻的模块表。
有了它，回溯可以在事后无限次重跑，不必再制造崩溃。
"""
from __future__ import annotations

import argparse
import base64
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import symbolize as S  # noqa: E402

IMAGE_FILE_MACHINE_AMD64 = 0x8664
ADDR_MODE_FLAT = 3  # ADDRESS_MODE::AddrModeFlat
CONTEXT_AMD64 = 0x00100000
CONTEXT_CONTROL = CONTEXT_AMD64 | 0x00000001
CONTEXT_INTEGER = CONTEXT_AMD64 | 0x00000002
CONTEXT_FULL = CONTEXT_AMD64 | 0x0000000B
CTX_CONTEXT_FLAGS = 0x30
THREAD_GET_CONTEXT = 0x0008
THREAD_QUERY_INFORMATION = 0x0040

STACK_READ_MAX = 0x20000

REGS_ORDER = [("rax", 0x78), ("rcx", 0x80), ("rdx", 0x88), ("rbx", 0x90),
              ("rsp", 0x98), ("rbp", 0xA0), ("rsi", 0xA8), ("rdi", 0xB0),
              ("r8", 0xB8), ("r9", 0xC0), ("r10", 0xC8), ("r11", 0xD0),
              ("r12", 0xD8), ("r13", 0xE0), ("r14", 0xE8), ("r15", 0xF0),
              ("rip", 0xF8)]

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("__alignment1", wt.DWORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__alignment2", wt.DWORD)]


def _bind():
    _k32.OpenThread.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
    _k32.OpenThread.restype = wt.HANDLE
    _k32.GetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
    _k32.GetThreadContext.restype = wt.BOOL
    _k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                       ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    _k32.ReadProcessMemory.restype = wt.BOOL
    _k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p,
                                    ctypes.POINTER(MEMORY_BASIC_INFORMATION),
                                    ctypes.c_size_t]
    _k32.VirtualQueryEx.restype = ctypes.c_size_t
    _k32.CloseHandle.argtypes = [wt.HANDLE]


_bind()


def _aligned_ctx_buffer():
    raw = ctypes.create_string_buffer(0x4D0 + 32)
    base = ctypes.addressof(raw)
    aligned = (base + 15) & ~15
    return raw, ctypes.c_void_p(aligned), aligned


def get_thread_context_raw(h_process, tid):
    """取故障线程的原始 CONTEXT（对齐好的缓冲区）。返回 (buf, ptr, off, regs)。"""
    h = _k32.OpenThread(THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, False, tid)
    if not h:
        return None
    try:
        raw, ptr, aligned = _aligned_ctx_buffer()
        base = ctypes.addressof(raw)
        off = aligned - base
        # ⚠ ContextFlags 在 CONTEXT 的 +0x30，写错位置 GetThreadContext 直接失败
        struct.pack_into("<I", raw, off + CTX_CONTEXT_FLAGS, CONTEXT_FULL)
        if not _k32.GetThreadContext(h, ptr):
            return None
        regs = {name: struct.unpack_from("<Q", raw, off + o)[0]
                for name, o in REGS_ORDER}
        return raw, ptr, off, regs
    finally:
        _k32.CloseHandle(h)


def build_context(regs: dict):
    """用寄存器字典重建一个 CONTEXT 缓冲区（离线回溯用）。"""
    raw, ptr, aligned = _aligned_ctx_buffer()
    base = ctypes.addressof(raw)
    off = aligned - base
    struct.pack_into("<I", raw, off + CTX_CONTEXT_FLAGS, CONTEXT_FULL)
    for name, o in REGS_ORDER:
        if name in regs and regs[name] is not None:
            struct.pack_into("<Q", raw, off + o, regs[name] & 0xFFFFFFFFFFFFFFFF)
    return raw, ptr, off


def read_stack_region(h_process, rsp, max_bytes=STACK_READ_MAX):
    mbi = MEMORY_BASIC_INFORMATION()
    if not _k32.VirtualQueryEx(h_process, ctypes.c_void_p(rsp), ctypes.byref(mbi),
                              ctypes.sizeof(mbi)):
        return 0, b""
    region_end = (mbi.BaseAddress or 0) + mbi.RegionSize
    size = min(region_end - rsp, max_bytes)
    if size <= 0:
        return 0, b""
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t(0)
    _k32.ReadProcessMemory(h_process, ctypes.c_void_p(rsp), buf, size, ctypes.byref(got))
    return (rsp, buf.raw[:got.value]) if got.value else (rsp, b"")


def load_modules_into(sz: S.Symbolizer, modules: list[dict], log=None):
    """把现场那一刻的模块表按真实基址登记进 dbghelp。

    必须用真实基址：StackWalk64 拿返回地址去查模块基址，基址错了整条链会当场断掉。
    """
    ok = 0
    for m in modules:
        path = m.get("path")
        if not path or not os.path.isfile(path):
            continue
        try:
            sz.load(path, base=int(m["base"]), size=int(m.get("size") or 0),
                    name=m.get("name"))
            ok += 1
        except S.SymError as e:
            if log:
                log(f"  [unwind] 跳过模块 {m.get('name')}: {e}")
    if log:
        log(f"  [unwind] 已登记 {ok}/{len(modules)} 个模块")
    return ok


def _walk(h_process, h_thread, sz: S.Symbolizer, ctx_ptr, max_frames=64,
          read_routine=None, log=None):
    sf = S.STACKFRAME64()
    ctx = ctypes.cast(ctx_ptr, ctypes.c_void_p)
    # 从 CONTEXT 里把种子寄存器抄进 STACKFRAME64
    off = ctx_ptr.value
    rip = ctypes.c_ulonglong.from_address(off + 0xF8).value
    rsp = ctypes.c_ulonglong.from_address(off + 0x98).value
    rbp = ctypes.c_ulonglong.from_address(off + 0xA0).value
    sf.AddrPC.Offset = rip
    sf.AddrStack.Offset = rsp
    sf.AddrFrame.Offset = rbp
    # ⚠ ADDRESS64.Mode 必须显式设成 AddrModeFlat(=3)。留 0（AddrMode1616）时
    #   StackWalk64 会直接返回 0 且一次内存回调都不发，看上去像"回溯不出来"。
    sf.AddrPC.Mode = ADDR_MODE_FLAT
    sf.AddrStack.Mode = ADDR_MODE_FLAT
    sf.AddrFrame.Mode = ADDR_MODE_FLAT

    fta = ctypes.cast(S.DBG.SymFunctionTableAccess64, ctypes.c_void_p)
    gmb = ctypes.cast(S.DBG.SymGetModuleBase64, ctypes.c_void_p)

    frames = []
    for i in range(max_frames):
        ok = S.DBG.StackWalk64(IMAGE_FILE_MACHINE_AMD64, h_process, h_thread,
                               ctypes.byref(sf), ctx, read_routine, fta, gmb, None)
        if not ok:
            break
        addr = sf.AddrPC.Offset
        if addr == 0:
            break
        mod = None
        for m in sz.modules.values():
            if m["base"] <= addr < m["base"] + max(m["size"], 1):
                mod = m
                break
        r = sz.resolve(addr)
        frames.append({
            "frame": i,
            "address": f"0x{addr:016X}",
            "module": mod["name"] if mod else None,
            "rva": (addr - mod["base"]) if mod else None,
            "rva_hex": f"0x{addr - mod['base']:X}" if mod else None,
            "symbol": r["symbol"],
            "function": r["function"],
            "displacement": r["displacement"],
            "source": r["source"],
            "line": r["line"],
            "stack": f"0x{sf.AddrStack.Offset:016X}",
            "frame_ptr": f"0x{sf.AddrFrame.Offset:016X}",
        })
        if log:
            loc = f"{r['source']}:{r['line']}" if r["source"] else "-"
            disp = f"+0x{r['displacement']:X}" if r["displacement"] else ""
            log(f"  #{i:<2} {frames[-1]['module'] or '?'}"
                f"{('+0x%X' % frames[-1]['rva']) if frames[-1]['rva'] is not None else ''}"
                f"  {r['symbol'] or '(无符号)'}{disp}   [{loc}]")
    return frames


def unwind_live(h_process, pid, tid, modules, sympath=None, max_frames=64,
                log=None):
    """在被调试进程仍冻结时做真实 unwind。返回 dict（可直接塞进现场 JSON）。"""
    got = get_thread_context_raw(h_process, tid)
    if not got:
        return {"ok": False, "reason": "GetThreadContext 失败"}
    raw, ptr, off, regs = got

    h_thread = _k32.OpenThread(THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, False, tid)
    try:
        sz = S.Symbolizer(sympath=sympath or S.LOCAL_ONLY_SYMPATH, hproc=h_process)
        try:
            load_modules_into(sz, modules, log=log)
            # 逐帧回溯需要按 RIP 查 .pdata；dbghelp 会通过 h_process 读被调试进程内存
            frames = _walk(h_process, h_thread or h_process, sz, ptr,
                           max_frames=max_frames, read_routine=None, log=log)
        finally:
            sz.close()
    finally:
        if h_thread:
            _k32.CloseHandle(h_thread)

    sbase, sdata = read_stack_region(h_process, regs.get("rsp", 0))
    return {
        "ok": bool(frames),
        "engine": "dbghelp StackWalk64 (live)",
        "thread_id": tid,
        "frames": frames,
        "registers": {k: f"0x{v:016X}" for k, v in regs.items()},
        "stack_base": f"0x{sbase:016X}" if sbase else None,
        "stack_bytes": len(sdata),
        "stack_b64": base64.b64encode(sdata).decode("ascii") if sdata else None,
        "modules": [{"name": m.get("name"), "base": int(m["base"]),
                     "size": int(m.get("size") or 0), "path": m.get("path")}
                    for m in modules],
    }


# --------------------------------------------------------------------------
# 离线重放：把"读进程内存"换成"读 sidecar 里的栈字节"
# --------------------------------------------------------------------------

READPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HANDLE, ctypes.c_ulonglong,
                              ctypes.c_void_p, ctypes.c_size_t,
                              ctypes.POINTER(ctypes.c_size_t))


class OfflineMemory:
    """离线内存模型：栈来自 sidecar，模块镜像来自磁盘。"""

    def __init__(self, stack_base, stack_bytes, module_ranges):
        self.base = stack_base
        self.data = stack_bytes
        self.ranges = module_ranges  # [(base, size, path)]

    def read(self, addr, size):
        if self.base and self.base <= addr < self.base + len(self.data):
            n = min(size, self.base + len(self.data) - addr)
            return self.data[addr - self.base:addr - self.base + n]
        for b, s, p in self.ranges:
            if b <= addr < b + s:
                try:
                    with open(p, "rb") as f:
                        f.seek(addr - b)
                        return f.read(size)
                except OSError:
                    return b""
        return b""


def unwind_offline(sidecar_path, sympath=None, max_frames=64, log=None):
    with open(sidecar_path, encoding="utf-8") as f:
        sc = json.load(f)
    regs = {k: int(v, 16) for k, v in (sc.get("registers") or {}).items()}
    sbase = int(sc["stack_base"], 16) if sc.get("stack_base") else 0
    sdata = base64.b64decode(sc["stack_b64"]) if sc.get("stack_b64") else b""
    modules = sc.get("modules") or []

    ranges = [(int(m["base"]), int(m.get("size") or 0), m.get("path"))
              for m in modules if m.get("path") and os.path.isfile(m["path"])]
    mem = OfflineMemory(sbase, sdata, ranges)

    @READPROC
    def _read(hp, addr, buf, size, nread):
        got = mem.read(addr, size)
        if not got:
            if nread:
                nread[0] = 0
            return False
        ctypes.memmove(buf, got, len(got))
        if nread:
            nread[0] = len(got)
        return True

    raw, ptr, off = build_context(regs)
    sz = S.Symbolizer(sympath=sympath or S.LOCAL_ONLY_SYMPATH)
    try:
        load_modules_into(sz, modules, log=log)
        # ⚠ 离线模式下 StackWalk64 的 hProcess/hThread 不能给 0：dbghelp 会拿它做
        #   内部查表并当场访问违例（实测 "access violation writing 0x1AC88"）。
        #   内存读取已由 _read 回调接管，这里给当前进程句柄即可。
        me = _k32.GetCurrentProcess()
        frames = _walk(me, me, sz, ptr, max_frames=max_frames,
                       read_routine=ctypes.cast(_read, ctypes.c_void_p), log=log)
    finally:
        sz.close()
    return {"ok": bool(frames), "engine": "dbghelp StackWalk64 (offline)",
            "frames": frames, "registers": sc.get("registers")}


def main():
    ap = argparse.ArgumentParser(description="用 dbghelp StackWalk64 做真实 unwind 回溯")
    ap.add_argument("--sidecar", required=True, help="unwind_live 落盘的低现场 JSON")
    ap.add_argument("--sympath", default=None)
    ap.add_argument("--max-frames", type=int, default=64)
    a = ap.parse_args()
    print(f"离线 unwind: {a.sidecar}\n")
    r = unwind_offline(a.sidecar, sympath=a.sympath, max_frames=a.max_frames,
                       log=print)
    print(f"\n引擎: {r['engine']}  成功={r['ok']}  帧数={len(r['frames'])}")
    return 0 if r["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
