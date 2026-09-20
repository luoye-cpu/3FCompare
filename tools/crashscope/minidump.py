"""minidump 解析：异常记录 / 模块表 / 线程栈，用于崩溃归属判定。

与 .review_pr/bsod/dumpmod.py 的区别：
  * dumpmod.py 面向内核转储（PAGEDU64 页式内核转储），只解 stream directory + 模块表。
  * 本模块面向用户态 AppCrash minidump（MDMP），补上异常流、线程流、
    Memory64ListStream，并提供按地址读取任意内存段的能力（栈扫描的前提）。

用法（作为库）：
    d = parse(path)
    d.exception.code / .address / .params
    d.modules.find(addr) -> Module | None
    d.crash_stack() -> (rsp, rip, base, data)
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field

MINIDUMP_SIGNATURE = 0x504D444D  # 'MDMP'

# stream types
ST_THREAD_LIST = 3
ST_MODULE_LIST = 4
ST_MEMORY_LIST = 5
ST_EXCEPTION = 6
ST_SYSTEM_INFO = 7
ST_MEMORY64_LIST = 9

# AMD64 CONTEXT 关键偏移（结构体 0x4D0 字节）
CTX_CONTEXT_FLAGS = 0x30
CTX_RSP = 0x98
CTX_RBP = 0xA0
CTX_RIP = 0xF8

# 常见 NTSTATUS 异常码
EXC_NAMES = {
    0xC0000005: "STATUS_ACCESS_VIOLATION",
    0xC000001D: "STATUS_ILLEGAL_INSTRUCTION",
    0xC0000006: "STATUS_IN_PAGE_ERROR",
    0xC0000094: "STATUS_INTEGER_DIVIDE_BY_ZERO",
    0xC0000096: "STATUS_PRIVILEGED_INSTRUCTION",
    0xC00000FD: "STATUS_STACK_OVERFLOW",
    0xC0000409: "STATUS_STACK_BUFFER_OVERRUN",
    0x80000003: "STATUS_BREAKPOINT",
    0xE06D7363: "CPP_EXCEPTION (C++ throw)",
}

# 访问违例的 ExceptionInformation[0]
AV_ACCESS = {0: "read", 1: "write", 8: "EXECUTE"}


@dataclass
class Module:
    base: int
    size: int
    path: str
    timestamp: int = 0

    @property
    def name(self) -> str:
        return self.path.replace("\\", "/").rsplit("/", 1)[-1]

    @property
    def end(self) -> int:
        return self.base + self.size

    def contains(self, addr: int) -> bool:
        return self.base <= addr < self.end


@dataclass
class ExceptionRecord:
    code: int
    address: int
    params: list
    flags: int = 0
    thread_id: int = 0

    @property
    def code_name(self) -> str:
        return EXC_NAMES.get(self.code, "UNKNOWN")

    @property
    def av_kind(self) -> str | None:
        """访问违例的读写/执行性质；非 AV 返回 None。"""
        if self.code != 0xC0000005 or not self.params:
            return None
        return AV_ACCESS.get(self.params[0], f"unknown({self.params[0]})")

    @property
    def av_target(self) -> int | None:
        """访问违例试图访问的地址（ExceptionInformation[1]）。"""
        if self.code != 0xC0000005 or len(self.params) < 2:
            return None
        return self.params[1]


@dataclass
class Thread:
    thread_id: int
    stack_start: int = 0
    stack_size: int = 0
    stack_rva: int = 0
    context_rva: int = 0
    context_size: int = 0
    rsp: int = 0
    rbp: int = 0
    rip: int = 0


class ModuleMap:
    def __init__(self, mods: list):
        self.mods = sorted(mods, key=lambda m: m.base)

    def find(self, addr: int):
        # 模块数 ~150，线性扫描足够；二分不必要但便宜
        lo, hi = 0, len(self.mods) - 1
        while lo <= hi:
            mid = (lo + hi) // 2
            m = self.mods[mid]
            if addr < m.base:
                hi = mid - 1
            elif addr >= m.end:
                lo = mid + 1
            else:
                return m
        return None

    def __iter__(self):
        return iter(self.mods)

    def __len__(self):
        return len(self.mods)


class Dump:
    def __init__(self, path: str):
        self.path = path
        self.data = open(path, "rb").read()
        self.modules: ModuleMap = ModuleMap([])
        self.exception: ExceptionRecord | None = None
        self.threads: list = []
        self._mem_segs: list = []   # (start, size, rva) 升序
        self.streams: dict = {}
        self.timestamp: int = 0
        self.flags: int = 0
        self._parse_header()
        self._parse_streams()

    # ---------- 头与流目录 ----------
    def _parse_header(self):
        d = self.data
        if len(d) < 32:
            raise ValueError("文件太小，不是 minidump")
        sig, ver, nstreams, dir_rva, _cs, ts, flags = struct.unpack_from("<IIIIIII", d, 0)
        if sig != MINIDUMP_SIGNATURE:
            raise ValueError(
                f"签名不是 MDMP：0x{sig:08x}。"
                "（内核转储 PAGEDU64/DMP 不适用本解析器）"
            )
        self.timestamp = ts
        self.flags = flags
        off = dir_rva
        for _ in range(nstreams):
            stype, dsize, rva = struct.unpack_from("<III", d, off)
            self.streams.setdefault(stype, (rva, dsize))
            off += 12

    def _parse_streams(self):
        self._parse_modules()
        self._parse_memory()
        self._parse_threads()
        self._parse_exception()

    def _parse_modules(self):
        if ST_MODULE_LIST not in self.streams:
            return
        rva, _ = self.streams[ST_MODULE_LIST]
        d = self.data
        n = struct.unpack_from("<I", d, rva)[0]
        mods = []
        p = rva + 4
        for _ in range(n):
            base, size, _cs, ts, name_rva = struct.unpack_from("<QIIII", d, p)
            p += 108
            ln = struct.unpack_from("<I", d, name_rva)[0]
            raw = d[name_rva + 4: name_rva + 4 + ln]
            name = raw.decode("utf-16-le", "replace")
            if size:
                mods.append(Module(base=base, size=size, path=name, timestamp=ts))
        self.modules = ModuleMap(mods)

    def _parse_memory(self):
        d = self.data
        segs = []
        if ST_MEMORY_LIST in self.streams:
            rva, _ = self.streams[ST_MEMORY_LIST]
            n = struct.unpack_from("<I", d, rva)[0]
            p = rva + 4
            for _ in range(n):
                start, dsize, mrva = struct.unpack_from("<QII", d, p)
                p += 16
                if dsize:
                    segs.append((start, dsize, mrva))
        if ST_MEMORY64_LIST in self.streams:
            rva, _ = self.streams[ST_MEMORY64_LIST]
            n, base_rva = struct.unpack_from("<QQ", d, rva)
            p = rva + 16
            cur = base_rva
            for _ in range(n):
                start, dsize = struct.unpack_from("<QQ", d, p)
                p += 16
                if dsize:
                    segs.append((start, dsize, cur))
                cur += dsize
        segs.sort()
        self._mem_segs = segs

    def read(self, start: int, size: int) -> bytes:
        """按虚拟地址读取内存；未包含在 dump 中则返回 b''。"""
        for seg_start, seg_size, seg_rva in self._mem_segs:
            if seg_start <= start and start + size <= seg_start + seg_size:
                off = seg_rva + (start - seg_start)
                return self.data[off: off + size]
        return b""

    def _parse_threads(self):
        if ST_THREAD_LIST not in self.streams:
            return
        rva, _ = self.streams[ST_THREAD_LIST]
        d = self.data
        n = struct.unpack_from("<I", d, rva)[0]
        p = rva + 4
        for _ in range(n):
            tid, _sc, _pc, _pr, _teb, s_start, s_size, s_rva, c_size, c_rva = \
                struct.unpack_from("<IIIIQQIIII", d, p)
            p += 48
            t = Thread(thread_id=tid, stack_start=s_start, stack_size=s_size,
                       stack_rva=s_rva, context_rva=c_rva, context_size=c_size)
            if c_rva and c_size >= 0x100:
                ctx = d[c_rva: c_rva + c_size]
                t.rsp = struct.unpack_from("<Q", ctx, CTX_RSP)[0]
                t.rbp = struct.unpack_from("<Q", ctx, CTX_RBP)[0]
                t.rip = struct.unpack_from("<Q", ctx, CTX_RIP)[0]
            self.threads.append(t)

    def _parse_exception(self):
        if ST_EXCEPTION not in self.streams:
            return
        rva, _ = self.streams[ST_EXCEPTION]
        d = self.data
        tid = struct.unpack_from("<I", d, rva)[0]
        code, flags, _nested, addr, nparam = struct.unpack_from("<IIQQI", d, rva + 8)
        params = list(struct.unpack_from("<15Q", d, rva + 8 + 32))[:nparam]
        self.exception = ExceptionRecord(code=code, address=addr, params=params,
                                        flags=flags, thread_id=tid)

    # ---------- 便捷访问 ----------
    def crash_thread(self):
        if not self.exception:
            return None
        for t in self.threads:
            if t.thread_id == self.exception.thread_id:
                return t
        return None

    def crash_stack(self):
        """返回 (rsp, rip, stack_base, stack_bytes)。栈字节取不到时 data 为空。"""
        t = self.crash_thread()
        if t is None:
            return 0, 0, 0, b""
        data = b""
        if t.stack_rva and t.stack_size:
            data = self.data[t.stack_rva: t.stack_rva + t.stack_size]
        elif t.stack_size:
            data = self.read(t.stack_start, t.stack_size)
        return t.rsp, t.rip, t.stack_start, data
