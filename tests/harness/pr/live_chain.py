"""在异常现场冻结进程，读原始 ExceptionRecord（含 ExceptionInformation）+ 探 chain 对象。

为什么要单独跑一次：dump_capture 的 MiniDumpWriteDump 带异常信息落盘在本机失败
（0x800703E6 ERROR_NOACCESS），退化的 dump 里**没有 ExceptionStream**，拿不到
ExceptionInformation；而"AV 到底读/写了哪个地址"正是本次要回答的核心问题之一。
调试器事件里的 EXCEPTION_RECORD 是原始字节，最可信。

放在 .review_pr/crashdumps/（gitignore），只是本次取证的现场脚本。
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "crashscope"))
import attribute as at  # noqa: E402
import capture as cap  # noqa: E402
import minidump as md  # noqa: E402
import unwind as uw  # noqa: E402

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
DEBUG_ONLY_THIS_PROCESS = 0x00000002
EXCEPTION_DEBUG_EVENT = 1
CREATE_PROCESS_DEBUG_EVENT = 3
EXIT_PROCESS_DEBUG_EVENT = 5
LOAD_DLL_DEBUG_EVENT = 6
DBG_CONTINUE = 0x00010002
DBG_EXCEPTION_NOT_HANDLED = 0x80010001
FATAL = {0xC0000005, 0xC000001D, 0xC0000006, 0xC0000094, 0xC0000096,
         0xC00000FD, 0xC0000409, 0xC0000374}

EXE = os.path.join(REPO, "src", "3FCompare", "bin", "Debug", "net11.0-windows",
                   "3FCompare.exe")
MEDIA = os.path.join(REPO, "testmedia", "media", "real", "real_4k_h264_60m.mp4")


def reader_for(hp):
    def rd(addr, n):
        buf = ctypes.create_string_buffer(n)
        got = ctypes.c_size_t(0)
        k32.ReadProcessMemory(hp, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
        return buf.raw[:got.value] if got.value else b""
    return rd


def main():
    import debugrun as dr
    si = dr.STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    pi = dr.PROCESS_INFORMATION()
    cmd = f'"{EXE}" --multitest "{MEDIA}" 4 30'
    ok = k32.CreateProcessW(EXE, ctypes.create_unicode_buffer(cmd), None, None,
                            False, DEBUG_ONLY_THIS_PROCESS, None,
                            os.path.dirname(EXE), ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        raise SystemExit(f"CreateProcess 失败 {ctypes.get_last_error()}")
    hp, pid = pi.hProcess, pi.dwProcessId
    print(f"[live] pid={pid} 已启动（调试附加）", flush=True)
    evbuf = ctypes.create_string_buffer(512)
    t0 = time.time()
    scenes = []
    while time.time() - t0 < 150:
        if not k32.WaitForDebugEvent(evbuf, 1000):
            continue
        code, dpid, dtid = struct.unpack_from("<III", evbuf, 0)
        cont = DBG_EXCEPTION_NOT_HANDLED
        if code == EXCEPTION_DEBUG_EVENT:
            ecode, eflags = struct.unpack_from("<II", evbuf, 16)
            eaddr = struct.unpack_from("<Q", evbuf, 32)[0]
            nparam = struct.unpack_from("<I", evbuf, 40)[0]
            params = list(struct.unpack_from("<15Q", evbuf, 48))[:min(nparam, 15)]
            first = struct.unpack_from("<I", evbuf, 168)[0]
            print(f"[live] 异常 0x{ecode:08X} addr=0x{eaddr:X} "
                  f"nparam={nparam} params={[hex(p) for p in params]} "
                  f"first_chance={bool(first)} tid={dtid}", flush=True)
            if ecode in FATAL and first:
                # 只抓 first-chance：second-chance 是同一条记录的复述，重复抓没意义
                try:
                    scenes.append(capture(hp, pid, dtid, ecode, eaddr, params,
                                          first, eflags))
                except Exception as e:
                    print(f"[live] 现场采集失败 {type(e).__name__}: {e}", flush=True)
            cont = DBG_CONTINUE if (ecode == 0x80000003 and first) else \
                DBG_EXCEPTION_NOT_HANDLED
        elif code == CREATE_PROCESS_DEBUG_EVENT:
            hf = struct.unpack_from("<Q", evbuf, 16)[0]
            if hf:
                k32.CloseHandle(ctypes.c_void_p(hf))
            cont = DBG_CONTINUE
        elif code == LOAD_DLL_DEBUG_EVENT:
            hf = struct.unpack_from("<Q", evbuf, 16)[0]
            if hf:
                k32.CloseHandle(ctypes.c_void_p(hf))
            cont = DBG_CONTINUE
        elif code == EXIT_PROCESS_DEBUG_EVENT:
            print("[live] 进程退出", flush=True)
            k32.ContinueDebugEvent(dpid, dtid, DBG_CONTINUE)
            break
        k32.ContinueDebugEvent(dpid, dtid, cont)
    # 优先选 dxgi+0x19530 那一支（本次取证的目标崩溃点）
    scene = None
    for s in scenes:
        if s["exception"]["address_in_module"] and s["exception"]["address_in_module"].startswith("dxgi.dll+0x19530"):
            scene = s
            break
    if scene is None and scenes:
        scene = scenes[0]
    out = os.path.join(HERE, "live_scene.json")
    if scene:
        scene["all_fatal_scenes"] = [
            {"code": s["exception"]["code"], "address": s["exception"]["address"],
             "params": s["exception"]["ExceptionInformation"],
             "tid": s["thread_id"]} for s in scenes]
        with open(out, "w", encoding="utf-8") as f:
            json.dump(scene, f, ensure_ascii=False, indent=2)
        print(f"[live] 现场已存 {out}（共 {len(scenes)} 个 fatal 现场）")
    try:
        k32.TerminateProcess(hp, 0)
    except Exception:
        pass
    return 0 if scene else 1


def capture(hp, pid, tid, ecode, eaddr, params, first, eflags=0):
    regs = dr.get_thread_regs(hp, tid) or {}
    rd = reader_for(hp)
    snap = cap.snapshot_modules(pid) or []
    modmap = at.modmap_from_snapshot(snap)

    def probe(a):
        return dr.probe_address(hp, a) if a else None

    def in_mod(a):
        m = modmap.find(a) if a else None
        return f"{m.name}+0x{a - m.base:X}" if m else None

    def obj_dump(a, n=0x40):
        out = {}
        for off in range(0, n, 8):
            raw = rd(a + off, 8)
            if len(raw) < 8:
                out[f"+0x{off:02X}"] = None
                continue
            v, = struct.unpack("<Q", raw)
            out[f"+0x{off:02X}"] = {"value": f"0x{v:016X}", "in_module": in_mod(v)}
        return out

    av_kind = {0: "read", 1: "write", 8: "EXECUTE"}.get(params[0], f"?({params[0]})") \
        if params else None
    av_target = params[1] if len(params) > 1 else None
    chain = regs.get("rcx")
    vtbl = regs.get("rax")
    rip = regs.get("rip", 0)
    rsp = regs.get("rsp", 0)

    vtbl_slot = None
    if vtbl:
        raw = rd(vtbl + 0x40, 8)
        if len(raw) == 8:
            v, = struct.unpack("<Q", raw)
            vtbl_slot = {"slot": "rax+0x40", "value": f"0x{v:016X}",
                         "in_module": in_mod(v), "equals_rip": (v == rip)}
    chain_vtbl = None
    if chain:
        raw = rd(chain, 8)
        if len(raw) == 8:
            v, = struct.unpack("<Q", raw)
            chain_vtbl = {"value": f"0x{v:016X}", "in_module": in_mod(v),
                          "equals_rax": (v == vtbl)}

    rets = []
    top = None
    for k in range(16):
        raw = rd(rsp + k * 8, 8)
        if len(raw) < 8:
            break
        v, = struct.unpack("<Q", raw)
        m = modmap.find(v)
        if m and at.PeInfo.get(m.path).ok and at.PeInfo.get(m.path).is_exec(v - m.base):
            top = v
            rets.append({"from_rsp": f"+0x{k*8:X}", "value": f"0x{v:016X}",
                         "in_module": f"{m.name}+0x{v-m.base:X}"})
            if len(rets) >= 4:
                break

    callsite = None
    if top:
        info, cands = at.find_callsite(rd, top, regs=regs, target=eaddr)
        callsite = {
            "return_address": f"0x{top:016X}",
            "return_in_module": in_mod(top),
            "selected": ({"site": f"0x{info['site']:016X}",
                          "text": info["text"], "raw": info.get("raw"),
                          "in_module": in_mod(info["site"]),
                          "selected_by": info.get("selected_by")} if info else None),
            "candidates": [{"site": f"0x{c['site']:016X}", "text": c["text"],
                            "raw": c.get("raw")} for c in cands],
        }

    unwind = None
    try:
        unwind = uw.unwind_live(hp, pid, tid, snap, log=lambda s: print(s, flush=True))
        unwind.pop("stack_b64", None)
        unwind.pop("modules", None)
    except Exception as e:
        unwind = {"ok": False, "reason": f"{type(e).__name__}: {e}"}

    return {
        "pid": pid, "thread_id": tid, "first_chance": bool(first),
        "exception": {
            "code": f"0x{ecode:08X}", "code_name": md.EXC_NAMES.get(ecode, "?"),
            "flags": f"0x{eflags:X}",
            "address": f"0x{eaddr:016X}", "address_in_module": in_mod(eaddr),
            "n_params": len(params),
            "ExceptionInformation": [f"0x{p:016X}" for p in params],
            "av_kind": av_kind, "av_target": f"0x{av_target:016X}" if av_target else None,
        },
        "registers": {k: f"0x{v:016X}" for k, v in regs.items()},
        "probe_av_target": probe(av_target),
        "probe_chain": probe(chain),
        "probe_vtable_rax": probe(vtbl),
        "probe_rsp": probe(rsp),
        "chain_object_first_0x40": obj_dump(chain) if chain else None,
        "chain_vtable": chain_vtbl,
        "vtable_slot_rax_plus_0x40": vtbl_slot,
        "instruction_live": at.decode_at(rd(rip, 16)),
        "instruction_disk": at.decode_at(at.PeInfo.get(
            modmap.find(rip).path).read_rva(rip - modmap.find(rip).base, 16))
        if modmap.find(rip) else None,
        "bytes_live": rd(rip, 16).hex(" "),
        "stack_returns": rets,
        "callsite": callsite,
        "unwind": unwind,
        "module_count": len(snap),
    }


if __name__ == "__main__":
    import debugrun as dr  # noqa: E402  (延迟导入：需要它的 STARTUPINFOW/PROCESS_INFORMATION)
    sys.exit(main())
