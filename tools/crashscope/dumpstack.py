"""从 minidump 里做**真实栈回溯 + 符号化**，回答"故障线程是谁、栈上有什么、chain 是否有效"。

为什么另起一个：`attribute.py` 对 dump 只做**栈扫描**（把栈上像代码指针的 8 字节槽当帧），
它给不出调用链、给不出函数名，也拿不到故障线程的完整寄存器。这里把 minidump 的
CONTEXT + 栈内存 + 模块表**转成 unwind.py 认的 sidecar**，再走 dbghelp StackWalk64 做
真正的展开（按 .pdata/UNWIND_INFO），最后符号化。

用法：
    python dumpstack.py --dump .review_pr/crashdumps/xxx.dmp
    python dumpstack.py --dump a.dmp --dump b.dmp --out .review_pr/crashdumps/analysis.json
    python dumpstack.py --dump old.dmp --tid 81792      # dump 无异常流时手工指定线程
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
import minidump as md  # noqa: E402
import symbolize as S  # noqa: E402
import unwind as uw  # noqa: E402
import attribute as at  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))

# AMD64 CONTEXT 通用寄存器偏移（与 unwind.REGS_ORDER 同源）
CTX_GPR = [("rax", 0x78), ("rcx", 0x80), ("rdx", 0x88), ("rbx", 0x90),
           ("rsp", 0x98), ("rbp", 0xA0), ("rsi", 0xA8), ("rdi", 0xB0),
           ("r8", 0xB8), ("r9", 0xC0), ("r10", 0xC8), ("r11", 0xD0),
           ("r12", 0xD8), ("r13", 0xE0), ("r14", 0xE8), ("r15", 0xF0),
           ("rip", 0xF8)]
CTX_SEG = [("cs", 0x38, 2), ("ds", 0x3A, 2), ("es", 0x3C, 2), ("fs", 0x3E, 2),
           ("gs", 0x40, 2), ("ss", 0x42, 2)]
CTX_EFLAGS = 0x44
CTX_MXCSR = 0x34

# 我们关心的"内核/图形/钩子"分类
HOOK_HINTS = ("rtss", "nvspcap", "nvinject", "overlay", "hook", "detours",
              "gameoverlay", "fraps", "msicontrol", "ep_hook", "winaero")


def _regs_from_context(ctx: bytes) -> dict:
    regs = {}
    for name, off in CTX_GPR:
        if off + 8 <= len(ctx):
            regs[name] = struct.unpack_from("<Q", ctx, off)[0]
    extra = {}
    for name, off, sz in CTX_SEG:
        if off + sz <= len(ctx):
            extra[name] = struct.unpack_from("<H", ctx, off)[0]
    if CTX_EFLAGS + 4 <= len(ctx):
        extra["eflags"] = struct.unpack_from("<I", ctx, CTX_EFLAGS)[0]
    if CTX_MXCSR + 4 <= len(ctx):
        extra["mxcsr"] = struct.unpack_from("<I", ctx, CTX_MXCSR)[0]
    return regs, extra


def _thread_stack_bytes(d: md.Dump, t) -> bytes:
    """从 dump 里取故障线程的栈字节。"""
    if t.stack_rva and t.stack_size:
        return d.data[t.stack_rva: t.stack_rva + t.stack_size]
    if t.stack_size:
        return d.read(t.stack_start, t.stack_size)
    # 退化：栈没进 MemoryList 时按 RSP 往上一页一页捞（可能拿不到）
    return d.read(t.rsp, uw.STACK_READ_MAX)


def build_sidecar(d: md.Dump, tid: int, path: str) -> dict:
    t = next((x for x in d.threads if x.thread_id == tid), None)
    if t is None:
        raise SystemExit(f"dump 里没有线程 {tid}")
    if not t.context_rva or t.context_size < 0x100:
        raise SystemExit(f"线程 {tid} 的 CONTEXT 缺失")
    ctx = d.data[t.context_rva: t.context_rva + t.context_size]
    regs, _extra = _regs_from_context(ctx)
    sdata = _thread_stack_bytes(d, t)
    sc = {
        "registers": {k: f"0x{v:016X}" for k, v in regs.items()},
        "stack_base": f"0x{t.stack_start:016X}" if t.stack_start else None,
        "stack_b64": base64.b64encode(sdata).decode("ascii") if sdata else None,
        "modules": [{"name": m.name, "base": m.base, "size": m.size, "path": m.path}
                    for m in d.modules],
        "thread_id": tid,
        "stack_bytes": len(sdata),
    }
    with open(path, "w", encoding="utf-8") as f:
        json.dump(sc, f)
    return sc


def module_inventory(d: md.Dump) -> list[dict]:
    out = []
    for m in d.modules:
        name = m.name
        low = name.lower()
        out.append({
            "name": name,
            "base": f"0x{m.base:016X}",
            "size": m.size,
            "path": m.path,
            "category": at.classify(m.path),
            "hook_hint": any(h in low for h in HOOK_HINTS),
        })
    return out


def sym_path() -> str:
    """符号路径：本地缓存（dxgi.pdb 平铺在这）+ FFF.Native.pdb 所在目录。"""
    parts = [S.SYMCACHE]
    pdb_dir = os.path.join(REPO, "third_party", "fff_project", "FFF.Native",
                           "x64", "Release")
    if os.path.isdir(pdb_dir):
        parts.append(pdb_dir)
    return ";".join(parts)


# --------------------------------------------------------------------------
# 把 dump 里的模块镜像"贴"进本进程，让 dbghelp 能读到 .pdata（x64 回溯的前提）
#
# 为什么必须这么做：离线 StackWalk64 的 hProcess 只能是本进程，而 dbghelp 查
# RUNTIME_FUNCTION(.pdata) 时是**直接按基址读进程内存**，不读磁盘。镜像不在本进程
# 里时它查不到展开信息，于是退化成"把 [rsp] 当返回地址、rsp+=8"的兜底路径——
# 表现为第 0→1 帧碰巧正确（call 刚压的返回地址），从第 2 帧起全是栈上数据（堆指针）。
# --------------------------------------------------------------------------
MEM_COMMIT = 0x1000
MEM_RESERVE = 0x2000
PAGE_READWRITE = 0x04
_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.VirtualAlloc.restype = ctypes.c_void_p
_k32.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD]
_k32.VirtualFree.argtypes = [ctypes.c_void_p, ctypes.c_size_t, wt.DWORD]


class _MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p),
                ("AllocationProtect", wt.DWORD), ("__a1", wt.DWORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD),
                ("Protect", wt.DWORD), ("Type", wt.DWORD), ("__a2", wt.DWORD)]


_k32.VirtualQuery.argtypes = [ctypes.c_void_p, ctypes.POINTER(_MBI), ctypes.c_size_t]


def _pe_layout(data: bytes):
    """最小 PE 解析：返回 (size_of_image, size_of_headers, sections)。"""
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise ValueError("PE 签名缺失")
    coff = e_lfanew + 4
    nsec, = struct.unpack_from("<H", data, coff + 2)
    opt_size, = struct.unpack_from("<H", data, coff + 16)
    opt = coff + 20
    size_of_image, = struct.unpack_from("<I", data, opt + 56)
    size_of_headers, = struct.unpack_from("<I", data, opt + 60)
    sec_off = opt + opt_size
    secs = []
    for i in range(nsec):
        o = sec_off + i * 40
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, o + 8)
        secs.append({"vsize": vsize, "vaddr": vaddr, "rawsize": rawsize,
                     "rawptr": rawptr})
    return size_of_image, size_of_headers, secs


def map_image(path: str, base: int, size: int) -> bool:
    """把 PE 镜像按节贴到本进程的 base 处。返回是否成功。

    ⚠ 分配长度不能直接用 dump 里的 SizeOfImage：节的原始数据可能超出它
    （实测 3FCompare.Core.dll 的节 vaddr+rawsize 比 SizeOfImage 大 0x20），
    按 SizeOfImage 分配会当场写越界。
    """
    try:
        with open(path, "rb") as f:
            data = f.read()
        soi, soh, secs = _pe_layout(data)
    except Exception:
        return False
    need = max([soi, soh] + [s["vaddr"] + max(s["vsize"], s["rawsize"])
                             for s in secs])
    need = (need + 0xFFFF) & ~0xFFFF
    mbi = _MBI()
    if _k32.VirtualQuery(ctypes.c_void_p(base), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        if mbi.State != 0x10000:  # MEM_FREE
            return False
    p = _k32.VirtualAlloc(ctypes.c_void_p(base), need, MEM_RESERVE | MEM_COMMIT,
                          PAGE_READWRITE)
    if not p:
        return False
    n = min(soh, len(data))
    ctypes.memmove(base, data[:n], n)
    for s in secs:
        n = min(s["vsize"] or s["rawsize"], s["rawsize"])
        if n and s["rawptr"] + n <= len(data):
            ctypes.memmove(base + s["vaddr"], data[s["rawptr"]:s["rawptr"] + n], n)
    return True


PAGE_NOACCESS = 0x01
PAGE_GUARD = 0x100


class MappedMemory:
    """离线内存模型：栈来自 sidecar；其余地址直接读本进程（模块镜像已被贴进来）。

    为什么不复用 unwind.OfflineMemory：它是"按文件偏移"读模块的（addr-base 当文件偏移），
    对 .pdata 这类按 RVA 定位的数据是错的；而且 dump 里的镜像基址与磁盘文件的节布局
    并不等价。贴图之后本进程就是最准确的镜像，直接读本进程即可。
    """

    def __init__(self, sbase: int, sdata: bytes):
        self.sbase, self.sdata = sbase, sdata

    def read(self, addr: int, size: int) -> bytes:
        if self.sbase and self.sbase <= addr < self.sbase + len(self.sdata):
            n = min(size, self.sbase + len(self.sdata) - addr)
            return self.sdata[addr - self.sbase: addr - self.sbase + n]
        mbi = _MBI()
        if not _k32.VirtualQuery(ctypes.c_void_p(addr), ctypes.byref(mbi),
                                 ctypes.sizeof(mbi)):
            return b""
        if mbi.State != 0x1000 or (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)):
            return b""
        n = min(size, (mbi.BaseAddress or 0) + mbi.RegionSize - addr)
        if n <= 0:
            return b""
        return ctypes.string_at(addr, n)


def unwind_offline_mapped(sidecar_path: str, sympath: str | None = None,
                          max_frames: int = 64, log=None) -> dict:
    """离线回溯：栈走 sidecar 回调，模块镜像贴进本进程（.pdata 才能被读到）。"""
    with open(sidecar_path, encoding="utf-8") as f:
        sc = json.load(f)
    regs = {k: int(v, 16) for k, v in (sc.get("registers") or {}).items()}
    sbase = int(sc["stack_base"], 16) if sc.get("stack_base") else 0
    sdata = base64.b64decode(sc["stack_b64"]) if sc.get("stack_b64") else b""
    modules = sc.get("modules") or []

    mem = MappedMemory(sbase, sdata)
    mapped, failed = [], []

    @uw.READPROC
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

    raw, ptr, off = uw.build_context(regs)
    sz = S.Symbolizer(sympath=sympath or uw.S.LOCAL_ONLY_SYMPATH)
    total = 0
    try:
        for m in modules:
            path = m.get("path")
            if not path or not os.path.isfile(path):
                failed.append((m.get("name"), "文件不存在"))
                continue
            b, s = int(m["base"]), int(m.get("size") or 0)
            if s > 64 * 1024 * 1024 or total + s > 512 * 1024 * 1024:
                failed.append((m.get("name"), "体积超限，跳过贴图"))
            elif map_image(path, b, s):
                mapped.append(m.get("name"))
                total += s
            else:
                failed.append((m.get("name"), "贴图失败(地址占用或PE异常)"))
            try:
                sz.load(path, base=b, size=s, name=m.get("name"))
            except S.SymError as e:
                failed.append((m.get("name"), f"符号加载: {e}"))
        if log:
            log(f"  [dumpstack] 已贴图 {len(mapped)}/{len(modules)} 个模块镜像"
                f"（{total/1048576:.0f} MB）")
        me = _k32.GetCurrentProcess()
        frames = uw._walk(me, me, sz, ptr, max_frames=max_frames,
                          read_routine=ctypes.cast(_read, ctypes.c_void_p), log=log)
    finally:
        sz.close()
        for m in modules:
            if m.get("name") in mapped and m.get("path") and os.path.isfile(m["path"]):
                _k32.VirtualFree(ctypes.c_void_p(int(m["base"])), 0, 0x8000)
    return {"ok": bool(frames), "engine": "dbghelp StackWalk64 (offline + 镜像贴图)",
            "frames": frames, "registers": sc.get("registers"),
            "mapped_modules": len(mapped), "skipped": failed[:20]}


def analyze(dump_path: str, tid: int | None = None, log=print) -> dict:
    d = md.Dump(dump_path)
    res = {
        "dump": os.path.abspath(dump_path),
        "dump_bytes": len(d.data),
        "dump_timestamp": d.timestamp,
        "dump_flags": f"0x{d.flags:X}",
        "streams": {f"0x{k:X}": {"rva": v[0], "size": v[1]} for k, v in d.streams.items()},
        "module_count": len(d.modules),
        "thread_count": len(d.threads),
        "exception": None,
        "faulting_thread": None,
        "unwind": None,
        "modules": None,
    }

    e = d.exception
    if e is not None:
        res["exception"] = {
            "code": f"0x{e.code:08X}",
            "code_name": e.code_name,
            "flags": f"0x{e.flags:X}",
            "address": f"0x{e.address:016X}",
            "n_params": len(e.params),
            "ExceptionInformation": [f"0x{p:016X}" for p in e.params],
            "av_kind": e.av_kind,
            "av_target": f"0x{e.av_target:016X}" if e.av_target is not None else None,
            "thread_id": e.thread_id,
        }
        if tid is None:
            tid = e.thread_id

    if tid is None:
        # 没有异常流：退化为"栈上带 dxgi/FFF 的那个线程"不可靠，必须人工给 --tid
        raise SystemExit(f"{dump_path}: 无异常流且未指定 --tid，无法定位故障线程")

    t = next((x for x in d.threads if x.thread_id == tid), None)
    if t is None:
        raise SystemExit(f"{dump_path}: 无线程 {tid}")

    ctx = d.data[t.context_rva: t.context_rva + t.context_size]
    regs, extra = _regs_from_context(ctx)
    res["faulting_thread"] = {
        "thread_id": tid,
        "stack_start": f"0x{t.stack_start:016X}",
        "stack_size": t.stack_size,
        "stack_captured": bool(_thread_stack_bytes(d, t)),
        "registers": {k: f"0x{v:016X}" for k, v in regs.items()},
        "segments": extra,
    }

    # ---- 真实回溯 ----
    sc_path = os.path.join(HERE, "scenes", f"fromdump_{os.path.basename(dump_path)}.json")
    os.makedirs(os.path.dirname(sc_path), exist_ok=True)
    build_sidecar(d, tid, sc_path)
    res["sidecar"] = sc_path
    if log:
        log(f"  [dumpstack] sidecar -> {sc_path}")
    r = unwind_offline_mapped(sc_path, sympath=sym_path(), log=log)
    res["unwind"] = r

    res["modules"] = module_inventory(d)
    return res


def print_report(res: dict):
    print("=" * 78)
    print(f"dump：{res['dump']}  ({res['dump_bytes']:,} 字节, flags={res['dump_flags']})")
    print(f"模块 {res['module_count']} 个，线程 {res['thread_count']} 个")
    print(f"流：{', '.join(res['streams'].keys())}")
    e = res.get("exception")
    if e:
        print("\n【异常记录】")
        print(f"  ExceptionCode      : {e['code']}  {e['code_name']}")
        print(f"  ExceptionAddress   : {e['address']}")
        print(f"  ExceptionFlags     : {e['flags']}")
        print(f"  NumberParameters   : {e['n_params']}")
        for i, p in enumerate(e["ExceptionInformation"]):
            tag = ""
            if e["code"] == "0xC0000005":
                tag = "  <- 读写性质" if i == 0 else ("  <- 试图访问的地址" if i == 1 else "")
            print(f"    ExceptionInformation[{i}] = {p}{tag}")
        print(f"  → 访问性质：{e['av_kind']}   目标地址：{e['av_target']}")
        print(f"  ExceptionThread    : tid={e['thread_id']}")
    ft = res.get("faulting_thread") or {}
    print(f"\n【故障线程】tid={ft.get('thread_id')}  栈 {ft.get('stack_start')} "
          f"size={ft.get('stack_size')}  抓到栈内存={ft.get('stack_captured')}")
    for k, v in (ft.get("registers") or {}).items():
        print(f"    {k:<4} = {v}")
    seg = ft.get("segments") or {}
    if seg:
        print("    " + "  ".join(f"{k}={v if not isinstance(v, int) or v < 0x10000 else hex(v)}"
                                for k, v in seg.items()))
    uw_res = res.get("unwind") or {}
    print(f"\n【故障线程原生栈（{uw_res.get('engine')}）】")
    if not uw_res.get("ok"):
        print("  ⚠ 回溯失败：", uw_res.get("reason") or "无帧")
    for f in (uw_res.get("frames") or []):
        disp = f"+0x{f['displacement']:X}" if f.get("displacement") else ""
        loc = f"{f['source']}:{f['line']}" if f.get("source") else ""
        print(f"  #{f['frame']:<2} {f.get('module') or '?'}"
              f"{('+0x%X' % f['rva']) if f.get('rva') is not None else ''}"
              f"  {f.get('symbol') or '(无符号)'}{disp}   [{loc}]")
    print("\n【模块清单中的可疑项（钩子/覆盖层）】")
    hooks = [m for m in (res.get("modules") or []) if m["hook_hint"]]
    for m in hooks:
        print(f"  {m['name']:<28} {m['base']}  {m['path']}")
    if not hooks:
        print("  （无）")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dump", action="append", required=True)
    ap.add_argument("--tid", type=int, default=None)
    ap.add_argument("--out", default=None)
    a = ap.parse_args()
    print(f"dbghelp: {S.DBG_SOURCE}")
    print(f"符号路径: {sym_path()}\n")
    all_res = []
    for dp in a.dump:
        print(f"\n### 分析 {dp}")
        res = analyze(dp, tid=a.tid)
        print_report(res)
        all_res.append(res)
    if a.out:
        os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
        with open(a.out, "w", encoding="utf-8") as f:
            json.dump(all_res if len(all_res) > 1 else all_res[0], f,
                      ensure_ascii=False, indent=2)
        print(f"\n-> {a.out}")


if __name__ == "__main__":
    main()
