"""P1「4 路崩溃」· vtable 热替换假说 —— 只读取证探针。

假说：RTSS 复制一份 dxgi 交换链 vtable → 改写 Present 槽 → 换掉对象 vtable 指针；
      多 presenter 线程并发时某线程取到"尚未写完/已失效"的槽 ⇒ 跳到野地址。

本探针要回答：
  ① 崩溃时 [chain+0]（对象 vtable 指针）落在 dxgi 模块内，还是可写私有内存？
  ② 进程内是否存在"dxgi vtable 的副本"，且其 Present 槽被改写成非 dxgi 地址？
  ③ 多次崩溃的 [chain+0] 是否变化（换表迹象）？
  ④ Present(0x38) / Present1(0xB0) 槽位是否被改（静态评估）。

只做观测：不改源码、不注入、不写 git。
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
import attribute as at   # noqa: E402
import capture as cap    # noqa: E402
import debugrun as dr    # noqa: E402
import minidump as md    # noqa: E402

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

MEM_COMMIT = 0x1000
MEM_FREE = 0x10000
MEM_RESERVE = 0x2000
MEM_IMAGE = 0x1000000
MEM_MAPPED = 0x40000
MEM_PRIVATE = 0x20000
PAGE_GUARD = 0x100
PAGE_NOACCESS = 0x01
READABLE = (0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80)
USER_MAX = 0x7FFFFFFF0000


def mbi_of(hp, addr):
    mbi = dr.MEMORY_BASIC_INFORMATION()
    n = k32.VirtualQueryEx(hp, ctypes.c_void_p(addr), ctypes.byref(mbi),
                           ctypes.sizeof(mbi))
    if not n:
        return None
    return mbi


def region_desc(hp, addr):
    mbi = mbi_of(hp, addr)
    if not mbi:
        return None
    state = {MEM_COMMIT: "MEM_COMMIT", MEM_FREE: "MEM_FREE",
             MEM_RESERVE: "MEM_RESERVE"}.get(mbi.State, f"0x{mbi.State:X}")
    typ = {MEM_IMAGE: "MEM_IMAGE", MEM_MAPPED: "MEM_MAPPED",
           MEM_PRIVATE: "MEM_PRIVATE"}.get(mbi.Type, f"0x{mbi.Type:X}")
    return {
        "address": f"0x{addr:016X}",
        "region_base": f"0x{int(mbi.BaseAddress or 0):016X}",
        "region_size": f"0x{int(mbi.RegionSize):X}",
        "allocation_base": f"0x{int(mbi.AllocationBase or 0):016X}",
        "state": state, "type": typ, "protect": f"0x{int(mbi.Protect):X}",
        "writable": bool(int(mbi.Protect) & (0x04 | 0x08 | 0x40 | 0x80)),
    }


def reader_for(hp):
    def rd(addr, n):
        buf = ctypes.create_string_buffer(n)
        got = ctypes.c_size_t(0)
        k32.ReadProcessMemory(hp, ctypes.c_void_p(addr), buf, n,
                              ctypes.byref(got))
        return buf.raw[:got.value] if got.value else b""
    return rd


def preferred_base(path):
    """PE 可选头 ImageBase（用于把磁盘镜像的槽值与运行时重定位后对比）。"""
    try:
        d = open(path, "rb").read(0x400)
        e = struct.unpack_from("<I", d, 0x3C)[0]
        return struct.unpack_from("<Q", d, e + 48)[0]
    except Exception:
        return None


def dxgi_exec_ranges(base, path):
    pe = at.PeInfo.get(path)
    if not pe.ok:
        return []
    return [(base + va, base + va + vsize)
            for va, vsize, _rp, _rs, ex, _nm in pe.sections if ex]


def dump_slots(rd, modmap, vtbl, n=40):
    out = []
    for i in range(n):
        raw = rd(vtbl + i * 8, 8)
        if len(raw) < 8:
            out.append({"slot": i, "off": f"0x{i*8:X}", "value": None})
            continue
        v, = struct.unpack("<Q", raw)
        m = modmap.find(v) if v else None
        ex = False
        if m:
            pe = at.PeInfo.get(m.path)
            ex = bool(pe.ok and pe.is_exec(v - m.base))
        out.append({
            "slot": i, "off": f"0x{i*8:X}", "value": f"0x{v:016X}",
            "in_module": f"{m.name}+0x{v-m.base:X}" if m else None,
            "exec": ex,
            "mem": region_desc_cheap(v) if v else None,
        })
    return out


def region_desc_cheap(addr):
    return None


def find_r9_load(code, callsite_off):
    """在 callsite 之前 96 字节里找 `mov r9, [reg+disp]`（4C 8B /0x08..0x0F modrm）。"""
    start = max(0, callsite_off - 96)
    window = code[start:callsite_off]
    hits = []
    i = 0
    while i < len(window) - 3:
        if window[i] == 0x4C and window[i + 1] == 0x8B:
            modrm = window[i + 2]
            if (modrm & 0x38) == 0x08:
                mod = modrm >> 6
                rm = modrm & 7
                j = i + 3
                sib = None
                disp = 0
                if rm == 4 and j < len(window):
                    sib = window[j]
                    j += 1
                    base_reg = (sib & 7)
                    if (sib & 0x38) == 0x00 and base_reg == 4:
                        base_reg = 12
                    elif (sib & 0x38) == 0x00 and base_reg == 5:
                        base_reg = -1
                else:
                    base_reg = rm
                if mod == 0 and base_reg == 5:
                    base_reg = -2  # RIP 相对
                if mod == 0:
                    pass
                elif mod == 1 and j < len(window):
                    disp = struct.unpack_from("<b", window, j)[0]
                    j += 1
                elif mod == 2 and j + 4 <= len(window):
                    disp = struct.unpack_from("<i", window, j)[0]
                    j += 4
                txt = f"mov r9, [{at._REGS[base_reg] if base_reg >= 0 else ('rip' if base_reg == -2 else '?')}"
                txt += (f"+0x{disp:X}]" if disp else "]")
                hits.append({
                    "at_rva_from_window_start": f"0x{start + i:X}",
                    "bytes": window[i:j].hex(" "),
                    "text": txt,
                    "slot_offset": f"0x{disp:X}" if disp else "0x0",
                })
        i += 1
    return hits


def scan_vtable_copies(hp, rd, modmap, genuine, dxgi_ranges, log=print,
                       budget_s=75.0):
    """扫描进程内存，找 dxgi 交换链 vtable 的副本。

    判据：某处连续 >=8 个 8 字节槽都指向 dxgi 可执行节（真表或其副本/改写版）。
    命中后展开 24 槽并与真表逐槽比对，标出被改写的槽及其目标模块。
    """
    def in_dxgi(v):
        for lo, hi in dxgi_ranges:
            if lo <= v < hi:
                return True
        return False

    CH = 0x400000          # 4MB 分块
    cand = []
    addr = 0
    scanned = 0
    t0 = time.time()
    while addr < USER_MAX:
        if time.time() - t0 > budget_s:
            log(f"[scan] 预算用尽（已扫 {scanned/1048576:.0f} MB）")
            break
        mbi = mbi_of(hp, addr)
        if not mbi:
            break
        base = int(mbi.BaseAddress or 0)
        size = int(mbi.RegionSize)
        if size <= 0:
            break
        prot = int(mbi.Protect)
        ok = (mbi.State == MEM_COMMIT and (prot & READABLE)
              and not (prot & PAGE_GUARD) and prot != PAGE_NOACCESS)
        if ok:
            off = 0
            while off < size:
                if time.time() - t0 > budget_s:
                    break
                n = min(CH, size - off)
                data = rd(base + off, n)
                if data:
                    scanned += len(data)
                    nv = len(data) // 8
                    vals = struct.unpack_from(f"<{nv}Q", data, 0)
                    i = 0
                    while i < nv:
                        if in_dxgi(vals[i]):
                            run = 0
                            j = i
                            while j < nv and in_dxgi(vals[j]):
                                run += 1
                                j += 1
                            # 阈值取 5：真表 18 槽，若仅 Present(槽7) 被换，
                            # 前段只剩 7 连，阈值 8 会漏判
                            if run >= 5:
                                cand.append((base + off + i * 8, run))
                                i = j
                                continue
                        i += 1
                off += n
        addr = base + size

    out = []
    seen = set()
    for a, run in cand:
        if a in seen:
            continue
        seen.add(a)
        slots = dump_slots(rd, modmap, a, 24)
        diff = []
        for k, s in enumerate(slots[:18]):
            gv = genuine[k] if k < len(genuine) else None
            if s["value"] and gv and int(s["value"], 16) != int(gv, 16):
                diff.append({"slot": k, "off": s["off"],
                             "copy_value": s["value"],
                             "copy_in_module": s["in_module"],
                             "genuine_value": gv,
                             "genuine_in_module": genuine_mod.get(k)})
        out.append({"address": f"0x{a:016X}", "region": region_desc(hp, a),
                    "dxgi_run": run, "slots": slots[:18],
                    "diff_vs_genuine": diff})
    log(f"[scan] 扫描 {scanned/1048576:.0f} MB / {time.time()-t0:.1f}s，"
        f"命中 dxgi-vtable 形状 {len(out)} 处")
    return out


genuine_mod = {}


def capture(hp, pid, tid, ecode, eaddr, params, first):
    regs = dr.get_thread_regs(hp, tid) or {}
    rd = reader_for(hp)
    snap = cap.snapshot_modules(pid) or []
    modmap = at.modmap_from_snapshot(snap)
    dxgi = next((m for m in snap if m["name"].lower() == "dxgi.dll"), None)
    fffn = next((m for m in snap if m["name"].lower() == "fff.native.dll"), None)
    rtss = [m for m in snap if "rtss" in m["name"].lower()]

    def in_mod(a):
        m = modmap.find(a) if a else None
        return f"{m.name}+0x{a - m.base:X}" if m else None

    chain = regs.get("rcx")
    vtbl = regs.get("rax")
    rip = regs.get("rip", 0)
    rsp = regs.get("rsp", 0)

    chain_vtbl = None
    if chain:
        raw = rd(chain, 8)
        if len(raw) == 8:
            v, = struct.unpack("<Q", raw)
            chain_vtbl = {"value": f"0x{v:016X}", "in_module": in_mod(v),
                          "equals_rax": (v == vtbl),
                          "mem": region_desc(hp, v)}
    slots = dump_slots(rd, modmap, vtbl, 40) if vtbl else []
    for s in slots:
        s["mem"] = region_desc(hp, int(s["value"], 16)) if s["value"] else None

    # 磁盘镜像比对（重定位校正）
    disk_diff = None
    if vtbl and dxgi:
        pe = at.PeInfo.get(dxgi["path"])
        pbase = preferred_base(dxgi["path"])
        if pe.ok and pbase:
            delta = dxgi["base"] - pbase
            rva = vtbl - dxgi["base"]
            d = []
            for k in range(40):
                db = pe.read_rva(rva + k * 8, 8)
                if len(db) < 8:
                    break
                dv, = struct.unpack("<Q", db)
                lv = int(slots[k]["value"], 16) if slots[k]["value"] else None
                if lv is not None and dv + delta != lv:
                    d.append({"slot": k, "disk": f"0x{dv+delta:016X}",
                              "live": slots[k]["value"]})
            disk_diff = {"preferred_base": f"0x{pbase:X}",
                         "delta": f"0x{delta:X}", "diffs": d}

    # 调用点 r9 来源
    r9_src = None
    if fffn:
        pe = at.PeInfo.get(fffn["path"])
        try:
            raw = open(fffn["path"], "rb").read()
            sec = pe.section_of(0x5CCBB)
            if sec:
                va, _vs, rp, _rs, _ex, _nm = sec
                off = rp + (0x5CCBB - va)
                r9_src = {"callsite_in_module": "FFF.Native.dll+0x5CCBB",
                          "callsite_bytes": raw[off:off + 12].hex(" "),
                          "loads": find_r9_load(raw[max(0, off - 96):off + 12], 96)}
        except Exception as e:
            r9_src = {"error": f"{type(e).__name__}: {e}"}

    # 内存扫描
    genuine = [s["value"] for s in slots]
    for k, s in enumerate(slots):
        genuine_mod[k] = s["in_module"]
    copies = []
    if dxgi:
        ranges = dxgi_exec_ranges(dxgi["base"], dxgi["path"])
        try:
            copies = scan_vtable_copies(hp, rd, modmap, genuine, ranges)
        except Exception as e:
            copies = [{"error": f"{type(e).__name__}: {e}"}]

    # 崩溃点代码是否被内联改写（区别于"换表"的另一种 hook 手法）
    code_vs_disk = None
    m = modmap.find(rip) if rip else None
    if m:
        pe = at.PeInfo.get(m.path)
        pbase = preferred_base(m.path)
        if pe.ok and pbase:
            live = rd(rip, 32)
            disk = pe.read_rva(rip - m.base, 32)
            diffs = [k for k in range(min(len(live), len(disk)))
                     if live[k] != disk[k]]
            code_vs_disk = {"module": m.name, "rva": f"0x{rip - m.base:X}",
                            "live": live.hex(" "), "disk": disk.hex(" "),
                            "diff_offsets": [f"0x{k:X}" for k in diffs],
                            "patched": bool(diffs)}

    # 栈顶（校验 call 的返回地址 = 调用者下一条指令）
    stack_top = []
    if rsp:
        for k in range(8):
            raw = rd(rsp + k * 8, 8)
            if len(raw) < 8:
                break
            v, = struct.unpack("<Q", raw)
            stack_top.append({f"+0x{k*8:X}": f"0x{v:016X}", "in_module": in_mod(v)})

    # 线程数（presenter 线程数的代理指标）
    thread_count = None
    try:
        TH32CS_SNAPTHREAD = 0x00000004

        class THREADENTRY32(ctypes.Structure):
            _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD),
                        ("th32ThreadID", wt.DWORD), ("th32OwnerProcessID", wt.DWORD),
                        ("tpBasePri", ctypes.c_long), ("tpDeltaPri", ctypes.c_long),
                        ("dwFlags", wt.DWORD)]
        snap = k32.CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)
        if snap and snap != ctypes.c_void_p(-1).value:
            te = THREADENTRY32()
            te.dwSize = ctypes.sizeof(te)
            n = 0
            if k32.Thread32First(snap, ctypes.byref(te)):
                while True:
                    if te.th32OwnerProcessID == pid:
                        n += 1
                    if not k32.Thread32Next(snap, ctypes.byref(te)):
                        break
            k32.CloseHandle(ctypes.c_void_p(snap))
            thread_count = n
    except Exception:
        pass

    return {
        "pid": pid, "thread_id": tid, "first_chance": bool(first),
        "thread_count": thread_count,
        "code_vs_disk": code_vs_disk,
        "stack_top": stack_top,
        "exception": {
            "code": f"0x{ecode:08X}", "code_name": md.EXC_NAMES.get(ecode, "?"),
            "address": f"0x{eaddr:016X}", "address_in_module": in_mod(eaddr),
            "ExceptionInformation": [f"0x{p:016X}" for p in params],
            "av_kind": {0: "read", 1: "write", 8: "EXECUTE"}.get(
                params[0], f"?({params[0]})") if params else None,
            "av_target": f"0x{params[1]:016X}" if len(params) > 1 else None,
        },
        "registers": {k: f"0x{v:016X}" for k, v in regs.items()},
        "probe_av_target": dr.probe_address(hp, params[1]) if len(params) > 1 else None,
        "probe_chain": dr.probe_address(hp, chain) if chain else None,
        "probe_rsp": dr.probe_address(hp, rsp) if rsp else None,
        "chain_object_first_0x40": {
            f"+0x{o:02X}": (f"0x{struct.unpack('<Q', rd(chain+o,8))[0]:016X}"
                            if len(rd(chain+o, 8)) == 8 else None)
            for o in range(0, 0x40, 8)} if chain else None,
        "chain_vtable": chain_vtbl,
        "vtable_slots": slots,
        "vtable_mem": region_desc(hp, vtbl) if vtbl else None,
        "dxgi_section_of_vtbl": None,
        "disk_vs_live": disk_diff,
        "instruction_live": at.decode_at(rd(rip, 16)),
        "bytes_live": rd(rip, 16).hex(" "),
        "r9_source": r9_src,
        "r9_value": regs.get("r9"),
        "rtss_modules": [{"name": m["name"], "base": f"0x{m['base']:X}",
                          "path": m["path"]} for m in rtss],
        "vtable_copies": copies,
        "module_count": len(snap),
    }


def run_once(run_idx, seconds, log=print):
    si = dr.STARTUPINFOW()
    si.cb = ctypes.sizeof(si)
    pi = dr.PROCESS_INFORMATION()
    cmd = f'"{EXE}" --multitest "{MEDIA}" 4 {seconds}'
    ok = k32.CreateProcessW(EXE, ctypes.create_unicode_buffer(cmd), None, None,
                            False, DEBUG_ONLY_THIS_PROCESS, None,
                            os.path.dirname(EXE), ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        return {"error": f"CreateProcess 失败 {ctypes.get_last_error()}"}
    hp, pid = pi.hProcess, pi.dwProcessId
    log(f"[run{run_idx}] pid={pid}")
    evbuf = ctypes.create_string_buffer(512)
    t0 = time.time()
    scene = None
    others = []
    while time.time() - t0 < 180:
        if not k32.WaitForDebugEvent(evbuf, 1000):
            continue
        code, dpid, dtid = struct.unpack_from("<III", evbuf, 0)
        cont = DBG_EXCEPTION_NOT_HANDLED
        if code == EXCEPTION_DEBUG_EVENT:
            ecode, = struct.unpack_from("<I", evbuf, 16)
            eaddr, = struct.unpack_from("<Q", evbuf, 32)
            nparam, = struct.unpack_from("<I", evbuf, 40)
            params = list(struct.unpack_from("<15Q", evbuf, 48))[:min(nparam, 15)]
            first, = struct.unpack_from("<I", evbuf, 168)
            if ecode in FATAL and first:
                log(f"[run{run_idx}] 异常 0x{ecode:08X} @0x{eaddr:X} "
                    f"params={[hex(p) for p in params]} tid={dtid}")
                if scene is None:
                    try:
                        scene = capture(hp, pid, dtid, ecode, eaddr, params, first)
                        scene["run"] = run_idx
                        scene["wall"] = time.strftime("%H:%M:%S")
                    except Exception as e:
                        log(f"[run{run_idx}] 采集失败 {type(e).__name__}: {e}")
                        scene = {"error": f"{type(e).__name__}: {e}"}
                    # 现场已冻结取完 → 立即终止，避免二次崩溃/蓝屏风险
                    k32.TerminateProcess(hp, 0)
                    k32.ContinueDebugEvent(dpid, dtid, DBG_CONTINUE)
                    break
                else:
                    others.append({"code": f"0x{ecode:08X}",
                                   "address": f"0x{eaddr:016X}",
                                   "params": [f"0x{p:016X}" for p in params],
                                   "tid": dtid})
                cont = DBG_CONTINUE
        elif code in (CREATE_PROCESS_DEBUG_EVENT, LOAD_DLL_DEBUG_EVENT):
            hf, = struct.unpack_from("<Q", evbuf, 16)
            if hf:
                k32.CloseHandle(ctypes.c_void_p(hf))
            cont = DBG_CONTINUE
        elif code == EXIT_PROCESS_DEBUG_EVENT:
            k32.ContinueDebugEvent(dpid, dtid, DBG_CONTINUE)
            break
        k32.ContinueDebugEvent(dpid, dtid, cont)
    if scene and isinstance(scene, dict):
        scene["other_fatal"] = others
    try:
        k32.TerminateProcess(hp, 0)
    except Exception:
        pass
    k32.CloseHandle(pi.hThread)
    k32.CloseHandle(pi.hProcess)
    return scene or {"run": run_idx, "error": "本次未捕获致命异常"}


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--out", default=os.path.join(HERE, "scenes.json"))
    a = ap.parse_args()

    results = []
    for i in range(1, a.runs + 1):
        r = run_once(i, a.seconds)
        results.append(r)
        if isinstance(r, dict) and r.get("error"):
            print(f"[run{i}] {r['error']}", flush=True)
        else:
            print(f"[run{i}] ok", flush=True)
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(results, f, ensure_ascii=False, indent=2)
    print(f"[done] {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
