"""崩溃归属判定：把异常地址落到具体模块；野地址时用崩溃线程栈反推"崩之前在执行谁的代码"。

两种输入模式：
  1. minidump（推荐，信息最全）：--dump <file.dmp>
       异常码/异常地址/参数 + 模块表 + 崩溃线程 CONTEXT + 栈内存
  2. Report.wer（无 dump 时的降级路径）：--wer <归档目录>
       故障模块 + 模块内 RVA（WER 已算好），可反查磁盘上的 DLL 直接译码故障指令

归属判定：
  * 异常地址落在某模块 [Base, Base+Size) -> 直接归属
  * 不落在任何模块（execute AV 跳到野地址的典型情形）-> 逐 8 字节扫描崩溃线程栈，
    凡落在模块区间内的值即为候选返回地址/函数指针；再用 PE 节表判断是否落在
    可执行节，以剔除纯数据指针误报；最后按栈序输出"模块序列"。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import minidump as md  # noqa: E402
import wer as wermod  # noqa: E402

# ───────────────────────── 模块分类 ─────────────────────────

_CAT_RULES = [
    ("kernel-ours", lambda n: n in ("fff.native.dll",)),
    ("app", lambda n: n.startswith("3fcompare")),
    ("overlay-hook", lambda n: n in ("rtsshooks64.dll", "nvspcap64.dll")
        or n.startswith("rtss") or "gameoverlay" in n or "discordhook" in n),
    ("gpu-driver", lambda n: n.startswith("nv") or n.startswith("ati")
        or n.startswith("amd") or n.startswith("ig")),
    ("graphics-ms", lambda n: n in ("dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d10.dll",
                                    "dxcore.dll", "d2d1.dll", "dcomp.dll", "dwmapi.dll",
                                    "d3dcompiler_47.dll", "opengl32.dll", "vulkan-1.dll",
                                    "nvoglv64.dll")),
    ("media-ffmpeg", lambda n: n.startswith("av") and n.endswith(".dll")
        or n.startswith("swresample") or n.startswith("swscale")),
    ("ui-avalonia", lambda n: n.startswith("avalonia") or n.startswith("skiasharp")
        or n.startswith("libskiasharp") or n.startswith("harfbuzz")
        or n.startswith("libharfbuzz")),
    ("runtime-dotnet", lambda n: n.startswith("system.") or n in (
        "coreclr.dll", "clrjit.dll", "hostfxr.dll", "hostpolicy.dll",
        "system.private.corelib.dll", "mscorlib.dll")),
    ("os", lambda n: n in (
        "ntdll.dll", "kernel32.dll", "kernelbase.dll", "user32.dll", "gdi32.dll",
        "gdi32full.dll", "win32u.dll", "ole32.dll", "combase.dll", "oleaut32.dll",
        "ucrtbase.dll", "msvcp_win.dll", "msvcrt.dll", "shcore.dll", "shell32.dll",
        "rpcrt4.dll", "sechost.dll", "advapi32.dll", "imm32.dll", "bcrypt.dll",
        "crypt32.dll", "setupapi.dll", "shlwapi.dll", "winmm.dll", "version.dll",
        "windows.ui.dll", "dwrite.dll", "uxtheme.dll", "profapi.dll",
        "kernel.appcore.dll", "ws2_32.dll", "imagehlp.dll", "clbcatq.dll",
        "msctf.dll", "textinputframework.dll", "oleacc.dll", "wintypes.dll",
        "windows.storage.dll", "twinapi.dll", "twinapi.appcore.dll",
        "dataexchange.dll", "directxdatabasehelper.dll", "windows.staterepositorycore.dll",
        "d3dcompiler_47.dll")),
]


def classify(path: str) -> str:
    n = (path or "").replace("\\", "/").rsplit("/", 1)[-1].lower()
    for cat, pred in _CAT_RULES:
        try:
            if pred(n):
                return cat
        except Exception:
            pass
    return "other"


# ───────────────────────── PE 解析（判定指针是否指向可执行代码） ─────────────────────────

class PeInfo:
    _cache: dict = {}

    def __init__(self, path: str):
        self.path = path
        self.ok = False
        self.sections = []          # (va, vsize, rawptr, rawsize, is_exec)
        self.timestamp = 0
        self._raw = b""
        try:
            self._raw = open(path, "rb").read()
            self._parse()
            self.ok = True
        except Exception:
            self.ok = False

    @classmethod
    def get(cls, path: str) -> "PeInfo":
        if path not in cls._cache:
            cls._cache[path] = PeInfo(path)
        return cls._cache[path]

    def _parse(self):
        d = self._raw
        if d[:2] != b"MZ":
            raise ValueError("not PE")
        e_lfanew = struct.unpack_from("<I", d, 0x3C)[0]
        if d[e_lfanew:e_lfanew + 4] != b"PE\0\0":
            raise ValueError("no PE sig")
        coff = e_lfanew + 4
        nsec, = struct.unpack_from("<H", d, coff + 2)
        size_opt, = struct.unpack_from("<H", d, coff + 16)
        self.timestamp, = struct.unpack_from("<I", d, coff + 4)
        opt = coff + 20
        sec_off = opt + size_opt
        for i in range(nsec):
            p = sec_off + i * 40
            name = d[p:p + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", d, p + 8)
            chars, = struct.unpack_from("<I", d, p + 36)
            is_exec = bool(chars & 0x20000000)
            self.sections.append((va, max(vsize, rawsize), rawptr, rawsize, is_exec, name))

    def section_of(self, rva: int):
        for va, vsize, rawptr, rawsize, is_exec, name in self.sections:
            if va <= rva < va + vsize:
                return (va, vsize, rawptr, rawsize, is_exec, name)
        return None

    def is_exec(self, rva: int) -> bool:
        s = self.section_of(rva)
        return bool(s and s[4])

    def read_rva(self, rva: int, n: int = 32) -> bytes:
        s = self.section_of(rva)
        if not s:
            return b""
        va, _vsize, rawptr, rawsize, _ex, _nm = s
        off = rawptr + (rva - va)
        if off >= len(self._raw):
            return b""
        return self._raw[off: off + n]


# ───────────────────────── 极简 x86-64 间接跳转译码 ─────────────────────────

_REGS = ["rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi",
         "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"]
_REGS8 = ["al", "cl", "dl", "bl", "spl", "bpl", "sil", "dil",
          "r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b", "r15b"]
_REGS32 = ["eax", "ecx", "edx", "ebx", "esp", "ebp", "esi", "edi",
           "r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d", "r15d"]


def _rm_str(modrm: int, buf: bytes, i: int, rex: int):
    """返回 (操作数描述, 下一字节下标)。仅覆盖常见寻址。"""
    mod = modrm >> 6
    rm = modrm & 7
    rex_b = rex & 1
    if mod == 3:
        return _REGS[rm + (8 if rex_b else 0)], i
    base = ""
    if rm == 4:
        if i >= len(buf):
            return "?", i
        sib = buf[i]
        i += 1
        idx = ((sib >> 3) & 7) + (8 if (rex >> 1) & 1 else 0)
        bs = (sib & 7) + (8 if rex_b else 0)
        scale = 1 << (sib >> 6)
        parts = []
        if idx != 4:
            parts.append(_REGS[idx] + (f"*{scale}" if scale > 1 else ""))
        if (sib & 7) == 5 and mod == 0:
            parts.append("rip")
        else:
            parts.append(_REGS[bs])
        base = "+".join(parts)
    elif rm == 5 and mod == 0:
        base = "rip"
    else:
        base = _REGS[rm + (8 if rex_b else 0)]
    disp = 0
    if mod == 0 and rm == 5:
        disp, = struct.unpack_from("<i", buf, i)
        i += 4
    elif mod == 1:
        disp, = struct.unpack_from("<b", buf, i)
        i += 1
    elif mod == 2:
        disp, = struct.unpack_from("<i", buf, i)
        i += 4
    if disp:
        return f"[{base}{disp:+#x}]", i
    return f"[{base}]", i


def decode_at(buf: bytes):
    """极简译码：覆盖常见指令，够判断故障指令在做什么（尤其间接跳转/ud2/int3）。"""
    if not buf:
        return None
    i = 0
    rex = 0
    opsz = False
    while i < len(buf) and 0x40 <= buf[i] <= 0x4F:
        rex = buf[i] - 0x40
        i += 1
    while i < len(buf) and buf[i] in (0x66, 0x67, 0x2E, 0x3E, 0x26, 0x36, 0x64, 0x65):
        if buf[i] == 0x66:
            opsz = True
        i += 1
    if i >= len(buf):
        return None
    op = buf[i]
    i += 1

    # 双字节
    if op == 0x0F and i < len(buf):
        op2 = buf[i]
        i += 1
        if op2 == 0x0B:
            return "ud2   <-- 显式非法指令（STATUS_ILLEGAL_INSTRUCTION 典型来源）"
        if op2 == 0x05:
            return "syscall"
        if op2 == 0x1F and i < len(buf):
            modrm = buf[i]
            i += 1
            operand, i = _rm_str(modrm, buf, i, rex)
            return f"nop {operand}"
        if op2 in (0x10, 0x11, 0x28, 0x29, 0x6E, 0x7E, 0x7F, 0x6F, 0x57, 0x54):
            return f"SSE/AVX 指令 0F {op2:02X}（未细译）"
        return f"0F {op2:02X}（未译码）"

    # 单字节
    if 0x50 <= op <= 0x57:
        return f"push {_REGS[(op - 0x50) + (8 if rex & 1 else 0)]}"
    if 0x58 <= op <= 0x5F:
        return f"pop {_REGS[(op - 0x58) + (8 if rex & 1 else 0)]}"
    if op == 0xC3:
        return "ret"
    if op == 0xCC:
        return "int3  <-- 断点/调试陷阱"
    if op == 0x90:
        return "nop"
    if op == 0xE8:
        rel, = struct.unpack_from("<i", buf, i) if i + 4 <= len(buf) else (0,)
        return f"call rel32 ({rel:+#x})"
    if op == 0xE9:
        rel, = struct.unpack_from("<i", buf, i) if i + 4 <= len(buf) else (0,)
        return f"jmp rel32 ({rel:+#x})"
    if op in (0x88, 0x89, 0x8A, 0x8B, 0x8D, 0x39, 0x3B, 0x85, 0x31, 0x33, 0x21, 0x23):
        if i >= len(buf):
            return None
        modrm = buf[i]
        i += 1
        reg = ((modrm >> 3) & 7) + (8 if (rex >> 2) & 1 else 0)
        operand, i = _rm_str(modrm, buf, i, rex)
        if op in (0x88, 0x89):
            return f"mov {operand}, {_REGS[reg] if not opsz else _REGS32[reg]}"
        if op in (0x8A, 0x8B):
            return f"mov {_REGS32[reg] if opsz else _REGS[reg]}, {operand}"
        if op == 0x8D:
            return f"lea {_REGS[reg]}, {operand}"
        if op in (0x39, 0x3B):
            return f"cmp {'r/m, reg' if op == 0x39 else 'reg, r/m'}  ({_REGS[reg]} , {operand})"
        if op == 0x85:
            return f"test {operand}, {_REGS[reg]}"
        return f"{'xor' if op in (0x31, 0x33) else 'and'} {operand}, {_REGS[reg]}"
    if op in (0xC6, 0xC7) and i < len(buf):
        modrm = buf[i]
        i += 1
        operand, i = _rm_str(modrm, buf, i, rex)
        n = 1 if op == 0xC6 else 4
        imm = buf[i:i + n].hex()
        return f"mov {operand}, 0x{imm}"
    if op == 0xFF and i < len(buf):
        modrm = buf[i]
        i += 1
        reg = (modrm >> 3) & 7
        operand, _ = _rm_str(modrm, buf, i, rex)
        mn = {2: "call", 3: "call far", 4: "jmp", 5: "jmp far", 6: "push",
              0: "inc", 1: "dec"}.get(reg)
        if mn:
            tag = "  <-- 间接跳转（经坏指针跳到野地址的典型形态）" if reg in (2, 3, 4, 5) else ""
            return f"{mn} {operand}{tag}"
        return f"ff /{reg} {operand}"
    return f"opcode 0x{op:02x}（未译码）"


# ───────────────────────── 栈扫描 ─────────────────────────

def scan_stack(modmap, rsp: int, stack_base: int, stack_data: bytes,
               max_slots: int = 512):
    """逐 8 字节扫描崩溃线程栈，返回命中模块的槽位列表（按栈序）。

    modmap 只需提供 .find(addr) -> Module|None（minidump.ModuleMap 或等价物），
    这样 minidump 路径与实时调试器路径可以共用同一套判定口径。
    """
    hits = []
    if not stack_data:
        return hits
    n = min(len(stack_data) // 8, max_slots)
    for k in range(n):
        slot = stack_base + k * 8
        v, = struct.unpack_from("<Q", stack_data, k * 8)
        if v < 0x10000:
            continue
        m = modmap.find(v)
        if m:
            rva = v - m.base
            pe = PeInfo.get(m.path) if m.path else None
            code = bool(pe and pe.ok and pe.is_exec(rva))
            hits.append({
                "slot": slot,
                "from_rsp": slot - rsp if rsp else None,
                "value": v,
                "module": m.name,
                "module_path": m.path,
                "category": classify(m.path),
                "rva": rva,
                "is_code_ptr": code,
            })
    return hits


def modmap_from_snapshot(modules: list):
    """把 capture.py 的模块快照转成与 dump 模块表同构的 ModuleMap。"""
    mods = []
    for m in modules:
        try:
            mods.append(md.Module(base=int(m["base"]), size=int(m["size"]),
                                  path=m.get("path") or m.get("name") or ""))
        except Exception:
            continue
    return md.ModuleMap(mods)


def decode_call_at(buf: bytes, addr: int):
    """尝试把 buf[0:] 译成一条 call 指令。

    返回 {"text", "length", "ptr_addr"}；ptr_addr 仅当是 RIP 相对间接调用时给出
    （即该处存放被调用函数指针的内存地址），否则为 None。
    """
    if not buf:
        return None
    i = 0
    rex = 0
    while i < len(buf) and 0x40 <= buf[i] <= 0x4F:
        rex = buf[i] - 0x40
        i += 1
    if i >= len(buf):
        return None
    op = buf[i]
    # call rel32
    if op == 0xE8 and i + 5 <= len(buf):
        rel, = struct.unpack_from("<i", buf, i + 1)
        tgt = addr + i + 5 + rel
        return {"text": f"call 0x{tgt:016X}（直接调用）", "length": i + 5, "ptr_addr": None}
    if op == 0xFF and i + 1 < len(buf):
        modrm = buf[i + 1]
        reg = (modrm >> 3) & 7
        mod = modrm >> 6
        rm = modrm & 7
        if reg not in (2, 3):
            return None
        # RIP 相对：FF 15/25 disp32
        if mod == 0 and rm == 5 and i + 6 <= len(buf):
            disp, = struct.unpack_from("<i", buf, i + 2)
            ptr = addr + i + 6 + disp
            return {"text": f"call qword ptr [rip{disp:+#x}]  -> 指针位于 0x{ptr:016X}",
                    "length": i + 6, "ptr_addr": ptr}
        # 寄存器间接：FF D0..D7 / FF 50+disp8 等
        base = _REGS[rm + (8 if rex & 1 else 0)] if mod == 3 else None
        if mod == 3 and base:
            return {"text": f"call {base}（寄存器间接）", "length": i + 2, "ptr_addr": None}
        if mod == 1 and i + 3 <= len(buf):
            disp = struct.unpack_from("<b", buf, i + 2)[0]
            r = _REGS[rm + (8 if rex & 1 else 0)]
            return {"text": f"call qword ptr [{r}{disp:+#x}]（寄存器间接）",
                    "length": i + 3, "ptr_addr": None}
        if mod == 2 and i + 6 <= len(buf):
            disp, = struct.unpack_from("<i", buf, i + 2)
            r = _REGS[rm + (8 if rex & 1 else 0)]
            return {"text": f"call qword ptr [{r}{disp:+#x}]（寄存器间接）",
                    "length": i + 6, "ptr_addr": None}
    return None


def find_callsite(reader, ret_addr: int, max_back: int = 16, regs: dict | None = None,
                  target: int | None = None):
    """在返回地址之前若干字节内找出可能的 call 指令，并用寄存器值消歧。

    ⚠ 不能取"最短匹配"：x86 变长指令下，长指令的尾巴常常能凑出一条合法的短指令。
      例如 `41 FF D1`(call r9, 3 字节) 的后两字节 `FF D1` 也译得出 `call rcx`(2 字节)。
      这里改为收集全部候选，再按"间接操作数寄存器的当前值 == 异常地址"来裁决，
      拿不到寄存器时退回最长候选并显式标注不确定。
    """
    cands = []
    for back in range(2, max_back + 1):
        start = ret_addr - back
        buf = reader(start, back + 6)
        if len(buf) < back:
            continue
        info = decode_call_at(buf, start)
        if info and info["length"] == back:
            info["site"] = start
            info["raw"] = buf[:back].hex(" ")
            cands.append(info)
    if not cands:
        return None, []
    if target is not None and regs:
        # 优先：间接操作数寄存器当前值恰好等于故障地址 —— 这条几乎可以确定是真凶
        for c in cands:
            if "寄存器间接" not in c["text"]:
                continue
            m = re.search(r"call\s+([a-z][a-z0-9]*)", c["text"])
            if m and m.group(1) in regs and regs[m.group(1)] == target:
                c["selected_by"] = f"寄存器 {m.group(1)} 的值等于故障地址"
                return c, cands
        # 次优：RIP 相对间接调用，指针槽里存的就是故障地址
        for c in cands:
            if c.get("ptr_addr"):
                raw = reader(c["ptr_addr"], 8)
                if len(raw) == 8:
                    pv, = struct.unpack("<Q", raw)
                    c["pointer_value"] = pv
                    if pv == target:
                        c["selected_by"] = "指针槽内容等于故障地址"
                        return c, cands
    best = max(cands, key=lambda c: c["length"])
    best["selected_by"] = "最长候选（无寄存器佐证，不确定）"
    return best, cands


def collapse(hits: list):
    """把栈命中压缩成模块序列：连续同模块合并计数。"""
    seq = []
    for h in hits:
        if seq and seq[-1]["module"] == h["module"]:
            seq[-1]["count"] += 1
            if h["is_code_ptr"]:
                seq[-1]["code_hits"] += 1
            seq[-1]["rva_last"] = h["rva"]
        else:
            seq.append({"module": h["module"], "category": h["category"],
                        "count": 1, "code_hits": 1 if h["is_code_ptr"] else 0,
                        "rva_first": h["rva"], "rva_last": h["rva"],
                        "first_slot": h["slot"], "first_from_rsp": h["from_rsp"]})
    return seq


# ───────────────────────── 归属 ─────────────────────────

def build_stack_report(modmap, rsp: int, stack_base: int, stack_data: bytes):
    """栈扫描 + 汇总，供 minidump 路径与实时调试器路径共用，避免两条路结论口径漂移。"""
    hits = scan_stack(modmap, rsp, stack_base, stack_data)
    seq = collapse(hits)
    return {
        "rsp": f"0x{rsp:016X}",
        "stack_base": f"0x{stack_base:016X}",
        "stack_bytes": len(stack_data),
        "slots_scanned": min(len(stack_data) // 8, 512),
        "total_module_hits": len(hits),
        "sequence": seq,
        "top_hits": hits[:48],
        "modules_present": sorted({h["module"] for h in hits}),
        "categories_present": sorted({h["category"] for h in hits}),
        "hook_modules_on_stack": sorted({h["module"] for h in hits
                                         if h["category"] == "overlay-hook"}),
        "kernel_ours_on_stack": sorted({h["module"] for h in hits
                                        if h["category"] == "kernel-ours"}),
    }


def resolve_module_path_for_wer(name: str, loaded: list):
    """在 Report.wer 的 LoadedModule 列表里找到该模块的磁盘路径。"""
    low = name.lower()
    for p in loaded:
        if p.replace("\\", "/").rsplit("/", 1)[-1].lower() == low:
            return p
    for base in (r"C:\Windows\System32", r"C:\Windows\SYSTEM32", r"C:\Windows"):
        cand = os.path.join(base, name)
        if os.path.exists(cand):
            return cand
    return None


def analyze_dump(path: str, run: dict | None = None):
    d = md.Dump(path)
    exc = d.exception
    res = {
        "source": "minidump",
        "dump": path,
        "dump_bytes": len(d.data),
        "module_count": len(d.modules),
        "thread_count": len(d.threads),
        "exception": None,
        "attribution": None,
        "stack_scan": None,
    }
    if exc is None:
        res["note"] = "dump 中没有异常流（可能不是崩溃转储）"
        return res

    t = d.crash_thread()
    res["exception"] = {
        "code": f"0x{exc.code:08X}",
        "code_name": exc.code_name,
        "address": f"0x{exc.address:016X}",
        "params": [f"0x{p:X}" for p in exc.params],
        "av_kind": exc.av_kind,
        "av_target": f"0x{exc.av_target:016X}" if exc.av_target is not None else None,
        "thread_id": exc.thread_id,
        "rsp": f"0x{t.rsp:016X}" if t else None,
        "rip": f"0x{t.rip:016X}" if t else None,
    }

    # ① 异常地址归属
    m = d.modules.find(exc.address)
    if m:
        rva = exc.address - m.base
        pe = PeInfo.get(m.path) if m.path else None
        res["attribution"] = {
            "kind": "in_module",
            "module": m.name,
            "module_path": m.path,
            "category": classify(m.path),
            "rva": f"0x{rva:X}",
            "rva_int": rva,
            "base": f"0x{m.base:016X}",
            "size": f"0x{m.size:X}",
            "in_exec_section": bool(pe and pe.ok and pe.is_exec(rva)),
            "instruction": decode_at(pe.read_rva(rva, 16)) if pe and pe.ok else None,
            "pe_timestamp_match": (pe.timestamp == m.timestamp) if pe and pe.ok else None,
        }
    else:
        # 野地址：找最近的上/下模块做参考
        below = None
        for mm in d.modules:
            if mm.end <= exc.address:
                below = mm
            else:
                break
        res["attribution"] = {
            "kind": "wild_address",
            "module": None,
            "nearest_below": (f"{below.name}+0x{exc.address - below.base:X}"
                              if below else None),
            "note": "异常地址不在任何已加载模块内 —— 典型的跳转/调用到野地址",
        }

    # ② 崩溃线程栈扫描
    rsp, rip, sbase, sdata = d.crash_stack()
    if sdata:
        res["stack_scan"] = build_stack_report(d.modules, rsp, sbase, sdata)
    else:
        res["stack_scan"] = {"note": "dump 未包含崩溃线程栈内存"}

    # ③ 与 capture.py 的模块快照交叉验证（ASLR 基址应一致）
    if run and run.get("modules_last"):
        snap = {m["name"].lower(): m for m in run["modules_last"]}
        mism = []
        for mm in d.modules:
            s = snap.get(mm.name.lower())
            if s and s["base"] != mm.base:
                mism.append({"module": mm.name, "snapshot_base": f"0x{s['base']:X}",
                             "dump_base": f"0x{mm.base:X}"})
        res["cross_check"] = {
            "snapshot_modules": len(run["modules_last"]),
            "dump_modules": len(d.modules),
            "base_mismatches": mism[:10],
            "consistent": not mism,
        }
    return res


def analyze_wer(dirpath: str):
    w = wermod.parse(os.path.join(dirpath, "Report.wer"))
    res = {
        "source": "report.wer",
        "wer_dir": dirpath,
        "event_epoch": wermod.filetime_to_epoch(w.event_time),
        "exception": {
            "code": w.exception_code,
            "code_int": (f"0x{w.exception_code_int:08X}" if w.exception_code_int else None),
            "code_name": md.EXC_NAMES.get(w.exception_code_int, "?") if w.exception_code_int else None,
            "data": w.exception_data,
            "av_kind": w.av_kind,
        },
        "faulting_module": w.faulting_module,
        "exception_offset": w.exception_offset,
        "stackhash": w.is_stackhash,
        "pch_hint": w.pch_hint,
        "attribution": None,
    }
    if w.is_stackhash:
        res["attribution"] = {
            "kind": "wild_address",
            "module": None,
            "note": "WER 无法把异常地址归入任何模块（StackHash）—— 野地址",
            "pch_hint": w.pch_hint,
        }
    else:
        rva = w.offset_rva
        mpath = resolve_module_path_for_wer(w.faulting_module, w.loaded_modules)
        pe = PeInfo.get(mpath) if mpath else None
        res["attribution"] = {
            "kind": "in_module",
            "module": w.faulting_module,
            "module_path": mpath,
            "category": classify(mpath or w.faulting_module),
            "rva": w.exception_offset,
            "rva_int": rva,
            "in_exec_section": bool(pe and pe.ok and rva is not None and pe.is_exec(rva)),
            "instruction": (decode_at(pe.read_rva(rva, 16))
                            if pe and pe.ok and rva is not None else None),
        }
    res["hooks_loaded"] = {
        "RTSSHooks64.dll": w.has_module("RTSSHooks64.dll"),
        "nvspcap64.dll": w.has_module("nvspcap64.dll"),
    }
    return res


def main():
    ap = argparse.ArgumentParser(description="崩溃归属判定")
    ap.add_argument("--dump", help="minidump 路径")
    ap.add_argument("--dump-latest", action="store_true", help="取 dump 目录里最新的 dmp")
    ap.add_argument("--dump-dir", default=os.path.join(
        os.environ.get("LOCALAPPDATA", ""), "Temp", "crashscope", "dumps"))
    ap.add_argument("--wer", help="Report.wer 所在目录")
    ap.add_argument("--run", help="capture.py 产出的 run JSON，用于交叉验证基址")
    ap.add_argument("--json", help="把结果写到该 JSON 文件")
    a = ap.parse_args()

    run = None
    if a.run and os.path.exists(a.run):
        run = json.load(open(a.run, encoding="utf-8"))

    if a.dump_latest:
        if not os.path.isdir(a.dump_dir):
            raise SystemExit(f"dump 目录不存在：{a.dump_dir}")
        cands = [os.path.join(a.dump_dir, f) for f in os.listdir(a.dump_dir)
                 if f.lower().endswith(".dmp")]
        if not cands:
            raise SystemExit(f"目录里没有 dmp：{a.dump_dir}")
        a.dump = max(cands, key=os.path.getmtime)

    if a.dump:
        res = analyze_dump(a.dump, run)
    elif a.wer:
        res = analyze_wer(a.wer)
    else:
        raise SystemExit("需要 --dump / --dump-latest / --wer 之一")

    _print_report(res)
    if a.json:
        os.makedirs(os.path.dirname(os.path.abspath(a.json)), exist_ok=True)
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
        print(f"\n-> {a.json}")


def _print_report(res: dict):
    print("=" * 78)
    print(f"来源：{res['source']}  {res.get('dump') or res.get('wer_dir')}")
    e = res.get("exception") or {}
    print(f"异常码：{e.get('code')}  {e.get('code_name')}   "
          f"访问性质：{e.get('av_kind')}   异常数据：{e.get('data') or e.get('params')}")
    if e.get("address"):
        print(f"异常地址：{e['address']}   目标：{e.get('av_target')}   "
              f"RIP：{e.get('rip')}  RSP：{e.get('rsp')}")
    a = res.get("attribution") or {}
    if a.get("kind") == "in_module":
        print(f"\n【归属】{a['module']}  ({a['category']})  RVA={a['rva']}  "
              f"落在可执行节：{a.get('in_exec_section')}")
        print(f"        模块路径：{a.get('module_path')}")
        if a.get("instruction"):
            print(f"        该 RVA 处指令（磁盘镜像译码）：{a['instruction']}")
        if a.get("pe_timestamp_match") is False:
            print("        ⚠ 磁盘文件与 dump 中模块时间戳不一致，译码仅供参考")
    else:
        print(f"\n【归属】不在任何模块内（野地址）")
        if a.get("nearest_below"):
            print(f"        下方最近模块：{a['nearest_below']}")
        if a.get("pch_hint"):
            print(f"        WER PCH 提示：{a['pch_hint']}")

    ss = res.get("stack_scan") or {}
    if ss.get("sequence"):
        print(f"\n【崩溃线程栈】RSP={ss['rsp']} 栈底={ss['stack_base']} "
              f"扫了 {ss['slots_scanned']} 槽，命中模块 {ss['total_module_hits']} 次")
        print("  栈顶模块序列（连续同模块已合并，c=命中数，* 为可执行节内指针）：")
        for s in ss["sequence"][:20]:
            star = "*" if s["code_hits"] else " "
            fr = s.get("first_from_rsp")
            loc = f"rsp+0x{fr:X}" if fr is not None else "?"
            print(f"    {star} {s['module']:<28} c={s['count']:<3} "
                  f"[{s['category']:<13}] +0x{s['rva_first']:X}..0x{s['rva_last']:X} ({loc})")
        print(f"\n  栈上出现的类别：{ss['categories_present']}")
        print(f"  栈上的覆盖层/钩子模块：{ss['hook_modules_on_stack'] or '无'}")
        print(f"  栈上我们的内核模块：{ss['kernel_ours_on_stack'] or '无'}")
    elif ss.get("note"):
        print(f"\n【崩溃线程栈】{ss['note']}")

    if res.get("cross_check"):
        cc = res["cross_check"]
        print(f"\n【基址交叉验证】快照 {cc['snapshot_modules']} 个 vs dump {cc['dump_modules']} 个 "
              f"-> {'一致' if cc['consistent'] else '存在不一致'}")
        for m in cc["base_mismatches"]:
            print(f"    ⚠ {m['module']}: 快照 {m['snapshot_base']} vs dump {m['dump_base']}")
    if res.get("hooks_loaded"):
        print(f"\n【已加载钩子】{res['hooks_loaded']}")
    print("=" * 78)


if __name__ == "__main__":
    main()
