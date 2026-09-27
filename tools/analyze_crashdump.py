#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
3FCompare 崩溃转储解析器（无需 WinDbg / cdb）
=============================================
本机没有 Windows 调试器，但 WER 一直在写完整内存转储（FullDump）。
本脚本直接解析 minidump 格式，产出：
  1. 异常码 / 异常地址 / 故障模块 + 偏移
  2. 崩溃线程全部寄存器
  3. 崩溃线程的近似调用栈（栈内存扫描，等价 WinDbg 的 dps）
  4. 全部线程的 RIP 分布（用于看多路 Present 是否并发）
  5. 已加载模块清单（含第三方钩子 DLL）

用法:
  python tools/analyze_crashdump.py <dump1.dmp> [dump2.dmp ...]
  python tools/analyze_crashdump.py --dir C:\Users\...\CrashDumps
"""

import argparse
import struct
import sys
import os
import glob
from datetime import datetime, timezone, timedelta

# ── minidump 常量 ────────────────────────────────────────────────────────────
STREAM = {
    3: "ThreadList", 4: "ModuleList", 5: "MemoryList", 6: "Exception",
    7: "SystemInfo", 8: "ThreadExList", 9: "Memory64List", 15: "MiscInfo",
    16: "MemoryInfoList", 17: "ThreadInfoList", 19: "Token",
}

EXC_NAMES = {
    0xC0000005: "STATUS_ACCESS_VIOLATION",
    0xC000001D: "STATUS_ILLEGAL_INSTRUCTION",
    0xC0000094: "STATUS_INTEGER_DIVIDE_BY_ZERO",
    0xC00000FD: "STATUS_STACK_OVERFLOW",
    0xC0000096: "STATUS_PRIVILEGED_INSTRUCTION",
    0xC000008E: "STATUS_DIVIDE_BY_ZERO",
    0xC0000374: "STATUS_HEAP_CORRUPTION",
    0xC0000409: "STATUS_STACK_BUFFER_OVERRUN",
    0xE0434352: "CLR_EXCEPTION",
    0xC0000006: "STATUS_IN_PAGE_ERROR",
    0x80000003: "STATUS_BREAKPOINT",
}

# CONTEXT_AMD64 寄存器偏移（实测校准：本机转储 ctx_size=1232，
# ContextFlags 位于 +0x30，EFlags +0x44，Rax +0x78 … Rip +0xF8。
# 网上常见的 +0x88/+0x108 版本是错的，用它读出来全是垃圾。）
REG_OFF = {
    "Rax": 0x78, "Rcx": 0x80, "Rdx": 0x88, "Rbx": 0x90,
    "Rsp": 0x98, "Rbp": 0xA0, "Rsi": 0xA8, "Rdi": 0xB0,
    "R8": 0xB8, "R9": 0xC0, "R10": 0xC8, "R11": 0xD0,
    "R12": 0xD8, "R13": 0xE0, "R14": 0xE8, "R15": 0xF0,
    "Rip": 0xF8, "EFlags": 0x44, "SegCs": 0x38, "SegSs": 0x42,
}


# ── 畸形输入防护 ────────────────────────────────────────────────────────────
# 解析对象是**外部**崩溃转储（可能被截断 / 被篡改 / 根本不是转储），
# 所有来自文件的计数字段都必须先夹上界：否则一个 0xFFFFFFFF 就会让
# 循环空转几十亿次，或让后续 struct.unpack_from 读到文件尾之外。
MAX_STREAMS = 4096
MAX_THREADS = 65536
MAX_MODULES = 65536
MAX_RANGES = 1 << 20


def _check_count(n, limit, what):
    """校验外部计数字段；超界直接报错（不静默截断，否则会给出错误结论）。"""
    if n < 0 or n > limit:
        raise ValueError(f"{what} 计数异常: {n}（合法范围 0..{limit}，疑似畸形/截断转储）")
    return n


class Dump:
    def __init__(self, path):
        self.path = path
        with open(path, "rb") as f:
            self.data = f.read()
        d = self.data
        sig = struct.unpack_from("<I", d, 0)[0]
        self.version = struct.unpack_from("<I", d, 4)[0]
        if sig != 0x504D444D:
            raise ValueError(f"不是 minidump（signature=0x{sig:08X}）")
        # MINIDUMP_HEADER: Sig(0) Version(4) NumberOfStreams(8) StreamDirectoryRva(12)
        #                  CheckSum(16) TimeDateStamp(20) Flags:u64(24)
        self.nstreams = struct.unpack_from("<I", d, 8)[0]
        self.dir_rva = struct.unpack_from("<I", d, 12)[0]
        self.checksum = struct.unpack_from("<I", d, 16)[0]
        self.timestamp = struct.unpack_from("<I", d, 20)[0]
        self.flags = struct.unpack_from("<Q", d, 24)[0]

        self.streams = {}
        self.threads = []      # (tid, ctx_rva, ctx_size, stack_start, stack_size)
        self.modules = []      # (base, size, name)
        self.exception = None  # dict
        self.ranges = []       # [(start, size, rva)]

        self._parse_dir()
        self._parse_modules()
        self._parse_threads()
        self._parse_memory()
        self._parse_exception()
        self._index_ranges()

    # ── 基础读取 ─────────────────────────────────────────────────────────────
    def _u16(self, off): return struct.unpack_from("<H", self.data, off)[0]
    def _u32(self, off): return struct.unpack_from("<I", self.data, off)[0]
    def _u64(self, off): return struct.unpack_from("<Q", self.data, off)[0]

    def _parse_dir(self):
        for i in range(_check_count(self.nstreams, MAX_STREAMS, "NumberOfStreams")):
            off = self.dir_rva + i * 12
            stype, dsize, rva = struct.unpack_from("<III", self.data, off)
            self.streams.setdefault(stype, []).append((dsize, rva))

    def _rva_str(self, rva):
        ln = self._u32(rva)
        return self.data[rva + 4: rva + 4 + ln].decode("utf-16-le", errors="replace")

    def _parse_modules(self):
        for dsize, rva in self.streams.get(4, []):
            n = _check_count(self._u32(rva), MAX_MODULES, "ModuleList.NumberOfModules")
            for i in range(n):
                base = rva + 4 + i * 108
                img_base = self._u64(base)
                img_size = self._u32(base + 8)
                name_rva = self._u32(base + 20)
                try:
                    name = self._rva_str(name_rva)
                except Exception:
                    name = "?"
                self.modules.append((img_base, img_size, name))
            break
        self.modules.sort()

    def _parse_threads(self):
        for stype in (3, 8):
            for dsize, rva in self.streams.get(stype, []):
                n = _check_count(self._u32(rva), MAX_THREADS, "ThreadList.NumberOfThreads")
                ent = rva + 4
                for i in range(n):
                    # MINIDUMP_THREAD: Tid(0) Suspend(4) PriClass(8) Pri(12)
                    #   Teb(16) Stack.Start(24) Stack.DataSize(32) Stack.Rva(36)
                    #   Ctx.DataSize(40) Ctx.Rva(44)  → 48 字节
                    # MINIDUMP_THREAD_EX 同前缀，64 字节
                    tid = self._u32(ent)
                    stack_start = self._u64(ent + 24)
                    stack_size = self._u32(ent + 32)
                    ctx_size = self._u32(ent + 40)
                    ctx_rva = self._u32(ent + 44)
                    adv = 48 if stype == 3 else 64
                    self.threads.append((tid, ctx_rva, ctx_size, stack_start, stack_size))
                    ent += adv
                if self.threads:
                    return

    def _parse_memory(self):
        # Memory64List (9)：NumberOfMemoryRanges(u64) BaseRva(u64)，
        # 描述符 {StartAddress:u64, DataSize:u64} 共 16 字节，
        # 数据从 BaseRva 起顺序排列（RVA 需累加，没有逐项 RVA 字段）。
        for dsize, rva in self.streams.get(9, []):
            n = _check_count(self._u64(rva), MAX_RANGES, "Memory64List.NumberOfMemoryRanges")
            base_rva = self._u64(rva + 8)
            ent = rva + 16
            cur = base_rva
            for _ in range(n):
                start = self._u64(ent)
                sz = self._u64(ent + 8)
                self.ranges.append((start, sz, cur))
                cur += sz
                ent += 16
        # MemoryList (5)：{StartAddress:u64, DataSize:u32, Rva:u32} 共 16 字节
        for dsize, rva in self.streams.get(5, []):
            n = _check_count(self._u32(rva), MAX_RANGES, "MemoryList.NumberOfMemoryRanges")
            ent = rva + 4
            for _ in range(n):
                start = self._u64(ent)
                dsz = self._u32(ent + 8)
                drva = self._u32(ent + 12)
                self.ranges.append((start, dsz, drva))
                ent += 16

    def _parse_exception(self):
        for dsize, rva in self.streams.get(6, []):
            tid = self._u32(rva)
            code = self._u32(rva + 8)
            flags = self._u32(rva + 12)
            inner = self._u64(rva + 16)
            addr = self._u64(rva + 24)
            npar = self._u32(rva + 32)
            params = [self._u64(rva + 40 + 8 * i) for i in range(min(npar, 15))]
            ctx_size = self._u32(rva + 160)
            ctx_rva = self._u32(rva + 164)
            self.exception = {
                "tid": tid, "code": code, "flags": flags, "inner": inner,
                "addr": addr, "npar": npar, "params": params,
                "ctx_rva": ctx_rva, "ctx_size": ctx_size,
            }
            break

    def _index_ranges(self):
        """建立有序索引：19323 个内存段做线性查找会慢到不可用"""
        import bisect
        self.ranges.sort()
        self._rstarts = [r[0] for r in self.ranges]
        self._bisect = bisect

    def read_mem(self, addr, length):
        if getattr(self, "_rstarts", None):
            i = self._bisect.bisect_right(self._rstarts, addr) - 1
            while i >= 0:
                start, size, rva = self.ranges[i]
                if start <= addr < start + size:
                    avail = min(length, int(start + size - addr))
                    off = rva + (addr - start)
                    if off + avail <= len(self.data):
                        return self.data[off: off + avail]
                    return b""
                if start + size <= addr - length:
                    break
                i -= 1
            return b""
        return self._read_mem_linear(addr, length)

    def _read_mem_linear(self, addr, length):
        for start, size, rva in self.ranges:
            if start <= addr < start + size:
                avail = min(length, int(start + size - addr))
                off = rva + (addr - start)
                if off + avail <= len(self.data):
                    return self.data[off: off + avail]
                return b""
        return b""

    # ── 查询 ─────────────────────────────────────────────────────────────────
    def module_of(self, addr):
        lo, hi = 0, len(self.modules)
        best = None
        for base, size, name in self.modules:
            if base <= addr < base + size:
                best = (name, addr - base)
                break
        return best

    def regs(self, ctx_rva):
        out = {}
        for name, off in REG_OFF.items():
            if off + (2 if name in ("EFlags", "SegCs", "SegSs") else 8) <= 0x200:
                if name in ("EFlags", "SegCs", "SegSs"):
                    out[name] = self._u32(ctx_rva + off)
                else:
                    out[name] = self._u64(ctx_rva + off)
        return out


def stack_scan(dump, rip, rsp, depth_bytes=0x6000, max_show=40):
    """近似 dps：扫描栈内存，找出落在模块范围内的 64 位值"""
    import bisect
    bases = [m[0] for m in dump.modules]
    found = []
    addr = rsp & ~7
    end = addr + depth_bytes
    cur = addr
    while cur < end:
        chunk = dump.read_mem(cur, min(0x1000, end - cur))
        if not chunk:
            cur += 0x1000
            continue
        for i in range(0, len(chunk) - 7, 8):
            v = struct.unpack_from("<Q", chunk, i)[0]
            if v < 0x10000:
                continue
            idx = bisect.bisect_right(bases, v) - 1
            if idx >= 0:
                base, size, name = dump.modules[idx]
                if base <= v < base + size:
                    found.append((cur + i, v, name, v - base))
        cur += len(chunk)
    return found[:max_show]


def pe_rva_to_offset(data, rva):
    """PE 的 RVA → 文件偏移（必须走节表，直接按 RVA seek 会整体错位）"""
    try:
        if data[:2] != b"MZ":
            return None
        e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
        if data[e_lfanew:e_lfanew + 4] != b"PE\x00\x00":
            return None
        coff = e_lfanew + 4
        nsec = struct.unpack_from("<H", data, coff + 2)[0]
        size_opt = struct.unpack_from("<H", data, coff + 16)[0]
        sec_off = coff + 20 + size_opt
        for i in range(nsec):
            s = sec_off + i * 40
            vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, s + 8)
            span = max(vsize, rawsize)
            if vaddr <= rva < vaddr + span:
                return rawptr + (rva - vaddr)
        return None
    except Exception:
        return None


def pe_exports(path):
    """解析 PE 导出表，返回 [(rva, name)] 按 rva 排序（用于把地址归到最近的导出符号）"""
    try:
        with open(path, "rb") as f:
            data = f.read(0x100000)
    except Exception:
        return []
    try:
        if data[:2] != b"MZ":
            return []
        e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
        coff = e_lfanew + 4
        size_opt = struct.unpack_from("<H", data, coff + 16)[0]
        opt = coff + 20
        magic = struct.unpack_from("<H", data, opt)[0]
        dd = opt + (112 if magic == 0x20B else 96)   # DataDirectory[0] = Export
        exp_rva = struct.unpack_from("<I", data, dd)[0]
        if exp_rva == 0:
            return []
        eo = pe_rva_to_offset(data, exp_rva)
        if eo is None or eo + 40 > len(data):
            return []
        nfunc = struct.unpack_from("<I", data, eo + 20)[0]
        nname = struct.unpack_from("<I", data, eo + 24)[0]
        afunc = struct.unpack_from("<I", data, eo + 28)[0]
        aname = struct.unpack_from("<I", data, eo + 32)[0]
        aord = struct.unpack_from("<I", data, eo + 36)[0]
        foff = pe_rva_to_offset(data, afunc)
        noff = pe_rva_to_offset(data, aname)
        ooff = pe_rva_to_offset(data, aord)
        if foff is None or noff is None or ooff is None:
            return []
        out = []
        for i in range(nname):
            nrva = struct.unpack_from("<I", data, noff + 4 * i)[0]
            soff = pe_rva_to_offset(data, nrva)
            if soff is None:
                continue
            end = data.find(b"\x00", soff)
            nm = data[soff:end].decode("ascii", errors="replace")
            ordi = struct.unpack_from("<H", data, ooff + 2 * i)[0]
            frva = struct.unpack_from("<I", data, foff + 4 * ordi)[0]
            out.append((frva, nm))
        out.sort()
        return out
    except Exception:
        return []


def read_file_at(path, rva, size):
    """按 RVA 从磁盘原文件读一段字节（用于和进程内存比对，找被改写处）"""
    try:
        if rva < 0:
            return None
        with open(path, "rb") as f:
            head = f.read(0x800)
            off = pe_rva_to_offset(head, rva)
            if off is None:
                return None
            f.seek(off)
            return f.read(size)
    except Exception:
        return None


def fmt_dt(ts):
    try:
        return (datetime(1970, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=ts)
                ).astimezone(timezone(timedelta(hours=8))).strftime("%Y-%m-%d %H:%M:%S")
    except Exception:
        return str(ts)


def analyze(path):
    d = Dump(path)
    W = 78
    print("=" * W)
    print(f"转储: {os.path.basename(path)}")
    print(f"  大小 {len(d.data)/1048576:.1f} MB | 时间 {fmt_dt(d.timestamp)} | "
          f"线程 {len(d.threads)} | 模块 {len(d.modules)} | 内存段 {len(d.ranges)}")

    # 1. 异常
    if not d.exception:
        print("  ⚠ 无异常流（非崩溃转储？）—— 未能解析出任何有效信息")
        return False
    e = d.exception
    code = e["code"]
    cname = EXC_NAMES.get(code, f"0x{code:08X}")
    print("-" * W)
    print(f"[异常] {cname} (0x{code:08X})  线程 TID={e['tid']}")
    m = d.module_of(e["addr"])
    if m:
        print(f"       异常地址 0x{e['addr']:016X}  →  {m[0]} +0x{m[1]:X}")
    else:
        print(f"       异常地址 0x{e['addr']:016X}  →  （不在任何模块内！疑似跳到野地址）")
    if e["npar"] >= 2 and code == 0xC0000005:
        op = {0: "读", 1: "写", 8: "执行"}.get(e["params"][0], str(e["params"][0]))
        print(f"       访问类型: {op}   目标地址: 0x{e['params'][1]:016X}")

    # 2. 崩溃线程寄存器
    r = d.regs(e["ctx_rva"])
    print("-" * W)
    print("[崩溃线程寄存器]")
    for grp in (("Rip", "Rsp", "Rbp"), ("Rax", "Rcx", "Rdx", "Rbx"),
                ("Rsi", "Rdi", "R8", "R9"), ("R10", "R11", "R12", "R13", "R14", "R15")):
        line = "  "
        for n in grp:
            v = r.get(n, 0)
            mm = d.module_of(v)
            tag = f"[{mm[0]}+0x{mm[1]:X}]" if mm else ""
            line += f"{n}=0x{v:012X} {tag:<34}"
        print(line.rstrip())
    print(f"  EFlags=0x{r.get('EFlags',0):08X}  SegCs=0x{r.get('SegCs',0):X}  SegSs=0x{r.get('SegSs',0):X}")

    # 3. 崩溃点附近的原始指令字节 + 与磁盘原文件比对（判断是否被 inline hook 改写）
    dump_at = e["addr"] - 32
    code_bytes = d.read_mem(dump_at, 64)
    if code_bytes and m:
        print("-" * W)
        print(f"[崩溃点前后机器码] @0x{e['addr']:X}   （上行=进程内存，下行=磁盘原文件）")
        raw = read_file_at(m[0], m[1] - 32, 64)
        for off in range(0, len(code_bytes), 16):
            a = dump_at + off
            seg = code_bytes[off:off + 16]
            mark = "  <== RIP" if a <= e["addr"] < a + 16 else ""
            print(f"  MEM  0x{a:X}: {seg.hex(' ')}{mark}")
            if raw is not None:
                fseg = raw[off:off + 16]
                if fseg and fseg != seg:
                    print(f"  FILE 0x{a:X}: {fseg.hex(' ')}   <<<< 与内存不一致（被改写！）")
        if raw is None:
            print(f"  （无法读取磁盘原文件 {m[0]}，跳过比对）")

    # 4. 栈扫描
    print("-" * W)
    print(f"[崩溃线程近似调用栈]  (RSP=0x{r.get('Rsp',0):X}，栈内存扫描)")
    frames = stack_scan(d, r.get("Rip", 0), r.get("Rsp", 0))
    for va, v, name, off in frames:
        star = " *" if v == r.get("Rip", 0) else ""
        print(f"  0x{va:X}  {v:016X}  {name} +0x{off:X}{star}")

    # 5. 全部线程 RIP 分布
    print("-" * W)
    print("[全部线程 RIP 分布]")
    mod_count = {}
    for tid, ctx_rva, csz, sstart, ssize in d.threads:
        try:
            rr = d.regs(ctx_rva)
            rip = rr.get("Rip", 0)
        except Exception:
            continue
        mm = d.module_of(rip)
        key = mm[0] if mm else "<未知>"
        mod_count[key] = mod_count.get(key, 0) + 1
    for k, v in sorted(mod_count.items(), key=lambda x: -x[1]):
        print(f"  {v:>3} 线程  →  {k}")

    # 6. 第三方 / 图形相关模块
    print("-" * W)
    print("[关键模块]")
    keys = ("RTSS", "nvspcap", "FFF.Native", "dxgi", "d3d11", "nvwgf2",
            "detoured", "Discord", "GameOverlay", "3FC.Wgc")
    for base, size, name in d.modules:
        low = name.lower()
        if any(k.lower() in low for k in keys):
            print(f"  0x{base:012X} - 0x{base+size:012X}  ({size/1024:>8.0f} KB)  {name}")

    return True


def main():
    ap = argparse.ArgumentParser(
        description="3FCompare 崩溃转储解析器（minidump，无需 WinDbg）",
        epilog="例: analyze_crashdump.py <a.dmp> [b.dmp ...]  或  analyze_crashdump.py --dir <目录>")
    ap.add_argument("dumps", nargs="*", help="待解析的 .dmp 文件（可多个）")
    ap.add_argument("--dir", help="解析该目录下全部 *.dmp（按 mtime 排序）")
    a = ap.parse_args()

    if a.dir:
        # 用 argparse 后缺参数会走它自己的友好报错（旧实现是 IndexError 堆栈）
        if not os.path.isdir(a.dir):
            print(f"错误: 目录不存在或不是目录: {a.dir}")
            return 2
        files = sorted(glob.glob(os.path.join(a.dir, "*.dmp")),
                       key=os.path.getmtime)
        if not files:
            print(f"错误: 目录下没有 *.dmp: {a.dir}")
            return 2
    elif a.dumps:
        files = a.dumps
    else:
        print(__doc__)
        return 1

    parsed = 0
    for f in files:
        try:
            if analyze(f):
                parsed += 1
        except Exception as ex:
            print(f"[解析失败] {f}: {ex}")
            import traceback
            traceback.print_exc()
    # 一个都没解析出有效信息 ⇒ 非 0：否则接进门禁会"假绿"（无判据却判通过）
    if parsed == 0:
        print(f"\n结论: 全部 {len(files)} 个转储均未解析出有效信息，无法作为判据")
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
