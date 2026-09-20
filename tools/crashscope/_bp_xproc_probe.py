"""跨进程硬件断点探针：判定"调试器用 SetThreadContext 给被调试线程下 DR0"是否生效。

背景：breaktrace 正对照里 hits=0，而同进程就地探针证明硬件断点本身有效
（DR0=kernel32!Sleep 后调 Sleep 会反复触发，进程卡死）。差别就在"跨进程"。
本探针把变量收敛到最小：只下一个断点，只打一个必然被调用的函数。

子进程循环调 kernel32!Sleep；调试器在 kernel32 加载后给主线程下 DR0=kernel32!Sleep。
  * 看到 STATUS_SINGLE_STEP ⇒ 跨进程 DR 有效，正对照的失败另有原因
  * 什么都没看到           ⇒ 跨进程 DR 无效，必须改用 INT3
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import capture as cap      # noqa: E402
import debugrun as dr      # noqa: E402
import breaktrace as bt    # noqa: E402

CHILD = os.path.join(HERE, "_bp_xproc_child.py")

DBG_CONTINUE = 0x00010002
DBG_EXCEPTION_NOT_HANDLED = 0x80010001
ERROR_SEM_TIMEOUT = 121
EXCEPTION_DEBUG_EVENT = 1
CREATE_PROCESS_DEBUG_EVENT = 3
LOAD_DLL_DEBUG_EVENT = 6
EXIT_PROCESS_DEBUG_EVENT = 5
STATUS_SINGLE_STEP = 0x80000004

_k32 = dr._k32


def main():
    exe = sys.executable
    cwd = HERE
    cmdline = f'"{exe}" "{CHILD}"'
    si = dr.STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    pi = dr.PROCESS_INFORMATION()
    ok = _k32.CreateProcessW(exe, ctypes.create_unicode_buffer(cmdline), None, None,
                             False, dr.DEBUG_ONLY_THIS_PROCESS, None, cwd,
                             ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        raise OSError(f"CreateProcess 失败 err={ctypes.get_last_error()}")
    pid, h = pi.dwProcessId, pi.hProcess
    _k32.CloseHandle(pi.hThread)
    t0 = time.time()
    print(f"pid={pid} cmd={cmdline}", flush=True)

    armed = False
    armed_info = None
    events = []
    evbuf = ctypes.create_string_buffer(512)
    try:
        while time.time() - t0 < 15:
            if not _k32.WaitForDebugEvent(evbuf, 500):
                if ctypes.get_last_error() == ERROR_SEM_TIMEOUT:
                    continue
                break
            code_, dpid, dtid = struct.unpack_from("<III", evbuf, 0)
            cont = DBG_CONTINUE
            if code_ == CREATE_PROCESS_DEBUG_EVENT:
                hf = ctypes.c_void_p(struct.unpack_from("<Q", evbuf, 16)[0])
                if hf:
                    _k32.CloseHandle(hf)
            elif code_ == LOAD_DLL_DEBUG_EVENT:
                hf = ctypes.c_void_p(struct.unpack_from("<Q", evbuf, 16)[0])
                if hf:
                    _k32.CloseHandle(hf)
                if not armed:
                    snap = cap.snapshot_modules(pid) or []
                    k32mod = next((m for m in snap
                                   if m["name"].lower() == "kernel32.dll"), None)
                    if k32mod:
                        rva = bt.export_rva(k32mod["path"], "Sleep")
                        addr = k32mod["base"] + rva
                        okk, before, back = bt.arm_thread(dtid, [addr])
                        armed = True
                        armed_info = {
                            "kernel32_base": f"0x{k32mod['base']:016X}",
                            "Sleep_rva": f"0x{rva:X}",
                            "Sleep_addr": f"0x{addr:X}",
                            "arm_ok": okk,
                            "dr7_before": before,
                            "dr0_readback": back,
                        }
                        print(f"已下 DR0=kernel32!Sleep @0x{addr:X} ok={okk} "
                              f"回读=0x{back:X}" if back else f"已下断点 ok={okk}", flush=True)
            elif code_ == EXCEPTION_DEBUG_EVENT:
                ecode = struct.unpack_from("<I", evbuf, 16)[0]
                eaddr = struct.unpack_from("<Q", evbuf, 32)[0]
                fc = struct.unpack_from("<I", evbuf, 168)[0]
                events.append({"code": f"0x{ecode:08X}", "addr": f"0x{eaddr:X}",
                               "first": bool(fc), "t": round(time.time() - t0, 2),
                               "tid": dtid})
                print(f"  异常 {ecode:#010x} @0x{eaddr:X} first={bool(fc)} "
                      f"t={time.time()-t0:.2f}", flush=True)
                if ecode == STATUS_SINGLE_STEP:
                    print("  ★ 命中硬件断点 ⇒ 跨进程 DR 有效", flush=True)
                cont = (DBG_CONTINUE if ecode in (0x80000003, STATUS_SINGLE_STEP)
                        else DBG_EXCEPTION_NOT_HANDLED)
            elif code_ == EXIT_PROCESS_DEBUG_EVENT:
                _k32.ContinueDebugEvent(dpid, dtid, DBG_CONTINUE)
                break
            _k32.ContinueDebugEvent(dpid, dtid, cont)
    finally:
        try:
            if _k32.WaitForSingleObject(h, 0) != 0:
                _k32.TerminateProcess(h, 1)
        except Exception:
            pass
        try:
            _k32.DebugActiveProcessStop(pid)
        except Exception:
            pass
        _k32.CloseHandle(h)

    hit = any(e["code"] == "0x80000004" for e in events)
    out = {"armed": armed_info, "events": events, "hw_breakpoint_worked": hit}
    print("\n" + json.dumps(out, ensure_ascii=False, indent=1))
    print("结论: " + ("跨进程硬件断点【有效】" if hit else "跨进程硬件断点【无效】→ 改用 INT3"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
