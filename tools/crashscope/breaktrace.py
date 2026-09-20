"""在"异常被投递的那一瞬间"抓投递者 —— P1「4 路崩溃」的因果定位实验。

矛盾
----
崩溃现场三条事实互斥：
  * 异常码 0xC0000005，ExceptionInformation = {0(读), 0xFFFFFFFFFFFFFFFF}
  * RIP 处指令是 `mov [rsp+0x10], rbx`（写栈），字节与磁盘逐字节一致（未被 patch）
  * RSP 所在页 MEM_COMMIT / PAGE_READWRITE，写完全合法
"写指令 + 合法可写栈页" 不可能产生 "读 -1"。

假设
----
NtRaiseException 允许调用者自带 EXCEPTION_RECORD 与 CONTEXT ⇒ 现场可能是伪造的
（伪造者把 RIP 指向 Present 入口很自然）。判定办法只有一条：在异常真正被投递的
那一瞬间下断点，看是谁在调用这些入口。

四个观测点与判据
----------------
  ntdll!RtlRaiseException         ← 用户态 `RaiseException` 的必经之路
  ntdll!NtRaiseException          ← RtlRaiseException 内部的系统调用桩
  ntdll!KiUserExceptionDispatcher ← 内核把异常交给用户态的**唯一**入口（硬件异常也走这里）
  kernelbase!RaiseFailFastException ← fail-fast 路径（用于排除 0xC0000409 类）

  命中 RtlRaise/NtRaise（且早于致命异常）  ⇒ 用户态有人主动 raise，投递者就是它的调用者
  只命中 KiUserExceptionDispatcher         ⇒ 走内核派发路径（硬件访存或内核态投递）
  四个都没命中                             ⇒ 异常根本没经过用户态派发入口

为什么用 INT3 而不是硬件断点（DR0-DR3）
---------------------------------------
原本想用 DR 槽（不改代码字节，正好 4 个目标占满 4 个槽），**实测本机无效**：
调试器跨进程 SetThreadContext 写 DR0/DR7 会返回成功、回读也正确，但被调试线程
永远不产生 #DB —— 见 _bp_xproc_probe.py（对照：同进程就地写 DR 是有效的，
见 _bp_dr_probe.py，会因断点反复触发而卡死）。故退回 INT3。
INT3 的代价是必须临时改写 ntdll 代码字节，因此每次命中后立刻还原、
单步跑掉那条指令、再重新下断，运行结束时全部还原。

用法
----
    python breaktrace.py --run-id bt01 --seconds 30
    python breaktrace.py --selftest --run-id bp_selftest   # 正对照，无 GUI
    python breaktrace.py --list                            # 只列断点地址
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import capture as cap    # noqa: E402
import debugrun as dr    # noqa: E402
import minidump as md    # noqa: E402
import symbolize as S    # noqa: E402
import unwind as uw      # noqa: E402

REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

# --------------------------------------------------------------------------
# 常量
# --------------------------------------------------------------------------
CREATE_PROCESS_DEBUG_EVENT = 3
CREATE_THREAD_DEBUG_EVENT = 2
EXIT_THREAD_DEBUG_EVENT = 4
EXIT_PROCESS_DEBUG_EVENT = 5
LOAD_DLL_DEBUG_EVENT = 6
EXCEPTION_DEBUG_EVENT = 1

DBG_CONTINUE = 0x00010002
DBG_EXCEPTION_NOT_HANDLED = 0x80010001
ERROR_SEM_TIMEOUT = 121

STATUS_BREAKPOINT = 0x80000003
STATUS_SINGLE_STEP = 0x80000004

THREAD_GET_CONTEXT = 0x0008
THREAD_SET_CONTEXT = 0x0010
THREAD_QUERY_INFORMATION = 0x0040

CONTEXT_AMD64 = 0x00100000
CONTEXT_CONTROL = CONTEXT_AMD64 | 0x00000001
CONTEXT_INTEGER = CONTEXT_AMD64 | 0x00000002

# x64 CONTEXT 字段偏移（与 debugrun.REGS_ORDER 同源，已实测一致）
CTX_CONTEXT_FLAGS = 0x30
CTX_EFLAGS = 0x44
CTX_RCX = 0x80
CTX_RDX = 0x88
CTX_R8 = 0xB8
CTX_RSP = 0x98
CTX_RIP = 0xF8
EFLAGS_TF = 0x100

EXCEPTION_RECORD_SIZE = 0x98
CONTEXT_SIZE = 0x4D0

INT3 = b"\xCC"

# 这些异常码是运行时/输入法的常规软件 raise，与 P1 无关。
# 对它们只留一行记录，不做 unwind —— 调试器处理事件时会冻结**全部**线程，
# 每次命中多花 40ms 就足以打散 P1 那个竞态（实测：不省开销时 4 路 30 秒跑完不崩）。
BENIGN_CODES = {0x406D1388, 0xE0434352, 0x04242420, 0x12345678, 0x80000003}
ALWAYS_UNWIND_FIRST = 6          # 前若干次仍然 unwind，保证有基线样本

_k32 = dr._k32
_k32.SetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
_k32.SetThreadContext.restype = wt.BOOL
_k32.OpenThread.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
_k32.OpenThread.restype = wt.HANDLE
_k32.WriteProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                    ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
_k32.WriteProcessMemory.restype = wt.BOOL

EXPORT_SOURCES = [
    ("RtlRaiseException", "ntdll.dll"),
    ("NtRaiseException", "ntdll.dll"),
    ("KiUserExceptionDispatcher", "ntdll.dll"),
    ("RaiseFailFastException", "KernelBase.dll"),
]

# 断点目标的参数布局
#   "regs"  —— (record 寄存器, context 寄存器)，入口处参数已在寄存器里
#   "stack" —— 内核把 (EXCEPTION_RECORD*, CONTEXT*) 压在被投递线程栈的 [rsp]/[rsp+8]。
#              KiUserExceptionDispatcher 实测入口是 `fc 48 8b 05 ...`（cld + CFG 检查），
#              此时 RCX/RDX 还没装载，必须从栈上取。
ARG_LAYOUT = {
    "RtlRaiseException": ("regs", "rcx", None),
    "NtRaiseException": ("regs", "rcx", "rdx"),
    "KiUserExceptionDispatcher": ("stack", None, None),
    "RaiseFailFastException": ("regs", "rcx", "rdx"),
}

SYMPATH = ";".join([
    S.LOCAL_ONLY_SYMPATH,
    os.path.join(REPO, "FFF.Native", "x64", "Release"),
    os.path.join(REPO, "src", "3FCompare", "bin", "Debug", "net11.0-windows"),
])


# --------------------------------------------------------------------------
# PE 导出表（从磁盘读；ntdll/kernelbase 的代码段不会运行时自改）
# --------------------------------------------------------------------------

def pe_export_rvas(path: str) -> dict:
    try:
        with open(path, "rb") as f:
            d = f.read()
    except OSError:
        return {}
    if d[:2] != b"MZ":
        return {}
    e = struct.unpack_from("<I", d, 0x3C)[0]
    if d[e:e + 4] != b"PE\0\0":
        return {}
    coff = e + 4
    nsec, = struct.unpack_from("<H", d, coff + 2)
    size_opt, = struct.unpack_from("<H", d, coff + 16)
    opt = coff + 20
    magic, = struct.unpack_from("<H", d, opt)
    dd = opt + (0x70 if magic == 0x20B else 0x60)
    exp_rva, _exp_size = struct.unpack_from("<II", d, dd)
    if not exp_rva:
        return {}

    secs = []
    so = opt + size_opt
    for i in range(nsec):
        p = so + i * 40
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", d, p + 8)
        secs.append((va, max(vsize, rawsize), rawptr))

    def r2o(rva):
        for va, vs, rp in secs:
            if va <= rva < va + vs:
                off = rp + (rva - va)
                return off if 0 <= off < len(d) else None
        return None

    off = r2o(exp_rva)
    if off is None:
        return {}
    _nfun, nnam = struct.unpack_from("<II", d, off + 20)
    afun, anam, aord = struct.unpack_from("<III", d, off + 28)
    # ⚠ AddressOfNameOrdinals 与 AddressOfFunctions 是两张不同的表。把名字序号
    #   从 AddressOfFunctions 上读，会得到看似合理但完全错误的 RVA
    #   （实测 ntdll!NtRaiseException 被算成 0x6D6F4365）。
    ofun, onam, oord = r2o(afun), r2o(anam), r2o(aord)
    if ofun is None or onam is None or oord is None:
        return {}

    out = {}
    for i in range(nnam):
        nrva, = struct.unpack_from("<I", d, onam + i * 4)
        no = r2o(nrva)
        if no is None:
            continue
        end = d.find(b"\0", no)
        if end < 0:
            continue
        name = d[no:end].decode("ascii", "replace")
        ordi, = struct.unpack_from("<H", d, oord + i * 2)
        frva, = struct.unpack_from("<I", d, ofun + ordi * 4)
        out[name] = frva
    return out


_EXPORT_CACHE: dict = {}


def export_rva(dll_path: str, name: str):
    if dll_path not in _EXPORT_CACHE:
        _EXPORT_CACHE[dll_path] = pe_export_rvas(dll_path)
    return _EXPORT_CACHE[dll_path].get(name)


# --------------------------------------------------------------------------
# 线程上下文 / 进程内存
# --------------------------------------------------------------------------

def _open_thread(tid):
    return _k32.OpenThread(
        THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION, False, tid)


def get_ctx(h_thread, flags):
    raw, ptr, aligned = dr._aligned_ctx_buffer()
    base = ctypes.addressof(raw)
    struct.pack_into("<I", raw, (aligned - base) + CTX_CONTEXT_FLAGS, flags)
    if not _k32.GetThreadContext(h_thread, ptr):
        return None, 0, 0
    return raw, aligned - base, aligned


def set_ctx(h_thread, raw, off, aligned):
    return bool(_k32.SetThreadContext(h_thread, ctypes.c_void_p(aligned)))


def read_proc(h_process, addr, n):
    if not addr:
        return b""
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t(0)
    _k32.ReadProcessMemory(h_process, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw[:got.value]


def write_proc(h_process, addr, data: bytes):
    buf = ctypes.create_string_buffer(data, len(data))
    got = ctypes.c_size_t(0)
    ok = _k32.WriteProcessMemory(h_process, ctypes.c_void_p(addr), buf, len(data),
                                 ctypes.byref(got))
    return bool(ok) and got.value == len(data)


def _looks_like_context(head: bytes) -> bool:
    if len(head) < 0x100:
        return False
    cflags, = struct.unpack_from("<I", head, CTX_CONTEXT_FLAGS)
    # 实测内核投递的 CONTEXT 用的是 CONTEXT_ALL = 0x0010005F（含 SEGMENTS/DEBUG/EXTENDED），
    # 掩码写窄到 0x0010001F 会把真正的那个漏掉。
    if not (cflags & CONTEXT_AMD64) or not (cflags & 0xF) or (cflags & ~0x0010007F):
        return False
    rip, = struct.unpack_from("<Q", head, CTX_RIP)
    rsp, = struct.unpack_from("<Q", head, CTX_RSP)
    return (0x10000 <= rip <= 0x00007FFFFFFFFFFF
            and 0x10000 <= rsp <= 0x00007FFFFFFFFFFF)


def _looks_like_record(head: bytes) -> bool:
    if len(head) < EXCEPTION_RECORD_SIZE:
        return False
    code, flags = struct.unpack_from("<II", head, 0)
    addr, = struct.unpack_from("<Q", head, 0x10)
    npar, = struct.unpack_from("<I", head, 0x18)
    return (code >= 0x80000000 and flags <= 3 and npar <= 15
            and 0x10000 <= addr <= 0x00007FFFFFFFFFFF)


def find_kuser_args(h_process, rsp):
    """在 KiUserExceptionDispatcher 入口找出 (EXCEPTION_RECORD*, CONTEXT*)。

    本机实测布局：**CONTEXT 就压在 RSP 上**（RSP+0x30 = ContextFlags = 0x0010005F，
    RSP+0xF8 = Rip = 故障指令地址），EXCEPTION_RECORD 紧随其后。
    按 [rsp]/[rsp+8] 当指针读会拿到垃圾（曾把记录读成 code=0x0000F700）。
    所以：先认 RSP+0 这个规范布局，再退化为"结构落在栈窗口内 / 栈槽存指针"两种扫描。
    """
    blob = read_proc(h_process, rsp, 0x800)
    if len(blob) < 16:
        return 0, 0

    # (a) 规范布局：CONTEXT 在 RSP 上
    if _looks_like_context(blob[:0x100]):
        ctx_ptr = rsp
        rec_ptr = 0
        for i in range(0, max(0, len(blob) - EXCEPTION_RECORD_SIZE) + 1, 8):
            if _looks_like_record(blob[i:i + 0x100]):
                rec_ptr = rsp + i
                break
        return rec_ptr, ctx_ptr

    rec_ptr = ctx_ptr = 0

    # (b) 结构本身落在栈窗口内
    for i in range(0, max(0, len(blob) - 0x100) + 1, 8):
        head = blob[i:i + 0x100]
        if not ctx_ptr and _looks_like_context(head):
            ctx_ptr = rsp + i
        if not rec_ptr and _looks_like_record(head):
            rec_ptr = rsp + i
        if rec_ptr and ctx_ptr:
            return rec_ptr, ctx_ptr

    # (c) 栈槽里是指向结构的指针
    for i in range(0, len(blob) - 8 + 1, 8):
        p, = struct.unpack_from("<Q", blob, i)
        if not (0x10000 <= p <= 0x00007FFFFFFFFFFF):
            continue
        head = read_proc(h_process, p, 0x100)
        if len(head) < 0x100:
            continue
        if not ctx_ptr and _looks_like_context(head):
            ctx_ptr = p
        if not rec_ptr and _looks_like_record(head):
            rec_ptr = p
        if rec_ptr and ctx_ptr:
            break
    return rec_ptr, ctx_ptr


def parse_exception_record(raw: bytes):
    if len(raw) < 0x20:
        return None
    code, flags = struct.unpack_from("<II", raw, 0)
    nested, = struct.unpack_from("<Q", raw, 8)
    addr, = struct.unpack_from("<Q", raw, 0x10)
    npar, = struct.unpack_from("<I", raw, 0x18)
    n = min(npar, 15)
    params = list(struct.unpack_from("<%dQ" % n, raw, 0x20)) if n else []
    return {
        "code": f"0x{code:08X}",
        "code_name": md.EXC_NAMES.get(code, "UNKNOWN"),
        "flags": flags,
        "nested_record": f"0x{nested:016X}",
        "address": f"0x{addr:016X}",
        "number_parameters": npar,
        "params": [f"0x{p:016X}" for p in params],
    }


def parse_context(raw: bytes, off: int = 0):
    if len(raw) < off + 0x100:
        return None
    g = lambda o: struct.unpack_from("<Q", raw, off + o)[0]  # noqa: E731
    out = {
        "context_flags": f"0x{struct.unpack_from('<I', raw, off + CTX_CONTEXT_FLAGS)[0]:08X}",
        "eflags": f"0x{struct.unpack_from('<I', raw, off + CTX_EFLAGS)[0]:08X}",
    }
    for name, o in (("rax", 0x78), ("rcx", CTX_RCX), ("rdx", CTX_RDX), ("rbx", 0x90),
                    ("rsp", CTX_RSP), ("rbp", 0xA0), ("rsi", 0xA8), ("rdi", 0xB0),
                    ("r8", CTX_R8), ("r9", 0xC0), ("r10", 0xC8), ("r11", 0xD0),
                    ("r12", 0xD8), ("r13", 0xE0), ("r14", 0xE8), ("r15", 0xF0),
                    ("rip", CTX_RIP)):
        out[name] = f"0x{g(o):016X}"
    return out


# --------------------------------------------------------------------------
# 持久符号器（一次 SymInitialize，按命中时模块表懒加载）
# --------------------------------------------------------------------------

class Sym:
    def __init__(self, h_process):
        self.h = h_process
        self.sz = S.Symbolizer(sympath=SYMPATH, hproc=h_process)
        self.loaded = set()

    def sync(self, modules, log=None):
        n = 0
        for m in modules:
            path = m.get("path")
            if not path or not os.path.isfile(path):
                continue
            key = (m.get("name"), int(m["base"]))
            if key in self.loaded:
                continue
            try:
                self.sz.load(path, base=int(m["base"]), size=int(m.get("size") or 0),
                             name=m.get("name"))
                n += 1
            except S.SymError:
                pass
            self.loaded.add(key)
        if n and log:
            log(f"  [sym] 新登记 {n} 个模块（累计 {len(self.loaded)}）")

    def describe(self, addr):
        if not addr:
            return None
        m = None
        for mm in self.sz.modules.values():
            if mm["base"] <= addr < mm["base"] + max(mm["size"], 1):
                m = mm
                break
        r = self.sz.resolve(addr)
        return {
            "address": f"0x{addr:016X}",
            "module": m["name"] if m else None,
            "rva": f"0x{addr - m['base']:X}" if m else None,
            "symbol": r["symbol"],
            "function": r["function"],
            "displacement": r["displacement"],
            "source": r["source"],
            "line": r["line"],
        }

    def close(self):
        self.sz.close()


def walk_at(h_process, h_thread, sz: Sym, regs, max_frames=48):
    """用给定寄存器做一次真实 unwind（StackWalk64 经 h_process 读被调试进程栈）。"""
    _raw, ptr, _off = uw.build_context(regs)
    return uw._walk(h_process, h_thread, sz.sz, ptr, max_frames=max_frames, log=None)


def frames_brief(frames):
    return [{"#": f["frame"], "module": f.get("module"), "rva": f.get("rva_hex"),
             "symbol": f.get("symbol"), "source": f.get("source"), "line": f.get("line")}
            for f in frames]


# --------------------------------------------------------------------------
# 主循环
# --------------------------------------------------------------------------

class Trace:
    def __init__(self, h_process, pid, log=print):
        self.h = h_process
        self.pid = pid
        self.log = log
        self.targets = {}
        self.resolved = False
        self.tids = set()
        self.hits = []
        self.all_exceptions = []
        self.patches = {}          # addr -> {"name","orig","armed"}
        self.pending_restep = {}   # tid -> addr（单步跑掉原指令后要重下断）
        self.patch_failures = []
        self.sym = None
        self.t0 = time.time()
        self.fatal = []
        self._mod_snap = None
        self._mod_snap_t = 0.0

    def modules(self, max_age=2.0):
        """模块表带缓存：EnumProcessModulesEx + 逐个 GetModuleInformation 是百毫秒级的，
        每次命中都做一遍会把被调试进程冻住太久。"""
        now = time.time()
        if self._mod_snap is None or now - self._mod_snap_t > max_age:
            snap = cap.snapshot_modules(self.pid)
            if snap:
                self._mod_snap = snap
                self._mod_snap_t = now
        return self._mod_snap or []

    def sym_of(self):
        # ⚠ dbghelp 的符号表是按进程句柄挂的：本对象与 unwind_live 内部各建一个
        #   Symbolizer 会互相踩（实测后者 SymLoadModuleEx 全部 err=0 失败）。
        #   所以这里懒建，并在调用 capture_scene 之前先 drop_sym() 让出。
        if self.sym is None:
            self.sym = Sym(self.h)
        return self.sym

    def drop_sym(self):
        if self.sym is not None:
            try:
                self.sym.close()
            except Exception:
                pass
            self.sym = None

    # -- 解析 -------------------------------------------------------------
    def resolve(self):
        snap = cap.snapshot_modules(self.pid) or []
        byname = {m["name"].lower(): m for m in snap}
        ok = True
        for name, dll in EXPORT_SOURCES:
            if name in self.targets:
                continue
            m = byname.get(dll.lower())
            path = m["path"] if m and m.get("path") else os.path.join(
                os.environ.get("SystemRoot", r"C:\Windows"), "System32", dll)
            rva = export_rva(path, name)
            if m is None or rva is None:
                ok = False
                continue
            self.targets[name] = {"module": dll, "base": m["base"],
                                  "addr": m["base"] + rva, "rva": f"0x{rva:X}"}
        self.resolved = ok and len(self.targets) == len(EXPORT_SOURCES)
        return self.resolved

    # -- INT3 下断 ---------------------------------------------------------
    def patch_one(self, addr, name):
        p = self.patches.get(addr)
        if p and p["armed"]:
            return True
        orig = p["orig"] if p else read_proc(self.h, addr, 1)
        if len(orig) != 1:
            self.patch_failures.append({"addr": f"0x{addr:X}", "target": name,
                                        "reason": "读不到原字节"})
            return False
        if not write_proc(self.h, addr, INT3):
            self.patch_failures.append({"addr": f"0x{addr:X}", "target": name,
                                        "reason": "WriteProcessMemory 被拒"})
            return False
        back = read_proc(self.h, addr, 1)
        if back != INT3:
            self.patch_failures.append({"addr": f"0x{addr:X}", "target": name,
                                        "reason": f"写后回读不符({back.hex()})"})
            return False
        self.patches[addr] = {"name": name, "orig": orig, "armed": True}
        return True

    def patch_all(self):
        n = 0
        for name, t in self.targets.items():
            if self.patch_one(t["addr"], name):
                n += 1
        if self.patch_failures:
            self.log(f"  [bp] ⚠ {len(self.patch_failures)} 个断点下失败："
                     f"{self.patch_failures[0]}")
        self.log(f"  [bp] 已下 {n}/{len(self.targets)} 个 INT3 断点")

    def rearm_pending(self, tid):
        addr = self.pending_restep.pop(tid, None)
        if addr is None:
            return
        p = self.patches.get(addr)
        if p:
            if write_proc(self.h, addr, INT3):
                p["armed"] = True
            else:
                self.log(f"  [bp] ⚠ 重下断失败 @0x{addr:X}")

    def restore_all(self):
        for addr, p in self.patches.items():
            if p["armed"]:
                write_proc(self.h, addr, p["orig"])
                p["armed"] = False

    # -- 命中 -------------------------------------------------------------
    def on_int3(self, tid, addr, h_thread):
        p = self.patches.get(addr)
        if not p or not p["armed"]:
            return None
        name = p["name"]
        kind, arg_rec, arg_ctx = ARG_LAYOUT.get(name, ("regs", "rcx", "rdx"))

        # 1) 先抓现场：此刻 RIP = addr+1（int3 已执行），RSP 是调用者的栈
        regs = dr.get_thread_regs(self.h, tid) or {}
        if not regs:
            return None
        regs["rip"] = addr          # 把 RIP 拉回断点处，便于符号归属

        # 2) 立刻还原原字节，并把真实 RIP 设回 addr + 打开 TF，
        #    让那条被替换掉的指令单步执行掉，之后在单步事件里重新下断
        raw, off, aligned = get_ctx(h_thread, CONTEXT_CONTROL | CONTEXT_INTEGER)
        restored = False
        if raw is not None:
            struct.pack_into("<Q", raw, off + CTX_RIP, addr)
            ef = struct.unpack_from("<I", raw, off + CTX_EFLAGS)[0]
            struct.pack_into("<I", raw, off + CTX_EFLAGS, ef | EFLAGS_TF)
            if set_ctx(h_thread, raw, off, aligned):
                restored = write_proc(self.h, addr, p["orig"])
                if restored:
                    p["armed"] = False
                    self.pending_restep[tid] = addr

        # 3) 读投递者传进来的 record / context
        rec_ptr = ctx_ptr = 0
        if kind == "stack":
            rec_ptr, ctx_ptr = find_kuser_args(self.h, regs.get("rsp", 0))
        else:
            rec_ptr = regs.get(arg_rec) if arg_rec else 0
            ctx_ptr = regs.get(arg_ctx) if arg_ctx else 0

        raw_rec = read_proc(self.h, rec_ptr, EXCEPTION_RECORD_SIZE) if rec_ptr else b""
        rec = parse_exception_record(raw_rec) if raw_rec else None
        pctx = None
        if ctx_ptr:
            raw_ctx = read_proc(self.h, ctx_ptr, CONTEXT_SIZE)
            pctx = parse_context(raw_ctx) if len(raw_ctx) >= 0x100 else None

        # 4) 真实调用者：[RSP] 就是返回地址。
        #    KiUserExceptionDispatcher 是内核→用户态交接点，[RSP] 是内核压的两个指针。
        ret = 0
        if kind != "stack":
            slot = read_proc(self.h, regs.get("rsp", 0), 8)
            ret = struct.unpack("<Q", slot)[0] if len(slot) == 8 else 0

        sz = self.sym_of()
        sz.sync(self.modules())
        frames = []
        # unwind 昂贵（要读 .pdata + 查符号），而调试器一进事件就冻结全部线程。
        # 只对"可疑"的异常做 unwind：0xC0000005、或不在常规集合里的码；另外前几次都做。
        interesting = (rec is None or rec.get("code") == "0xC0000005"
                       or int(rec.get("code", "0x0"), 16) not in BENIGN_CODES)
        if len(self.hits) < ALWAYS_UNWIND_FIRST or interesting:
            try:
                frames = walk_at(self.h, h_thread, sz, regs, max_frames=48)
            except Exception as e:
                self.log(f"  [hit] unwind 失败：{type(e).__name__}: {e}")

        hit = {
            "seq": len(self.hits),
            "elapsed_s": round(time.time() - self.t0, 3),
            "target": name,
            "target_module": self.targets[name]["module"],
            "target_addr": f"0x{addr:016X}",
            "thread_id": tid,
            "delivery_kind": ("内核→用户态派发入口（异常由内核投递）"
                              if kind == "stack" else "用户态主动 raise 入口"),
            "byte_restored_ok": restored,
            "real_regs": {k: f"0x{v:016X}" for k, v in regs.items()},
            # 符号解析也走 dbghelp，同样只对可疑命中做
            "real_ret_slot": (self.sym.describe(ret)
                              if ret and (interesting
                                          or len(self.hits) < ALWAYS_UNWIND_FIRST)
                              else None),
            "record_ptr": f"0x{rec_ptr:016X}" if rec_ptr else None,
            "context_ptr": f"0x{ctx_ptr:016X}" if ctx_ptr else None,
            "passed_exception_record": rec,
            "passed_context": pctx,
            # 内核→用户态交接点的栈顶原样留档，便于事后复核参数定位是否正确
            "stack_top_hex": (read_proc(self.h, regs.get("rsp", 0), 0x100).hex(" ")
                              if kind == "stack" else None),
            "unwind": frames_brief(frames),
        }
        self.hits.append(hit)

        self.log(f"  ★ 命中 {name} tid={tid} @0x{addr:X} t={hit['elapsed_s']}s "
                 f"（还原字节={restored}）")
        if not interesting:
            if rec:
                self.log(f"      [常规 raise] code={rec['code']} → 不展开栈")
            return hit

        if hit["real_ret_slot"]:
            r = hit["real_ret_slot"]
            src = f"  [{r['source']}:{r['line']}]" if r.get("source") else ""
            self.log(f"      真实调用者([RSP]): {r['module']}+{r['rva']} {r['symbol']}{src}")
        if rec:
            self.log(f"      传入 EXCEPTION_RECORD: code={rec['code']} "
                     f"flags=0x{rec['flags']:X} addr={rec['address']} params={rec['params']}")
        if pctx:
            self.log(f"      传入 CONTEXT: rip={pctx['rip']} rsp={pctx['rsp']} "
                     f"flags={pctx['context_flags']}")
        for f in frames[:14]:
            loc = f"  [{f['source']}:{f['line']}]" if f.get("source") else ""
            self.log(f"      #{f['frame']:<2} {f.get('module')}+{f.get('rva_hex')}  "
                     f"{f.get('symbol')}{loc}")
        return hit


def run(exe, media, routes, seconds, run_id, outdir, hard_timeout=150.0,
        log_path=None, cmdline=None, cwd=None):
    cwd = cwd or os.path.dirname(exe)
    if cmdline is None:
        cmdline = f'"{exe}" --multitest "{media}" {routes} {seconds}'
    si = dr.STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    pi = dr.PROCESS_INFORMATION()

    fh = open(log_path, "w", encoding="utf-8", errors="replace") if log_path else None

    def log(*a):
        msg = " ".join(str(x) for x in a)
        print(msg, flush=True)
        if fh:
            fh.write(msg + "\n")
            fh.flush()

    t0 = time.time()
    ok = _k32.CreateProcessW(exe, ctypes.create_unicode_buffer(cmdline), None, None,
                             False, dr.DEBUG_ONLY_THIS_PROCESS, None, cwd,
                             ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        if fh:
            fh.close()
        raise OSError(f"CreateProcess 失败 err={ctypes.get_last_error()}")

    pid, h_process = pi.dwProcessId, pi.hProcess
    _k32.CloseHandle(pi.hThread)
    tr = Trace(h_process, pid, log=log)
    tr.tids.add(pi.dwThreadId)
    tr.t0 = t0

    evbuf = ctypes.create_string_buffer(512)
    exit_code = None
    timed_out = False
    patched_at = None

    log(f"[{run_id}] pid={pid}  cmd={cmdline}")

    try:
        while True:
            if time.time() - t0 > hard_timeout:
                timed_out = True
                break
            if not _k32.WaitForDebugEvent(evbuf, 500):
                if ctypes.get_last_error() == ERROR_SEM_TIMEOUT:
                    continue
                break
            code_, dpid, dtid = struct.unpack_from("<III", evbuf, 0)

            if code_ == CREATE_PROCESS_DEBUG_EVENT:
                hf = ctypes.c_void_p(struct.unpack_from("<Q", evbuf, 16)[0])
                if hf:
                    _k32.CloseHandle(hf)
                cont = DBG_CONTINUE
            elif code_ == LOAD_DLL_DEBUG_EVENT:
                hf = ctypes.c_void_p(struct.unpack_from("<Q", evbuf, 16)[0])
                if hf:
                    _k32.CloseHandle(hf)
                if not tr.resolved and tr.resolve():
                    tr.patch_all()
                    patched_at = round(time.time() - t0, 3)
                cont = DBG_CONTINUE
            elif code_ == CREATE_THREAD_DEBUG_EVENT:
                hf = ctypes.c_void_p(struct.unpack_from("<Q", evbuf, 16)[0])
                if hf:
                    _k32.CloseHandle(hf)
                tr.tids.add(dtid)
                cont = DBG_CONTINUE
            elif code_ == EXIT_THREAD_DEBUG_EVENT:
                tr.tids.discard(dtid)
                tr.pending_restep.pop(dtid, None)
                cont = DBG_CONTINUE
            elif code_ == EXCEPTION_DEBUG_EVENT:
                ecode, _eflags = struct.unpack_from("<II", evbuf, 16)
                eaddr = struct.unpack_from("<Q", evbuf, 32)[0]
                nparam = struct.unpack_from("<I", evbuf, 40)[0]
                params = list(struct.unpack_from("<15Q", evbuf, 48))[:min(nparam, 15)]
                first_chance = struct.unpack_from("<I", evbuf, 168)[0]
                tr.all_exceptions.append({
                    "code": f"0x{ecode:08X}",
                    "name": md.EXC_NAMES.get(ecode, "UNKNOWN"),
                    "first_chance": bool(first_chance),
                    "address": f"0x{eaddr:016X}",
                    "params": [f"0x{p:X}" for p in params],
                    "thread_id": dtid,
                    "elapsed_s": round(time.time() - t0, 2),
                })
                cont = DBG_EXCEPTION_NOT_HANDLED

                if ecode == STATUS_BREAKPOINT and eaddr in tr.patches:
                    h_thread = _k32.OpenThread(
                        THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION,
                        False, dtid)
                    if h_thread:
                        try:
                            tr.on_int3(dtid, eaddr, h_thread)
                        except Exception as e:
                            log(f"  [hit] 采集失败：{type(e).__name__}: {e}")
                        finally:
                            _k32.CloseHandle(h_thread)
                    cont = DBG_CONTINUE
                elif ecode == STATUS_SINGLE_STEP and dtid in tr.pending_restep:
                    h_thread = _k32.OpenThread(
                        THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION,
                        False, dtid)
                    if h_thread:
                        try:
                            raw, off, aligned = get_ctx(
                                h_thread, CONTEXT_CONTROL | CONTEXT_INTEGER)
                            if raw is not None:
                                ef = struct.unpack_from("<I", raw, off + CTX_EFLAGS)[0]
                                struct.pack_into("<I", raw, off + CTX_EFLAGS,
                                                 ef & ~EFLAGS_TF)
                                set_ctx(h_thread, raw, off, aligned)
                            tr.rearm_pending(dtid)
                        finally:
                            _k32.CloseHandle(h_thread)
                    cont = DBG_CONTINUE
                elif ecode == STATUS_BREAKPOINT:
                    cont = DBG_CONTINUE          # 初始断点/进程自身 DebugBreak
                elif ecode in dr.FATAL_CODES:
                    try:
                        # 让出 dbghelp：capture_scene 内部会自建 Symbolizer 做 unwind
                        tr.drop_sym()
                        scene = dr.capture_scene(h_process, pid, dtid, {
                            "code": ecode, "address": eaddr, "params": params})
                        scene["first_chance"] = bool(first_chance)
                        scene["elapsed_s"] = round(time.time() - t0, 2)
                        tr.fatal.append(scene)
                    except Exception as e:
                        log(f"  [fatal] 现场采集失败：{type(e).__name__}: {e}")
                    cont = DBG_EXCEPTION_NOT_HANDLED
            elif code_ == EXIT_PROCESS_DEBUG_EVENT:
                exit_code = struct.unpack_from("<I", evbuf, 16)[0]
                _k32.ContinueDebugEvent(dpid, dtid, DBG_CONTINUE)
                break
            else:
                cont = DBG_CONTINUE

            _k32.ContinueDebugEvent(dpid, dtid, cont)
    finally:
        try:
            tr.restore_all()          # 必须还原 ntdll 字节，不留 patch
        except Exception:
            pass
        try:
            alive = _k32.WaitForSingleObject(h_process, 0) != 0
        except Exception:
            alive = True
        if alive:
            _k32.TerminateProcess(h_process, 1)
        try:
            _k32.DebugActiveProcessStop(pid)
        except Exception:
            pass
        try:
            tr.drop_sym()
        except Exception:
            pass
        _k32.CloseHandle(h_process)

    t1 = time.time()
    res = {
        "run_id": run_id, "cmd": cmdline, "pid": pid,
        "start_local": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(t0)),
        "duration_s": round(t1 - t0, 2),
        "exit_code": exit_code,
        "exit_code_hex": (f"0x{exit_code:08X}" if exit_code is not None else None),
        "timed_out": timed_out,
        "breakpoints": tr.targets,
        "breakpoints_patched_at_s": patched_at,
        "patch_failures": tr.patch_failures,
        "threads_seen": len(tr.tids),
        "hits": tr.hits,
        "hit_count": len(tr.hits),
        "all_exceptions": tr.all_exceptions,
        "fatal_scenes": tr.fatal,
        "sympath": SYMPATH,
        "log": log_path,
    }
    if fh:
        fh.close()
    return res


def main():
    ap = argparse.ArgumentParser(description="在异常投递瞬间抓投递者（INT3 断点）")
    ap.add_argument("--exe", default=cap.DEFAULT_EXE)
    ap.add_argument("--media", default=cap.DEFAULT_MEDIA)
    ap.add_argument("--routes", type=int, default=4)
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--run-id", default="bt01")
    ap.add_argument("--outdir", default=os.path.join(HERE, "scenes"))
    ap.add_argument("--hard-timeout", type=float, default=150.0)
    ap.add_argument("--list", action="store_true", help="只列断点地址，不启动进程")
    ap.add_argument("--selftest", action="store_true",
                    help="正对照：调试 _bp_selftest_child.py（无 GUI，不占主实验次数）")
    a = ap.parse_args()

    if a.list:
        sysdir = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
        for name, dll in EXPORT_SOURCES:
            rva = export_rva(os.path.join(sysdir, dll), name)
            print(f"  {dll}!{name}  RVA={('0x%X' % rva) if rva else '解析失败'}")
        return 0

    os.makedirs(a.outdir, exist_ok=True)
    logdir = os.path.join(a.outdir, "logs")
    os.makedirs(logdir, exist_ok=True)
    log_path = os.path.join(logdir, f"{a.run_id}.log")

    if a.selftest:
        child = os.path.join(HERE, "_bp_selftest_child.py")
        res = run(sys.executable, a.media, a.routes, a.seconds, a.run_id, a.outdir,
                  hard_timeout=30.0, log_path=log_path,
                  cmdline=f'"{sys.executable}" "{child}"', cwd=HERE)
    else:
        res = run(a.exe, a.media, a.routes, a.seconds, a.run_id, a.outdir,
                  hard_timeout=a.hard_timeout, log_path=log_path)

    with open(os.path.join(a.outdir, f"{a.run_id}.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=2)

    print(f"\n[{a.run_id}] 退出码={res['exit_code_hex']} 耗时={res['duration_s']}s "
          f"线程数={res['threads_seen']} 断点命中={res['hit_count']} "
          f"致命现场={len(res['fatal_scenes'])}")
    if res["patch_failures"]:
        print(f"  ⚠ 下断失败 {len(res['patch_failures'])} 个：{res['patch_failures'][0]}")
    print(f"  -> {os.path.join(a.outdir, a.run_id + '.json')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
