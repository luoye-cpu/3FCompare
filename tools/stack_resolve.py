#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
stack_resolve.py —— 把 dump_capture.py 抓到的原始栈解析成候选调用序列。

输入：同一个前缀的 `.stack.bin`（ReadProcessMemory 拷出的栈）+ `.meta.txt`（RSP/RIP）
      + 一份同次崩溃的 `.dmp`（只为取模块表）。
输出：自 RSP 向上按模块归属的候选帧序列；FFF.Native.dll 的帧会用 PDB 解析到函数名。

局限（必须知道）：无符号栈扫描无法校验某个栈槽是否真是返回地址，
给出的是**候选**调用序列；但对于"崩溃前最后一个自有模块是哪个"这一判断足够。

用法：
    python tools/stack_resolve.py <前缀>            # 前缀 = xxx.stack.bin 去掉后缀
    python tools/stack_resolve.py <前缀> --pdb <FFF.Native.pdb> --dll <FFF.Native.dll>
"""

import os
import re
import struct
import subprocess
import sys

NOISE = {"ntdll.dll", "kernel32.dll", "kernelbase.dll", "user32.dll", "win32u.dll",
         "gdi32.dll", "msvcrt.dll", "ucrtbase.dll", "ole32.dll", "oleaut32.dll",
         "combase.dll", "rpcrt4.dll", "advapi32.dll", "sechost.dll", "bcrypt.dll",
         "shell32.dll", "shcore.dll", "imm32.dll", "msvcp140.dll", "vcruntime140.dll",
         "concrt140.dll", "dbghelp.dll", "dbgcore.dll", "powrprof.dll", "umpdc.dll",
         "cfgmgr32.dll", "propsys.dll", "windows.storage.dll", "wintypes.dll"}


def read_modules(dmp):
    """从 minidump 的 ModuleListStream 取模块基址/大小/名字。"""
    data = open(dmp, "rb").read()
    nstreams = struct.unpack_from("<I", data, 8)[0]
    dirrva = struct.unpack_from("<I", data, 12)[0]
    for i in range(nstreams):
        stype, _, rva = struct.unpack_from("<III", data, dirrva + i * 12)
        if stype != 4:
            continue
        cnt = struct.unpack_from("<I", data, rva)[0]
        mods = []
        for k in range(cnt):
            off = rva + 4 + k * 108
            base, size = struct.unpack_from("<QI", data, off)
            # MINIDUMP_MODULE: BaseOfImage(8) SizeOfImage(4) CheckSum(4)
            #   TimeDateStamp(4) ModuleNameRva(4) —— 名字 RVA 在 +20，不是 +16
            name_rva = struct.unpack_from("<I", data, off + 20)[0]
            if not name_rva:
                continue
            ln = struct.unpack_from("<I", data, name_rva)[0]
            name = data[name_rva + 4:name_rva + 4 + ln].decode("utf-16-le", "replace")
            mods.append({"base": base, "size": size, "name": os.path.basename(name)})
        return mods
    return []


def module_of(addr, mods):
    for m in mods:
        if m["base"] <= addr < m["base"] + m["size"]:
            return m
    return None


def main():
    args = [a for a in sys.argv[1:]]
    pdb = dll = None
    if "--pdb" in args:
        pdb = args[args.index("--pdb") + 1]
        args = args[:args.index("--pdb")] + args[args.index("--pdb") + 2:]
    if "--dll" in args:
        dll = args[args.index("--dll") + 1]
        args = args[:args.index("--dll")] + args[args.index("--dll") + 2:]
    if not args:
        print(__doc__)
        return 2
    prefix = args[0]
    stack_path = prefix + ".stack.bin"
    meta_path = prefix + ".meta.txt"
    if not os.path.exists(stack_path):
        print(f"找不到 {stack_path}")
        return 2

    meta = {}
    if os.path.exists(meta_path):
        for line in open(meta_path, encoding="utf-8"):
            if "=" in line:
                k, v = line.strip().split("=", 1)
                meta[k] = v
    print("崩溃线程: TID=%s  RIP=%s  RSP=%s  RBP=%s" % (
        meta.get("tid", "?"), meta.get("rip", "?"), meta.get("rsp", "?"), meta.get("rbp", "?")))

    dmp = None
    if "--dmp" in args:
        dmp = args[args.index("--dmp") + 1]
        args = args[:args.index("--dmp")] + args[args.index("--dmp") + 2:]
    if not dmp:
        d = os.path.dirname(os.path.abspath(stack_path)) or "."
        for cand in sorted(os.listdir(d)):
            if cand.endswith(".dmp"):      # 同目录下任一份同批次转储都能提供模块表
                dmp = os.path.join(d, cand)
                break
    mods = read_modules(dmp) if dmp else []
    if not mods:
        print("未找到同批次的 .dmp，无法做模块归属（只输出原始地址）")

    raw = open(stack_path, "rb").read()
    rsp = int(meta["rsp"], 16) if meta.get("rsp") else 0

    print("候选调用序列（自 RSP 向上，去重相邻同模块；[系统]=Windows 模块）:")
    last = None
    shown = 0
    native_rvas = []
    for off in range(0, len(raw) - 7, 8):
        val = struct.unpack_from("<Q", raw, off)[0]
        if val < 0x10000 or val > 0x000F000000000000:
            continue
        m = module_of(val, mods) if mods else None
        nm = m["name"] if m else None
        if nm is None:
            continue
        if nm == last:
            continue
        last = nm
        rva = val - m["base"]
        tag = "  [系统]" if nm.lower() in NOISE else ""
        print("   +0x%-6X 0x%016X  %-24s +0x%-8X%s" % (off, val, nm, rva, tag))
        if nm.lower() == "fff.native.dll":
            native_rvas.append(rva)
        shown += 1
        if shown >= 45:
            print("   ...（截断）")
            break
    if not shown:
        print("   （栈内未找到落在模块映像区间的指针）")

    if native_rvas and pdb and dll and os.path.exists(pdb):
        print("\nFFF.Native.dll 帧符号化:")
        try:
            out = subprocess.run(
                [sys.executable, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                              "pdb_resolve.py"), pdb, dll]
                + [hex(r) for r in native_rvas],
                capture_output=True, text=True, timeout=300)
            print(out.stdout or out.stderr)
        except Exception as exc:
            print(f"  符号化失败: {exc}")
    elif native_rvas:
        print(f"\nFFF.Native.dll 帧 RVA: {[hex(r) for r in native_rvas]}"
              f"（加 --pdb/--dll 可解析函数名）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
