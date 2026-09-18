#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
dump_capture.py —— 以调试器身份启动目标进程，在**第一现场**（first-chance 致命异常）
直接调 MiniDumpWriteDump 落盘。

为什么需要它：
  本机 WER 自 2026-09-14 起不再为 3FCompare.exe 产生转储（WerSvc 被停用；
  即便把服务拉起来，.NET 进程内的原生访问违例仍会被运行时吞掉，LocalDumps 不落盘）。
  没有转储就只能靠读码猜根因——而 issue #7 的锁修复已证明"猜"不可靠。
  本脚本不依赖 WER、不依赖 cdb/windbg，只需 dbghelp.dll（系统自带）。

用法：
  python tools/dump_capture.py --out <目录> -- <exe> <参数...>
退出码：
  0 = 抓到转储；1 = 进程正常退出（没崩）；2 = 用法/运行错误
"""
import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
dbg = ctypes.WinDLL("dbghelp", use_last_error=True)

DEBUG_PROCESS = 0x00000001
INFINITE = 0xFFFFFFFF

EXCEPTION_DEBUG_EVENT = 1
CREATE_PROCESS_DEBUG_EVENT = 3
EXIT_PROCESS_DEBUG_EVENT = 5
LOAD_DLL_DEBUG_EVENT = 6

DBG_EXCEPTION_NOT_HANDLED = 0x80010001
DBG_CONTINUE = 0x00010002

# 只对这些"真致命"异常落盘。.NET 运行时会大量使用断点/线程命名异常，
# 若不分青红皂白全抓，第一个 0x80000003 就会误报。
FATAL = {
    0xC0000005: "ACCESS_VIOLATION",
    0xC000001D: "ILLEGAL_INSTRUCTION",
    0xC0000006: "IN_PAGE_ERROR",
    0xC0000094: "INT_DIVIDE_BY_ZERO",
    0xC0000095: "INT_OVERFLOW",
    0xC0000374: "HEAP_CORRUPTION",
    0xC0000409: "STACK_BUFFER_OVERRUN",
    0xC000041D: "FATAL_USER_CALLBACK",
    0xC0000602: "FAIL_FAST",
}

CONTEXT_SIZE = 1232          # x64 CONTEXT
EXC_REC_SIZE = 152           # x64 EXCEPTION_RECORD


class STARTUPINFOW(ctypes.Structure):
    _fields_ = [
        ("cb", wt.DWORD), ("lpReserved", wt.LPWSTR), ("lpDesktop", wt.LPWSTR),
        ("lpTitle", wt.LPWSTR),
        ("dwX", wt.DWORD), ("dwY", wt.DWORD), ("dwXSize", wt.DWORD), ("dwYSize", wt.DWORD),
        ("dwXCountChars", wt.DWORD), ("dwYCountChars", wt.DWORD), ("dwFillAttribute", wt.DWORD),
        ("dwFlags", wt.DWORD), ("wShowWindow", wt.WORD), ("cbReserved2", wt.WORD),
        ("lpReserved2", ctypes.POINTER(ctypes.c_ubyte)),
        ("hStdInput", wt.HANDLE), ("hStdOutput", wt.HANDLE), ("hStdError", wt.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE),
                ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


class DEBUG_EVENT(ctypes.Structure):
    # 三个 DWORD 之后联合体按 8 字节对齐 —— 必须显式补一个 DWORD，
    # 否则 ctypes 会把联合体放在偏移 12，异常记录整体读错。
    _fields_ = [("dwDebugEventCode", wt.DWORD), ("dwProcessId", wt.DWORD),
                ("dwThreadId", wt.DWORD), ("_pad", wt.DWORD),
                ("u", ctypes.c_ubyte * 512)]


class MINIDUMP_EXCEPTION_INFORMATION(ctypes.Structure):
    _fields_ = [("ThreadId", wt.DWORD),
                ("ExceptionPointers", ctypes.c_void_p),
                ("ClientPointers", wt.BOOL)]


k32.CreateProcessW.argtypes = [wt.LPCWSTR, wt.LPWSTR, ctypes.c_void_p, ctypes.c_void_p,
                               wt.BOOL, wt.DWORD, ctypes.c_void_p, wt.LPCWSTR,
                               ctypes.POINTER(STARTUPINFOW), ctypes.POINTER(PROCESS_INFORMATION)]
k32.CreateProcessW.restype = wt.BOOL
k32.WaitForDebugEvent.argtypes = [ctypes.POINTER(DEBUG_EVENT), wt.DWORD]
k32.WaitForDebugEvent.restype = wt.BOOL
k32.ContinueDebugEvent.argtypes = [wt.DWORD, wt.DWORD, wt.DWORD]
k32.ContinueDebugEvent.restype = wt.BOOL
k32.GetThreadContext.argtypes = [wt.HANDLE, ctypes.c_void_p]
k32.GetThreadContext.restype = wt.BOOL
k32.TerminateProcess.argtypes = [wt.HANDLE, ctypes.c_uint]
k32.TerminateProcess.restype = wt.BOOL
k32.CloseHandle.argtypes = [wt.HANDLE]
k32.CloseHandle.restype = wt.BOOL
k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                            wt.DWORD, wt.DWORD, wt.HANDLE]
k32.CreateFileW.restype = wt.HANDLE
k32.OpenThread.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
k32.OpenThread.restype = wt.HANDLE
dbg.MiniDumpWriteDump.argtypes = [wt.HANDLE, wt.DWORD, wt.HANDLE, ctypes.c_uint,
                                  ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
dbg.MiniDumpWriteDump.restype = wt.BOOL


psapi = ctypes.WinDLL("psapi", use_last_error=True)


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p), ("SizeOfImage", wt.DWORD),
                ("EntryPoint", ctypes.c_void_p)]


psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.POINTER(wt.HMODULE), wt.DWORD,
                                       ctypes.POINTER(wt.DWORD), wt.DWORD]
psapi.EnumProcessModulesEx.restype = wt.BOOL
psapi.GetModuleInformation.argtypes = [wt.HANDLE, wt.HMODULE,
                                      ctypes.POINTER(MODULEINFO), wt.DWORD]
psapi.GetModuleInformation.restype = wt.BOOL
psapi.GetModuleBaseNameW.argtypes = [wt.HANDLE, wt.HMODULE, wt.LPWSTR, wt.DWORD]
psapi.GetModuleBaseNameW.restype = wt.DWORD


def attribute_module(hproc, addr):
    """把崩溃地址归属到具体模块的 +偏移 —— 不需要 windbg 就能判定是否命中同一个点。"""
    try:
        arr = (wt.HMODULE * 2048)()
        needed = wt.DWORD()
        if not psapi.EnumProcessModulesEx(hproc, arr, ctypes.sizeof(arr),
                                          ctypes.byref(needed), 0x03):
            return f"EnumProcessModulesEx 失败: {ctypes.get_last_error()}"
        count = min(needed.value // ctypes.sizeof(wt.HMODULE), 2048)
        for i in range(count):
            mi = MODULEINFO()
            if not psapi.GetModuleInformation(hproc, arr[i], ctypes.byref(mi),
                                              ctypes.sizeof(mi)):
                continue
            base = mi.lpBaseOfDll or 0
            if base and base <= addr < base + mi.SizeOfImage:
                buf = ctypes.create_unicode_buffer(260)
                psapi.GetModuleBaseNameW(hproc, arr[i], buf, 260)
                return f"{buf.value} +0x{addr - base:X}（基址 0x{base:X}）"
        return "未落在任何已加载模块内（页对齐/不可执行 ⇒ 失效指针跳转的典型特征）"
    except Exception as exc:                                    # pragma: no cover
        return f"归属失败: {exc}"


# x64 CONTEXT 内寄存器偏移（与 MSDN 一致；Rip/Rsp/Rbp 用于定位栈）
CTX_RAX, CTX_RSP, CTX_RBP, CTX_RIP = 0x78, 0x98, 0xA0, 0xF8


def capture_stack(hproc, hthread, dump_path, tid, want=0x10000):
    """崩溃现场直接读栈。

    为什么不只靠 minidump：本机 MiniDumpWriteDump 带异常信息会
    ERROR_INVALID_PARAMETER，退化的转储又不带完整线程栈（154 个线程总共才 294 KB，
    扫不出调用链）。索性在这里自己把 RSP 起的 64 KB 拷出来——后续配合
    tools/stack_resolve.py 即可还原候选调用序列。
    """
    try:
        ctx = ctypes.create_string_buffer(CONTEXT_SIZE)
        struct = ctypes.c_uint32.from_buffer(ctx, 0x30)
        struct.value = 0x0010000B          # CONTEXT_FULL
        if not k32.GetThreadContext(hthread, ctx):
            print(f"[dump_capture] GetThreadContext 失败: {ctypes.get_last_error()}",
                  file=sys.stderr)
            return
        rip = int.from_bytes(bytes(ctx[CTX_RIP:CTX_RIP + 8]), "little")
        rsp = int.from_bytes(bytes(ctx[CTX_RSP:CTX_RSP + 8]), "little")
        rbp = int.from_bytes(bytes(ctx[CTX_RBP:CTX_RBP + 8]), "little")
        print(f"[dump_capture] 崩溃线程 TID={tid}  RIP=0x{rip:X}  RSP=0x{rsp:X}  RBP=0x{rbp:X}",
              flush=True)

        buf = ctypes.create_string_buffer(want)
        read = ctypes.c_size_t(0)
        k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                          ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
        k32.ReadProcessMemory.restype = wt.BOOL
        ok = k32.ReadProcessMemory(hproc, rsp, buf, want, ctypes.byref(read))
        base = dump_path[:-4] if dump_path.endswith(".dmp") else dump_path
        meta = f"{base}.meta.txt"
        stack = f"{base}.stack.bin"
        with open(stack, "wb") as fh:
            fh.write(bytes(buf[:read.value]))
        with open(meta, "w", encoding="utf-8") as fh:
            fh.write(f"tid={tid}\nrip=0x{rip:X}\nrsp=0x{rsp:X}\nrbp=0x{rbp:X}\n"
                     f"stack_file={os.path.basename(stack)}\n"
                     f"stack_bytes={read.value}\nread_ok={int(bool(ok))}\n")
        print(f"[dump_capture] 栈已存: {stack}（{read.value} 字节）；元信息: {meta}", flush=True)
    except Exception as exc:                                    # pragma: no cover
        print(f"[dump_capture] 抓栈失败: {exc}", file=sys.stderr)


def die(msg, code=2):
    print(f"[dump_capture] {msg}", file=sys.stderr)
    sys.exit(code)


def main(argv):
    if "--" not in argv:
        die("用法: dump_capture.py --out <目录> -- <exe> <参数...>")
    head, tail = argv[:argv.index("--")], argv[argv.index("--") + 1:]
    out_dir = "."
    if "--out" in head:
        out_dir = head[head.index("--out") + 1]
    if not tail:
        die("缺少目标命令行")
    os.makedirs(out_dir, exist_ok=True)

    app = tail[0]
    # lpCommandLine 必须可写，且要把 exe 本身也带上（CreateProcess 不会替你拼）
    cmdline = " ".join(f'"{a}"' if " " in a else a for a in tail)

    si = STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    pi = PROCESS_INFORMATION()

    ok = k32.CreateProcessW(app, cmdline, None, None, False, DEBUG_PROCESS,
                            None, os.path.dirname(app) or None,
                            ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        die(f"CreateProcess 失败: {ctypes.get_last_error()}")
    print(f"[dump_capture] 已启动 pid={pi.dwProcessId}（调试附加）", flush=True)

    evt = DEBUG_EVENT()
    dumped = None
    exit_code = None
    deadline = time.time() + 600

    while time.time() < deadline:
        if not k32.WaitForDebugEvent(ctypes.byref(evt), 1000):
            continue
        code = evt.dwDebugEventCode
        status = DBG_EXCEPTION_NOT_HANDLED

        if code == EXCEPTION_DEBUG_EVENT:
            exc_code = int.from_bytes(bytes(evt.u[0:4]), "little")
            if exc_code in FATAL:
                name = FATAL[exc_code]
                addr = int.from_bytes(bytes(evt.u[16:24]), "little")
                print(f"[dump_capture] 捕获 {name} (0x{exc_code:08X}) @ 0x{addr:X} "
                      f"tid={evt.dwThreadId}", flush=True)
                print(f"[dump_capture] 模块归属: {attribute_module(pi.hProcess, addr)}", flush=True)
                path = os.path.join(out_dir, f"crash_{name}_0x{exc_code:08X}_{int(time.time())}.dmp")
                # ⚠ 崩溃线程通常不是 pi.hThread（初始线程）。必须按 tid 打开它，
                #   否则 GetThreadContext 取到的是主线程上下文，栈全是错的。
                THREAD_GET_CONTEXT = 0x0008
                THREAD_QUERY_INFORMATION = 0x0040
                ht = k32.OpenThread(THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
                                    False, evt.dwThreadId)
                if not ht:
                    print(f"[dump_capture] OpenThread 失败: {ctypes.get_last_error()} "
                          f"（回退初始线程，栈可能不准）", file=sys.stderr)
                    ht = pi.hThread
                try:
                    # 必须在拿到崩溃线程句柄之后（ht）再抓栈，否则 NameError
                    # 会让整个脚本在崩溃那一刻挂掉，连转储都落不了盘。
                    capture_stack(pi.hProcess, ht, path, evt.dwThreadId)
                    dumped = path if write_dump(pi.hProcess, pi.dwProcessId,
                                                ht, evt, exc_code, path) else None
                finally:
                    if ht != pi.hThread:
                        k32.CloseHandle(wt.HANDLE(ht))
                if dumped:
                    k32.TerminateProcess(pi.hProcess, 0xC0000005)
                    k32.ContinueDebugEvent(evt.dwProcessId, evt.dwThreadId, DBG_CONTINUE)
                    break
            status = DBG_EXCEPTION_NOT_HANDLED
        elif code == CREATE_PROCESS_DEBUG_EVENT:
            hfile = int.from_bytes(bytes(evt.u[0:8]), "little")
            if hfile:
                k32.CloseHandle(wt.HANDLE(hfile))
        elif code == LOAD_DLL_DEBUG_EVENT:
            hfile = int.from_bytes(bytes(evt.u[0:8]), "little")
            if hfile:
                k32.CloseHandle(wt.HANDLE(hfile))
        elif code == EXIT_PROCESS_DEBUG_EVENT:
            exit_code = int.from_bytes(bytes(evt.u[0:4]), "little")
            k32.ContinueDebugEvent(evt.dwProcessId, evt.dwThreadId, DBG_CONTINUE)
            break

        k32.ContinueDebugEvent(evt.dwProcessId, evt.dwThreadId, status)

    k32.CloseHandle(pi.hThread)
    k32.CloseHandle(pi.hProcess)

    if dumped:
        print(f"[dump_capture] 转储已落盘: {dumped}  ({os.path.getsize(dumped)} 字节)")
        return 0
    print(f"[dump_capture] 未捕获致命异常（进程退出码={exit_code}）")
    return 1


def write_dump(hproc, pid, hthread, evt, exc_code, path):
    # 异常记录：直接从调试事件缓冲拷到本进程内存
    rec = ctypes.create_string_buffer(bytes(evt.u[0:EXC_REC_SIZE]), EXC_REC_SIZE)
    # 线程上下文：必须取崩溃线程的真实 CONTEXT
    ctx = ctypes.create_string_buffer(CONTEXT_SIZE)
    if not k32.GetThreadContext(hthread, ctx):
        print(f"[dump_capture] GetThreadContext 失败: {ctypes.get_last_error()}", file=sys.stderr)
    ptrs = ctypes.create_string_buffer(16)
    ptrs[0:8] = ctypes.addressof(rec).to_bytes(8, "little")
    ptrs[8:16] = ctypes.addressof(ctx).to_bytes(8, "little")

    info = MINIDUMP_EXCEPTION_INFORMATION()
    info.ThreadId = evt.dwThreadId
    info.ExceptionPointers = ctypes.addressof(ptrs)
    # ⚠ 指针在**调试器**（本进程）地址空间 ⇒ 必须是 TRUE。
    #   写成 FALSE 会让 MiniDumpWriteDump 按目标进程地址空间去解引用，返回
    #   ERROR_INVALID_PARAMETER（0x80070578），表现为"转储文件 0 字节"。
    info.ClientPointers = True

    hfile = k32.CreateFileW(path, 0x40000000, 0, None, 2, 0x80, None)
    if hfile == ctypes.c_void_p(-1).value or hfile == -1:
        print(f"[dump_capture] CreateFile 失败: {ctypes.get_last_error()}", file=sys.stderr)
        return False
    ok = dbg.MiniDumpWriteDump(hproc, pid, wt.HANDLE(hfile), 0,
                               ctypes.byref(info), None, None)
    if not ok:
        err = ctypes.get_last_error()
        print(f"[dump_capture] 带异常信息落盘失败 (0x{err & 0xFFFFFFFF:08X})，"
              f"降级为不带 ExceptionStream 的转储（线程栈仍在）", file=sys.stderr)
        ok = dbg.MiniDumpWriteDump(hproc, pid, wt.HANDLE(hfile), 0, None, None, None)
        if not ok:
            print(f"[dump_capture] MiniDumpWriteDump 仍失败: {ctypes.get_last_error()}",
                  file=sys.stderr)
    k32.CloseHandle(wt.HANDLE(hfile))
    return bool(ok)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
