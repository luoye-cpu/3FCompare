#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
给 minidump 里的地址做符号解析（dbghelp / ctypes，无需安装 WinDbg）。

为什么绕这一圈：本机没有 cdb/windbg，也不允许安装。但 Windows 自带的 dbghelp.dll
本身就具备完整的 PDB 解析能力，用 ctypes 直接调用即可把「模块+RVA」变成「函数名+偏移」。

关键点：SymInitialize 用 fInvadeProcess=FALSE、句柄传当前进程，再用 SymLoadModuleEx
把转储里的模块按**转储记录中的基址**装载——这样即使模块实际没被加载，也能正确解析。

用法：
    python tools/dump_symbols.py <dump.dmp> [--online]
    --online 追加微软符号服务器（首次会下载 dxgi/d3d11 等系统 PDB，较慢且需网络）

输出：异常地址 + 崩溃线程栈顶若干候选帧的符号化结果。
"""

import ctypes
import ctypes.wintypes as wt
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dump_triage import Dump, scan_stack  # noqa: E402

SYMOPT_UNDNAME = 0x00000002
SYMOPT_DEFERRED_LOADS = 0x00000004
SYMOPT_LOAD_LINES = 0x00000010
SYMOPT_FAIL_CRITICAL_ERRORS = 0x00000200
SYMOPT_NO_PROMPTS = 0x00080000

MAX_NAME = 2000


class SYMBOL_INFO(ctypes.Structure):
    _fields_ = [
        ("SizeOfStruct", ctypes.c_ulong),
        ("TypeIndex", ctypes.c_ulong),
        ("Reserved", ctypes.c_ulonglong * 2),
        ("Index", ctypes.c_ulong),
        ("Size", ctypes.c_ulong),
        ("ModBase", ctypes.c_ulonglong),
        ("Flags", ctypes.c_ulong),
        ("Value", ctypes.c_ulonglong),
        ("Address", ctypes.c_ulonglong),
        ("Register", ctypes.c_ulong),
        ("Scope", ctypes.c_ulong),
        ("Tag", ctypes.c_ulong),
        ("NameLen", ctypes.c_ulong),
        ("MaxNameLen", ctypes.c_ulong),
        ("Name", ctypes.c_char * MAX_NAME),
    ]


class IMAGEHLP_LINE64(ctypes.Structure):
    _fields_ = [
        ("SizeOfStruct", ctypes.c_ulong),
        ("Key", ctypes.c_void_p),
        ("LineNumber", ctypes.c_ulong),
        ("FileName", ctypes.c_char_p),
        ("Address", ctypes.c_ulonglong),
    ]


class PdbFallback:
    """dbghelp 不肯装载 PDB 时的兜底：直接用自研的 MSF/PDB 解析器。

    本机 dbghelp 对 FFF.Native.dll 只装载导出符号（即使 CodeView GUID/age 与
    旁边那份 PDB 完全匹配），所以内部函数必须走这条路。
    """

    def __init__(self, dll_name, pdb_path, dll_path):
        from pdb_resolve import Pdb
        self.pdb = Pdb(pdb_path, dll_path)

    def resolve(self, rva):
        r = self.pdb.lookup(rva)
        if not r:
            return None
        name, off = r
        return "%s+0x%X" % (name, off)


class Resolver:
    def __init__(self, online=False, extra_paths=(), pdb_map=None):
        self.dbghelp = ctypes.WinDLL("dbghelp")
        self.kernel32 = ctypes.WinDLL("kernel32")
        # 必须显式声明 restype：GetCurrentProcess 返回 HANDLE（x64 上 8 字节），
        # 默认 c_int 会把伪句柄 -1 截断成 32 位，导致后续 SymLoadModuleEx 静默失败。
        self.kernel32.GetCurrentProcess.restype = ctypes.c_void_p
        self.kernel32.GetCurrentProcess.argtypes = []
        self.proc = self.kernel32.GetCurrentProcess()
        self.dbghelp.SymInitialize.argtypes = [ctypes.c_void_p, ctypes.c_char_p, wt.BOOL]
        self.dbghelp.SymInitialize.restype = wt.BOOL
        self.dbghelp.SymLoadModuleEx.argtypes = [
            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p,
            ctypes.c_ulonglong, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong]
        self.dbghelp.SymLoadModuleEx.restype = ctypes.c_ulonglong
        self.dbghelp.SymFromAddr.argtypes = [
            ctypes.c_void_p, ctypes.c_ulonglong,
            ctypes.POINTER(ctypes.c_ulonglong), ctypes.POINTER(SYMBOL_INFO)]
        self.dbghelp.SymFromAddr.restype = wt.BOOL
        self.dbghelp.SymGetLineFromAddr64.argtypes = [
            ctypes.c_void_p, ctypes.c_ulonglong,
            ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(IMAGEHLP_LINE64)]
        self.dbghelp.SymGetLineFromAddr64.restype = wt.BOOL
        self.dbghelp.SymSetOptions(
            SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS | SYMOPT_LOAD_LINES
            | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS)
        parts = list(extra_paths)
        if online:
            cache = os.path.join(os.getcwd(), "_symcache")
            os.makedirs(cache, exist_ok=True)
            parts.append("srv*%s*https://msdl.microsoft.com/download/symbols" % cache)
        path = ";".join(parts).encode("utf-8") or None
        if not self.dbghelp.SymInitialize(self.proc, path, False):
            raise OSError("SymInitialize 失败: %d" % ctypes.get_last_error())
        self.loaded = {}
        self.pdb_map = pdb_map or {}

    def load(self, mod):
        """按转储记录的基址装载模块。返回是否成功。"""
        if mod["name"] in self.loaded:
            return self.loaded[mod["name"]]
        ok = False
        cand = mod["path"]
        if not os.path.exists(cand):
            # 转储记录的路径在本机可能不存在（例如换过目录），退回到系统目录同名文件
            alt = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                               "System32", mod["name"])
            cand = alt if os.path.exists(alt) else mod["path"]
        try:
            base = self.dbghelp.SymLoadModuleEx(
                self.proc, None,
                cand.encode("utf-8") if cand else None, None,
                ctypes.c_ulonglong(mod["base"]), ctypes.c_ulong(mod["size"]),
                None, 0)
            ok = bool(base)
        except Exception:
            ok = False
        self.loaded[mod["name"]] = ok
        return ok

    def resolve(self, addr, mods):
        m = None
        for x in mods:
            if x["base"] <= addr < x["end"]:
                m = x
                break
        if not m:
            return None
        fb = self.pdb_map.get(m["name"].lower())
        if fb:
            got = fb.resolve(addr - m["base"])
            if got:
                return "%s!%s" % (m["name"].replace(".dll", ""), got)
        if not self.load(m):
            return "%s+0x%X (符号未装载)" % (m["name"], addr - m["base"])
        sym = SYMBOL_INFO()
        sym.SizeOfStruct = ctypes.sizeof(SYMBOL_INFO)
        sym.MaxNameLen = MAX_NAME
        disp = ctypes.c_ulonglong(0)
        if self.dbghelp.SymFromAddr(self.proc, ctypes.c_ulonglong(addr),
                                    ctypes.byref(disp), ctypes.byref(sym)):
            name = sym.Name.decode("utf-8", "replace")
            suffix = ""
            line = IMAGEHLP_LINE64()
            line.SizeOfStruct = ctypes.sizeof(IMAGEHLP_LINE64)
            ld = ctypes.c_ulong(0)
            if self.dbghelp.SymGetLineFromAddr64(
                    self.proc, ctypes.c_ulonglong(addr), ctypes.byref(ld),
                    ctypes.byref(line)) and line.FileName:
                fn = line.FileName.decode("utf-8", "replace")
                suffix = "  [%s:%d]" % (os.path.basename(fn), line.LineNumber)
            return "%s!%s+0x%X%s" % (m["name"].replace(".dll", "").replace(".exe", ""),
                                     name, disp.value, suffix)
        return "%s+0x%X (无符号)" % (m["name"], addr - m["base"])

    def close(self):
        # 必须声明 argtypes：self.proc 是 c_void_p 形式的伪句柄（0xFFFFFFFFFFFFFFFF），
        # 不声明会被当成 32 位 int 而抛 OverflowError。
        try:
            self.dbghelp.SymCleanup.argtypes = [ctypes.c_void_p]
            self.dbghelp.SymCleanup.restype = wt.BOOL
            self.dbghelp.SymCleanup(self.proc)
        except Exception:
            pass


def main():
    args = sys.argv[1:]
    online = "--online" in args
    args = [a for a in args if a != "--online"]
    # --pdb FFF.Native.dll=<pdb 路径>：为指定模块挂上自研 PDB 解析兜底
    pdb_spec = {}
    rest = []
    i = 0
    while i < len(args):
        if args[i] == "--pdb" and i + 1 < len(args):
            k, v = args[i + 1].split("=", 1)
            pdb_spec[k.lower()] = v
            i += 2
        else:
            rest.append(args[i])
            i += 1
    args = rest
    if not args:
        print(__doc__)
        return 2
    d = Dump(args[0])
    mods = d.modules()
    exc = d.exception()
    extra = [os.path.dirname(m["path"]) for m in mods if m["path"]]
    res = Resolver(online=online, extra_paths=sorted(set(extra)))
    for m in mods:
        p = pdb_spec.get(m["name"].lower())
        if p and os.path.exists(m["path"]):
            res.pdb_map[m["name"].lower()] = PdbFallback(m["name"], p, m["path"])
    print("转储: %s" % os.path.basename(args[0]))
    if exc:
        print("异常地址: 0x%016X" % exc["addr"])
        print("   → %s" % (res.resolve(exc["addr"], mods) or "<不属于任何模块>"))
        ctx = exc.get("ctx") or {}
        if ctx.get("rip"):
            print("   RIP: 0x%016X" % ctx["rip"])
        thr = None
        for t in d.threads():
            if t["tid"] == exc["tid"]:
                thr = t
                break
        if thr:
            seq = scan_stack(d, thr, mods, from_va=ctx.get("rsp"), limit=400)
            print("崩溃线程栈（自 RSP 向上，最多 25 帧）:")
            shown, last = 0, None
            for off, val, mm in seq:
                if last == (mm["name"], val):
                    continue
                last = (mm["name"], val)
                print("   +0x%04X  %s" % (off, res.resolve(val, mods)))
                shown += 1
                if shown >= 25:
                    break
    res.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
