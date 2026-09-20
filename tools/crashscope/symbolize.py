"""符号化：把 模块+RVA 翻译成 函数名+偏移（+源码行）。

为什么单独写一个：crashscope 之前只有"栈扫描"——把栈上每个 8 字节槽当成候选返回
地址，再看它落不落在某个模块的可执行节里。栈扫描能告诉你"哪个模块"，但给不出
"哪个函数"，而且会把数据（碰巧长得像代码指针的整数）误判成帧。符号化 + unwind
才是能落到函数名/行号的证据。

dbghelp.dll 的取用策略
---------------------
优先用 Windows Kits 里的调试版 dbghelp（`C:\\Program Files (x86)\\Windows Kits\\10\\
Debuggers\\x64\\dbghelp.dll`）。System32 里那份是精简版：它不带 symsrv 支持，配
`_NT_SYMBOL_PATH` 指向微软符号服务器也不会去下载。symsrv.dll 必须与 dbghelp.dll 同
目录，所以两者一起从 Kits 目录加载。

PDB 与 DLL 必须同一次构建
------------------------
dbghelp 只按 DLL 调试目录里的 RSDS 签名去找 PDB；签名不符时它**不会报错**，而是静默
退回"只有导出表符号"，于是你会拿到一堆 `FFF.Native.dll!?xxx` 或干脆解析失败，然后
误以为"符号没发布"。所以这里自己校验一遍：从 DLL 的调试目录读 RSDS 的 GUID/Age，
从 PDB 的 MSF 信息流（stream 1）读 GUID/Age，两者必须一致；不一致就直接拒绝符号化
并提示换 PDB，绝不硬解。

用法
----
    # 单个地址
    python symbolize.py --module third_party/fff_project/FFF.Native/x64/Release/FFF.Native.dll \
                        --rva 0x5CCBB

    # 一次给多个
    python symbolize.py --module .../FFF.Native.dll --rva 0x5CCBB,0x5CCBE,0x5D486

    # 直接吃 crashscope 的现场 JSON（自动取栈上/调用点的全部 RVA）
    python symbolize.py --scene scenes/final.json --module .../FFF.Native.dll

    # 走微软公共符号服务器解析系统 DLL（需联网）
    python symbolize.py --module C:/Windows/System32/dxgi.dll --rva 0x19530 --msf

    # 真实 unwind 回溯（需要一个带 raw_stack/context 的 sidecar，见 unwind.py）
    python symbolize.py --unwind scenes/final.raw.json --module .../FFF.Native.dll
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

KITS_DBGHELP = r"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbghelp.dll"

MAX_SYM_NAME = 2000
SYMOPT_CASE_INSENSITIVE = 0x00000001
SYMOPT_UNDNAME = 0x00000002
SYMOPT_DEFERRED_LOADS = 0x00000004
SYMOPT_LOAD_LINES = 0x00000010
SYMOPT_OMAP_FIND_NEAREST = 0x00000020
SYMOPT_FAIL_CRITICAL_ERRORS = 0x00000200
SYMOPT_DEBUG = 0x80000000
SYMOPT_AUTO_PUBLICS = 0x00010000
SYMOPT_LOAD_ANYTHING = 0x00000040
SYMOPT_NO_PROMPTS = 0x00080000

# 默认符号搜索路径：本地缓存 + 微软公共符号服务器。
# 顺序很重要：先本地缓存，避免每次都走网络。
SYMCACHE = os.path.join(os.environ.get("LOCALAPPDATA", HERE), "3fc-symcache")
DEFAULT_SYMPATH = f"srv*{SYMCACHE}*https://msdl.microsoft.com/download/symbols"
# 只认本地缓存、不联网。unwind 会遍历上百个系统模块，联网逐个下载 PDB 会非常慢；
# 系统模块没有 PDB 时 dbghelp 仍会退回读导出表，帧名照样够用。
LOCAL_ONLY_SYMPATH = SYMCACHE


# --------------------------------------------------------------------------
# dbghelp 绑定
# --------------------------------------------------------------------------

class SYMBOL_INFO(ctypes.Structure):
    _fields_ = [
        ("SizeOfStruct", wt.ULONG),
        ("TypeIndex", wt.ULONG),
        ("Reserved", ctypes.c_ulonglong * 2),
        ("Index", wt.ULONG),
        ("Size", wt.ULONG),
        ("ModBase", ctypes.c_ulonglong),
        ("Flags", wt.ULONG),
        ("Value", ctypes.c_ulonglong),
        ("Address", ctypes.c_ulonglong),
        ("Register", wt.ULONG),
        ("Scope", wt.ULONG),
        ("Tag", wt.ULONG),
        ("NameLen", wt.ULONG),
        ("MaxNameLen", wt.ULONG),
        ("Name", ctypes.c_char * 1),
    ]


class IMAGEHLP_LINE64(ctypes.Structure):
    _fields_ = [
        ("SizeOfStruct", wt.DWORD),
        ("Key", ctypes.c_void_p),
        ("LineNumber", wt.DWORD),
        ("FileName", ctypes.c_char_p),
        ("Address", ctypes.c_ulonglong),
    ]


class KDHELP64(ctypes.Structure):
    _fields_ = [
        ("Thread", ctypes.c_ulonglong),
        ("ThCallbackStack", wt.DWORD),
        ("ThCallbackBStore", wt.DWORD),
        ("NextCallback", wt.DWORD),
        ("FramePointer", wt.DWORD),
        ("KiCallUserMode", ctypes.c_ulonglong),
        ("KeUserCallbackDispatcher", ctypes.c_ulonglong),
        ("SystemRangeStart", ctypes.c_ulonglong),
        ("KiUserExceptionDispatcher", ctypes.c_ulonglong),
        ("StackBase", ctypes.c_ulonglong),
        ("StackLimit", ctypes.c_ulonglong),
        ("BuildVersion", ctypes.c_ulonglong),
        ("Reserved", ctypes.c_ulonglong * 4),
    ]


class ADDRESS64(ctypes.Structure):
    _fields_ = [
        ("Offset", ctypes.c_ulonglong),
        ("Segment", wt.WORD),
        # ADDRESS_MODE 是 enum -> 4 字节，且会补到 8 字节对齐
        ("Mode", wt.DWORD),
    ]


class STACKFRAME64(ctypes.Structure):
    """x64 版 STACKFRAME64。

    ⚠ 这个结构体必须**逐字节**对：StackWalk64 会往里写 KdHelp 等字段，结构体给小了
      它就直接越界写（实测表现为 StackWalk64 静默返回 0，或 dbghelp 内部访问违例）。
      x64 下 sizeof(STACKFRAME64) 必须是 264。构造完断言一次。
    """
    _fields_ = [
        ("AddrPC", ADDRESS64),
        ("AddrReturn", ADDRESS64),
        ("AddrFrame", ADDRESS64),
        ("AddrStack", ADDRESS64),
        ("AddrBStore", ADDRESS64),
        ("FuncTableEntry", ctypes.c_void_p),
        ("Params", ctypes.c_ulonglong * 4),
        ("Far", wt.BOOL),
        ("Virtual", wt.BOOL),
        ("Reserved", ctypes.c_ulonglong * 3),
        ("KdHelp", KDHELP64),
    ]


assert ctypes.sizeof(STACKFRAME64) == 264, \
    f"STACKFRAME64 布局错误：sizeof={ctypes.sizeof(STACKFRAME64)}，应为 264"
assert ctypes.sizeof(ADDRESS64) == 16, ctypes.sizeof(ADDRESS64)


def _load_dbghelp():
    """加载调试版 dbghelp；失败则退回 System32 版（并明确告知能力降级）。"""
    if os.path.isfile(KITS_DBGHELP):
        try:
            # 先按完整路径加载，确保同目录的 symsrv.dll 能被找到
            return ctypes.WinDLL(KITS_DBGHELP, use_last_error=True), KITS_DBGHELP
        except OSError:
            pass
    return ctypes.WinDLL("dbghelp", use_last_error=True), "dbghelp(系统)"


DBG, DBG_SOURCE = _load_dbghelp()


def _bind():
    D = DBG
    D.SymSetOptions.argtypes = [wt.DWORD]
    D.SymSetOptions.restype = wt.DWORD
    D.SymInitialize.argtypes = [wt.HANDLE, wt.LPCSTR, wt.BOOL]
    D.SymInitialize.restype = wt.BOOL
    D.SymCleanup.argtypes = [wt.HANDLE]
    D.SymCleanup.restype = wt.BOOL
    D.SymSetSearchPath.argtypes = [wt.HANDLE, wt.LPCSTR]
    D.SymSetSearchPath.restype = wt.BOOL
    D.SymGetSearchPath.argtypes = [wt.HANDLE, wt.LPSTR, wt.DWORD]
    D.SymGetSearchPath.restype = wt.BOOL
    D.SymLoadModuleEx.argtypes = [
        wt.HANDLE, wt.HANDLE, wt.LPCSTR, wt.LPCSTR,
        ctypes.c_ulonglong, wt.DWORD, ctypes.c_void_p, wt.DWORD]
    D.SymLoadModuleEx.restype = ctypes.c_ulonglong
    D.SymFromAddr.argtypes = [wt.HANDLE, ctypes.c_ulonglong,
                              ctypes.POINTER(ctypes.c_ulonglong),
                              ctypes.POINTER(SYMBOL_INFO)]
    D.SymFromAddr.restype = wt.BOOL
    D.SymGetLineFromAddr64.argtypes = [wt.HANDLE, ctypes.c_ulonglong,
                                       ctypes.POINTER(wt.DWORD),
                                       ctypes.POINTER(IMAGEHLP_LINE64)]
    D.SymGetLineFromAddr64.restype = wt.BOOL
    D.SymGetModuleBase64.argtypes = [wt.HANDLE, ctypes.c_ulonglong]
    D.SymGetModuleBase64.restype = ctypes.c_ulonglong
    D.StackWalk64.argtypes = [
        wt.DWORD, wt.HANDLE, wt.HANDLE, ctypes.POINTER(STACKFRAME64),
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
        ctypes.c_void_p]
    D.StackWalk64.restype = wt.BOOL
    D.SymFunctionTableAccess64.argtypes = [wt.HANDLE, ctypes.c_ulonglong]
    D.SymFunctionTableAccess64.restype = ctypes.c_void_p
    D.SymGetModuleInfo64.argtypes = [wt.HANDLE, ctypes.c_ulonglong,
                                     ctypes.c_void_p]
    D.SymGetModuleInfo64.restype = wt.BOOL


_bind()


class SymError(RuntimeError):
    pass


class Symbolizer:
    """把一个或多个模块加载进 dbghelp，然后把地址翻译成符号。

    hproc 可以是被调试进程的句柄：StackWalk64 需要 dbghelp 能在"那个"进程里
    读内存与查函数表，所以符号表必须挂在被调试进程的句柄上，而不是当前进程。
    """

    def __init__(self, sympath: str | None = None, verbose: bool = False,
                 hproc: int | None = None):
        self.verbose = verbose
        if hproc is None:
            hproc = ctypes.WinDLL("kernel32").GetCurrentProcess()
        self.hproc = hproc
        opts = (SYMOPT_UNDNAME | SYMOPT_LOAD_LINES | SYMOPT_FAIL_CRITICAL_ERRORS
                | SYMOPT_NO_PROMPTS | SYMOPT_AUTO_PUBLICS)
        DBG.SymSetOptions(opts)
        if not DBG.SymInitialize(self.hproc, None, False):
            raise SymError(f"SymInitialize 失败 err={ctypes.get_last_error()}")
        self.search_path = sympath or DEFAULT_SYMPATH
        DBG.SymSetSearchPath(self.hproc, self.search_path.encode())
        self.modules: dict[str, dict] = {}

    def close(self):
        try:
            DBG.SymCleanup(self.hproc)
        except Exception:
            pass

    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()

    # -- 加载 --------------------------------------------------------------

    def load(self, path: str, base: int | None = None, size: int = 0,
             name: str | None = None) -> dict:
        path = os.path.abspath(path)
        if base is None:
            base = pe_image_base(path)
        if size == 0:
            size = pe_image_size(path)
        modname = name or os.path.basename(path)
        got = DBG.SymLoadModuleEx(self.hproc, None, path.encode(), modname.encode(),
                                  base, size, None, 0)
        if not got:
            err = ctypes.get_last_error()
            raise SymError(f"SymLoadModuleEx 失败 {modname}: err={err}")
        info = {"path": path, "name": modname, "base": got, "size": size}
        self.modules[modname.lower()] = info
        if self.verbose:
            print(f"  [dbghelp] 已加载 {modname} base=0x{got:X} size=0x{size:X}")
        return info

    def base_of(self, name: str) -> int | None:
        m = self.modules.get(name.lower())
        return m["base"] if m else None

    # -- 解析 --------------------------------------------------------------

    def _sym_from_addr(self, addr: int):
        buf = ctypes.create_string_buffer(ctypes.sizeof(SYMBOL_INFO) + MAX_SYM_NAME)
        si = ctypes.cast(buf, ctypes.POINTER(SYMBOL_INFO))
        si.contents.SizeOfStruct = ctypes.sizeof(SYMBOL_INFO)
        si.contents.MaxNameLen = MAX_SYM_NAME
        disp = ctypes.c_ulonglong(0)
        if not DBG.SymFromAddr(self.hproc, addr, ctypes.byref(disp), si):
            return None
        # ⚠ 不能直接读 si.contents.Name：它在结构体里声明为 char[1]，ctypes 只会给出
        #   1 个字符（早期版本因此把 `?foo@@...` 之类名字截成 "P"/"s" 这种单字母）。
        #   必须按 NameLen 从 Name 字段的地址上整段取。
        name_addr = ctypes.addressof(si.contents) + SYMBOL_INFO.Name.offset
        name = ctypes.string_at(name_addr, si.contents.NameLen).decode("utf-8", "replace")
        return {"name": name, "displacement": disp.value,
                "sym_addr": si.contents.Address, "size": si.contents.Size,
                "tag": si.contents.Tag}

    def _line_from_addr(self, addr: int):
        line = IMAGEHLP_LINE64()
        line.SizeOfStruct = ctypes.sizeof(IMAGEHLP_LINE64)
        disp = wt.DWORD(0)
        if not DBG.SymGetLineFromAddr64(self.hproc, addr, ctypes.byref(disp), ctypes.byref(line)):
            return None
        fname = line.FileName.decode("utf-8", "replace") if line.FileName else None
        return {"file": fname, "line": line.LineNumber, "displacement": disp.value}

    def resolve(self, addr: int) -> dict:
        """绝对地址 -> 符号 + 源码行。"""
        sym = self._sym_from_addr(addr)
        line = self._line_from_addr(addr)
        out = {"address": addr, "symbol": None, "function": None,
               "displacement": None, "line": None, "source": None}
        if sym:
            out["symbol"] = sym["name"]
            # dbghelp 对 C++ 符号给出 `void __cdecl foo(int)` 或 `ns::Class::method` 形态。
            # 取"函数名"这一段：去掉参数列表，便于阅读与 grep。
            out["function"] = sym["name"].split("(")[0].strip() or sym["name"]
            out["displacement"] = sym["displacement"]
        if line:
            out["source"] = line["file"]
            out["line"] = line["line"]
        return out

    def resolve_rva(self, module: str, rva: int) -> dict:
        base = self.base_of(module)
        if base is None:
            raise SymError(f"模块 {module} 未加载")
        r = self.resolve(base + rva)
        r["module"] = module
        r["rva"] = rva
        return r


# --------------------------------------------------------------------------
# PDB / DLL 签名校验（不依赖 dbghelp 的静默行为）
# --------------------------------------------------------------------------

def _pe_sections(data: bytes):
    if data[:2] != b"MZ":
        raise SymError("不是 PE 文件")
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    if data[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise SymError("PE 签名缺失")
    coff = e_lfanew + 4
    nsec, = struct.unpack_from("<H", data, coff + 2)
    opt_size, = struct.unpack_from("<H", data, coff + 16)
    opt = coff + 20
    magic, = struct.unpack_from("<H", data, opt)
    pe32plus = magic == 0x20B
    dd_off = opt + (112 if pe32plus else 96)
    # IMAGE_DIRECTORY_ENTRY_DEBUG = 6
    dbg_rva, dbg_size = struct.unpack_from("<II", data, dd_off + 6 * 8)
    image_base = struct.unpack_from("<Q", data, opt + 24)[0] if pe32plus \
        else struct.unpack_from("<I", data, opt + 28)[0]
    size_of_image, = struct.unpack_from("<I", data, opt + 56)
    sec_off = opt + opt_size
    secs = []
    for i in range(nsec):
        o = sec_off + i * 40
        name = data[o:o + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, o + 8)
        secs.append({"name": name, "vsize": vsize, "vaddr": vaddr,
                     "rawsize": rawsize, "rawptr": rawptr,
                     "characteristics": struct.unpack_from("<I", data, o + 36)[0]})
    return {"image_base": image_base, "size_of_image": size_of_image,
            "sections": secs, "dbg_rva": dbg_rva, "dbg_size": dbg_size}


def _rva_to_off(pe, rva):
    for s in pe["sections"]:
        if s["vaddr"] <= rva < s["vaddr"] + max(s["vsize"], s["rawsize"]):
            return s["rawptr"] + (rva - s["vaddr"])
    return None


def pe_image_base(path: str) -> int:
    with open(path, "rb") as f:
        return _pe_sections(f.read(0x1000))["image_base"]


def pe_image_size(path: str) -> int:
    with open(path, "rb") as f:
        return _pe_sections(f.read(0x1000))["size_of_image"]


def read_rsds(dll_path: str) -> dict | None:
    """从 DLL 的调试目录读 CodeView RSDS：GUID + Age + PDB 路径。"""
    with open(dll_path, "rb") as f:
        data = f.read()
    pe = _pe_sections(data)
    if not pe["dbg_rva"] or not pe["dbg_size"]:
        return None
    off = _rva_to_off(pe, pe["dbg_rva"])
    if off is None:
        return None
    for i in range(pe["dbg_size"] // 28):
        o = off + i * 28
        typ, = struct.unpack_from("<I", data, o + 12)
        size, = struct.unpack_from("<I", data, o + 16)
        ptr, = struct.unpack_from("<I", data, o + 24)
        if typ != 2:  # IMAGE_DEBUG_TYPE_CODEVIEW
            continue
        blob = data[ptr:ptr + size]
        if blob[:4] != b"RSDS":
            continue
        guid = blob[4:20]
        age, = struct.unpack_from("<I", blob, 20)
        name = blob[24:].split(b"\0")[0].decode("utf-8", "replace")
        return {"guid": guid.hex().upper(), "age": age, "pdb_name": name,
                "guid_raw": guid}
    return None


_MSF_MAGIC = b"Microsoft C/C++ MSF 7.00\r\n\x1aDS\0\0\0"


def read_pdb_identity(pdb_path: str) -> dict | None:
    """从 PDB 的 MSF 信息流（stream 1）读 GUID + Age。

    PDB 7.0 文件是 MSF 容器：超级块 -> 块映射 -> 目录 -> 各流。stream 1 头部
    依次是 Version / Signature / Age / GUID。dbghelp 就是拿这里的 GUID+Age 与 DLL
    里 RSDS 的做比对。
    """
    with open(pdb_path, "rb") as f:
        data = f.read()
    if not data.startswith(_MSF_MAGIC):
        return None
    page_size, _free, _npages, dir_size, _unk, block_map = struct.unpack_from(
        "<IIIIII", data, 32)
    if page_size == 0 or page_size > (1 << 20):
        return None

    def page(n):
        return data[n * page_size:(n + 1) * page_size]

    # 块映射页里存的是"目录块"的页号数组
    n_dir_blocks = (dir_size + page_size - 1) // page_size
    bm = page(block_map)
    dir_blocks = struct.unpack_from(f"<{n_dir_blocks}I", bm, 0)
    directory = b"".join(page(b) for b in dir_blocks)

    n_streams, = struct.unpack_from("<I", directory, 0)
    sizes = struct.unpack_from(f"<{n_streams}I", directory, 4)
    p = 4 + n_streams * 4
    stream_blocks = []
    for i in range(n_streams):
        # MSF 用 0xFFFFFFFF 标记"未使用的流"；不识别它会把 0xFFFFFFFF 当成
        # 约 4GB 的流长度，进而算出天文数字的页数并炸掉解析（Debug PDB 上真实踩到）。
        if sizes[i] == 0xFFFFFFFF:
            stream_blocks.append(())
            continue
        nb = (sizes[i] + page_size - 1) // page_size
        if nb == 0 or p + nb * 4 > len(directory):
            stream_blocks.append(())
            continue
        blocks = struct.unpack_from(f"<{nb}I", directory, p)
        p += nb * 4
        stream_blocks.append(blocks)

    if n_streams < 2:
        return None
    s1 = b"".join(page(b) for b in stream_blocks[1])
    if len(s1) < 28:
        return None
    version, signature, age = struct.unpack_from("<III", s1, 0)
    guid = s1[12:28]
    return {"guid": guid.hex().upper(), "age": age, "version": version,
            "signature": signature, "guid_raw": guid}


def verify_pair(dll_path: str, pdb_path: str) -> dict:
    """校验 DLL 与 PDB 是否同一次构建。返回结论，不抛异常。"""
    res = {"dll": os.path.abspath(dll_path), "pdb": os.path.abspath(pdb_path),
           "match": False, "reason": None, "dll_guid": None, "pdb_guid": None,
           "age": None, "pdb_name_in_dll": None}
    if not os.path.isfile(dll_path):
        res["reason"] = "DLL 不存在"
        return res
    if not os.path.isfile(pdb_path):
        res["reason"] = "PDB 不存在"
        return res
    rsds = read_rsds(dll_path)
    if not rsds:
        res["reason"] = "DLL 调试目录里没有 CodeView(RSDS) 记录"
        return res
    res["dll_guid"] = rsds["guid"]
    res["age"] = rsds["age"]
    res["pdb_name_in_dll"] = rsds["pdb_name"]
    ident = read_pdb_identity(pdb_path)
    if not ident:
        res["reason"] = "PDB 不是 MSF 7.0 容器或读取失败"
        return res
    res["pdb_guid"] = ident["guid"]
    if ident["guid"] != rsds["guid"]:
        res["reason"] = "GUID 不一致：PDB 与 DLL 不是同一次构建"
        return res
    if ident["age"] != rsds["age"]:
        res["reason"] = f"Age 不一致：DLL={rsds['age']} PDB={ident['age']}"
        return res
    res["match"] = True
    res["reason"] = "GUID/Age 完全一致"
    return res


def find_pdb_for_dll(dll_path: str) -> str | None:
    """按 RSDS 里记录的 PDB 文件名 + DLL 同目录/构建产物目录去找 PDB。"""
    rsds = read_rsds(dll_path)
    cands = []
    if rsds and rsds["pdb_name"]:
        base = os.path.basename(rsds["pdb_name"])
        d = os.path.dirname(os.path.abspath(dll_path))
        cands.append(os.path.join(d, base))
        # 大小写不敏感地找同目录
        try:
            for f in os.listdir(d):
                if f.lower() == base.lower():
                    cands.append(os.path.join(d, f))
        except OSError:
            pass
    for c in cands:
        if os.path.isfile(c):
            return c
    return None


# --------------------------------------------------------------------------
# 现场 JSON 的地址抽取
# --------------------------------------------------------------------------

def addresses_from_scene(scene_path: str) -> list[dict]:
    """从 crashscope 现场 JSON 里抽出所有 (模块, RVA) 候选。"""
    with open(scene_path, encoding="utf-8") as f:
        d = json.load(f)
    cs = d.get("crash_scene") or (d.get("scenes") or [None])[-1]
    if not cs:
        return []
    out = []
    seen = set()

    def add(module, rva, where):
        if not module or rva is None:
            return
        key = (module.lower(), int(rva))
        if key in seen:
            return
        seen.add(key)
        out.append({"module": module, "rva": int(rva), "where": where})

    ct = cs.get("callsite") or {}
    if ct.get("callsite"):
        c = ct["callsite"]
        add(c.get("module"), int(c["rva"], 16) if c.get("rva") else None,
            "调用点(指令地址)")
    if ct.get("return_module") and ct.get("return_rva"):
        add(ct["return_module"], int(ct["return_rva"], 16), "call 的返回地址")

    ss = cs.get("stack_scan") or {}
    for h in (ss.get("top_hits") or []):
        add(h.get("module"), h.get("rva"), f"栈槽 rsp+{h.get('from_rsp')}")
    for h in (cs.get("raw_stack_top") or []):
        add(h.get("in_module"), int(h["rva"], 16) if h.get("rva") else None,
            f"栈顶原始槽 rsp+{h.get('from_rsp')}")
    for u in (cs.get("unwind") or {}).get("frames", []):
        add(u.get("module"), u.get("rva"), f"unwind 帧#{u.get('frame')}")
    return out


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------

def _fmt_symbol(r, module=None, rva=None):
    tag = f"{module}+0x{rva:X}" if module is not None and rva is not None \
        else f"0x{r['address']:X}"
    if r["symbol"]:
        s = f"{tag}  ->  {r['symbol']}"
        if r["displacement"]:
            s += f" + 0x{r['displacement']:X}"
        if r["source"]:
            s += f"\n{'':>{len(tag)}}      源码: {r['source']}:{r['line']}"
        else:
            s += "\n" + " " * (len(tag) + 6) + "源码: （无行号信息）"
    else:
        s = f"{tag}  ->  （未解析出符号）"
    return s


def main():
    ap = argparse.ArgumentParser(
        description="把 模块+RVA 符号化成 函数名+偏移（+源码行）")
    ap.add_argument("--module", action="append", default=[],
                    help="模块路径（DLL）；可多次指定")
    ap.add_argument("--rva", default=None,
                    help="RVA（十六进制，可逗号分隔多个）")
    ap.add_argument("--addr", default=None,
                    help="绝对地址（十六进制，可逗号分隔多个）")
    ap.add_argument("--scene", default=None, help="crashscope 现场 JSON")
    ap.add_argument("--pdb", default=None, help="显式指定 PDB（默认自动查找）")
    ap.add_argument("--base", default=None, help="加载基址（默认取 PE ImageBase）")
    ap.add_argument("--sympath", default=None, help="符号搜索路径（覆盖默认）")
    ap.add_argument("--msf", action="store_true",
                    help="启用微软公共符号服务器（需联网）")
    ap.add_argument("--no-verify", action="store_true",
                    help="跳过 PDB/DLL 签名校验（不推荐）")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()

    if not a.module and not a.scene:
        ap.error("至少需要 --module 或 --scene")
    if not a.module:
        ap.error("--scene 模式也需要 --module 指明要解析哪个模块的地址")

    sympath = a.sympath
    if sympath is None:
        sympath = DEFAULT_SYMPATH if a.msf else os.path.join(HERE, "_nosymsrv")
        if not a.msf:
            # 不给符号服务器时，把本地缓存目录也塞进去，仍能命中已下载的符号
            sympath = DEFAULT_SYMPATH

    print(f"dbghelp: {DBG_SOURCE}")
    print(f"符号路径: {sympath}")
    print()

    with Symbolizer(sympath=sympath, verbose=a.verbose) as sz:
        loaded = {}
        for mp in a.module:
            mp_abs = os.path.abspath(mp)
            if not os.path.isfile(mp_abs):
                print(f"⚠ 模块不存在: {mp_abs}")
                continue
            # ---- PDB 校验 ----
            pdb = a.pdb or find_pdb_for_dll(mp_abs)
            if pdb and not a.no_verify:
                v = verify_pair(mp_abs, pdb)
                print(f"PDB 校验: {os.path.basename(mp_abs)}")
                print(f"  DLL GUID={v['dll_guid']} Age={v['age']}")
                print(f"  PDB GUID={v['pdb_guid']}")
                print(f"  结论: {'✅ 匹配' if v['match'] else '❌ 不匹配'} ({v['reason']})")
                if not v["match"]:
                    print("  ⇒ 按约束拒绝符号化（不硬解）。请换用与运行 DLL 同一次"
                          "构建的 PDB。")
                    continue
                print()
            elif not pdb:
                print(f"⚠ 未找到 {os.path.basename(mp_abs)} 的 PDB，将只有导出表符号\n")
            base = int(a.base, 16) if a.base else None
            info = sz.load(mp_abs, base=base)
            # 用小写名做键：现场 JSON 里的模块名大小写不一定与磁盘文件名一致
            loaded[info["name"].lower()] = info

        if not loaded:
            print("没有可用模块，退出。")
            return 1

        jobs = []
        if a.rva:
            for x in a.rva.split(","):
                jobs.append(("rva", int(x.strip(), 16), None, next(iter(loaded))))
        if a.addr:
            for x in a.addr.split(","):
                jobs.append(("addr", int(x.strip(), 16), None, None))
        if a.scene:
            for item in addresses_from_scene(a.scene):
                key = item["module"].lower()
                if key in loaded:
                    jobs.append(("rva", item["rva"], item["where"], key))

        if not jobs:
            print("没有要解析的地址。")
            return 1

        print("=" * 78)
        for job in jobs:
            kind, val, where, modkey = job
            if kind == "rva":
                mod = loaded[modkey]["name"]
                r = sz.resolve_rva(mod, val)
                line = _fmt_symbol(r, mod, val)
            else:
                r = sz.resolve(val)
                line = _fmt_symbol(r)
            if where:
                print(f"[{where}]")
            print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
