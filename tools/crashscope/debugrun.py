"""实时崩溃取证：把 3FCompare 作为被调试进程启动，在异常发生的那一刻就地抓现场。

为什么不用 WER LocalDumps：
  实测本机 `HKCU\\...\\Windows Error Reporting\\LastRateLimitedDumpGenerationTime` 非空，
  WER 的速率限制器会压制 dump 生成 —— 连续 3 次崩溃一个 dump 都没落地，
  而 WER 归档又要延迟数秒才写入 ReportArchive。调试器路径把这两个不确定性一次去掉。

抓什么（与 minidump 路径同口径）：
  * 异常码 / 异常地址 / 异常参数（ExceptionRecord 原始值）
  * 故障线程 CONTEXT 的 RIP / RSP
  * 故障线程栈内存（ReadProcessMemory 实时读取，进程还活着，一定拿得到）
  * 那一刻的模块表（EnumProcessModulesEx，进程被冻结，快照精确）

用法：
    python debugrun.py --run-id d01 --seconds 30
    python debugrun.py --times 6 --tag p2
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

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import attribute as at  # noqa: E402
import capture as cap  # noqa: E402
import minidump as md  # noqa: E402
import unwind as uw  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

DEBUG_ONLY_THIS_PROCESS = 0x00000002
CREATE_NEW_CONSOLE = 0x00000010

EXCEPTION_DEBUG_EVENT = 1
CREATE_THREAD_DEBUG_EVENT = 2
CREATE_PROCESS_DEBUG_EVENT = 3
EXIT_THREAD_DEBUG_EVENT = 4
EXIT_PROCESS_DEBUG_EVENT = 5
LOAD_DLL_DEBUG_EVENT = 6
UNLOAD_DLL_DEBUG_EVENT = 7
OUTPUT_DEBUG_STRING_EVENT = 8
RIP_EVENT = 9

DBG_CONTINUE = 0x00010002
DBG_EXCEPTION_NOT_HANDLED = 0x80010001
ERROR_SEM_TIMEOUT = 121

THREAD_GET_CONTEXT = 0x0008
THREAD_QUERY_INFORMATION = 0x0040
CONTEXT_AMD64 = 0x00100000
CONTEXT_CONTROL = CONTEXT_AMD64 | 0x00000001
CONTEXT_INTEGER = CONTEXT_AMD64 | 0x00000002

STACK_READ_MAX = 0x20000   # 从 RSP 起最多读 128KB

# 需要就地下现场的致命异常码
FATAL_CODES = {0xC0000005, 0xC000001D, 0xC0000006, 0xC0000094,
               0xC0000096, 0xC00000FD, 0xC0000409, 0xC0000374}

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("lpReserved", wt.LPWSTR), ("lpDesktop", wt.LPWSTR),
                ("lpTitle", wt.LPWSTR), ("dwX", wt.DWORD), ("dwY", wt.DWORD),
                ("dwXSize", wt.DWORD), ("dwYSize", wt.DWORD),
                ("dwXCountChars", wt.DWORD), ("dwYCountChars", wt.DWORD),
                ("dwFillAttribute", wt.DWORD), ("dwFlags", wt.DWORD),
                ("wShowWindow", wt.WORD), ("cbReserved2", wt.WORD),
                ("lpReserved2", ctypes.c_void_p), ("hStdInput", wt.HANDLE),
                ("hStdOutput", wt.HANDLE), ("hStdError", wt.HANDLE)]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE),
                ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


class MEMORY_BASIC_INFORMATION(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("__alignment1", wt.DWORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__alignment2", wt.DWORD)]


def _bind():
    _k32.CreateProcessW.argtypes = [
        wt.LPCWSTR, wt.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, wt.BOOL,
        wt.DWORD, ctypes.c_void_p, wt.LPCWSTR,
        ctypes.POINTER(STARTUPINFOW), ctypes.POINTER(PROCESS_INFORMATION)]
    _k32.CreateProcessW.restype = wt.BOOL
    _k32.WaitForDebugEvent.argtypes = [ctypes.c_void_p, wt.DWORD]
    _k32.WaitForDebugEvent.restype = wt.BOOL
    _k32.ContinueDebugEvent.argtypes = [wt.DWORD, wt.DWORD, wt.DWORD]
    _k32.ContinueDebugEvent.restype = wt.BOOL
    _k32.DebugActiveProcessStop.argtypes = [wt.DWORD]
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
    _k32.TerminateProcess.argtypes = [wt.HANDLE, wt.UINT]
    _k32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
    _k32.WaitForSingleObject.restype = wt.DWORD
    _k32.CloseHandle.argtypes = [wt.HANDLE]


_bind()


def _aligned_ctx_buffer():
    raw = ctypes.create_string_buffer(0x4D0 + 32)
    addr = ctypes.addressof(raw)
    aligned = (addr + 15) & ~15
    return raw, ctypes.c_void_p(aligned), aligned


REGS_ORDER = [("rax", 0x78), ("rcx", 0x80), ("rdx", 0x88), ("rbx", 0x90),
              ("rsp", 0x98), ("rbp", 0xA0), ("rsi", 0xA8), ("rdi", 0xB0),
              ("r8", 0xB8), ("r9", 0xC0), ("r10", 0xC8), ("r11", 0xD0),
              ("r12", 0xD8), ("r13", 0xE0), ("r14", 0xE8), ("r15", 0xF0),
              ("rip", 0xF8)]


def get_thread_regs(h_process, tid):
    """读取故障线程全部通用寄存器；失败返回 None。"""
    h = _k32.OpenThread(THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, False, tid)
    if not h:
        return None
    try:
        raw, ptr, aligned = _aligned_ctx_buffer()
        base = ctypes.addressof(raw)
        # ⚠ ContextFlags 在 CONTEXT 结构体的 +0x30，不是 +0；写错位置 GetThreadContext 直接失败
        struct.pack_into("<I", raw, (aligned - base) + md.CTX_CONTEXT_FLAGS,
                         CONTEXT_CONTROL | CONTEXT_INTEGER)
        if not _k32.GetThreadContext(h, ptr):
            return None
        off = aligned - base
        return {name: struct.unpack_from("<Q", raw, off + o)[0]
                for name, o in REGS_ORDER}
    finally:
        _k32.CloseHandle(h)


def read_stack(h_process, rsp, max_bytes=STACK_READ_MAX):
    """从 RSP 起读到该内存区末尾，返回 (base, data)。"""
    if not rsp:
        return 0, b""
    mbi = MEMORY_BASIC_INFORMATION()
    n = _k32.VirtualQueryEx(h_process, ctypes.c_void_p(rsp), ctypes.byref(mbi),
                            ctypes.sizeof(mbi))
    if not n:
        return 0, b""
    region_end = (mbi.BaseAddress or 0) + mbi.RegionSize
    size = min(region_end - rsp, max_bytes)
    if size <= 0:
        return 0, b""
    buf = ctypes.create_string_buffer(size)
    got = ctypes.c_size_t(0)
    if not _k32.ReadProcessMemory(h_process, ctypes.c_void_p(rsp), buf, size,
                                  ctypes.byref(got)):
        if got.value == 0:
            return 0, b""
    return rsp, buf.raw[:got.value]


def probe_address(h_process, addr):
    """探测某个地址的内存状态 —— 判定野地址到底是"已卸载"还是"从未映射"。"""
    mbi = MEMORY_BASIC_INFORMATION()
    n = _k32.VirtualQueryEx(h_process, ctypes.c_void_p(addr), ctypes.byref(mbi),
                            ctypes.sizeof(mbi))
    if not n:
        return None
    state = {0x1000: "MEM_COMMIT", 0x10000: "MEM_FREE", 0x2000: "MEM_RESERVE"}.get(
        mbi.State, f"0x{mbi.State:X}")
    typ = {0x1000000: "MEM_IMAGE", 0x40000: "MEM_MAPPED",
           0x20000: "MEM_PRIVATE"}.get(mbi.Type, f"0x{mbi.Type:X}")
    return {
        "address": f"0x{addr:016X}",
        "region_base": f"0x{(mbi.BaseAddress or 0):016X}",
        "region_size": f"0x{mbi.RegionSize:X}",
        "allocation_base": f"0x{(mbi.AllocationBase or 0):016X}",
        "state": state, "type": typ,
        "protect": f"0x{mbi.Protect:X}",
        "offset_in_region": f"0x{addr - (mbi.BaseAddress or 0):X}",
    }


def make_reader(h_process):
    def read(addr, n):
        buf = ctypes.create_string_buffer(n)
        got = ctypes.c_size_t(0)
        _k32.ReadProcessMemory(h_process, ctypes.c_void_p(addr), buf, n,
                               ctypes.byref(got))
        return buf.raw[:got.value] if got.value else b""
    return read


def callsite_forensics(h_process, modmap, stack_hits, wild_addr, regs=None):
    """在栈顶第一条"可执行节内指针"处回找 call 指令，并用寄存器值消歧。

    这一步回答的是"谁把执行流送到了故障地址"——是直接 call，还是经寄存器/指针表
    间接调用，而那个寄存器或表项是坏的。
    """
    reader = make_reader(h_process)
    top = None
    for h in stack_hits:
        if h["is_code_ptr"]:
            top = h
            break
    if top is None:
        return {"note": "栈顶附近没有可执行节内指针，无法定位调用点"}
    ret_addr = top["value"]
    info, cands = at.find_callsite(reader, ret_addr, regs=regs, target=wild_addr)
    out = {
        "return_address": f"0x{ret_addr:016X}",
        "return_module": top["module"],
        "return_rva": f"0x{top['rva']:X}",
        "from_rsp": f"0x{top['from_rsp']:X}" if top["from_rsp"] is not None else None,
        "callsite": None,
        "candidates": [
            {"site": f"0x{c['site']:016X}", "raw": c.get("raw"), "text": c["text"],
             "length": c["length"]} for c in cands],
        "pointer_value": None,
        "pointer_points_to_wild": None,
    }
    if not info:
        out["callsite"] = {"note": "返回地址前 16 字节内未识别出 call"}
        return out
    site = info["site"]
    m = modmap.find(site)
    out["callsite"] = {
        "address": f"0x{site:016X}",
        "module": m.name if m else None,
        "category": at.classify(m.path) if m else None,
        "rva": f"0x{site - m.base:X}" if m else None,
        "text": info["text"],
        "raw": info.get("raw"),
        "length": info["length"],
        "selected_by": info.get("selected_by"),
    }
    # 顺带把该指令附近的内存字节留档，便于人工复核
    win = reader(site - 8, 32)
    out["window_bytes"] = win.hex(" ") if win else None
    if info.get("ptr_addr"):
        raw = reader(info["ptr_addr"], 8)
        if len(raw) == 8:
            pv, = struct.unpack("<Q", raw)
            out["pointer_value"] = {
                "ptr_slot": f"0x{info['ptr_addr']:016X}",
                "value": f"0x{pv:016X}",
                "in_module": (modmap.find(pv).name if modmap.find(pv) else None),
            }
            out["pointer_points_to_wild"] = (pv == wild_addr)
        else:
            out["pointer_value"] = {
                "ptr_slot": f"0x{info['ptr_addr']:016X}",
                "note": "指针槽不可读（该页可能已被释放）"}
    return out


def raw_stack_top(h_process, rsp, modmap, n_slots=24):
    """打印栈顶原始槽位（含不落在任何模块的野值），用于看清被压入的返回地址。"""
    reader = make_reader(h_process)
    raw = reader(rsp, n_slots * 8)
    out = []
    for k in range(min(n_slots, len(raw) // 8)):
        v, = struct.unpack_from("<Q", raw, k * 8)
        m = modmap.find(v)
        out.append({
            "from_rsp": f"0x{k * 8:X}",
            "value": f"0x{v:016X}",
            "in_module": m.name if m else None,
            "rva": f"0x{v - m.base:X}" if m else None,
            "is_code_ptr": bool(m and at.PeInfo.get(m.path).ok
                                and at.PeInfo.get(m.path).is_exec(v - m.base)) if m else False,
        })
    return out


def capture_scene(h_process, pid, tid, er):
    """在异常现场就地抓取：异常记录 + 寄存器 + 栈 + 模块表 + 调用点取证。"""
    regs = get_thread_regs(h_process, tid) or {}
    rsp = regs.get("rsp", 0)
    rip = regs.get("rip", 0)
    sbase, sdata = read_stack(h_process, rsp) if rsp else (0, b"")
    snap = cap.snapshot_modules(pid) or []
    modmap = at.modmap_from_snapshot(snap)

    code, addr = er["code"], er["address"]
    m = modmap.find(addr)
    attribution = None
    if m:
        rva = addr - m.base
        pe = at.PeInfo.get(m.path) if m.path else None
        # ⚠ 必须从进程内存取指令：dxgi.dll 带 .detourc/.detourd 节，运行时会自打补丁，
        #   磁盘镜像与内存中的指令可能不一致，用磁盘字节译码会得出错误结论。
        mem_bytes = make_reader(h_process)(addr, 16)
        attribution = {
            "kind": "in_module", "module": m.name, "module_path": m.path,
            "category": at.classify(m.path), "rva": f"0x{rva:X}", "rva_int": rva,
            "base": f"0x{m.base:016X}", "size": f"0x{m.size:X}",
            "in_exec_section": bool(pe and pe.ok and pe.is_exec(rva)),
            "instruction_in_memory": at.decode_at(mem_bytes),
            "bytes_in_memory": mem_bytes.hex(" "),
            "function_window": make_reader(h_process)(addr, 64).hex(" "),
            "instruction_on_disk": at.decode_at(pe.read_rva(rva, 16)) if pe and pe.ok else None,
            "bytes_on_disk": (pe.read_rva(rva, 16).hex(" ") if pe and pe.ok else None),
            # 故障指令所在页在当前进程里的真实权限 —— 用于判断是否为取指/执行类故障
            "page_probe": probe_address(h_process, addr),
            "stack_page_probe": probe_address(h_process, regs.get("rsp", 0)) if regs.get("rsp") else None,
            "av_target_probe": (probe_address(h_process, av_target)
                                if 'av_target' in dir() and av_target else None),
        }
        attribution["memory_matches_disk"] = (
            attribution["bytes_in_memory"] == attribution["bytes_on_disk"])
    else:
        below = above = None
        for mm in modmap:
            if mm.end <= addr:
                below = mm
            elif above is None:
                above = mm
        attribution = {
            "kind": "wild_address", "module": None,
            "nearest_below": (f"{below.name}+0x{addr - below.base:X}" if below else None),
            "nearest_above": (f"{above.name}-0x{above.base - addr:X}" if above else None),
            "gap_below": f"0x{addr - below.end:X}" if below else None,
            "note": "异常地址不在任何已加载模块内 —— 跳转/调用到野地址",
            "memory_probe": probe_address(h_process, addr),
        }

    params = er["params"]
    av_kind = None
    av_target = None
    if code == 0xC0000005 and params:
        av_kind = {0: "read", 1: "write", 8: "EXECUTE"}.get(params[0], f"unknown({params[0]})")
        if len(params) > 1:
            av_target = params[1]

    scene = {
        "source": "live-debugger",
        "pid": pid,
        "thread_id": tid,
        "exception": {
            "code": f"0x{code:08X}",
            "code_name": md.EXC_NAMES.get(code, "UNKNOWN"),
            "address": f"0x{addr:016X}",
            "params": [f"0x{p:X}" for p in params],
            "av_kind": av_kind,
            "av_target": f"0x{av_target:016X}" if av_target is not None else None,
            "rsp": f"0x{rsp:016X}" if rsp else None,
            "rip": f"0x{rip:016X}" if rip else None,
            "rip_matches_address": (rip == addr) if rip else None,
        },
        "attribution": attribution,
        "target_probe": (probe_address(h_process, av_target)
                         if av_target is not None else None),
        "registers": {k: f"0x{v:016X}" for k, v in regs.items()},
        "module_count": len(snap),
        "modules": [{"name": x["name"], "base": x["base"], "size": x["size"],
                     "path": x["path"]} for x in snap],
    }
    if sdata:
        scene["stack_scan"] = at.build_stack_report(modmap, rsp, sbase, sdata)
        scene["raw_stack_top"] = raw_stack_top(h_process, rsp, modmap)
        scene["callsite"] = callsite_forensics(
            h_process, modmap, scene["stack_scan"]["top_hits"], addr, regs)
    else:
        scene["stack_scan"] = {"note": "未能读取故障线程栈"}

    # 真实 unwind 回溯：栈扫描只知道"槽里像不像代码指针"，unwind 才知道真正的调用链。
    # 必须在进程还冻结在这里时做——dbghelp 要通过被调试进程的句柄读栈。
    # 带 stack_b64 的原始现场由 main() 拆到 <id>.raw.json，避免把现场 JSON 撑大。
    try:
        scene["_unwind_raw"] = uw.unwind_live(h_process, pid, tid, snap, log=print)
    except Exception as e:  # 回溯失败不能拖垮现场采集
        scene["unwind"] = {"ok": False, "reason": f"{type(e).__name__}: {e}"}
    return scene


def run_debugged(exe: str, media: str, routes: int, seconds: int,
                 hard_timeout: float = 150.0, log_path: str | None = None):
    cwd = os.path.dirname(exe)
    cmdline = f'"{exe}" --multitest "{media}" {routes} {seconds}'
    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    pi = PROCESS_INFORMATION()

    fh = open(log_path, "w", encoding="utf-8", errors="replace") if log_path else None
    t0 = time.time()
    ok = _k32.CreateProcessW(
        exe, ctypes.create_unicode_buffer(cmdline), None, None, False,
        DEBUG_ONLY_THIS_PROCESS, None, cwd, ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        if fh:
            fh.close()
        raise OSError(f"CreateProcess 失败，err={ctypes.get_last_error()}")

    pid = pi.dwProcessId
    h_process = pi.hProcess
    _k32.CloseHandle(pi.hThread)

    dll_loads = []
    scenes = []
    all_exceptions = []
    exit_code = None
    timed_out = False
    evbuf = ctypes.create_string_buffer(512)

    try:
        while True:
            el = time.time() - t0
            if el > hard_timeout:
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
                base = struct.unpack_from("<Q", evbuf, 24)[0]
                dll_loads.append(base)
                cont = DBG_CONTINUE
            elif code_ == EXCEPTION_DEBUG_EVENT:
                ecode, eflags = struct.unpack_from("<II", evbuf, 16)
                eaddr = struct.unpack_from("<Q", evbuf, 32)[0]
                nparam = struct.unpack_from("<I", evbuf, 40)[0]
                params = list(struct.unpack_from("<15Q", evbuf, 48))[:min(nparam, 15)]
                first_chance = struct.unpack_from("<I", evbuf, 168)[0]
                all_exceptions.append({
                    "code": f"0x{ecode:08X}",
                    "name": md.EXC_NAMES.get(ecode, "UNKNOWN"),
                    "first_chance": bool(first_chance),
                    "address": f"0x{eaddr:016X}",
                    "params": [f"0x{p:X}" for p in params],
                    "thread_id": dtid,
                    "elapsed_s": round(time.time() - t0, 2),
                })
                if ecode in FATAL_CODES:
                    scene = capture_scene(h_process, pid, dtid,
                                          {"code": ecode, "address": eaddr,
                                           "params": params})
                    scene["first_chance"] = bool(first_chance)
                    scene["elapsed_s"] = round(time.time() - t0, 2)
                    scenes.append(scene)
                # 初始断点交给调试器消化，其余交还进程自身处理
                cont = DBG_CONTINUE if (ecode == 0x80000003 and first_chance) \
                    else DBG_EXCEPTION_NOT_HANDLED
            elif code_ == EXIT_PROCESS_DEBUG_EVENT:
                exit_code = struct.unpack_from("<I", evbuf, 16)[0]
                _k32.ContinueDebugEvent(dpid, dtid, DBG_CONTINUE)
                break
            else:
                cont = DBG_CONTINUE

            _k32.ContinueDebugEvent(dpid, dtid, cont)
    finally:
        # 无论正常结束、超时还是采集器自身抛异常，都必须终止被调试进程，
        # 否则会留下一个继续跑 4 路播放的孤儿（曾经真的漏过一次）。
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
        _k32.CloseHandle(h_process)
        if fh:
            fh.close()

    t1 = time.time()
    unsigned = exit_code
    kind = ("clean" if exit_code == 0 else
            "crash" if exit_code in FATAL_CODES else
            "timeout" if timed_out else "other")
    return {
        "run_id": None,
        "cmd": cmdline,
        "pid": pid,
        "start_epoch": t0, "end_epoch": t1,
        "start_local": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(t0)),
        "duration_s": round(t1 - t0, 2),
        "exit_code": exit_code,
        "exit_code_hex": f"0x{unsigned:08X}" if unsigned is not None else None,
        "exit_kind": kind,
        "timed_out": timed_out,
        "dll_load_events": len(dll_loads),
        "fatal_exception_count": len(scenes),
        "all_exceptions": all_exceptions,
        "scenes": scenes,
        "crash_scene": scenes[-1] if scenes else None,
        "log": log_path,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=cap.DEFAULT_EXE)
    ap.add_argument("--media", default=cap.DEFAULT_MEDIA)
    ap.add_argument("--routes", type=int, default=4)
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--run-id", default=None)
    ap.add_argument("--times", type=int, default=1)
    ap.add_argument("--tag", default="dbg")
    ap.add_argument("--outdir", default=os.path.join(HERE, "scenes"))
    ap.add_argument("--all-scenes", action="store_true",
                    help="保留全部致命异常现场（默认只留最后一个）")
    a = ap.parse_args()

    os.makedirs(a.outdir, exist_ok=True)
    ids = [a.run_id] if a.run_id else [f"{a.tag}_{i:02d}" for i in range(1, a.times + 1)]
    for rid in ids:
        log = os.path.join(a.outdir, "logs", f"{rid}.log")
        os.makedirs(os.path.dirname(log), exist_ok=True)
        res = run_debugged(a.exe, a.media, a.routes, a.seconds, log_path=log)
        res["run_id"] = rid
        if not a.all_scenes:
            res["scenes"] = res["scenes"][-1:]
        # 把带原始栈字节的 unwind 现场拆出去单独落盘（离线可重放，见 unwind.py）
        raws = []
        for sc in res["scenes"]:
            raw = sc.pop("_unwind_raw", None)
            if raw is None:
                continue
            light = {k: v for k, v in raw.items() if k != "stack_b64"}
            sc["unwind"] = light
            raws.append(raw)
        if raws:
            with open(os.path.join(a.outdir, f"{rid}.raw.json"), "w",
                      encoding="utf-8") as f:
                json.dump(raws[-1], f, ensure_ascii=False, indent=2)
        with open(os.path.join(a.outdir, f"{rid}.json"), "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
        cs = res["crash_scene"]
        print(f"[{rid}] 退出码={res['exit_code_hex']} ({res['exit_kind']}) "
              f"耗时={res['duration_s']}s 致命异常次数={res['fatal_exception_count']}")
        if cs:
            e = cs["exception"]
            print(f"    异常码={e['code']} {e['code_name']} 性质={e['av_kind']} "
                  f"地址={e['address']} 目标={e['av_target']}")
            print(f"    RIP={e['rip']} RSP={e['rsp']} RIP==地址:{e['rip_matches_address']}")
            rg = cs.get("registers") or {}
            if rg:
                print("    寄存器: " + " ".join(
                    f"{k}={rg[k]}" for k in ("rax", "rcx", "rdx", "rbx", "rsi", "rdi",
                                             "r8", "r9", "r10", "r11") if k in rg))
            aa = cs["attribution"]
            if aa["kind"] == "in_module":
                print(f"    归属={aa['module']} [{aa['category']}] RVA={aa['rva']} "
                      f"可执行节={aa['in_exec_section']}")
                print(f"    内存指令: {aa.get('instruction_in_memory')}   "
                      f"bytes={aa.get('bytes_in_memory')}")
                if aa.get("memory_matches_disk") is False:
                    print(f"    ⚠ 内存与磁盘字节不一致（运行时被打过补丁）"
                          f"  磁盘: {aa.get('instruction_on_disk')}")
            else:
                mp = aa.get("memory_probe") or {}
                print(f"    归属=野地址  下方最近={aa.get('nearest_below')}  "
                      f"上方最近={aa.get('nearest_above')}")
                if mp:
                    print(f"    该地址内存: {mp.get('state')} / {mp.get('type')} / "
                          f"protect={mp.get('protect')}  区基址={mp.get('region_base')}")
            ct = cs.get("callsite") or {}
            if ct.get("callsite"):
                c = ct["callsite"]
                print(f"    调用点: {c.get('module')} RVA={c.get('rva')}  "
                      f"{c.get('text')}  raw={c.get('raw')}")
                print(f"            返回地址={ct['return_address']} "
                      f"({ct['return_module']}+{ct['return_rva']}, rsp+{ct.get('from_rsp')})  "
                      f"裁决依据={c.get('selected_by')}")
                if len(ct.get("candidates") or []) > 1:
                    print("            全部候选: " + " | ".join(
                        f"0x{x['site'][-4:]}:{x['text']}" for x in ct["candidates"]))
                if ct.get("pointer_value"):
                    pv = ct["pointer_value"]
                    print(f"    指针槽 {pv.get('ptr_slot')} -> {pv.get('value')} "
                          f"(在模块内={pv.get('in_module')}) "
                          f"指向故障地址={ct.get('pointer_points_to_wild')}")
            elif ct.get("note"):
                print(f"    调用点: {ct['note']}")
            ss = cs.get("stack_scan") or {}
            if ss.get("sequence"):
                print(f"    栈顶模块序列: " +
                      " -> ".join(f"{s['module']}(c{s['count']})" for s in ss["sequence"][:10]))
                print(f"    栈上钩子={ss['hook_modules_on_stack'] or '无'}  "
                      f"栈上内核={ss['kernel_ours_on_stack'] or '无'}")
            uwr = cs.get("unwind") or {}
            if uwr.get("frames"):
                print(f"    真实 unwind 回溯（{uwr.get('engine')}）:")
                for fr in uwr["frames"]:
                    rva = f"+{fr['rva_hex']}" if fr.get("rva_hex") else ""
                    disp = f" + 0x{fr['displacement']:X}" if fr.get("displacement") else ""
                    src = f"  [{fr['source']}:{fr['line']}]" if fr.get("source") else ""
                    print(f"      #{fr['frame']:<2} {fr.get('module') or '?'}{rva}  "
                          f"{fr.get('symbol') or '(无符号)'}{disp}{src}")
            elif uwr:
                print(f"    真实 unwind 回溯: 失败（{uwr.get('reason')}）")


if __name__ == "__main__":
    main()
